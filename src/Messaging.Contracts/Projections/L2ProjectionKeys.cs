namespace Messaging.Contracts.Projections;

/// <summary>
/// Single source of truth for the L2 (Redis) projection key formats (L2-PROJECT-02 / HARDEN-03).
/// Hoisted into the Messaging.Contracts leaf (Phase 21) so the writer
/// (<c>BaseApi.Service.RedisProjectionKeys</c>) and reader (<c>Orchestrator.OrchestratorL2Keys</c>)
/// consume ONE shape — a future GUID-format/suffix change cannot silently desynchronize them.
/// <para>
/// The scheme is FLAT: a single prefix followed by GUID(s), with NO type
/// discriminator (D-02). Consequently <see cref="Root"/> and <see cref="Processor"/> produce
/// byte-identical strings for the same GUID — they are disambiguated only by their
/// GUID namespace (a workflow id is never a processor id). GUIDs render in the default
/// <c>Guid.ToString()</c> ("D") format — hyphenated — NOT the "N" (32-digit) format; <see cref="Root"/>
/// makes this explicit with the <c>:D</c> format specifier (byte-identical to a bare interpolation).
/// The prefix is now a compile-time <c>const Prefix = "skp:"</c> owned HERE (D-01, Phase 22),
/// consumed by both forwarders — it is no longer a per-host config value and no longer a builder
/// parameter. This eliminates any config-injection path into key names.
/// </para>
/// <list type="bullet">
///   <item><description>ParentIndex: <c>{Prefix}</c> (the bare prefix — the parent-index SET key)</description></item>
///   <item><description>Root: <c>{Prefix}{workflowId}</c></description></item>
///   <item><description>Step: <c>{Prefix}{workflowId}:{stepId}</c></description></item>
///   <item><description>Processor: <c>{Prefix}{processorId}</c></description></item>
///   <item><description>ExecutionData: <c>{Prefix}data:{entryId:D}</c> — the sole GUID-keyed data builder (D-08; the legacy 64-hex content-addressed string overload was removed in v4.0.0).</description></item>
///   <item><description>MessageIndex: <c>{Prefix}msg:{messageId:D}</c> — the processor-owned slot-array allocation-index key (D-04; a Redis HASH of int-slot → entryId, D-05). No TTL baked in.</description></item>
/// </list>
/// </summary>
public static class L2ProjectionKeys
{
    public const string Prefix = "skp:";

    public static string ParentIndex() => Prefix;

    public static string Root(Guid workflowId) => $"{Prefix}{workflowId:D}";

    public static string Step(Guid workflowId, Guid stepId) => $"{Prefix}{workflowId}:{stepId}";

    public static string Processor(Guid processorId) => $"{Prefix}{processorId}";

    /// <summary>D-08: the sole GUID-keyed L2 data builder — <c>skp:data:{entryId:D}</c> (no TTL baked
    /// in; caller concern). The legacy 64-hex content-addressed string overload was removed in v4.0.0.</summary>
    public static string ExecutionData(Guid entryId) => $"{Prefix}data:{entryId:D}";

    /// <summary>D-04: the processor-owned slot-array allocation-index key —
    /// <c>skp:msg:{messageId:D}</c> (a Redis HASH of int-slot → entryId; D-05). No TTL baked
    /// in (caller concern, mirrors <see cref="ExecutionData"/>; the random TTL lands in Phase 51).
    /// <c>messageId</c> is the MassTransit broker MessageId (a Guid).</summary>
    public static string MessageIndex(Guid messageId) => $"{Prefix}msg:{messageId:D}";

    /// <summary>D-03: probe scratch key — short-TTL write-then-delete; the TTL is the crash net-zero net.</summary>
    public static string KeeperProbe(string h) => $"{Prefix}keeper:probe:{h}";   // "skp:keeper:probe:{h}"
}
