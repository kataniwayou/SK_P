using Messaging.Contracts.Projections;

namespace BaseApi.Service.Features.Orchestration.Projection;

/// <summary>
/// Writer-side L2 (Redis) projection key forwarder. The authoritative key shapes live in
/// <see cref="L2ProjectionKeys"/> (Messaging.Contracts) — this thin shim preserves the existing
/// internal call sites (<c>RedisProjectionWriter</c>, <c>RedisL2Cleanup</c>) while sharing one
/// source of truth with the orchestrator reader (HARDEN-03).
/// </summary>
internal static class RedisProjectionKeys
{
    public static string Root(string prefix, Guid workflowId) => L2ProjectionKeys.Root(prefix, workflowId);

    public static string Step(string prefix, Guid workflowId, Guid stepId) => L2ProjectionKeys.Step(prefix, workflowId, stepId);

    public static string Processor(string prefix, Guid processorId) => L2ProjectionKeys.Processor(prefix, processorId);
}
