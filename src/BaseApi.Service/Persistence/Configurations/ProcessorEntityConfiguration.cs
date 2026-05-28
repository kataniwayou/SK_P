using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BaseApi.Service.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="ProcessorEntity"/>. This is the LOAD-BEARING
/// file for Plan 08-08 SC#2 (duplicate SourceHash POST returns 409 with offending field name) —
/// the explicit unique-index name <c>uq_processor_source_hash</c> + explicit FK constraint
/// names <c>fk_processor_input_schema_id</c> / <c>fk_processor_output_schema_id</c> /
/// <c>fk_processor_config_schema_id</c> match the Phase 4 PostgresExceptionMapper Option A regex
/// (preserves <c>_id</c> suffix; rejects EF auto-names which would diverge with <c>ix_</c> prefix
/// or extra <c>_schemas_</c> segment).
/// <para>
/// <b>Lambda-less <c>HasOne&lt;SchemaEntity&gt;().WithMany()</c> form</b> per RESEARCH Pitfall 4 —
/// avoids EF generating navigation properties between entities, satisfying ENTITY-09
/// "no nav props between entities" while still creating the Postgres FK constraint.
/// </para>
/// </summary>
internal sealed class ProcessorEntityConfiguration : IEntityTypeConfiguration<ProcessorEntity>
{
    public void Configure(EntityTypeBuilder<ProcessorEntity> entity)
    {
        // PERSIST-14 + ERROR-11 — explicit unique-index name; EF auto-name would be ix_processor_source_hash.
        entity.HasIndex(e => e.SourceHash)
            .IsUnique()
            .HasDatabaseName("uq_processor_source_hash");

        // ENTITY-04 — nullable FK; explicit constraint name per ERROR-11 + Phase 4 Option A regex.
        entity.HasOne<SchemaEntity>()
            .WithMany()
            .HasForeignKey(e => e.InputSchemaId)
            .HasConstraintName("fk_processor_input_schema_id")
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasOne<SchemaEntity>()
            .WithMany()
            .HasForeignKey(e => e.OutputSchemaId)
            .HasConstraintName("fk_processor_output_schema_id")
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasOne<SchemaEntity>()
            .WithMany()
            .HasForeignKey(e => e.ConfigSchemaId)
            .HasConstraintName("fk_processor_config_schema_id")
            .OnDelete(DeleteBehavior.SetNull);

        // VALID-10 — SHA-256 hex is exactly 64 chars; lock at DB.
        entity.Property(e => e.SourceHash)
            .IsRequired()
            .HasMaxLength(64);
    }
}
