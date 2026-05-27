namespace BaseApi.Service.Features.Step;

/// <summary>
/// Junction entity for the Step M2M self-reference. Encodes "Step <c>StepId</c> may flow
/// into Step <c>NextStepId</c>" rows. <b>NOT derived from <c>BaseEntity</c></b> — junction
/// rows have no Id, no audit fields, no xmin concurrency token per RESEARCH §Composite PK
/// + FK on junctions (the <c>BaseDbContext.OnModelCreating</c> iteration filtering by
/// <c>BaseEntity.IsAssignableFrom</c> naturally excludes junctions).
/// <para>
/// The composite PK <c>(StepId, NextStepId)</c> and the two self-ref FKs to
/// <c>StepEntity</c> (both <c>OnDelete(Restrict)</c>) are configured in
/// <c>StepNextStepsConfiguration</c>.
/// </para>
/// </summary>
public sealed class StepNextSteps
{
    public Guid StepId { get; set; }
    public Guid NextStepId { get; set; }
}
