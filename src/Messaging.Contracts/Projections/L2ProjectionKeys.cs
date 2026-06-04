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
///   <item><description>Cancelled: {Prefix}cancelled:{workflowId:D} — the no-TTL in-flight cancellation marker (D-02/D-07)</description></item>
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

    /// <summary>
    /// Phase 32 (D-02/D-07): the no-TTL in-flight cancellation marker — <c>skp:cancelled:{workflowId:D}</c>.
    /// <para>
    /// Renders <c>:D</c> (hyphenated) to match the <see cref="Root"/> workflow-id precedent — workflow ids
    /// render hyphenated everywhere in this codebase. This is the single source of truth consumed by the
    /// processor SET site (Plan 04) and both consumer CHECK sites (Plan 03), so a format change cannot
    /// desync writer/reader. Only the <c>Guid workflowId</c> shape is needed — both
    /// <c>EntryStepDispatch.WorkflowId</c> and <c>ExecutionResult.WorkflowId</c> are <c>Guid</c>.
    /// </para>
    /// </summary>
    public static string Cancelled(Guid workflowId) => $"{Prefix}cancelled:{workflowId:D}";

    /// <summary>
    /// Phase 32 (D-02): the single sentinel value written to / checked at the cancelled marker — ONE literal,
    /// both sites (the processor SET in Plan 04 and the consumer CHECKs in Plan 03), so writer and reader
    /// cannot desync on the value either.
    /// </summary>
    public const string CancelledMarkerValue = "true";
}
