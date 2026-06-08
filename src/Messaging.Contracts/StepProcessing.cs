namespace Messaging.Contracts;

// bus envelope — NO [JsonPropertyName], default STJ serialization (mirrors the retired ExecutionResult).
/// <summary>D-06: the PROCESSING (in-flight) step-result record. No output data key — EntryId
/// hard-defaults to the Guid.Empty sentinel (D-06a). Carries NEITHER diagnostic field (D-06b).</summary>
public sealed record StepProcessing(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IStepResult
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public Guid EntryId       { get; init; } = Guid.Empty;   // hard-default sentinel (D-06a)
}
