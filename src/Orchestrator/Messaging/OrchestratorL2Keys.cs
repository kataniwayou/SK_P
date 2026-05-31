using Messaging.Contracts.Projections;

namespace Orchestrator.Messaging;

/// <summary>
/// Reader-side L2 root key forwarder. The authoritative key shape lives in
/// <see cref="L2ProjectionKeys"/> (Messaging.Contracts) — this thin shim preserves the existing
/// internal call sites (<c>StartOrchestrationConsumer</c>, <c>StopOrchestrationConsumer</c>) while
/// sharing one source of truth with the WebApi writer, so a GUID-format change cannot silently
/// desynchronize writer and reader (HARDEN-03).
/// </summary>
internal static class OrchestratorL2Keys
{
    public static string Root(Guid workflowId) => L2ProjectionKeys.Root(workflowId);

    public static string ParentIndex() => L2ProjectionKeys.ParentIndex();

    public static string Step(Guid workflowId, Guid stepId) => L2ProjectionKeys.Step(workflowId, stepId);
}
