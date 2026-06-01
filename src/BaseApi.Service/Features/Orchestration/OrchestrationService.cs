using BaseApi.Core.Exceptions;
using BaseApi.Core.Persistence;
using BaseApi.Service.Features.Orchestration.Loading;
using BaseApi.Service.Features.Orchestration.Projection;
using BaseApi.Service.Features.Orchestration.Validation;
using BaseApi.Service.Features.Workflow;
using FluentValidation;
using FluentValidation.Results;
using MassTransit;
using Messaging.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace BaseApi.Service.Features.Orchestration;

/// <summary>
/// Phase 13 thin cross-entity orchestrator (ORCH-SPLIT-03). NOT a
/// <c>BaseService&lt;TEntity,...&gt;</c> subclass — there is no single entity to
/// project (CONTEXT D-04).
/// <para>
/// <b>StartAsync</b> (D-07 per-workflow loop) runs the upfront Postgres existence check
/// (404 fast-fail before any mutation), resolves the X-Correlation-Id ONCE (D-01), then for
/// EACH requested workflow first-win probes the L2 root (WEBAPI-SUPPRESS-01 / D-04): if the root
/// already exists the WHOLE write path is SKIPPED for that id (no overwrite, no republish — first
/// write wins); otherwise tolerant pre-clean (<see cref="IRedisL2Cleanup"/> — GC for orphan keys)
/// → <see cref="IWorkflowGraphLoader"/> (single-element list) → the three Phase 14 validators in
/// LOCKED order (<see cref="CycleDetector"/> → <see cref="SchemaEdgeValidator"/> →
/// <see cref="PayloadConfigSchemaValidator"/>) → <see cref="IRedisProjectionWriter"/>. The
/// per-iteration <see cref="WorkflowGraphSnapshot"/> is disposed via a <c>using</c>
/// declaration on success AND on any throw. The StartOrchestration publish carries ONLY the
/// newly-written (deduped) subset, and is suppressed entirely when nothing was written.
/// </para>
/// <para>
/// <b>StopAsync</b> (R1/R3 — 24.1 probe-not-delete) rule-validates the ids then, per workflow,
/// PROBES the L2 root via <c>KeyExistsAsync</c> (R1 dedup gate = the data key); a PRESENT root is
/// handed to <see cref="IRedisL2Cleanup.StopCleanupAsync"/> which (R3) reads the root, BFS-collects
/// the per-step keys, and deletes root + steps + SREMs parent in ONE atomic batch (→ controller 204).
/// The probe (not a delete) keeps the root present so cleanup can discover the step keys. An ABSENT
/// root is a first-win no-op (NOT 422 — supersedes the Phase 22 422-on-missing-root gate). The
/// StopOrchestration publish carries ONLY the deduped subset, and is suppressed entirely when nothing
/// was deleted (a second Stop of an absent workflow is a clean no-op).
/// </para>
/// <para>
/// <b>OBSERV-REDIS-03:</b> a Redis fault on either path is caught, tagged with the offending
/// op name (<c>Data["redisOp"]</c> = <c>"UpsertAsync"</c>/<c>"KeyDeleteAsync"</c>) and
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
    private readonly ProcessorLivenessValidator _processorLivenessValidator;
    private readonly IRedisProjectionWriter _redisProjectionWriter;
    private readonly IRedisL2Cleanup _cleanup;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<OrchestrationService> _logger;

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
        ProcessorLivenessValidator processorLivenessValidator,
        IRedisProjectionWriter redisProjectionWriter,
        IRedisL2Cleanup cleanup,
        IHttpContextAccessor httpContextAccessor,
        IConnectionMultiplexer multiplexer,
        IPublishEndpoint publishEndpoint,
        ILogger<OrchestrationService> logger)
    {
        _db                           = db                           ?? throw new ArgumentNullException(nameof(db));
        _idsValidator                 = idsValidator                 ?? throw new ArgumentNullException(nameof(idsValidator));
        _loader                       = loader                       ?? throw new ArgumentNullException(nameof(loader));
        _cycleDetector                = cycleDetector                ?? throw new ArgumentNullException(nameof(cycleDetector));
        _schemaEdgeValidator          = schemaEdgeValidator          ?? throw new ArgumentNullException(nameof(schemaEdgeValidator));
        _payloadConfigSchemaValidator = payloadConfigSchemaValidator ?? throw new ArgumentNullException(nameof(payloadConfigSchemaValidator));
        _processorLivenessValidator   = processorLivenessValidator   ?? throw new ArgumentNullException(nameof(processorLivenessValidator));
        _redisProjectionWriter        = redisProjectionWriter        ?? throw new ArgumentNullException(nameof(redisProjectionWriter));
        _cleanup                      = cleanup                      ?? throw new ArgumentNullException(nameof(cleanup));
        _httpContextAccessor          = httpContextAccessor          ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        _multiplexer                  = multiplexer                  ?? throw new ArgumentNullException(nameof(multiplexer));
        _publishEndpoint              = publishEndpoint              ?? throw new ArgumentNullException(nameof(publishEndpoint));
        _logger                       = logger                       ?? throw new ArgumentNullException(nameof(logger));
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

        // WEBAPI-SUPPRESS-01 / D-04 — first-win duplicate-suppression lives HERE (the WebApi is the
        // single dedup point so the orchestrator's Start/Stop consumers stay conditionless, Plan 05).
        // Track the ids actually written so a request that is ENTIRELY duplicate emits NO
        // StartOrchestration (and a mixed request publishes ONLY the newly-written ids). This
        // DELIBERATELY supersedes the Phase 22 ORCH-START-05 pre-clean-then-overwrite contract:
        // a re-Start of an already-present root no longer overwrites/refreshes it.
        var db = _multiplexer.GetDatabase();
        var started = new List<Guid>();

        foreach (var workflowId in workflowIds)
        {
            // 1b. First-win existence probe (WEBAPI-SUPPRESS-01). If the root key already exists,
            //     SKIP the WHOLE write path for this id — no pre-clean, no load, no validators, no
            //     Upsert — and exclude it from the published list (no overwrite, no republish).
            //     RATIONALE for KeyExistsAsync-then-skip (not When.NotExists on the real root): the
            //     real root JSON is built INSIDE UpsertAsync with a freshly-minted jobId, so a
            //     When.NotExists placeholder claim followed by UpsertAsync's unconditional overwrite
            //     would defeat first-win. The probe-then-skip keeps the write path single-owner.
            //     The small TOCTOU window between the probe and the write is ACCEPTED this phase
            //     (single active WebApi replica assumed; concurrent-Start dedup deferred —
            //     ORCH-SCALE-01 / threat T-24-04). A genuine Redis fault on the probe is tagged with
            //     the Start op name (OBSERV-REDIS-03) so the 500 body reports a stable op.
            bool alreadyPresent;
            try
            {
                alreadyPresent = await db.KeyExistsAsync(RedisProjectionKeys.Root(workflowId));
            }
            catch (RedisException ex)
            {
                ex.Data["redisOp"] = "UpsertAsync";
                throw;
            }

            if (alreadyPresent)
            {
                // First write wins — this id is already live; do not overwrite or republish it.
                continue;
            }

            // 2. Tolerant pre-clean — deletes any stale root + per-step keys so a re-Start of a
            //    SHRUNK graph leaves no orphan per-step keys (delete-then-write, ORCH-START-05).
            //    The pre-clean is the FIRST half of the Start L2-write path; a genuine Redis
            //    connection fault here (NOT a missing key — that is tolerated inside the routine)
            //    is tagged with the Start op name so the 500 body reports a single stable
            //    "UpsertAsync" op regardless of which Redis call faults first (OBSERV-REDIS-03).
            //    (Under first-win this only runs for an ABSENT root — it now GCs any orphan per-step
            //    keys left behind by a prior crash, not a deliberate re-Start overwrite.)
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

            // 6b. Processor-liveness gate (PROC-LIVE-01, D-15) — ASYNC: reads each participating
            //     processor's self-registered skp:{procId} L2 entry, requiring existence + liveness
            //     (timestamp + interval*2 > now). Absent/stale throws OrchestrationValidationException
            //     (gate "processorLiveness") → 422 — NOT a RedisException, so it propagates uncaught
            //     past the redisOp catch below to the 422 handler. A genuine Redis fault on these GETs
            //     IS a RedisException and is tagged with the Start op name (OBSERV-REDIS-03 / T-22-13).
            try
            {
                await _processorLivenessValidator.ValidateAsync(snapshot, ct);
            }
            catch (RedisException ex)
            {
                ex.Data["redisOp"] = "UpsertAsync";
                throw;
            }

            // 7. L2 projection write. A Redis fault is tagged with the offending op name
            //    (OBSERV-REDIS-03) and rethrown → FallbackExceptionHandler → 500.
            //    R2 (parent-index compensation): UpsertAsync does root+parent(SADD)+steps in ONE
            //    batch, so a batch failure already leaves nothing written (atomic). The SREM below is
            //    the belt-and-suspenders compensation that makes the intent explicit and testable for
            //    any partial/older path: on a write fault, SREM this id from the parent index so the
            //    enumeration-only index never retains an id whose L2 write did not land. It is
            //    best-effort — a fault during the SREM itself degrades to R1 self-healing (dedup reads
            //    the L2 ROOT, not the parent) and must NOT mask the original UpsertAsync fault. The id
            //    is excluded from `started` (nothing publishes for it) because the throw exits the loop.
            try
            {
                await _redisProjectionWriter.UpsertAsync(snapshot, correlationId, ct);
            }
            catch (RedisException ex)
            {
                ex.Data["redisOp"] = "UpsertAsync";
                try
                {
                    await db.SetRemoveAsync(RedisProjectionKeys.ParentIndex(), workflowId.ToString("D"));
                }
                catch (RedisException)
                {
                    // Best-effort compensation: swallow a compensation fault (degrades to R1
                    // self-healing). Never mask the original UpsertAsync fault.
                }

                throw;
            }

            // First-win: this id was genuinely written (its root was absent) — include it in the
            // StartOrchestration publish so the orchestrator only ever sees a genuine deduped start.
            started.Add(workflowId);
            // snapshot.Dispose() runs here AND on any throw above (using declaration).
        }

        // WEBAPI-SUPPRESS-01 — a request that is ENTIRELY duplicate writes nothing and must emit NO
        // StartOrchestration (acceptance: "a second Start for an existing workflowId does not re-emit
        // to the orchestrator"). Only publish when at least one id was newly written.
        if (started.Count == 0)
        {
            return;
        }

        // MSG-WEBAPI-02 / D-02: roots written to L2 — broadcast the StartOrchestration control
        // message carrying ONLY the newly-written (deduped) subset, NOT the full input list.
        // Correlation is set on the message BODY ONLY via a freshly-minted NewId (sequential —
        // better broker/ES locality); the MassTransit envelope CorrelationId is left unset (single
        // source of truth, T-19-envelope-leak). The HTTP-stage correlationId resolved above is a
        // SEPARATE stage and is deliberately NOT carried onto the bus message (per-stage handoff,
        // D-01). A publish failure (broker unreachable) PROPAGATES out of this method — the broker
        // is a hard dep for Start (MSG-WEBAPI-03) → FallbackExceptionHandler → 500.
        var startCorr = NewId.NextGuid();
        await _publishEndpoint.Publish(
            new StartOrchestration(started.ToArray()) { CorrelationId = startCorr },
            ct);
        _logger.LogInformation("Published StartOrchestration CorrelationId={CorrelationId}", startCorr);
    }

    /// <summary>
    /// R1/R3 probe-not-delete Stop (24.1). Rule-validates the ids (null-body guard +
    /// the <see cref="WorkflowIdsValidator"/> rules — mirrors <see cref="ExistenceCheckAsync"/>'s
    /// pre-check), then per workflow PROBES the L2 root via <c>KeyExistsAsync</c>: an ABSENT root is a
    /// tolerant first-win no-op (NOT 422 — this supersedes the Phase 22 422-on-missing-root gate). A
    /// PRESENT root is handed to <see cref="IRedisL2Cleanup.StopCleanupAsync"/> which (R3) reads the
    /// root, BFS-collects the per-step keys, and deletes root + steps + SREMs parent in ONE atomic
    /// batch (→ controller 204) — the probe (not a delete) keeps the root present so cleanup can
    /// discover the step keys. R2: a delete-batch fault SADDs the id back into the parent index
    /// (best-effort compensation) before rethrow, and the id is excluded from the publish. A second
    /// Stop of an already-cleaned workflow deletes nothing and is a clean no-op (idempotent). The
    /// publish carries ONLY the deduped subset and is suppressed when nothing was deleted. A Redis
    /// fault on the delete path is tagged <c>"KeyDeleteAsync"</c> (OBSERV-REDIS-03) → 500.
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

        // WEBAPI-SUPPRESS-01 / D-04 — delete-if-present (first-win symmetric Stop). This
        // DELIBERATELY supersedes the Phase 22 422-on-missing-root gate: a second Stop of an
        // already-cleaned (absent) workflow is now a NO-OP (no 422), not an error. Track the ids
        // whose root was genuinely present-then-deleted so the StopOrchestration publish carries
        // ONLY the deduped subset — a Stop that deletes nothing emits no StopOrchestration.
        var db = _multiplexer.GetDatabase();
        var stopped = new List<Guid>();

        // Per workflow: KeyExistsAsync probe; cleanup owns the atomic delete (R1/R3). The lead op is
        // a non-mutating root-existence PROBE (R1 dedup gate = the data key), NOT a delete: an absent
        // root is a first-win no-op (NOT 422). For a PRESENT root, StopCleanupAsync (R3) reads the
        // root, BFS-collects the per-step keys, and deletes root + steps + SREMs parent in ONE atomic
        // batch — so the root MUST still exist when cleanup runs (the old delete-root-first ordering
        // destroyed cleanup's discovery input and leaked per-step keys). A genuine Redis connection
        // fault on either call (NOT a missing key — that is tolerated) is tagged with the Stop path's
        // stable op name so the 500 body reports a single stable "KeyDeleteAsync" op (OBSERV-REDIS-03).
        foreach (var workflowId in workflowIds)
        {
            bool rootPresent;
            try
            {
                rootPresent = await db.KeyExistsAsync(RedisProjectionKeys.Root(workflowId));
            }
            catch (RedisException ex)
            {
                ex.Data["redisOp"] = "KeyDeleteAsync";
                throw;
            }

            if (!rootPresent)
            {
                // First-win symmetric: absent root → no-op for this id (NOT 422). Skip the
                // cleanup + the publish contribution.
                continue;
            }

            // Root is present — StopCleanupAsync owns the atomic discover-then-delete (root + steps +
            // SREM parent in one batch, R3). R2 (parent-index compensation): if the delete batch
            // throws, the cleanup may already have SREM'd the parent index → SADD this id back
            // (compensate) so the enumeration-only index stays consistent with the still-present L2,
            // then rethrow (tagged). Compensation is best-effort — a fault during the SADD degrades to
            // R1 self-healing (dedup reads the L2 ROOT). The id is excluded from `stopped` (nothing
            // publishes) because the throw exits the loop before the Add.
            try
            {
                await _cleanup.StopCleanupAsync(workflowId, ct);
            }
            catch (RedisException ex)
            {
                ex.Data["redisOp"] = "KeyDeleteAsync";
                try
                {
                    await db.SetAddAsync(RedisProjectionKeys.ParentIndex(), workflowId.ToString("D"));
                }
                catch (RedisException)
                {
                    // Best-effort compensation: swallow a compensation fault (degrades to R1
                    // self-healing). Never mask the original delete fault.
                }

                throw;
            }

            stopped.Add(workflowId);
        }

        // WEBAPI-SUPPRESS-01 — a Stop that deleted no roots (all absent) is a no-op and must emit NO
        // StopOrchestration (acceptance: "a second Stop for an absent workflowId is a no-op").
        if (stopped.Count == 0)
        {
            return;
        }

        // MSG-WEBAPI-02 / D-02: at least one root deleted — broadcast the StopOrchestration control
        // message carrying ONLY the deduped (deleted) subset. Same body-only correlation contract as
        // Start (NewId on the BODY, envelope CorrelationId left unset; HTTP-stage id not carried onto
        // the bus). A publish failure PROPAGATES out of StopAsync (MSG-WEBAPI-03 hard dep) → 500.
        await _publishEndpoint.Publish(
            new StopOrchestration(stopped.ToArray()) { CorrelationId = NewId.NextGuid() },
            ct);
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
