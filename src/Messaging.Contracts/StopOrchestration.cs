namespace Messaging.Contracts;

/// <summary>Control message: stop orchestration for the given workflows. No correlation field on the wire (D-10).</summary>
public sealed record StopOrchestration(Guid[] WorkflowIds);
