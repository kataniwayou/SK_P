using BaseApi.Service.Features.Assignment;
using BaseApi.Service.Features.Step;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BaseApi.Service.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="AssignmentEntity"/>. Wires the
/// <c>Payload</c> column as Postgres <c>jsonb</c> (PERSIST-08) so the column is
/// efficiently stored and indexable.
/// <para>
/// One NON-nullable FK: <c>StepId</c> → <see cref="StepEntity"/> with
/// <c>OnDelete(Restrict)</c> per RESEARCH §Cascade behaviors. The constraint name is
/// EXPLICIT (<c>fk_assignment_step_id</c>) to match the Phase 4 PostgresExceptionMapper
/// Option A regex (fk_{table}_{column}_id pattern; ERROR-11). EF auto-names would
/// diverge (e.g. include extra principal-table segments) and break the 23503 →
/// field-name mapping path.
/// </para>
/// <para>
/// <b>Lambda-less <c>HasOne&lt;StepEntity&gt;().WithMany()</c> form</b> per RESEARCH
/// Pitfall 4 — creates the Postgres FK constraint without generating navigation
/// properties on either entity, satisfying ENTITY-09 "no nav props between entities".
/// </para>
/// </summary>
internal sealed class AssignmentEntityConfiguration : IEntityTypeConfiguration<AssignmentEntity>
{
    public void Configure(EntityTypeBuilder<AssignmentEntity> entity)
    {
        // PERSIST-08 — Payload jsonb.
        entity.Property(e => e.Payload)
            .IsRequired()
            .HasColumnType("jsonb");

        // ENTITY-07 + ERROR-11 — non-nullable FK to Step; Restrict per cascade table line 584.
        entity.HasOne<StepEntity>()
            .WithMany()
            .HasForeignKey(e => e.StepId)
            .HasConstraintName("fk_assignment_step_id")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
