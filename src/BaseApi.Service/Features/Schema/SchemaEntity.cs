using BaseApi.Core.Entities;

namespace BaseApi.Service.Features.Schema;

/// <summary>
/// Schema domain entity — the root of the entity FK graph (Processor.InputSchemaId /
/// Processor.OutputSchemaId reference it; Assignment.SchemaId references it).
/// <para>
/// <c>Definition</c> stores a JSON Schema document (draft 2020-12) as a Postgres
/// <c>jsonb</c> column (PERSIST-08; wired by <c>SchemaEntityConfiguration</c>). The
/// validator (Phase 8 / VALID-08) parses the value and evaluates it against
/// <c>MetaSchemas.Draft202012</c> before persistence.
/// </para>
/// </summary>
public sealed class SchemaEntity : BaseEntity
{
    public string Definition { get; set; } = string.Empty;
}
