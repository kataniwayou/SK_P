namespace BaseApi.Core.Configuration;

/// <summary>
/// Phase 12 Redis projection options bound to the <c>"Redis:*"</c> config section
/// (INFRA-COMP-04). YAGNI-pruned per CONTEXT D-15. Phase 22 (L2PREFIX-01) removed the
/// configurable key-prefix property — the L2 key prefix is now a compile-time const on
/// <c>L2ProjectionKeys</c>, so these options bind only <c>ProcessorKeyTtlDays</c> and
/// <see cref="Serialization"/>.<see cref="SerializationOptions.JsonOptions"/>.
/// No <c>Database</c> int, no <c>CommandFlags</c>, no <c>ConnectionString</c> property
/// (D-16: connection-string source-of-truth is <c>IConfiguration.GetConnectionString("Redis")</c>).
/// Database / Cluster / replica knobs can be added in v3.4 when a real scale driver appears.
/// Phase 15 writer reads exactly the fields it needs.
/// </summary>
public sealed class RedisProjectionOptions
{
    /// <summary>Processor-key TTL in days (D-08, default 100). Refresh-on-write: every Start
    /// re-SETs processor keys with this expiry. &lt;= 0 ⇒ no expiry (disable from config).</summary>
    public int ProcessorKeyTtlDays { get; set; } = 100;

    /// <summary>
    /// Nested serialization options. v3.3.0 ships a single string discriminator
    /// <c>"default"</c>; future revisions may add <c>JsonOptions = "snake_case"</c> etc.
    /// Phase 15 writer wires the actual System.Text.Json options factory.
    /// </summary>
    public SerializationOptions Serialization { get; set; } = new();

    public sealed class SerializationOptions
    {
        public string JsonOptions { get; set; } = "default";
    }
}
