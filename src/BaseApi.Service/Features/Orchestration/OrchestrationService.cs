using BaseApi.Core.Configuration;
using BaseApi.Core.Exceptions;
using BaseApi.Core.Persistence;
using BaseApi.Service.Features.Orchestration.Loading;
using BaseApi.Service.Features.Orchestration.Projection;
using BaseApi.Service.Features.Orchestration.Validation;
using BaseApi.Service.Features.Workflow;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace BaseApi.Service.Features.Orchestration;

/// <summary>
/// Phase 13 thin cross-entity orchestrator (ORCH-SPLIT-03). NOT a
/// <c>BaseService&lt;TEntity,...&gt;</c> subclass — there is no single entity to
/// project (CONTEXT D-04).
/// <para>
/// <b>StartAsync</b> (D-07 per-workflow loop) runs the upfront Postgres existence check
/// (404 fast-fail before any mutation), resolves the X-Correlation-Id ONCE (D-01), then
/// for EACH requested workflow: tolerant pre-clean (<see cref="IRedisL2Cleanup"/> — GC for
/// shrunk graphs, ORCH-START-05 delete-then-write) → <see cref="IWorkflowGraphLoader"/>
/// (single-element list) → the three Phase 14 validators in LOCKED order
/// (<see cref="CycleDetector"/> → <see cref="SchemaEdgeValidator"/> →
/// <see cref="PayloadConfigSchemaValidator"/>) → <see cref="IRedisProjectionWriter"/>. The
/// per-iteration <see cref="WorkflowGraphSnapshot"/> is disposed via a <c>using</c>
/// declaration on success AND on any throw.
/// </para>
/// <para>
/// <b>StopAsync</b> (D-04/D-06 Redis EXISTS gate + cleanup) rule-validates the ids, batch
/// <c>KeyExistsAsync</c>-checks every workflow root key, and if ANY root is missing throws
/// <see cref="OrchestrationValidationException.MissingRoots"/> (→ 422 with the FULL missing
/// list, NO deletion); only when all exist does it run the per-workflow
/// <see cref="IRedisL2Cleanup.StopCleanupAsync"/> (→ controller 204). A repeated Stop of an
/// already-cleaned workflow re-fails the gate (422 — non-idempotent by design).
/// </para>
/// <para>
/// <b>OBSERV-REDIS-03:</b> a Redis fault on either path is caught, tagged with the offending
/// op name (<c>Data["redisOp"]</c> = <c>"UpsertAsync"</c>/<c>"KeyExistsAsync"</c>) and
/// rethrown; <c>FallbackExceptionHandler</c> surfaces that op name (only) in the 500 body
/// alongside the Phase 4 correlationId — never a connection string or stack.
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
    private readonly IRedisL2Cleanup _cleanup;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly string _keyPrefix;

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
        IRedisProjectionWriter redisProjectionWriter,
        IRedisL2Cleanup cleanup,
        IHttpContextAccessor httpContextAccessor,
        IConnectionMultiplexer multiplexer,
        IOptions<RedisProjectionOptions> options)
    {
        _db                           = db                           ?? throw new ArgumentNullException(nameof(db));
        _idsValidator                 = idsValidator                 ?? throw new ArgumentNullException(nameof(idsValidator));
        _loader                       = loader                       ?? throw new ArgumentNullException(nameof(loader));
        _cycleDetector                = cycleDetector                ?? throw new ArgumentNullException(nameof(cycleDetector));
        _schemaEdgeValidator          = schemaEdgeValidator          ?? throw new ArgumentNullException(nameof(schemaEdgeValidator));
        _payloadConfigSchemaValidator = payloadConfigSchemaValidator ?? throw new ArgumentNullException(nameof(payloadConfigSchemaValidator));
        _redisProjectionWriter        = redisProjectionWriter        ?? throw new ArgumentNullException(nameof(redisProjectionWriter));
        _cleanup                      = cleanup                      ?? throw new ArgumentNullException(nameof(cleanup));
        _httpContextAccessor          = httpContextAccessor          ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        _multiplexer                  = multiplexer                  ?? throw new ArgumentNullException(nameof(multiplexer));
        _keyPrefix                    = (options ?? throw new ArgumentNullException(nameof(options))).Value.KeyPrefix;
    }

    /// <summary>
    /// D-07 per-workflow Start loop. <see cref="ExistenceCheckAsync"/> runs FIRST as the
    /// upfront 404 gate (all ids checked before any mutation). The X-Correlation-Id is
    /// resolved ONCE (D-01) and passed explicitly to the writer. Each workflow is then
    /// processed independently: tolerant pre-clean (delete-then-write GC for shrunk graphs,
    /// ORCH-START-05) → per-workflow <c>LoadL1Async([id])</c> → the three validators in the
    /// LOCKED Phase 14 order → <c>UpsertAsync</c>. The per-iteration snapshot is disposed by
    /// the <c>using</c> declaration on success AND on any throw above it.
    /// </summary>
    public async Task StartAsync(IReadOnlyList<Guid> workflowIds, CancellationToken ct)
    {
        await ExistenceCheckAsync(workflowIds, ct);   // 1. D-08 404 fast-fail (ALL ids, before any mutation)

        // D-01 — resolve the correlationId ONCE from HttpContext.Items (set by
        // CorrelationIdMiddleware; same read as AuditInterceptor) and pass it explicitly
        // to the HTTP-agnostic writer for every workflow in this request.
        var correlationId = _httpContextAccessor.HttpContext?.Items["CorrelationId"] as string ?? string.Empty;

        foreach (var workflowId in workflowIds)
        {
            // 2. Tolerant pre-clean — deletes any stale root + per-step keys so a re-Start of a
            //    SHRUNK graph leaves no orphan per-step keys (delete-then-write, ORCH-START-05).
            //    The pre-clean is the FIRST half of the Start L2-write path; a genuine Redis
            //    connection fault here (NOT a missing key — that is tolerated inside the routine)
            //    is tagged with the Start op name so the 500 body reports a single stable
            //    "UpsertAsync" op regardless of which Redis call faults first (OBSERV-REDIS-03).
            try
            {
                await _cleanup.StopCleanupAsync(workflowId, ct);
            }
            catch (RedisException ex)
            {
                ex.Data["redisOp"] = "UpsertAsync";
                throw;
            }

            // 3. Per-workflow L1 build (single-element list — no new loader variant, RES Pattern 5).
            //    Disposed by the `using` on success AND on any throw below (L1 cleanup contract).
            using var snapshot = await _loader.LoadL1Async(new[] { workflowId }, ct);

            // 4-6. Validator gate order is LOCKED (Phase 14): cycle → schema-edge → payload-config.
            _cycleDetector.Validate(snapshot);
            _schemaEdgeValidator.Validate(snapshot);
            _payloadConfigSchemaValidator.Validate(snapshot);

            // 7. L2 projection write. A Redis fault is tagged with the offending op name
            //    (OBSERV-REDIS-03) and rethrown → FallbackExceptionHandler → 500.
            try
            {
                await _redisProjectionWriter.UpsertAsync(snapshot, correlationId, ct);
            }
            catch (RedisException ex)
            {
                ex.Data["redisOp"] = "UpsertAsync";
                throw;
            }
            // snapshot.Dispose() runs here AND on any throw above (using declaration).
        }
    }

    /// <summary>
    /// D-04/D-06 Stop gate + cleanup. Rule-validates the ids (null-body guard + the
    /// <see cref="WorkflowIdsValidator"/> rules — mirrors <see cref="ExistenceCheckAsync"/>'s
    /// pre-check), batch <c>KeyExistsAsync</c>-checks every workflow root key, and collects
    /// ALL ids whose root is missing (NOT fail-fast). If any are missing it throws
    /// <see cref="OrchestrationValidationException.MissingRoots"/> (→ 422 with the FULL list,
    /// NO deletion). Only when every root exists does it run the per-workflow tolerant
    /// <see cref="IRedisL2Cleanup.StopCleanupAsync"/> (→ controller 204). Repeated Stop of an
    /// already-cleaned workflow re-fails the gate (422 — non-idempotent by design). A Redis
    /// fault on the EXISTS batch is tagged <c>"KeyExistsAsync"</c> (OBSERV-REDIS-03) → 500.
    /// </summary>
    public async Task StopAsync(IReadOnlyList<Guid> workflowIds, CancellationToken ct)
    {
        // WR-01 null-body guard + rule validation — mirror ExistenceCheckAsync (lines below)
        // so a JSON null body / malformed id list is rejected (400) before any Redis touch
        // (T-15-12 input validation runs first on Stop too).
        if (workflowIds is null)
        {
            throw new ValidationException(new[]
            {
                new ValidationFailure(nameof(workflowIds), "Request body must not be null."),
            });
        }

        await _idsValidator.ValidateAndThrowAsync(workflowIds, ct);

        // D-04 — batch EXISTS on each workflow root key; collect ALL missing (not fail-fast).
        var db = _multiplexer.GetDatabase();
        List<Guid> missing;
        try
        {
            var checks = workflowIds
                .Select(id => (Id: id, Task: db.KeyExistsAsync(RedisProjectionKeys.Root(_keyPrefix, id))))
                .ToList();
            await Task.WhenAll(checks.Select(c => c.Task));
            missing = checks.Where(c => !c.Task.Result).Select(c => c.Id).ToList();
        }
        catch (RedisException ex)
        {
            ex.Data["redisOp"] = "KeyExistsAsync";
            throw;
        }

        // D-04 — any missing root → 422 with the FULL missing list and NO deletion.
        if (missing.Count > 0)
        {
            throw OrchestrationValidationException.MissingRoots(missing);
        }

        // D-06 — all roots exist → per-workflow tolerant traverse-and-delete (root + per-step
        // keys; never processor keys). Controller maps the completed Task to 204.
        // A genuine Redis connection fault during the post-gate deletes (NOT a missing key —
        // that is tolerated inside the routine) is tagged with the Stop path's stable op name
        // so the 500 body reports a single stable "KeyExistsAsync" op regardless of which Redis
        // call faults first (OBSERV-REDIS-03) — mirroring the Start pre-clean convention above.
        try
        {
            foreach (var workflowId in workflowIds)
            {
                await _cleanup.StopCleanupAsync(workflowId, ct);
            }
        }
        catch (RedisException ex)
        {
            ex.Data["redisOp"] = "KeyExistsAsync";
            throw;
        }
    }

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
