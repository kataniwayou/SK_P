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
///   <item><description>ExecutionData: {Prefix}data:{64hex} — content-addressed (was Guid; D-01). The string overload takes the 64-hex content address directly (no <c>:D</c>); the Guid overload is retained transitionally for the not-yet-migrated Phase-27 callers and is removed in Plan 02.</description></item>
///   <item><description>Flag: {Prefix}flag:{64hex} — effect-first dedup state (D-05)</description></item>
/// </list>
/// </summary>
public static class L2ProjectionKeys
{
    public const string Prefix = "skp:";

    public static string ParentIndex() => Prefix;

    public static string Root(Guid workflowId) => $"{Prefix}{workflowId:D}";

    public static string Step(Guid workflowId, Guid stepId) => $"{Prefix}{workflowId}:{stepId}";

    public static string Processor(Guid processorId) => $"{Prefix}{processorId}";

    /// <summary>D-01: content-addressed data key over the 64-hex entryId string (the new canonical path).</summary>
    public static string ExecutionData(string entryId) => $"{Prefix}data:{entryId}";

    /// <summary>
    /// Transitional Guid overload retained so the not-yet-migrated Phase-27 callers
    /// (<c>EntryStepDispatchConsumer</c>) and their golden tests still compile/pass this plan; the
    /// content address becomes the 64-hex <see cref="ExecutionData(string)"/> in Plan 02. Renders <c>:D</c>.
    /// </summary>
    public static string ExecutionData(Guid entryId) => $"{Prefix}data:{entryId:D}";

    /// <summary>D-05: effect-first dedup flag key — <c>skp:flag:{64hex}</c>.</summary>
    public static string Flag(string h) => $"{Prefix}flag:{h}";

    /// <summary>D-03: probe scratch key — short-TTL write-then-delete; the TTL is the crash net-zero net.</summary>
    public static string KeeperProbe(string h) => $"{Prefix}keeper:probe:{h}";   // "skp:keeper:probe:{h}"

    /// <summary>KHARD-01: per-H outer recover-attempt counter — atomic INCR, TTL-bounded, DEL on park.</summary>
    public static string KeeperRecoverAttempts(string h) => $"{Prefix}keeper:attempts:{h}";   // "skp:keeper:attempts:{h}"
}
