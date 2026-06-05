namespace Messaging.Contracts;

/// <summary>Control message: pause the given workflow's cron (Quartz PauseJob). Body-carries correlation id (D-01).</summary>
public sealed record PauseWorkflow(Guid WorkflowId, string H) : ICorrelated
{
    public Guid CorrelationId { get; init; }
}
