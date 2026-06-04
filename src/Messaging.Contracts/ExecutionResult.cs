namespace Messaging.Contracts;

// NOTE: bus envelope — NO [JsonPropertyName], default STJ serialization
// (mirrors EntryStepDispatch). This is a wire contract, NOT a Redis projection.
// There is deliberately NO output/payload field (SPEC req 1): the result carries
// only the outcome + optional diagnostic messages, never the processor's output data.
public sealed record ExecutionResult(
    Guid WorkflowId,
    Guid StepId,
    Guid ProcessorId,
    StepOutcome Outcome) : IExecutionCorrelated
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId { get; init; }
    public string EntryId { get; init; } = "";

    /// <summary>Deterministic effect identity (Phase 31 D-02), empty until Plan 04 populates it.</summary>
    public string H { get; init; } = "";

    /// <summary>Diagnostic message for a <see cref="StepOutcome.Failed"/> result; null otherwise.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Diagnostic message for a <see cref="StepOutcome.Cancelled"/> result; null otherwise.</summary>
    public string? CancellationMessage { get; init; }
}
