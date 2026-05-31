using Messaging.Contracts.Projections;

namespace Orchestrator.L1;

/// <summary>
/// In-memory L1 entry for one workflow (D-06): the root fields the scheduler/fire path needs
/// plus a <c>stepId -> StepProjection</c> map. Reuses the hoisted Messaging.Contracts read-shapes
/// (<see cref="StepProjection"/>, <see cref="LivenessProjection"/>) — these are NOT redefined here.
/// <para>
/// <b>L1 holds NO processor key and NOT the parent-index key (D-06)</b> — only what a fire needs:
/// the entry-step set, the cron string, the Quartz jobId, the per-step projections, and the
/// mutable liveness. <see cref="Liveness"/> is a settable property (not a ctor param) because the
/// fire path refreshes its timestamp in-memory on each fire (Task 3) by replacing the immutable
/// <see cref="LivenessProjection"/> record wholesale.
/// </para>
/// </summary>
public sealed record WorkflowL1(
    List<Guid> EntryStepIds,
    string Cron,
    Guid JobId,
    IReadOnlyDictionary<Guid, StepProjection> Steps)
{
    /// <summary>
    /// Mutable across fires: the fire path constructs a new <see cref="LivenessProjection"/>
    /// preserving interval/status with an updated timestamp and assigns it here (in-memory only —
    /// zero L2 writes).
    /// </summary>
    public LivenessProjection Liveness { get; set; } = default!;
}
