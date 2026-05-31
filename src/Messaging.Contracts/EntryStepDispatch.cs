namespace Messaging.Contracts;

/// <summary>
/// Orchestrator->processor entry-step dispatch message (ORCH-CONTRACT-02). Sent (NOT published) to
/// queue:{processorId} on each job fire. Body carries the per-fire correlationId (D-05 — minted with
/// NewId.NextGuid()); executionId/entryId are Guid.Empty per SPEC. No [JsonPropertyName] targets:
/// MassTransit serializes the message envelope, not a Redis JSON projection.
/// </summary>
public sealed record EntryStepDispatch(
    Guid WorkflowId, Guid StepId, Guid ProcessorId, string Payload) : IExecutionCorrelated
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId  { get; init; } = Guid.Empty;
    public Guid EntryId      { get; init; } = Guid.Empty;
}
