using BaseApi.Core.Exceptions;
using BaseApi.Core.Persistence;
using BaseApi.Service.Features.Orchestration.Loading;
using BaseApi.Service.Features.Orchestration.Projection;
using BaseApi.Service.Features.Orchestration.Validation;
using BaseApi.Service.Features.Workflow;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;

namespace BaseApi.Service.Features.Orchestration;

/// <summary>
/// Phase 13 thin cross-entity orchestrator (ORCH-SPLIT-03). NOT a
/// <c>BaseService&lt;TEntity,...&gt;</c> subclass — there is no single entity to
/// project (CONTEXT D-04).
/// <para>
/// <b>StartAsync</b> orchestrates the locked pipeline (D-01): existence check →
/// <see cref="IWorkflowGraphLoader"/> → <see cref="CycleDetector"/> →
/// <see cref="SchemaEdgeValidator"/> → <see cref="PayloadConfigSchemaValidator"/> →
/// <see cref="IRedisProjectionWriter"/>, with the transient
/// <see cref="WorkflowGraphSnapshot"/> disposed via a <c>using</c> declaration on
/// success AND on any throw. The 5 entity mappers were relocated to
/// <see cref="WorkflowGraphLoader"/> (D-05); the orchestrator keeps only
/// <see cref="BaseDbContext"/> + the ids validator for the existence check.
/// </para>
/// <para>
/// <b>StopAsync</b> is a separate public method (D-07 / ORCH-SPLIT-04) — Start and
/// Stop no longer share a single <c>ValidateWorkflowIdsAsync</c>. Both run the same
/// existence semantics via the private <see cref="ExistenceCheckAsync"/> helper
/// (distinct public surfaces; a shared private helper is not "sharing a method" in
/// the D-07 sense). Phase 15 swaps StopAsync's body to a Redis EXISTS check.
/// </para>
/// </summary>
public sealed class OrchestrationService
{
    private readonly BaseDbContext _db;
    private readonly IValidator<IReadOnlyList<Guid>> _idsValidator;
    private readonly IWorkflowGraphLoader _loader;
    private readonly CycleDetector _cycleDetector;
    private readonly SchemaEdgeValidator _schemaEdgeValidator;
    private readonly PayloadConfigSchemaValidator _payloadConfigSchemaValidator;
    private readonly IRedisProjectionWriter _redisProjectionWriter;

    // Ctor is internal (not public): it accepts the internal seam types
    // (IWorkflowGraphLoader, CycleDetector, ...) which CS0051 forbids on a public
    // member. The class stays `public sealed` (Phase 9 D-06 — controller injects the
    // concrete type). DI resolves this ctor in-assembly; InternalsVisibleTo lets
    // BaseApi.Tests construct it directly.
    internal OrchestrationService(
        BaseDbContext db,
        IValidator<IReadOnlyList<Guid>> idsValidator,
        IWorkflowGraphLoader loader,
        CycleDetector cycleDetector,
        SchemaEdgeValidator schemaEdgeValidator,
        PayloadConfigSchemaValidator payloadConfigSchemaValidator,
        IRedisProjectionWriter redisProjectionWriter)
    {
        _db                           = db                           ?? throw new ArgumentNullException(nameof(db));
        _idsValidator                 = idsValidator                 ?? throw new ArgumentNullException(nameof(idsValidator));
        _loader                       = loader                       ?? throw new ArgumentNullException(nameof(loader));
        _cycleDetector                = cycleDetector                ?? throw new ArgumentNullException(nameof(cycleDetector));
        _schemaEdgeValidator          = schemaEdgeValidator          ?? throw new ArgumentNullException(nameof(schemaEdgeValidator));
        _payloadConfigSchemaValidator = payloadConfigSchemaValidator ?? throw new ArgumentNullException(nameof(payloadConfigSchemaValidator));
        _redisProjectionWriter        = redisProjectionWriter        ?? throw new ArgumentNullException(nameof(redisProjectionWriter));
    }

    /// <summary>
    /// Orchestrates the locked Start pipeline (ORCH-SPLIT-03, order LOCKED by D-01).
    /// The transient <see cref="WorkflowGraphSnapshot"/> is disposed by the
    /// <c>using</c> declaration on success AND on any throw above it. In Plan 13-01
    /// the loader returns an empty snapshot and all 4 validator/writer seams are
    /// no-ops; the orchestrator body is structurally final.
    /// </summary>
    public async Task StartAsync(IReadOnlyList<Guid> workflowIds, CancellationToken ct)
    {
        await ExistenceCheckAsync(workflowIds, ct);                          // 1. D-08 404 fast-fail
        using var snapshot = await _loader.LoadL1Async(workflowIds, ct);     // 2. disposed on success AND throw
        _cycleDetector.Validate(snapshot);                                  // 3. no-op P13
        _schemaEdgeValidator.Validate(snapshot);                            // 4. no-op P13
        _payloadConfigSchemaValidator.Validate(snapshot);                   // 5. no-op P13
        // 6. L2 projection write (Plan 15-02). correlationId is currently string.Empty:
        // Plan 04 wires OrchestrationService to resolve X-Correlation-Id once and pass it
        // here explicitly (D-01). Until then the call site only needs to satisfy the widened
        // signature; the writer itself is exercised with a real correlationId by its own facts.
        await _redisProjectionWriter.UpsertAsync(snapshot, string.Empty, ct);
        // 7. snapshot.Dispose() runs implicitly here AND on any throw above (using declaration).
    }

    /// <summary>
    /// Stop semantics (D-07 / ORCH-SPLIT-04). In v3.3.0 this is the same existence
    /// check as Start's step 1 — a distinct public method, NOT a shared one. Phase 15
    /// swaps this body to a Redis EXISTS check.
    /// </summary>
    public Task StopAsync(IReadOnlyList<Guid> workflowIds, CancellationToken ct)
        => ExistenceCheckAsync(workflowIds, ct);

    /// <summary>
    /// Validates the supplied <paramref name="ids"/> against the
    /// <see cref="WorkflowIdsValidator"/> rules (duplicates, null/empty, Guid.Empty)
    /// THEN verifies every id resolves to an existing <see cref="WorkflowEntity"/>
    /// row (D-08). Throws <see cref="FluentValidation.ValidationException"/> (→ Phase 4
    /// → HTTP 400) on rule violation; throws <see cref="NotFoundException"/> (→ Phase
    /// 4 → HTTP 404) listing the missing id(s) when any id is unresolved.
    /// <para>
    /// Existence check uses a single SQL <c>SELECT id WHERE id IN (...)</c>
    /// projection — <c>AsNoTracking()</c> + <c>Select(w =&gt; w.Id)</c> hydrates only
    /// the id column (CONTEXT D-10). No N-query loop, no full entity materialization.
    /// The error shape is LOCKED — existing Start/Stop facts assert
    /// <c>string.Join(", ", missing)</c> exactly.
    /// </para>
    /// </summary>
    private async Task ExistenceCheckAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
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
