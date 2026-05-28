using BaseApi.Core.Exceptions;
using BaseApi.Core.Mapping;
using BaseApi.Core.Persistence;
using BaseApi.Service.Features.Assignment;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Schema;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;

namespace BaseApi.Service.Features.Orchestration;

/// <summary>
/// Phase 9 cross-entity composition service. NOT a <c>BaseService&lt;TEntity,...&gt;</c>
/// subclass — there is no single entity to project (CONTEXT D-04). Composes over
/// the existing <see cref="WorkflowEntity"/> persistence via direct
/// <see cref="BaseDbContext"/> access for batch reads.
/// <para>
/// <b>v1 surface:</b> a single method <see cref="ValidateWorkflowIdsAsync"/> that both
/// <c>Start</c> and <c>Stop</c> endpoints delegate to (CONTEXT D-12 — Start and Stop
/// are functionally identical in v1). The method runs the auto-discovered
/// <see cref="WorkflowIdsValidator"/> as step 1 (mirrors
/// <c>BaseService.CreateAsync</c> line 97 verbatim), then performs a lightweight
/// id-projection existence check (CONTEXT D-10) and throws
/// <see cref="NotFoundException"/> on any missing id. No entity hydration, no
/// response DTO, no side-effects (SPEC.md amended Acceptance Criteria 2026-05-28).
/// </para>
/// <para>
/// <b>v2 ctor surface stability (CONTEXT D-05):</b> all 5 entity mappers are
/// injected up-front even though v1 uses zero of them. The user has stated future
/// phases will need all 5 entities; pre-injecting now means future phases add
/// methods, not ctor params. Known smell accepted for design-mirror stability.
/// </para>
/// </summary>
public sealed class OrchestrationService
{
    private readonly BaseDbContext _db;
    private readonly IValidator<IReadOnlyList<Guid>> _idsValidator;
    // 5 entity mappers injected up-front per CONTEXT D-05 (build for the second use).
    private readonly IEntityMapper<SchemaEntity,     SchemaCreateDto,     SchemaUpdateDto,     SchemaReadDto>     _schemaMapper;
    private readonly IEntityMapper<ProcessorEntity,  ProcessorCreateDto,  ProcessorUpdateDto,  ProcessorReadDto>  _processorMapper;
    private readonly IEntityMapper<StepEntity,       StepCreateDto,       StepUpdateDto,       StepReadDto>       _stepMapper;
    private readonly IEntityMapper<AssignmentEntity, AssignmentCreateDto, AssignmentUpdateDto, AssignmentReadDto> _assignmentMapper;
    private readonly IEntityMapper<WorkflowEntity,   WorkflowCreateDto,   WorkflowUpdateDto,   WorkflowReadDto>   _workflowMapper;

    public OrchestrationService(
        BaseDbContext db,
        IValidator<IReadOnlyList<Guid>> idsValidator,
        IEntityMapper<SchemaEntity,     SchemaCreateDto,     SchemaUpdateDto,     SchemaReadDto>     schemaMapper,
        IEntityMapper<ProcessorEntity,  ProcessorCreateDto,  ProcessorUpdateDto,  ProcessorReadDto>  processorMapper,
        IEntityMapper<StepEntity,       StepCreateDto,       StepUpdateDto,       StepReadDto>       stepMapper,
        IEntityMapper<AssignmentEntity, AssignmentCreateDto, AssignmentUpdateDto, AssignmentReadDto> assignmentMapper,
        IEntityMapper<WorkflowEntity,   WorkflowCreateDto,   WorkflowUpdateDto,   WorkflowReadDto>   workflowMapper)
    {
        _db                = db                ?? throw new ArgumentNullException(nameof(db));
        _idsValidator      = idsValidator      ?? throw new ArgumentNullException(nameof(idsValidator));
        _schemaMapper      = schemaMapper      ?? throw new ArgumentNullException(nameof(schemaMapper));
        _processorMapper   = processorMapper   ?? throw new ArgumentNullException(nameof(processorMapper));
        _stepMapper        = stepMapper        ?? throw new ArgumentNullException(nameof(stepMapper));
        _assignmentMapper  = assignmentMapper  ?? throw new ArgumentNullException(nameof(assignmentMapper));
        _workflowMapper    = workflowMapper    ?? throw new ArgumentNullException(nameof(workflowMapper));

        // Suppress IDE0052 "unused private field" diagnostics for the 4 mappers that
        // v1 does not yet read — they are intentionally retained per CONTEXT D-05.
        _ = _schemaMapper;
        _ = _processorMapper;
        _ = _stepMapper;
        _ = _assignmentMapper;
        _ = _workflowMapper;
    }

    /// <summary>
    /// Validates the supplied <paramref name="ids"/> against the
    /// <see cref="WorkflowIdsValidator"/> rules (duplicates, null/empty, Guid.Empty)
    /// THEN verifies every id resolves to an existing <see cref="WorkflowEntity"/>
    /// row. Throws <see cref="FluentValidation.ValidationException"/> (→ Phase 4 →
    /// HTTP 400) on rule violation; throws <see cref="NotFoundException"/> (→ Phase
    /// 4 → HTTP 404) listing the missing id(s) when any id is unresolved.
    /// <para>
    /// Existence check uses a single SQL <c>SELECT id WHERE id IN (...)</c>
    /// projection — <c>AsNoTracking()</c> + <c>Select(w =&gt; w.Id)</c> hydrates only
    /// the id column (CONTEXT D-10). No N-query loop, no full entity materialization.
    /// </para>
    /// </summary>
    public async Task ValidateWorkflowIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        // WR-01 guard: a JSON `null` body (or empty body) binds `ids` to `null`.
        // FluentValidation 11+/12 ValidateAndThrowAsync throws ArgumentNullException
        // for a null instance — that becomes HTTP 500. Translate to a
        // ValidationException so the Phase 4 ValidationExceptionHandler maps it to
        // the 400 ValidationProblemDetails advertised by the [ProducesResponseType]
        // contract on Start/Stop. The WorkflowIdsValidator.NotNull() rule is
        // unreachable for the bare-null case (validator never runs when input is null).
        if (ids is null)
        {
            throw new ValidationException(new[]
            {
                new ValidationFailure(nameof(ids), "Request body must not be null."),
            });
        }

        // Step 1: rule validation (mirrors BaseService.CreateAsync line 97 verbatim).
        await _idsValidator.ValidateAndThrowAsync(ids, ct);

        // Step 2: existence check via lightweight id-projection (CONTEXT D-10).
        var existingIds = await _db.Set<WorkflowEntity>()
            .AsNoTracking()
            .Where(w => ids.Contains(w.Id))
            .Select(w => w.Id)
            .ToListAsync(ct);

        var missing = ids.Except(existingIds).ToList();
        if (missing.Count > 0)
        {
            throw new NotFoundException(
                nameof(WorkflowEntity),
                string.Join(", ", missing));
        }
    }
}
