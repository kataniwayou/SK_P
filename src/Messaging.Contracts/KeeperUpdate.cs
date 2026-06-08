namespace Messaging.Contracts;

// bus envelope — NO [JsonPropertyName], default STJ serialization.
/// <summary>D-11: Keeper UPDATE state — re-writes recovered/validated data to L2. Carries the 5-id
/// base (corr/wf/step/proc/exec) plus the UPDATE-only <see cref="ValidatedData"/> extra. StepId rides
/// as a record property but is NOT on the IKeeperRecoverable partition marker (D-12).</summary>
public sealed record KeeperUpdate(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public string ValidatedData { get; init; } = "";   // D-11: UPDATE-only extra
}
