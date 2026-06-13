using Microsoft.Extensions.Configuration;

namespace BaseProcessor.Core.Configuration;

/// <summary>
/// CONFIG-01 / CONFIG-02: the processor liveness/heartbeat + execution-data knobs, bound from the
/// <c>"Processor"</c> config section. Six INDEPENDENT seconds-int auto-properties with baked defaults
/// (mirrors <c>RedisProjectionOptions</c>).
///
/// <para>
/// The bound config keys are <c>Interval</c>/<c>StartupInterval</c>/<c>Ttl</c>/<c>RequestTimeout</c>/<c>BackoffCap</c>/<c>ExecutionDataTtl</c>
/// (no <c>Seconds</c> suffix); the property names carry the <c>Seconds</c> suffix for clarity, so
/// each property declares a <see cref="ConfigurationKeyNameAttribute"/> mapping it to the bare key.
/// </para>
/// </summary>
public sealed class ProcessorLivenessOptions
{
    /// <summary>Heartbeat delay in seconds (CONFIG-01 / D-11; default 10). Written as the L2 <c>interval</c>
    /// field on <c>healthy</c> heartbeat entries, used by the reader's <c>timestamp + interval×2</c>
    /// staleness math.</summary>
    [ConfigurationKeyName("Interval")]
    public int IntervalSeconds { get; set; } = 10;

    /// <summary>Startup-loop staleness anchor in seconds (D-12; default 30 = BackoffCap). Recorded as the
    /// <c>interval</c> on the orchestrator's <c>unhealthy</c> entries so the Phase-61 staleness math + the
    /// derived TTL cover the slowest backoff write.</summary>
    [ConfigurationKeyName("StartupInterval")]
    public int StartupIntervalSeconds { get; set; } = 30;

    /// <summary>Sliding liveness-key expiry floor in seconds (CONFIG-01; default 30). INDEPENDENT of
    /// <see cref="IntervalSeconds"/> — the TTL floor folded into the per-instance key
    /// (<c>skp:proc:{id}:{instanceId}</c>) via the writer's derived-TTL formula
    /// <c>max(interval×2, TtlSeconds)</c> (the old flat <c>skp:{id}</c> write was removed this phase — D-05,
    /// no dual-write).</summary>
    [ConfigurationKeyName("Ttl")]
    public int TtlSeconds { get; set; } = 30;

    /// <summary>Per-<c>IRequestClient</c> request timeout in seconds (D-04; default 8, ~5–10s).</summary>
    [ConfigurationKeyName("RequestTimeout")]
    public int RequestTimeoutSeconds { get; set; } = 8;

    /// <summary>Retry backoff cap in seconds (D-04; default 30). The bounded-backoff curve doubles
    /// the delay up to this cap.</summary>
    [ConfigurationKeyName("BackoffCap")]
    public int BackoffCapSeconds { get; set; } = 30;

    /// <summary>Execution-data L2-key TTL in seconds (CONFIG-02 / D-17; default 300). DISTINCT from
    /// the liveness <see cref="TtlSeconds"/> — applied on every <c>L2[data(newEntryId)]</c> output
    /// write so step-to-step chained data outlives a fast dispatch but is bounded (FUT-PROC-02).</summary>
    [ConfigurationKeyName("ExecutionDataTtl")]
    public int ExecutionDataTtlSeconds { get; set; } = 300;
}
