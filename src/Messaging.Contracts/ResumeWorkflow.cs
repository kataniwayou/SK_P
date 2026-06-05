namespace Messaging.Contracts;

/// <summary>Control message: resume the given workflow's cron (Quartz ResumeJob). Body-carries correlation id (D-01).</summary>
public sealed record ResumeWorkflow(Guid WorkflowId, string H) : ICorrelated
{
    public Guid CorrelationId { get; init; }
}
