namespace Messaging.Contracts;

/// <summary>
/// Orchestrator->processor entry-step dispatch message (ORCH-CONTRACT-02). Sent (NOT published) to
/// queue:{processorId} on each job fire. Body carries the per-fire correlationId (D-05 — minted with
/// NewId.NextGuid()); executionId is Guid.Empty and entryId is the empty string on an entry-step
/// fire (Phase 31 D-01: EntryId is a content-addressed 64-hex string, empty when no input is carried).
/// <c>H</c> is the deterministic effect identity (Phase 31 D-02), empty until Plan 04 populates it.
/// No [JsonPropertyName] targets: MassTransit serializes the message envelope, not a Redis JSON projection.
/// </summary>
public sealed record EntryStepDispatch(
    Guid WorkflowId, Guid StepId, Guid ProcessorId, string Payload) : IExecutionCorrelated
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId  { get; init; } = Guid.Empty;
    public string EntryId    { get; init; } = "";
    public string H          { get; init; } = "";
}
