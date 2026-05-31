namespace Messaging.Contracts.Projections;

/// <summary>
/// Single source of truth for the three L2 (Redis) projection key formats (L2-PROJECT-02 / HARDEN-03).
/// Hoisted into the Messaging.Contracts leaf (Phase 21) so the writer
/// (<c>BaseApi.Service.RedisProjectionKeys</c>) and reader (<c>Orchestrator.OrchestratorL2Keys</c>)
/// consume ONE shape — a future GUID-format/suffix change cannot silently desynchronize them.
/// <para>
/// The scheme is FLAT: a single configured prefix followed by GUID(s), with NO type
/// discriminator (D-02). Consequently <see cref="Root"/> and <see cref="Processor"/> produce
/// byte-identical strings for the same prefix + GUID — they are disambiguated only by their
/// GUID namespace (a workflow id is never a processor id). GUIDs render in the default
/// <c>Guid.ToString()</c> ("D") format — hyphenated — NOT the "N" (32-digit) format; <see cref="Root"/>
/// makes this explicit with the <c>:D</c> format specifier (byte-identical to a bare interpolation).
/// <c>prefix</c> stays a parameter on every builder (D-05) — this leaf has no config access; each
/// host owns its <c>Redis:KeyPrefix</c> value ("skp:").
/// </para>
/// <list type="bullet">
///   <item><description>Root: <c>{prefix}{workflowId}</c></description></item>
///   <item><description>Step: <c>{prefix}{workflowId}:{stepId}</c></description></item>
///   <item><description>Processor: <c>{prefix}{processorId}</c></description></item>
/// </list>
/// </summary>
public static class L2ProjectionKeys
{
    public static string Root(string prefix, Guid workflowId) => $"{prefix}{workflowId:D}";

    public static string Step(string prefix, Guid workflowId, Guid stepId) => $"{prefix}{workflowId}:{stepId}";

    public static string Processor(string prefix, Guid processorId) => $"{prefix}{processorId}";
}
