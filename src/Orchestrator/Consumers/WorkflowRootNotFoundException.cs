namespace Orchestrator.Consumers;

/// <summary>
/// Business failure (D-07): a <c>WorkflowId</c> absent from the L2 root. The retry pipeline
/// <c>Ignore&lt;&gt;</c>s this type so a business outcome never triggers a retry storm
/// (MSG-ACK-03). Role analog: BaseApi.Core's <c>NotFoundException</c>.
/// </summary>
public sealed class WorkflowRootNotFoundException(Guid workflowId)
    : Exception($"Workflow root {workflowId:D} absent from L2.")
{
    /// <summary>The WorkflowId that was absent from the L2 root.</summary>
    public Guid WorkflowId { get; } = workflowId;
}
