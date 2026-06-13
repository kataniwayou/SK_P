using System.Text.Json;
using BaseProcessor.Core.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace BaseProcessor.Core.Liveness;

/// <summary>
/// The single shared internal liveness writer (RESEARCH primary recommendation + CONTEXT Specifics) that
/// BOTH the startup orchestrator (<c>unhealthy</c> writer) and the heartbeat (<c>healthy</c> writer) call,
/// so the L2-SET + index-SADD + L1-Update + derived-TTL disciplines cannot drift between the two loops.
///
/// <para>
/// <b>One write path (D-09):</b> <see cref="WriteAsync"/> Updates the L1 holder from the SAME immutable
/// <see cref="ProcessorLivenessEntry"/> it SETs to L2 — L1 and L2 are provably identical that iteration.
/// </para>
/// <para>
/// <b>L1 unconditional (Open Q3 RESOLVED):</b> the L1 record is Updated BEFORE / independent of the Redis
/// attempt — the Phase-61 self-watchdog wants the latest in-process truth, not Redis reachability.
/// </para>
/// <para>
/// <b>Derived TTL (D-13):</b> the per-instance key TTL = <c>max(entry.Interval × 2, Ttl-floor)</c>. The
/// caller already baked the active interval into the entry via <see cref="ProcessorLivenessEntry.Create"/>
/// (heartbeat → 10, startup → 30), so the writer never branches on "which loop am I".
/// </para>
/// <para>
/// <b>Idempotent SADD (D-15):</b> set semantics make a double-add by both loops harmless; no
/// "have I added?" flag.
/// </para>
/// <para>
/// <b>Resilience (D-11 / T-26-10):</b> a Redis fault is logged-and-continued — <see cref="WriteAsync"/>
/// never throws and never crashes the host / aborts resolution; L1 is updated regardless.
/// </para>
/// <para>
/// <c>public sealed</c> so <c>services.AddSingleton&lt;ProcessorLivenessWriter&gt;()</c> resolves and
/// <c>AddBaseProcessorFacts</c> can assert its descriptor without <c>InternalsVisibleTo</c> — same
/// rationale as <c>ProcessorContext</c> / <see cref="ProcessorLivenessState"/>.
/// </para>
/// </summary>
public sealed class ProcessorLivenessWriter
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IProcessorLivenessState _l1;
    private readonly ProcessorLivenessOptions _options;
    private readonly ILogger<ProcessorLivenessWriter> _logger;

    public ProcessorLivenessWriter(
        IConnectionMultiplexer redis,
        IProcessorLivenessState l1,
        IOptions<ProcessorLivenessOptions> options,
        ILogger<ProcessorLivenessWriter> logger)
    {
        _redis   = redis ?? throw new ArgumentNullException(nameof(redis));
        _l1      = l1 ?? throw new ArgumentNullException(nameof(l1));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger  = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task WriteAsync(Guid processorId, string instanceId, ProcessorLivenessEntry entry)
    {
        // D-09 / Open Q3 RESOLVED: update L1 UNCONDITIONALLY — the watchdog wants latest in-process truth,
        // not Redis reachability. The L1 record IS the same immutable entry written to L2 this iteration.
        _l1.Update(entry);
        try
        {
            var db = _redis.GetDatabase();
            var json = JsonSerializer.Serialize(entry);                  // DEFAULT options — the [JsonPropertyName] pins carry the wire shape (Pitfall 2: do NOT reshape)
            var ttl = Math.Max(entry.Interval * 2, _options.TtlSeconds); // D-13: max(activeInterval*2, Ttl-floor); entry.Interval is the recorded active interval

            // Blind whole-value last-write-wins SET..EX — NO RMW, NO stoppingToken threaded in (command timeout is the bound). Key via L2ProjectionKeys, never a literal.
            await db.StringSetAsync(
                L2ProjectionKeys.PerInstance(processorId, instanceId),
                json,
                expiry: TimeSpan.FromSeconds(ttl));

            // Idempotent SADD (D-15) — set semantics make a double-add by both loops harmless; no "have I added?" flag.
            // instanceId is already a string (InstanceId.Resolve()) — no ToString.
            await db.SetAddAsync(L2ProjectionKeys.InstanceIndex(processorId), instanceId);
        }
        catch (Exception ex)
        {
            // Resilience (D-11 / heartbeat T-26-10): log-and-CONTINUE; never throw, never crash the host / abort resolution.
            _logger.LogWarning(ex, "Liveness write failed for processor {ProcessorId}; will retry next iteration", processorId);
        }
    }
}
