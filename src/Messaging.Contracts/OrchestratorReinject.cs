namespace Messaging.Contracts;

// bus envelope — NO [JsonPropertyName], default STJ serialization.
/// <summary>ORCV-06 / D-06/D-07: the origin-split Keeper REINJECT state for the ORCHESTRATOR result
/// path. Mirrors <see cref="ProcessorReinject"/>'s 5-id base (corr/wf/step/proc/exec) + <see cref="EntryId"/>
/// and implements <see cref="IKeeperRecoverable"/> so the existing 4-tuple partitioner serializes it
/// unchanged (origin-agnostic). The D-07 divergence: it carries a <see cref="Outcome"/> discriminator
/// (reusing the existing <see cref="StepOutcome"/> enum) plus the IStepResult result-field superset as
/// DISCRETE fields — <see cref="ErrorMessage"/> (Failed only) and <see cref="CancellationMessage"/>
/// (Cancelled only) — NOT a serialized polymorphic blob. The Wave-3 consumer reconstructs the right
/// IStepResult subtype from <see cref="Outcome"/> via a factory. StepId rides as a record property but
/// is NOT on the IKeeperRecoverable partition marker (D-12).</summary>
public sealed record OrchestratorReinject(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public Guid EntryId       { get; init; }   // present on Completed; Guid.Empty on Failed/Cancelled/Processing
    public StepOutcome Outcome { get; init; }           // D-07 discriminator → IStepResult subtype factory
    public string? ErrorMessage { get; init; }          // union field — Failed only
    public string? CancellationMessage { get; init; }   // union field — Cancelled only
}
