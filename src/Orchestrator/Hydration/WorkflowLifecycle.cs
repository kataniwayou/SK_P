using System.Text.Json;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging;
using Orchestrator.L1;
using Orchestrator.Messaging;
using Orchestrator.Scheduling;
using Quartz;
using StackExchange.Redis;

namespace Orchestrator.Hydration;

/// <summary>
/// Shared hydrate-one + teardown-one unit (D-15) reused by the startup
/// <see cref="HydrationBackgroundService"/> AND both consumers (Start = teardown+hydrate+schedule,
/// Stop = teardown-only). All L2 access is READ-ONLY — this type never issues any string-set /
/// set-add / key-delete mutation against any <c>skp:</c> key (ORCH-STOP-01 / T-23-11).
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

        // Follow the FULL reachable step graph into L1 (read-only), BFS from the entry steps along
        // each step's NextStepIds. Hydrating only entry steps would leave downstream steps absent from
        // the L1 map, so StepAdvancement.SelectNext would silently skip every continuation edge
        // (TryGetValue miss) and no multi-step series could advance past its entry step. A
        // corrupt/missing step is a business skip for THAT step — its subtree is simply not enqueued
        // beyond what other paths reach; the rest still hydrate.
        var steps = new Dictionary<Guid, StepProjection>();
        var toVisit = new Queue<Guid>(root.EntryStepIds);
        var seen = new HashSet<Guid>(root.EntryStepIds);
        while (toVisit.Count > 0)
        {
            var stepId = toVisit.Dequeue();
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
                foreach (var nextId in step.NextStepIds ?? Enumerable.Empty<Guid>())
                    if (seen.Add(nextId))
                        toVisit.Enqueue(nextId);
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
    /// <para>
    /// Used by the conditionless Start reload pre-clean ONLY (Pitfall 4): Start must unschedule the
    /// old Quartz job before re-scheduling, and the immediate re-hydrate re-Upserts L1 so the transient
    /// <c>store.Remove</c> is harmless. Stop must NOT use this — it would drop L1 and break drain;
    /// Stop uses <see cref="UnscheduleOnlyAsync"/> instead (D-07).
    /// </para>
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
    /// Stop path (D-07 — ORCH-STOP-DRAIN-01): resolve the jobId from L1 and
    /// <c>DeleteJob(JobKey(jobId))</c>, but KEEP the L1 entry so late
    /// <c>ExecutionResult</c> messages for the stopped workflow still
    /// resolve in L1 and drain (dispatch their next steps). Unlike <see cref="TeardownAsync"/> this
    /// does NOT call <c>store.Remove</c>. Absent-from-L1 is a business no-op; ZERO L2 writes.
    /// </summary>
    public async Task UnscheduleOnlyAsync(Guid workflowId, CancellationToken ct)
    {
        if (!store.TryGet(workflowId, out var wf))
        {
            // BUSINESS no-op — nothing to unschedule.
            return;
        }

        await scheduler.UnscheduleAsync(wf.JobId, ct); // jobId-addressed DeleteJob — NO store.Remove (keep L1)
    }

    /// <summary>Pause path (D-06/D-08): resolve jobId from L1 and PauseJob — KEEP the L1 entry.
    /// Absent-from-L1 is a business no-op. Idempotent (re-pausing a paused job is a Quartz no-op).</summary>
    public async Task PauseOnlyAsync(Guid workflowId, CancellationToken ct)
    {
        if (!store.TryGet(workflowId, out var wf))
        {
            // BUSINESS no-op — nothing to pause.
            return;
        }

        await scheduler.PauseAsync(wf.JobId, ct); // PauseJob — preserves the job + trigger, keeps L1
    }

    /// <summary>Resume path (D-06/D-09): act ONLY if the trigger is Paused; delete the stale paused
    /// job and schedule a FRESH from-now trigger off L1's cron. None(Stopped)/Normal(Running) ignored.
    /// Guard on == Paused exactly (RESEARCH §4 — Blocked/Error fall through to ignore).</summary>
    public async Task ResumeAsync(Guid workflowId, CancellationToken ct)
    {
        if (!store.TryGet(workflowId, out var wf))
        {
            // BUSINESS no-op — nothing to resume.
            return;
        }

        var state = await scheduler.GetTriggerStateAsync(wf.JobId, ct);
        if (state != TriggerState.Paused)
        {
            // None(Stopped)/Normal(Running)/Blocked/Error -> ignore (D-09).
            return;
        }

        await scheduler.UnscheduleAsync(wf.JobId, ct);                    // DeleteJob (removes stale paused job)
        await scheduler.ScheduleAsync(workflowId, wf.JobId, wf.Cron, ct); // fresh from-now trigger -> Normal (sidesteps misfire)
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
