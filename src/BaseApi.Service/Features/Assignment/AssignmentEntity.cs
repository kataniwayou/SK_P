using BaseApi.Core.Entities;

namespace BaseApi.Service.Features.Assignment;

/// <summary>
/// Assignment domain entity — leaf node of the entity FK graph. ENTITY-07 verbatim:
/// 2 new scalar properties on top of <see cref="BaseEntity"/>.
/// <para>
/// <c>StepId</c> is a NON-nullable FK to <c>StepEntity</c>. Postgres FK constraint
/// <c>fk_assignment_step_id</c> (wired by <c>AssignmentEntityConfiguration</c>) uses
/// <c>OnDelete(Restrict)</c> — deleting a Step while an Assignment references it
/// returns 23503 → 422 via the Phase 4 PostgresExceptionMapper.
/// </para>
/// <para>
/// <c>Payload</c> stores an arbitrary JSON document as a Postgres <c>jsonb</c> column
/// (PERSIST-08). The validator (VALID-16) confirms valid JSON syntax via
/// <see cref="System.Text.Json.JsonDocument.Parse(string,System.Text.Json.JsonDocumentOptions)"/>
/// and enforces a 1 MB max length. Schema-conformance validation (VALID-21) is now
/// structurally impossible at this layer — Phase 10 removed the direct schema
/// reference from Assignment. Any future Payload-vs-schema validation would need a
/// new design (e.g., a processor-side schema reference). Deferred to v2.
/// </para>
/// </summary>
public sealed class AssignmentEntity : BaseEntity
{
    public Guid StepId { get; set; }
    public string Payload { get; set; } = string.Empty;
}
