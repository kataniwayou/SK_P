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
/// <see cref="WorkflowGraphSnapshot"/> into the three flat Redis keyspaces
/// (<c>{prefix}{wf}</c>, <c>{prefix}{wf}:{step}</c>, <c>{prefix}{proc}</c>) in one
/// <see cref="IBatch"/> pipeline.
/// <para>
/// <b>TTL (D-08):</b> only the per-processor keys carry an expiry of
/// <see cref="RedisProjectionOptions.ProcessorKeyTtlDays"/> (refresh-on-write); root and
/// per-step keys carry NO TTL. A configured value &lt;= 0 disables processor-key expiry.
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
        var prefix = _options.KeyPrefix;
        var days = _options.ProcessorKeyTtlDays;

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
        tasks.Add(batch.StringSetAsync(RedisProjectionKeys.Root(prefix, wf.Id), rootJson));

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
            tasks.Add(batch.StringSetAsync(RedisProjectionKeys.Step(prefix, wf.Id, step.Id), stepJson));
        }

        // --- Per-processor values — TTL ONLY here (D-08 / Pitfall 2) ---
        TimeSpan? ttl = days <= 0 ? (TimeSpan?)null : TimeSpan.FromDays(days);
        foreach (var proc in snapshot.Processors.Values)
        {
            var inputDefinition = proc.InputSchemaId is { } isid ? snapshot.Schemas[isid].Definition : null;
            var outputDefinition = proc.OutputSchemaId is { } osid ? snapshot.Schemas[osid].Definition : null;
            var procProjection = new ProcessorProjection(inputDefinition, outputDefinition, liveness);
            var procJson = JsonSerializer.Serialize(procProjection);
            // when: When.Always disambiguates from the newer Expiration/ValueCondition
            // StringSetAsync overload (SE.Redis 2.13.1) so the classic TimeSpan? expiry
            // overload binds — Rule 1 library-API fix vs the plan's verbatim snippet.
            tasks.Add(batch.StringSetAsync(
                RedisProjectionKeys.Processor(prefix, proc.Id), procJson, expiry: ttl, when: When.Always));
        }

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
