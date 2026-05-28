using BaseApi.Core.Entities;

namespace BaseApi.Service.Features.Processor;

/// <summary>
/// Processor domain entity — sits one level below <c>SchemaEntity</c> in the FK topology
/// and one level above <c>StepEntity</c>. ENTITY-04 verbatim: 3 new scalar properties on
/// top of <see cref="BaseEntity"/>.
/// <para>
/// <c>SourceHash</c> is a lowercase SHA-256 hex string (64 chars) — uniquely identifies
/// the processor implementation; the unique index <c>uq_processor_source_hash</c>
/// (wired by <c>ProcessorEntityConfiguration</c>) is the load-bearing artifact for
/// Phase 4 PostgresExceptionMapper SQLSTATE 23505 → HTTP 409 mapping (PERSIST-14 + ERROR-11).
/// </para>
/// <para>
/// <c>InputSchemaId</c> / <c>OutputSchemaId</c> / <c>ConfigSchemaId</c> are nullable Guid FK
/// references to <c>SchemaEntity</c>. Null is permitted on all three — supports source processors
/// (no input), sink processors (no output), and unconfigured processors (no config). The Postgres
/// FK constraints are still enforced when the value is non-null (constraint names
/// <c>fk_processor_input_schema_id</c> / <c>fk_processor_output_schema_id</c> /
/// <c>fk_processor_config_schema_id</c> match the Phase 4 Option A regex naming convention).
/// </para>
/// </summary>
public sealed class ProcessorEntity : BaseEntity
{
    public string SourceHash { get; set; } = string.Empty;
    public Guid? InputSchemaId { get; set; }
    public Guid? OutputSchemaId { get; set; }
    public Guid? ConfigSchemaId { get; set; }
}
