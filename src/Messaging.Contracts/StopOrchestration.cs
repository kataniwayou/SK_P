namespace Messaging.Contracts;

/// <summary>Control message: stop orchestration for the given workflows. Body-carries the correlation id (D-01).</summary>
public sealed record StopOrchestration(Guid[] WorkflowIds) : ICorrelated
{
    public Guid CorrelationId { get; init; }
}
