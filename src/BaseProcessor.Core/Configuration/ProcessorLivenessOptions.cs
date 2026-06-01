using Microsoft.Extensions.Configuration;

namespace BaseProcessor.Core.Configuration;

/// <summary>
/// CONFIG-01 / CONFIG-02: the processor liveness/heartbeat + execution-data knobs, bound from the
/// <c>"Processor"</c> config section. Five INDEPENDENT seconds-int auto-properties with baked defaults
/// (mirrors <c>RedisProjectionOptions</c>).
///
/// <para>
/// The bound config keys are <c>Interval</c>/<c>Ttl</c>/<c>RequestTimeout</c>/<c>BackoffCap</c>/<c>ExecutionDataTtl</c>
/// (no <c>Seconds</c> suffix); the property names carry the <c>Seconds</c> suffix for clarity, so
/// each property declares a <see cref="ConfigurationKeyNameAttribute"/> mapping it to the bare key.
/// </para>
/// </summary>
public sealed class ProcessorLivenessOptions
{
    /// <summary>Heartbeat delay in seconds (CONFIG-01; default 10). Written as the L2 <c>interval</c>
    /// field, used by the reader's <c>timestamp + interval×2</c> staleness math.</summary>
    [ConfigurationKeyName("Interval")]
    public int IntervalSeconds { get; set; } = 10;

    /// <summary>Sliding liveness-key expiry in seconds (CONFIG-01; default 30). INDEPENDENT of
    /// <see cref="IntervalSeconds"/> — the SET..EX TTL on <c>skp:{processorId:D}</c>.</summary>
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
