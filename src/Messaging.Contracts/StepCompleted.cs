namespace Messaging.Contracts;

// bus envelope — NO [JsonPropertyName], default STJ serialization (mirrors the retired ExecutionResult).
/// <summary>D-06: the COMPLETED step-result record. EntryId is the REAL L2 data key (D-06a) — NO
/// Guid.Empty default, the processor mints a fresh GUID and writes the output there. Carries the six
/// ids, no diagnostic field.</summary>
public sealed record StepCompleted(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IStepResult
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public Guid EntryId       { get; init; }   // the REAL data key (D-06a) — no Guid.Empty default
}
