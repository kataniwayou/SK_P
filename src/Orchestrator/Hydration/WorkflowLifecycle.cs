using System.Text.Json;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging;
using Orchestrator.L1;
using Orchestrator.Messaging;
using Orchestrator.Scheduling;
using StackExchange.Redis;

namespace Orchestrator.Hydration;

/// <summary>
/// Shared hydrate-one + teardown-one unit (D-15) reused by the startup
/// <see cref="HydrationBackgroundService"/> AND both consumers (Start = teardown+hydrate+schedule,
/// Stop = teardown-only). All L2 access is READ-ONLY — this type never issues a
/// <c>StringSetAsync</c>/<c>SetAddAsync</c>/<c>KeyDeleteAsync</c> against any <c>skp:</c> key
/// (ORCH-STOP-01 / T-23-11).
/// <para>
/// <b>Business vs infra split (ORCH-ACK-01):</b> absent root, malformed JSON, and missing/corrupt
/// steps are BUSINESS outcomes — logged + skipped, never thrown. Redis connection/timeout faults are
/// INFRA — they propagate out of <c>GetDatabase</c>/<c>StringGetAsync</c> so the caller (consumer
/// retry pipeline or the hydration backoff loop) can react. See <see cref="IsBusiness"/> /
/// <see cref="IsInfra"/>, mirroring the consumer ack-split + <c>WorkflowRootNotFoundException</c> idiom.
/// </para>
/// </summary>
public sealed class WorkflowLifecycle(
    IConnectionMultiplexer redis,
    IWorkflowL1Store store,
    WorkflowScheduler scheduler,
    TimeProvider timeProvider,
    ILogger<WorkflowLifecycle> logger)
{
    /// <summary>
    /// Hydrate one workflow from L2 into L1 (root + step entries only — NO processor key, NO
    /// parent-index key) and schedule its one-shot Quartz job. Infra faults propagate; business
    /// outcomes (absent root, malformed step) log + skip.
    /// </summary>
    public async Task HydrateAndScheduleAsync(Guid workflowId, CancellationToken ct)
    {
        var db = redis.GetDatabase(); // infra fault THROWS -> propagates (D-02 / ORCH-ACK-01)

        var rootRaw = await db.StringGetAsync(OrchestratorL2Keys.Root(workflowId));
        if (rootRaw.IsNullOrEmpty)
        {
            // BUSINESS — workflow absent from L2 root; log + return (NEVER throw).
            logger.LogWarning("Workflow {WorkflowId} absent from L2 root — skipping hydration (business)", workflowId);
            return;
        }

        WorkflowRootProjection root;
        try
        {
            root = JsonSerializer.Deserialize<WorkflowRootProjection>(rootRaw!)
                   ?? throw new JsonException("root deserialized to null");
        }
        catch (Exception ex) when (IsBusiness(ex))
        {
            logger.LogWarning(ex, "Workflow {WorkflowId} root is malformed — skipping hydration (business)", workflowId);
            return;
        }

        if (string.IsNullOrWhiteSpace(root.Cron))
        {
            // BUSINESS — a workflow with no cron cannot be scheduled (D-09 business skip).
            logger.LogWarning("Workflow {WorkflowId} has no cron — skipping hydration (business)", workflowId);
            return;
        }

        // Follow each entry-step key into L1 (read-only). A corrupt/missing step is a business skip
        // for THAT step (the rest still hydrate).
        var steps = new Dictionary<Guid, StepProjection>();
        foreach (var stepId in root.EntryStepIds)
        {
            var stepRaw = await db.StringGetAsync(OrchestratorL2Keys.Step(workflowId, stepId));
            if (stepRaw.IsNullOrEmpty)
            {
                logger.LogWarning(
                    "Step {StepId} of workflow {WorkflowId} absent from L2 — skipping step (business)", stepId, workflowId);
                continue;
            }

            try
            {
                var step = JsonSerializer.Deserialize<StepProjection>(stepRaw!)
                           ?? throw new JsonException("step deserialized to null");
                steps[stepId] = step;
            }
            catch (Exception ex) when (IsBusiness(ex))
            {
                logger.LogWarning(
                    ex, "Step {StepId} of workflow {WorkflowId} is malformed — skipping step (business)", stepId, workflowId);
            }
        }

        // D-09: liveness interval = whole-seconds between the next two Cronos occurrences. Preserve the
        // stored timestamp/status; override only the computed interval (construct a fresh record).
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var interval = CronInterval.IntervalSeconds(root.Cron, nowUtc);
        var liveness = root.Liveness is { } l
            ? l with { Interval = interval }
            : new LivenessProjection(nowUtc, interval, "active");

        var entry = new WorkflowL1(root.EntryStepIds, root.Cron, root.JobId, steps)
        {
            Liveness = liveness,
        };

        store.Upsert(workflowId, entry);
        await scheduler.ScheduleAsync(workflowId, root.JobId, root.Cron, ct);
    }

    /// <summary>
    /// Tear down one workflow: resolve its jobId from L1, <c>DeleteJob(JobKey(jobId))</c>, and clear
    /// the L1 entry — ZERO L2 writes (ORCH-STOP-01). Absent-from-L1 is a business no-op (D-16).
    /// </summary>
    public async Task TeardownAsync(Guid workflowId, CancellationToken ct)
    {
        if (!store.TryGet(workflowId, out var wf))
        {
            // BUSINESS no-op — nothing to tear down (D-16).
            return;
        }

        await scheduler.UnscheduleAsync(wf.JobId, ct); // jobId-addressed DeleteJob (Pitfall 4c)
        store.Remove(workflowId);                      // NO L2 mutation
    }

    /// <summary>
    /// INFRA fault = a Redis connection/timeout fault (or a broker fault during scheduling). These
    /// propagate to the caller's retry/backoff. Everything else (JSON/format/absent) is business.
    /// </summary>
    public static bool IsInfra(Exception ex) =>
        ex is RedisConnectionException or RedisTimeoutException or RedisException;

    /// <summary>
    /// BUSINESS fault = malformed/absent L2 data (JSON deserialization, format, argument). The
    /// inverse of <see cref="IsInfra"/>: a fault we log + skip rather than propagate.
    /// </summary>
    public static bool IsBusiness(Exception ex) => !IsInfra(ex);
}
