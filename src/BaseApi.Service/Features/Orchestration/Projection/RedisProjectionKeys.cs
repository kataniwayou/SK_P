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
    public static string ParentIndex() => L2ProjectionKeys.ParentIndex();

    public static string Root(Guid workflowId) => L2ProjectionKeys.Root(workflowId);

    public static string Step(Guid workflowId, Guid stepId) => L2ProjectionKeys.Step(workflowId, stepId);
}
