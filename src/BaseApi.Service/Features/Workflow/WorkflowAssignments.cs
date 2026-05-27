namespace BaseApi.Service.Features.Workflow;

/// <summary>
/// Junction entity for the Workflow M2M to Assignment. Encodes "Workflow
/// <c>WorkflowId</c> includes Assignment <c>AssignmentId</c>". <b>NOT derived from
/// <c>BaseEntity</c></b> — junction rows have no Id, no audit fields, no xmin
/// concurrency token per RESEARCH §Composite PK + FK on junctions.
/// <para>
/// The composite PK <c>(WorkflowId, AssignmentId)</c>, the FK to
/// <c>WorkflowEntity</c> (<c>OnDelete(Cascade)</c>) and the FK to
/// <c>AssignmentEntity</c> (<c>OnDelete(Restrict)</c>) are configured in
/// <c>WorkflowAssignmentsConfiguration</c>.
/// </para>
/// </summary>
public sealed class WorkflowAssignments
{
    public Guid WorkflowId { get; set; }
    public Guid AssignmentId { get; set; }
}
