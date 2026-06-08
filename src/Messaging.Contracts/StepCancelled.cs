namespace Messaging.Contracts;

// bus envelope — NO [JsonPropertyName], default STJ serialization (mirrors the retired ExecutionResult).
/// <summary>D-06: the CANCELLED step-result record. No output data key — EntryId hard-defaults to the
/// Guid.Empty sentinel (D-06a). Carries the diagnostic <see cref="CancellationMessage"/> (D-06b —
/// present on Cancelled only).</summary>
public sealed record StepCancelled(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IStepResult
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public Guid EntryId       { get; init; } = Guid.Empty;   // hard-default sentinel (D-06a)

    /// <summary>Diagnostic message for the cancellation (D-06b); null otherwise.</summary>
    public string? CancellationMessage { get; init; }
}
