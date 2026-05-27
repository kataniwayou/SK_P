using BaseApi.Service.Features.Step;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BaseApi.Service.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for the <see cref="StepNextSteps"/> junction table.
/// PATTERNS §G lines 370-398 verbatim: composite PK on <c>(StepId, NextStepId)</c> +
/// two self-ref FKs to <see cref="StepEntity"/> with explicit constraint names
/// <c>fk_step_next_steps_step_id</c> and <c>fk_step_next_steps_next_step_id</c> (Phase 4
/// PostgresExceptionMapper Option A regex; ERROR-11).
/// <para>
/// <b>Both FK sides use <c>OnDelete(Restrict)</c></b> per RESEARCH §Cascade behaviors —
/// deleting a Step referenced by a junction row (either as source or target) returns
/// 23503 → 422 via Phase 4 mapper. Admin must explicitly clean up junction rows via PUT
/// (next-step collection cleared) before deleting the principal Step.
/// </para>
/// <para>
/// <b>Lambda-less <c>HasOne&lt;StepEntity&gt;().WithMany()</c> form</b> per RESEARCH
/// Pitfall 4 — avoids EF generating navigation properties on <see cref="StepEntity"/>
/// (satisfies ENTITY-09 "no nav props between entities" — the junction is configured
/// without leaking nav props onto the principal type).
/// </para>
/// </summary>
internal sealed class StepNextStepsConfiguration : IEntityTypeConfiguration<StepNextSteps>
{
    public void Configure(EntityTypeBuilder<StepNextSteps> entity)
    {
        // Composite PK — order matches PATTERNS §G excerpt (StepId, NextStepId).
        entity.HasKey(e => new { e.StepId, e.NextStepId });

        // Self-ref FK #1 — StepId references the principal StepEntity.
        entity.HasOne<StepEntity>()
            .WithMany()
            .HasForeignKey(e => e.StepId)
            .HasConstraintName("fk_step_next_steps_step_id")
            .OnDelete(DeleteBehavior.Restrict);

        // Self-ref FK #2 — NextStepId also references StepEntity (the next step in the DAG).
        entity.HasOne<StepEntity>()
            .WithMany()
            .HasForeignKey(e => e.NextStepId)
            .HasConstraintName("fk_step_next_steps_next_step_id")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
