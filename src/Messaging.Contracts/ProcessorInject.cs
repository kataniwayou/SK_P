namespace Messaging.Contracts;

// bus envelope — NO [JsonPropertyName], default STJ serialization.
/// <summary>D-08: Keeper INJECT state (A18) — the 5-id base (corr/wf/step/proc/exec) plus the
/// INJECT id-set: <see cref="EntryId"/> (allocation to write) + <see cref="Data"/> (raw-JSON output,
/// in-hand on the envelope). INJECT is non-destructive (no source delete — KINJ-01).
/// StepId rides as a record property but is NOT on the IKeeperRecoverable partition marker (D-12).</summary>
public sealed record ProcessorInject(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public Guid EntryId       { get; init; }   // D-08: allocation to write L2[entryId]=data
    public string Data        { get; init; } = "";   // D-08: raw-JSON output, in-hand on the envelope
}
