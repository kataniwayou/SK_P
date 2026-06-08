namespace Messaging.Contracts;

// bus envelope — NO [JsonPropertyName], default STJ serialization.
/// <summary>D-11: Keeper CLEANUP state — the 5-id base (corr/wf/step/proc/exec), no extra field.
/// StepId rides as a record property but is NOT on the IKeeperRecoverable partition marker (D-12).</summary>
public sealed record KeeperCleanup(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
}
