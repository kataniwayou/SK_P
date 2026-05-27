using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Step;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BaseApi.Service.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="StepEntity"/>. Wires the non-nullable FK
/// to <see cref="ProcessorEntity"/> with explicit constraint name
/// <c>fk_step_processor_id</c> (Phase 4 PostgresExceptionMapper Option A regex; ERROR-11)
/// and <c>OnDelete(Restrict)</c> per RESEARCH §Cascade behaviors line 584 — deleting a
/// Processor while a Step references it returns 23503 → 422 via Phase 4 mapper. Differs
/// from Processor's nullable FKs which use SetNull.
/// <para>
/// <c>EntryCondition</c> stays as the default int mapping (no <c>HasConversion</c> per
/// 08-CONTEXT Claude's Discretion line 204) — preserves the ENTITY-06 numeric values
/// across migrations.
/// </para>
/// </summary>
internal sealed class StepEntityConfiguration : IEntityTypeConfiguration<StepEntity>
{
    public void Configure(EntityTypeBuilder<StepEntity> entity)
    {
        // ENTITY-05 + ERROR-11 + RESEARCH cascade table:
        // Non-nullable FK; Restrict because Phase 8 SC#5 (deleting a Step referenced by a Workflow → 422)
        // requires the principal side to refuse delete while dependents reference it.
        entity.HasOne<ProcessorEntity>()
            .WithMany()
            .HasForeignKey(e => e.ProcessorId)
            .HasConstraintName("fk_step_processor_id")
            .OnDelete(DeleteBehavior.Restrict);

        // ENTITY-06 — int mapping (Claude's Discretion line 204; preserves numeric values).
        entity.Property(e => e.EntryCondition)
            .IsRequired();
    }
}
