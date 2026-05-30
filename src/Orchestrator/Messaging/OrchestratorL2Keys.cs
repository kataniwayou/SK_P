namespace Orchestrator.Messaging;

/// <summary>
/// L2 root key shape — duplicated from BaseApi.Service <c>RedisProjectionKeys</c> (which is
/// <c>internal</c> there, so the orchestrator cannot reference it; hoist-vs-duplicate resolved
/// to duplicate per Claude's discretion, 19-RESEARCH Open Question 3).
/// <para>
/// Single source of the wire shape on this side: <c>{prefix}{workflowId}</c> with the GUID
/// rendered in the DEFAULT <c>Guid.ToString()</c> ("D", hyphenated) format — NOT the "N"
/// (32-digit) format. <c>$"{prefix}{workflowId:D}"</c> renders byte-identically to the writer's
/// bare <c>$"{prefix}{workflowId}"</c>. A format mismatch would cause silent L2 read misses
/// (the orchestrator would read a key the writer never wrote). Prefix comes from
/// <c>Redis:KeyPrefix</c> config ("skp:").
/// </para>
/// </summary>
internal static class OrchestratorL2Keys
{
    public static string Root(string prefix, Guid workflowId) => $"{prefix}{workflowId:D}";
}
