using BaseApi.Core.Entities;

namespace BaseApi.Service.Features.Assignment;

/// <summary>
/// Assignment domain entity — leaf node of the entity FK graph. ENTITY-07 verbatim:
/// 3 new scalar properties on top of <see cref="BaseEntity"/>.
/// <para>
/// <c>StepId</c> and <c>SchemaId</c> are NON-nullable FKs to <c>StepEntity</c> and
/// <c>SchemaEntity</c> respectively. Postgres FK constraints
/// <c>fk_assignment_step_id</c> + <c>fk_assignment_schema_id</c> (wired by
/// <c>AssignmentEntityConfiguration</c>) both use <c>OnDelete(Restrict)</c> — deleting
/// a Step or Schema while an Assignment references it returns 23503 → 422 via the
/// Phase 4 PostgresExceptionMapper.
/// </para>
/// <para>
/// <c>Payload</c> stores an arbitrary JSON document as a Postgres <c>jsonb</c> column
/// (PERSIST-08). The validator (VALID-16) confirms valid JSON syntax via
/// <see cref="System.Text.Json.JsonDocument.Parse(string,System.Text.Json.JsonDocumentOptions)"/>
/// and enforces a 1 MB max length. Schema conformance (VALID-21 — does the payload
/// match the referenced Schema?) is OUT OF SCOPE for v1 per 08-CONTEXT line 23.
/// </para>
/// </summary>
public sealed class AssignmentEntity : BaseEntity
{
    public Guid StepId { get; set; }
    public Guid SchemaId { get; set; }
    public string Payload { get; set; } = string.Empty;
}
