using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BaseApi.Service.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for the <see cref="WorkflowEntrySteps"/> junction table.
/// PATTERNS §G + RESEARCH cascade table verbatim: composite PK on
/// <c>(WorkflowId, StepId)</c> + two FKs with explicit constraint names
/// (<c>fk_workflow_entry_steps_workflow_id</c> for the Cascade-on-parent and
/// <c>fk_workflow_entry_steps_step_id</c> for the Restrict-on-referenced) matching the
/// Phase 4 PostgresExceptionMapper Option A regex (ERROR-11).
/// <para>
/// <b>Asymmetric cascade behavior:</b>
/// <list type="bullet">
///   <item><b>Workflow side — <c>OnDelete(Cascade)</c></b>: junction lifecycle is owned
///     by the parent Workflow. DELETE Workflow auto-removes the entry-step junction
///     rows so cleanup is implicit.</item>
///   <item><b>Step side — <c>OnDelete(Restrict)</c></b>: <b>SC#5 load-bearing</b> —
///     deleting a Step that a Workflow references via this junction returns 23503 → 422
///     via the Phase 4 PostgresExceptionMapper. Admin must remove the Workflow
///     reference (or delete the Workflow) before deleting the Step.</item>
/// </list>
/// </para>
/// <para>
/// <b>Lambda-less <c>HasOne&lt;...&gt;().WithMany()</c> forms</b> per RESEARCH
/// Pitfall 4 — avoids EF generating navigation properties (satisfies ENTITY-09 "no
/// nav props between entities" — the junction is configured without leaking nav props
/// onto the principals).
/// </para>
/// </summary>
internal sealed class WorkflowEntryStepsConfiguration : IEntityTypeConfiguration<WorkflowEntrySteps>
{
    public void Configure(EntityTypeBuilder<WorkflowEntrySteps> entity)
    {
        // Composite PK — order matches plan body excerpt (WorkflowId, StepId).
        entity.HasKey(e => new { e.WorkflowId, e.StepId });

        // FK to Workflow — Cascade (junction lifecycle owned by parent Workflow).
        entity.HasOne<WorkflowEntity>()
            .WithMany()
            .HasForeignKey(e => e.WorkflowId)
            .HasConstraintName("fk_workflow_entry_steps_workflow_id")
            .OnDelete(DeleteBehavior.Cascade);

        // FK to Step — Restrict (SC#5: deleting a Step referenced by a Workflow → 422).
        entity.HasOne<StepEntity>()
            .WithMany()
            .HasForeignKey(e => e.StepId)
            .HasConstraintName("fk_workflow_entry_steps_step_id")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
