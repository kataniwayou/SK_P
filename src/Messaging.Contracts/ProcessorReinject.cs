namespace Messaging.Contracts;

// bus envelope — NO [JsonPropertyName], default STJ serialization.
/// <summary>D-11: Keeper REINJECT state — re-injects recovered work to its origin. Carries the 5-id
/// base (corr/wf/step/proc/exec) plus the REINJECT-only <see cref="EntryId"/> extra. StepId rides as a
/// record property but is NOT on the IKeeperRecoverable partition marker (D-12).</summary>
public sealed record ProcessorReinject(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public Guid EntryId       { get; init; }   // D-11: REINJECT-only extra
    public string Payload     { get; init; } = "";   // D-01: REINJECT carries the step config for faithful EntryStepDispatch reconstruction
}
