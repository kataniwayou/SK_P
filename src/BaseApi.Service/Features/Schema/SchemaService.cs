using System.Text;
using System.Text.Json;
using BaseApi.Core.Mapping;
using BaseApi.Core.Persistence;
using BaseApi.Core.Persistence.Repositories;
using BaseApi.Core.Services;
using BaseApi.Service.Features.Processor;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace BaseApi.Service.Features.Schema;

/// <summary>
/// Service for <see cref="SchemaEntity"/>. Schema has no junction tables, so the 6-step
/// <c>CreateAsync</c> verb order is inherited verbatim from
/// <see cref="BaseService{TEntity,TCreate,TUpdate,TRead}"/>.
/// <para>
/// <b>Frozen-once-referenced (CFG-10 / D-06 / D-08):</b> <see cref="UpdateAsync"/> is overridden to
/// reject a <c>Definition</c> change on a schema referenced by any processor FK
/// (<c>Input/Output/ConfigSchemaId</c>) with <see cref="SchemaDefinitionFrozenException"/> → HTTP 409.
/// This closes the Gate-A↔Gate-B TOCTOU window by construction. Only <c>Definition</c> is frozen —
/// <c>Name</c>/<c>Description</c> edits (and an unchanged Definition) flow through to base (D-07).
/// </para>
/// </summary>
public sealed class SchemaService :
    BaseService<SchemaEntity, SchemaCreateDto, SchemaUpdateDto, SchemaReadDto>
{
    public SchemaService(
        IValidator<SchemaCreateDto> createValidator,
        IValidator<SchemaUpdateDto> updateValidator,
        IEntityMapper<SchemaEntity, SchemaCreateDto, SchemaUpdateDto, SchemaReadDto> mapper,
        IRepository<SchemaEntity> repo,
        BaseDbContext dbContext)
        : base(createValidator, updateValidator, mapper, repo, dbContext) { }

    /// <summary>
    /// PUT /api/v1/schemas/{id} — layers the frozen-once-referenced precondition (CFG-10 / D-06 / D-08)
    /// in front of the inherited base verb order. The freeze check runs BEFORE <c>base.UpdateAsync</c>
    /// because that call mutates the entity via the mapper, losing the pre-mutation <c>Definition</c>.
    /// </summary>
    public override async Task<SchemaReadDto> UpdateAsync(Guid id, SchemaUpdateDto dto, CancellationToken ct)
    {
        var existing = await DbContext.Set<SchemaEntity>().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, ct);

        // D-07: only Definition is frozen — Name/Description (and an unchanged Definition) flow through.
        // The stored Definition is a Postgres `jsonb` column, so it round-trips NORMALIZED (insignificant
        // whitespace stripped, object keys re-ordered) and will rarely be byte-identical to the raw request
        // string. Compare CANONICAL JSON, not the raw bytes — otherwise a Name/Description-only re-PUT of the
        // same schema body looks like a Definition change and falsely 409s (D-07 regression).
        if (existing is not null && DefinitionChanged(existing.Definition, dto.Definition))
        {
            // Pitfall 6: all THREE schema roles count as "referenced" (D-06 uniform). AssignmentEntity has
            // no direct schema FK (RESEARCH A5) — the ProcessorEntity FK query is sufficient.
            var referenced = await DbContext.Set<ProcessorEntity>().AsNoTracking().AnyAsync(
                p => p.InputSchemaId == id || p.OutputSchemaId == id || p.ConfigSchemaId == id, ct);
            if (referenced)
                throw new SchemaDefinitionFrozenException(id);   // → 409 (D-08)
        }

        // NotFound (missing id), Name/Description edits, and unchanged-Definition edits all flow through.
        return await base.UpdateAsync(id, dto, ct);
    }

    /// <summary>
    /// True only when <paramref name="incoming"/> is a SEMANTICALLY different JSON Schema body than
    /// <paramref name="stored"/>. Because <c>SchemaEntity.Definition</c> is persisted as Postgres
    /// <c>jsonb</c>, the stored value is normalized on write; an ordinal string compare against the raw
    /// request body produces false positives (whitespace / key-order differences). Both values are
    /// already-validated JSON (the DTO validator parses them), so we canonicalize and compare. A value
    /// that fails to parse is treated as changed (conservative — prefer freezing over a silent bypass).
    /// </summary>
    private static bool DefinitionChanged(string? stored, string? incoming)
    {
        if (string.Equals(stored, incoming, StringComparison.Ordinal)) return false;
        if (stored is null || incoming is null) return true;
        try
        {
            using var a = JsonDocument.Parse(stored);
            using var b = JsonDocument.Parse(incoming);
            return !string.Equals(Canonicalize(a.RootElement), Canonicalize(b.RootElement), StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>Compact JSON with object keys recursively sorted (array order preserved — it is significant).</summary>
    private static string Canonicalize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, element);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(prop.Name);
                    WriteCanonical(writer, prop.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
