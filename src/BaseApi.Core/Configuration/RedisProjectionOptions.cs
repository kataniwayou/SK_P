namespace BaseApi.Core.Configuration;

/// <summary>
/// Phase 12 Redis projection options bound to the <c>"Redis:*"</c> config section
/// (INFRA-COMP-04). YAGNI-pruned per CONTEXT D-15: <see cref="KeyPrefix"/> and
/// <see cref="Serialization"/>.<see cref="SerializationOptions.JsonOptions"/> only.
/// No <c>Database</c> int, no <c>CommandFlags</c>, no <c>ConnectionString</c> property
/// (D-16: connection-string source-of-truth is <c>IConfiguration.GetConnectionString("Redis")</c>).
/// Database / Cluster / replica knobs can be added in v3.4 when a real scale driver appears.
/// Phase 15 writer reads exactly the fields it needs.
/// </summary>
public sealed class RedisProjectionOptions
{
    /// <summary>
    /// Prefix prepended to all L2 keys (INFRA-REDIS-05 — default <c>"skp:"</c>).
    /// Production = <c>"skp:"</c>; tests override per-class to <c>"test:cls-{Guid:N}:"</c>
    /// via <c>Phase8WebAppFactory.AddInMemoryCollection</c> (D-08 / D-12).
    /// </summary>
    public string KeyPrefix { get; set; } = "skp:";

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
