using System.Text.Json;
using Messaging.Contracts.Projections;
using StackExchange.Redis;

namespace BaseApi.Service.Features.Orchestration.Validation;

/// <summary>
/// Per-replica processor-liveness gate (GATE-01/02/03, D-06/07/08/09/10). ASYNC — for each participating
/// processor it discovers its replicas via SMEMBERS skp:proc:{procId} (the instance-index SET) with NO
/// prior knowledge of instanceIds, then GETs each per-instance key skp:proc:{procId}:{instanceId} and
/// deserializes a <see cref="ProcessorLivenessEntry"/>. The processor PASSES iff &gt;=1 discovered replica is
/// present AND status == LivenessStatus.Healthy AND non-stale (timestamp + interval*2 &gt; now, SECONDS) —
/// a single healthy+fresh replica admits even when siblings are unhealthy/stale/absent/malformed
/// (first-qualifier-wins short-circuit). When no replica qualifies it throws
/// OrchestrationValidationException.ProcessorNotLive → 422 + RFC 7807 with an aggregate count-only reason.
/// Replaces the legacy single last-write-wins GET skp:{procId} (ProcessorProjection) read.
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
            var members = await db.SetMembersAsync(L2ProjectionKeys.InstanceIndex(proc.Id)); // RedisValue[]
            int absent = 0, unhealthy = 0, stale = 0, malformed = 0;
            bool qualified = false;

            // WR-01 (per-replica) — each per-instance value is EXTERNAL self-registered data we do not own.
            // Every deterministic data state maps to the 422 gate (counted): a missing per-instance key =>
            // absent; invalid JSON or null Summary => malformed; status != Healthy => unhealthy; expired
            // freshness window => stale. None of them may escape as a JsonException/NRE — only a genuine
            // transport RedisException on SMEMBERS/GET propagates untouched to the caller's redisOp catch
            // (→ 500). The validator NEVER adds a RedisException catch (the 422-vs-500 split lives in the
            // caller, OrchestrationService.StartAsync).
            foreach (var member in members)
            {
                var instanceId = member.ToString();
                var raw = await db.StringGetAsync(L2ProjectionKeys.PerInstance(proc.Id, instanceId));
                if (raw.IsNullOrEmpty)
                {
                    absent++;
                    // D-09: absent-only lazy SREM, fire-and-forget — never awaited, never faults the verdict.
                    _ = db.SetRemoveAsync(L2ProjectionKeys.InstanceIndex(proc.Id), member, CommandFlags.FireAndForget);
                    continue;
                }

                ProcessorLivenessEntry? entry;
                try { entry = JsonSerializer.Deserialize<ProcessorLivenessEntry>(raw!); }
                catch (JsonException) { malformed++; continue; }          // WR-01: never a 500
                if (entry?.Summary is null) { malformed++; continue; }    // null-shape => fail that replica
                if (entry.Status != LivenessStatus.Healthy) { unhealthy++; continue; }
                if (entry.Timestamp.AddSeconds(entry.Interval * 2) <= now) { stale++; continue; }
                qualified = true; break;                                  // >=1 healthy+fresh => PASS
            }

            if (!qualified)
            {
                // D-08 / SECURITY V7 (T-14-02): COUNTS only — never instanceIds, connection strings, or stack traces.
                var reason = $"no healthy replica ({members.Length} checked: " +
                             $"{absent} absent, {unhealthy} unhealthy, {stale} stale, {malformed} malformed)";
                throw OrchestrationValidationException.ProcessorNotLive(proc.Id, reason);
            }
        }
    }
}
