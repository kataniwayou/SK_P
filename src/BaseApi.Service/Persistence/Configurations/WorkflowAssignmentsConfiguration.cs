using BaseApi.Service.Features.Assignment;
using BaseApi.Service.Features.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BaseApi.Service.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for the <see cref="WorkflowAssignments"/> junction
/// table. Mirrors <c>WorkflowEntryStepsConfiguration</c>: composite PK on
/// <c>(WorkflowId, AssignmentId)</c> + two FKs with explicit constraint names
/// (<c>fk_workflow_assignments_workflow_id</c> for Cascade-on-parent and
/// <c>fk_workflow_assignments_assignment_id</c> for Restrict-on-referenced) matching
/// the Phase 4 PostgresExceptionMapper Option A regex (ERROR-11).
/// <para>
/// Asymmetric cascade: Cascade on Workflow side (parent owns junction lifecycle),
/// Restrict on Assignment side (deleting an Assignment referenced by a Workflow
/// returns 23503 → 422; admin must remove the reference first).
/// </para>
/// </summary>
internal sealed class WorkflowAssignmentsConfiguration : IEntityTypeConfiguration<WorkflowAssignments>
{
    public void Configure(EntityTypeBuilder<WorkflowAssignments> entity)
    {
        // Composite PK (WorkflowId, AssignmentId).
        entity.HasKey(e => new { e.WorkflowId, e.AssignmentId });

        // FK to Workflow — Cascade.
        entity.HasOne<WorkflowEntity>()
            .WithMany()
            .HasForeignKey(e => e.WorkflowId)
            .HasConstraintName("fk_workflow_assignments_workflow_id")
            .OnDelete(DeleteBehavior.Cascade);

        // FK to Assignment — Restrict.
        entity.HasOne<AssignmentEntity>()
            .WithMany()
            .HasForeignKey(e => e.AssignmentId)
            .HasConstraintName("fk_workflow_assignments_assignment_id")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
