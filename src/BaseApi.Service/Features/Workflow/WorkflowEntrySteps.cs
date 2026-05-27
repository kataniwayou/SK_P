namespace BaseApi.Service.Features.Workflow;

/// <summary>
/// Junction entity for the Workflow M2M to Step on the entry-step path. Encodes
/// "Workflow <c>WorkflowId</c> has Step <c>StepId</c> as one of its entry steps".
/// <b>NOT derived from <c>BaseEntity</c></b> — junction rows have no Id, no audit
/// fields, no xmin concurrency token per RESEARCH §Composite PK + FK on junctions
/// (the <c>BaseDbContext.OnModelCreating</c> iteration filtering by
/// <c>BaseEntity.IsAssignableFrom</c> naturally excludes junctions).
/// <para>
/// The composite PK <c>(WorkflowId, StepId)</c>, the FK to <c>WorkflowEntity</c>
/// (<c>OnDelete(Cascade)</c>) and the FK to <c>StepEntity</c>
/// (<c>OnDelete(Restrict)</c> — SC#5 load-bearing) are configured in
/// <c>WorkflowEntryStepsConfiguration</c>.
/// </para>
/// </summary>
public sealed class WorkflowEntrySteps
{
    public Guid WorkflowId { get; set; }
    public Guid StepId { get; set; }
}
