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
        if (existing is not null && !string.Equals(existing.Definition, dto.Definition, StringComparison.Ordinal))
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
}
