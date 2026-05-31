using System.Text.Json;
using BaseApi.Service.Features.Orchestration.Projection;
using Messaging.Contracts.Projections;
using StackExchange.Redis;

namespace BaseApi.Service.Features.Orchestration.Validation;

/// <summary>
/// Processor existence + timestamp-liveness gate (PROC-LIVE-01). ASYNC — reads each participating
/// processor's self-registered L2 entry (skp:{procId}), unlike the three sync gates. Absent or stale
/// (timestamp + interval*2 &lt;= now) throws OrchestrationValidationException.ProcessorNotLive → 422,
/// the same contract as SchemaEdgeValidator. `interval` is interpreted in SECONDS and sourced from the
/// entry (NOT the hardcoded 0). Slots AFTER the sync trio and BEFORE UpsertAsync (D-15). The only Redis
/// reads are the per-processor liveness GETs (external self-registered data, not in the L1 snapshot).
/// </summary>
internal sealed class ProcessorLivenessValidator
{
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly TimeProvider _clock;

    public ProcessorLivenessValidator(IConnectionMultiplexer multiplexer, TimeProvider clock)
    {
        _multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
        _clock       = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task ValidateAsync(WorkflowGraphSnapshot snapshot, CancellationToken ct)
    {
        var db = _multiplexer.GetDatabase();
        var now = _clock.GetUtcNow().UtcDateTime;   // mirrors RedisProjectionWriter.cs:60
        foreach (var proc in snapshot.Processors.Values)
        {
            var raw = await db.StringGetAsync(RedisProjectionKeys.Processor(proc.Id));
            if (raw.IsNullOrEmpty)
                throw OrchestrationValidationException.ProcessorNotLive(proc.Id, "absent");

            // WR-01 — the entry is EXTERNAL self-registered data we do not own. System.Text.Json does NOT
            // enforce the non-nullable Liveness annotation at runtime (RespectNullableAnnotations is not
            // configured), so {}, {"liveness":null}, or invalid JSON would otherwise NRE/JsonException and
            // escape the redisOp catch as a 500. Map both malformed shapes to the 422 gate (not-live).
            ProcessorProjection? projection;
            try
            {
                projection = JsonSerializer.Deserialize<ProcessorProjection>(raw!);
            }
            catch (JsonException)
            {
                throw OrchestrationValidationException.ProcessorNotLive(proc.Id, "malformed");
            }

            if (projection?.Liveness is not { } liveness)
                throw OrchestrationValidationException.ProcessorNotLive(proc.Id, "malformed");

            // D-16 — interval in SECONDS, from the entry (not hardcoded 0): timestamp + interval*2 > now.
            var deadline = liveness.Timestamp.AddSeconds(liveness.Interval * 2);
            if (deadline <= now)
                throw OrchestrationValidationException.ProcessorNotLive(proc.Id, "stale");
        }
    }
}
