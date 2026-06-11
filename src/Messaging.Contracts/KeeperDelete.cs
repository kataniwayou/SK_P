namespace Messaging.Contracts;

// bus envelope — NO [JsonPropertyName], default STJ serialization.
/// <summary>D-11: Keeper DELETE state — deletes the L2 data key. Carries the 5-id base
/// (corr/wf/step/proc/exec) plus the DELETE-only <see cref="EntryId"/> extra and the A19
/// <see cref="MessageId"/> origin-index extra (the keeper both-key DEL). StepId rides as a record
/// property but is NOT on the IKeeperRecoverable partition marker (D-12).</summary>
public sealed record KeeperDelete(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public Guid EntryId       { get; init; }   // D-11: DELETE-only extra
    public Guid MessageId     { get; init; }   // A19: the origin index id, for the keeper both-key DEL
}
