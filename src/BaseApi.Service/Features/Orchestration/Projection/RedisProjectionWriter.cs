using System.Text.Json;
using BaseApi.Core.Configuration;
using BaseApi.Service.Features.Orchestration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace BaseApi.Service.Features.Orchestration.Projection;

/// <summary>
/// L1→L2 write engine (L2-PROJECT-01). Projects a single-workflow
/// <see cref="WorkflowGraphSnapshot"/> into the root + per-step Redis keyspaces in one
/// <see cref="IBatch"/> pipeline, and SADDs the workflow id into the shared parent-index SET.
/// <para>
/// <b>Parent index (Phase 22 L2IDX-01):</b> on Start the writer <c>SADD</c>s the workflow id
/// (rendered <c>D</c>-format) into <c>RedisProjectionKeys.ParentIndex()</c> — idempotent on re-Start.
/// </para>
/// <para>
/// <b>Processor keys (Phase 22 PROC-NOCREATE-01):</b> the writer creates ZERO processor keys.
/// Processor L2 entries are owned solely by external self-registration; the writer writes only the
/// root key and the per-step keys (both with NO TTL).
/// </para>
/// <para>
/// <b>Liveness (D-05):</b> the shared <see cref="LivenessProjection"/> timestamp comes from
/// the injected <see cref="TimeProvider"/>; <c>status</c> is <c>"Pending"</c> and
/// <c>interval</c> is 0. <c>jobId</c> on the root is a fresh <see cref="Guid"/> per call.
/// </para>
/// <para>
/// <b>Partial-failure (D-03):</b> a mid-batch fault rethrows the first faulting task's
/// exception after one structured warning naming the workflow id, so Phase 4's fallback
/// handler maps it to a generic 500. Serialization is default System.Text.Json — the
/// camelCase shapes are pinned by <c>[property: JsonPropertyName]</c> on the Plan 01
/// records — no source-generated object mapper is used (L2-PROJECT-06).
/// </para>
/// </summary>
internal sealed class RedisProjectionWriter : IRedisProjectionWriter
{
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly RedisProjectionOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<RedisProjectionWriter> _logger;

    public RedisProjectionWriter(
        IConnectionMultiplexer multiplexer,
        IOptions<RedisProjectionOptions> options,
        TimeProvider clock,
        ILogger<RedisProjectionWriter> logger)
    {
        _multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
        _options     = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _clock       = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger      = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task UpsertAsync(WorkflowGraphSnapshot snapshot, string correlationId, CancellationToken ct)
    {
        // D-05 — single liveness sub-document shared by the root + every processor value.
        // Timestamp via TimeProvider (AuditInterceptor precedent); status "Pending"; interval 0.
        var now = _clock.GetUtcNow().UtcDateTime;
        var liveness = new LivenessProjection(now, 0, "Pending");

        // A per-workflow snapshot (Plan 04 calls LoadL1Async([oneId])) has exactly one Workflow.
        var wf = snapshot.Workflows.Values.Single();

        // --- Root value (field-source table; D-01 correlationId, D-05 fresh jobId) ---
        var root = new WorkflowRootProjection(
            EntryStepIds: wf.EntryStepIds ?? new List<Guid>(),
            Cron: wf.CronExpression,
            JobId: Guid.NewGuid(),
            Liveness: liveness,
            CorrelationId: correlationId);
        var rootJson = JsonSerializer.Serialize(root);

        var db = _multiplexer.GetDatabase();
        var batch = db.CreateBatch();
        var tasks = new List<Task>();

        // Root key — NO expiry.
        tasks.Add(batch.StringSetAsync(RedisProjectionKeys.Root(wf.Id), rootJson));

        // Parent index (L2IDX-01 / D-08) — SADD this workflow id into the shared parent-index SET.
        // Idempotent on re-Start (a SET add of an already-present member is a no-op).
        tasks.Add(batch.SetAddAsync(RedisProjectionKeys.ParentIndex(), wf.Id.ToString("D")));

        // --- Per-step values — NO expiry ---
        foreach (var step in snapshot.Steps.Values)
        {
            // A step is NOT guaranteed to have an Assignment (Workflow.AssignmentIds is
            // nullable per ENTITY-08; a Workflow may carry steps with no payload binding).
            // FirstOrDefault avoids a crash on that valid shape; an unbound step projects an
            // empty payload string (StepProjection.Payload is a non-nullable string member).
            var payload = snapshot.Assignments.Values
                .FirstOrDefault(a => a.StepId == step.Id)?.Payload ?? string.Empty;
            var stepProjection = new StepProjection(
                step.EntryCondition,
                step.ProcessorId,
                payload,
                step.NextStepIds ?? new List<Guid>());
            var stepJson = JsonSerializer.Serialize(stepProjection);
            tasks.Add(batch.StringSetAsync(RedisProjectionKeys.Step(wf.Id, step.Id), stepJson));
        }

        // Phase 22 PROC-NOCREATE-01 — the writer creates ZERO processor keys. Processor L2
        // entries are owned solely by external self-registration; the prior per-processor
        // TTL'd write loop was removed here.

        batch.Execute();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception)
        {
            // D-03 — one structured warning naming the workflow id, then rethrow the first
            // faulting task's exception so Phase 4's fallback handler maps it to a 500.
            // Partial state may exist (a subsequent Start re-SETs all keys; PUT-like idempotency).
            _logger.LogWarning(
                "L2 projection write partially failed for workflow {WorkflowId}; partial state may exist",
                wf.Id);
            throw;
        }
    }
}
