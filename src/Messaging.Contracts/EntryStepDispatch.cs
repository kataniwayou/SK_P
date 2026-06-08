namespace Messaging.Contracts;

/// <summary>
/// Orchestrator->processor entry-step dispatch message (ORCH-CONTRACT-02). Sent (NOT published) to
/// queue:{processorId} on each job fire. Body carries the per-fire correlationId (D-05 — minted with
/// NewId.NextGuid()); executionId is Guid.Empty (lineage) and entryId is Guid.Empty on an entry-step
/// fire (D-04/D-05: EntryId is a GUID data key; Guid.Empty is the source-step sentinel recognized via
/// <see cref="SourceStep.IsSource"/>). The legacy content-hash <c>H</c> member was removed in v4.0.0
/// (RETIRE-01). No [JsonPropertyName] targets: MassTransit serializes the message envelope, not a Redis JSON projection.
/// </summary>
public sealed record EntryStepDispatch(
    Guid WorkflowId, Guid StepId, Guid ProcessorId, string Payload) : IExecutionCorrelated
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId  { get; init; } = Guid.Empty;
    public Guid EntryId      { get; init; } = Guid.Empty;
}
