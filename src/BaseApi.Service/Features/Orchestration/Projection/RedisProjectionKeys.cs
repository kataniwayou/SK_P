namespace BaseApi.Service.Features.Orchestration.Projection;

/// <summary>
/// Single source of truth for the three L2 (Redis) projection key formats (L2-PROJECT-02).
/// <para>
/// The scheme is FLAT: a single configured prefix followed by GUID(s), with NO type
/// discriminator (D-02). Consequently <see cref="Root"/> and <see cref="Processor"/> produce
/// byte-identical strings for the same prefix + GUID — they are disambiguated only by their
/// GUID namespace (a workflow id is never a processor id). GUIDs render in the default
/// <c>Guid.ToString()</c> ("D") format — hyphenated — NOT the "N" (32-digit) format.
/// </para>
/// <list type="bullet">
///   <item><description>Root: <c>{prefix}{workflowId}</c></description></item>
///   <item><description>Step: <c>{prefix}{workflowId}:{stepId}</c></description></item>
///   <item><description>Processor: <c>{prefix}{processorId}</c></description></item>
/// </list>
/// </summary>
internal static class RedisProjectionKeys
{
    public static string Root(string prefix, Guid workflowId) => $"{prefix}{workflowId}";

    public static string Step(string prefix, Guid workflowId, Guid stepId) => $"{prefix}{workflowId}:{stepId}";

    public static string Processor(string prefix, Guid processorId) => $"{prefix}{processorId}";
}
