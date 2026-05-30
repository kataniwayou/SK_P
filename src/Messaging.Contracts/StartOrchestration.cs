namespace Messaging.Contracts;

/// <summary>Control message: start orchestration for the given workflows. Body-carries the correlation id (D-01).</summary>
public sealed record StartOrchestration(Guid[] WorkflowIds) : ICorrelated
{
    public Guid CorrelationId { get; init; }
}
