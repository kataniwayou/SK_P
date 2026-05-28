---
phase: 09-add-getbysourcehash-to-processor-controller-and-new-orchestr
plan: 01
subsystem: api
tags: [aspnetcore, efcore, mapperly, processor, sourcehash, problemdetails]

# Dependency graph
requires:
  - phase: 08-entity-build-out-migrations-docker-runtime-tests
    provides: ProcessorEntity + ProcessorService + ProcessorsController + uq_processor_source_hash unique index + AddProcessorFeature DI
  - phase: 07-generic-http-base-composition-root
    provides: BaseService<TEntity,TCreate,TUpdate,TRead> + BaseController<...> + 5-method IRepository<T> contract
  - phase: 06-validation-mapping-base
    provides: IEntityMapper<TEntity,TCreate,TUpdate,TRead> contract + AddBaseApiMapping closed-generic registration
  - phase: 04-cross-cutting-middleware-error-handling
    provides: NotFoundExceptionHandler → 404 ProblemDetails with resourceType/resourceId extensions
  - phase: 03-ef-core-persistence-base
    provides: BaseDbContext protected property on BaseService for direct DbContext.Set<T>() access
provides:
  - ProcessorService.GetBySourceHashAsync(string, CancellationToken) method (concrete-side extension over abstract base)
  - GET /api/v1/processors/by-source-hash/{sourceHash} HTTP endpoint returning 200 + ProcessorReadDto or 404 ProblemDetails
  - Pattern: direct DbContext.Set<TEntity>() + AsNoTracking() + predicate-based FirstOrDefaultAsync (CONTEXT D-04 alternative to extending IRepository<T>)
  - Pattern: concrete-service injection alongside abstract-base alias in a single controller ctor (PATTERNS Section 1 Option A)
  - Pattern: duplicate mapper injection on derived service (Option B over promoting BaseService._mapper to protected)
affects: 09-02, 09-03

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Concrete-service hash-lookup using DbContext.Set<T>() + AsNoTracking() (avoids IRepository<T> expansion per Phase 3 D-04)"
    - "Dual ctor injection on controller (abstract BaseService alias + concrete ProcessorService) — preserves inherited verbs while exposing entity-specific actions"
    - "Mapper duplicate-injection on derived service — concrete service holds its own IEntityMapper<...> reference; BaseService._mapper stays private"

key-files:
  created: []
  modified:
    - src/BaseApi.Service/Features/Processor/ProcessorService.cs
    - src/BaseApi.Service/Features/Processor/ProcessorController.cs

key-decisions:
  - "Used DbContext.Set<ProcessorEntity>() + AsNoTracking() + FirstOrDefaultAsync directly in ProcessorService instead of expanding IRepository<T> (CONTEXT D-04 + Phase 3 D-04 — IRepository<T> stays at exactly 5 methods)"
  - "Duplicated IEntityMapper<...> injection on ProcessorService rather than promoting BaseService._mapper from private to protected (PATTERNS.md Section 2 Option B — cross-cutting visibility change rejected for one consumer)"
  - "Controller injects BOTH abstract BaseService<...> (for inherited 5 CRUD verbs via Phase 7 Warning 7 option b) AND concrete ProcessorService (for new GetBySourceHash action) — no DI change because AddProcessorFeature already registers both shapes"
  - "Route literal 'by-source-hash/{sourceHash}' chosen over bare '{sourceHash}' to avoid collision with inherited BaseController.GetById's '{id:guid}' constraint"
  - "No route-level validation on {sourceHash} parameter — off-format strings (non-hex, wrong length) 404 via row-miss per SPEC.md Constraint + CONTEXT D-03"
  - "NotFoundException(nameof(ProcessorEntity), sourceHash) — resourceType uses entity class name to match Phase 8 service convention (will inform Plan 09-03 integration test assertions)"

patterns-established:
  - "Concrete-service extension over abstract BaseService<...>: derived service can add public methods that consume the protected DbContext property without modifying the base"
  - "ProblemDetails 404 carries the supplied lookup key verbatim in resourceId — no PII transformation needed because the key was supplied by the caller"
  - "DI alias-plus-concrete dual registration (already in AddProcessorFeature) enables controllers to inject both shapes in a single ctor without DI graph changes"

requirements-completed: [REQ-1]

# Metrics
duration: 3min
completed: 2026-05-28
---

# Phase 09 Plan 01: Add ProcessorService.GetBySourceHashAsync + Controller Route Summary

**ProcessorService extended with a SourceHash lookup using direct DbContext.Set<T>() + AsNoTracking(), wired to a new GET /api/v1/processors/by-source-hash/{sourceHash} action that returns 200+ProcessorReadDto on hit and 404 ProblemDetails on miss — IRepository<T>'s 5-method surface and BaseService internals untouched.**

## Performance

- **Duration:** ~3 min (build-only verification; no test execution per plan — Plan 09-03 owns integration tests)
- **Started:** 2026-05-28T05:23:32Z
- **Completed:** 2026-05-28T05:26:13Z
- **Tasks:** 3 (2 source-modifying + 1 build verification)
- **Files modified:** 2

## Accomplishments

- ProcessorService.GetBySourceHashAsync added: direct EF Core query against ProcessorEntity using DbContext.Set<T>() + AsNoTracking() + predicate-based FirstOrDefaultAsync; throws NotFoundException on miss
- ProcessorsController.GetBySourceHash action added: GET /api/v1/processors/by-source-hash/{sourceHash} with proper [ProducesResponseType] for both 200 and 404 ProblemDetails
- Dual ctor injection pattern (abstract BaseService alias + concrete ProcessorService) preserves all 5 inherited CRUD verbs while exposing the new entity-specific action
- Solution builds cleanly in BOTH Release and Debug configurations (0 warnings under TreatWarningsAsErrors=true)
- Phase 3 D-04 invariant preserved: IRepository<T> still has exactly 5 public method signatures

## Task Commits

Each task was committed atomically:

1. **Task 1: Extend ProcessorService with GetBySourceHashAsync** — `f99974a` (feat)
2. **Task 2: Add GetBySourceHash method to ProcessorsController** — `f3872cd` (feat)
3. **Task 3: Full-solution build + Phase 1-8 regression smoke (build-only)** — no source diff (build-only verification; both Release/Debug exited 0 with zero warnings; no additional commit needed)

**Plan metadata:** (separate docs commit applied after this SUMMARY)

## Files Created/Modified

- `src/BaseApi.Service/Features/Processor/ProcessorService.cs` — extended ctor with `IEntityMapper<...>` parameter stored in `_mapper` private field; new `GetBySourceHashAsync` public method
- `src/BaseApi.Service/Features/Processor/ProcessorController.cs` — extended ctor with `ProcessorService` parameter stored in `_processorService` private field; new `GetBySourceHash` action mapped to `by-source-hash/{sourceHash}`

## Confirmations from Plan Output Spec

### Final diff of ProcessorService.cs (before → after)

**Before (26 lines):** passthrough ctor only (5 params → `: base(...)`); empty body.

**After (63 lines):** ctor extended to keep 5 base params identical but additionally store `mapper` in a new `_mapper` private readonly field; new `GetBySourceHashAsync(string sourceHash, CancellationToken ct)` method calls `DbContext.Set<ProcessorEntity>().AsNoTracking().FirstOrDefaultAsync(p => p.SourceHash == sourceHash, ct)`, throws `NotFoundException(nameof(ProcessorEntity), sourceHash)` on null, returns `_mapper.ToRead(entity)` otherwise.

### Final diff of ProcessorController.cs (before → after)

**Before (26 lines):** passthrough ctor with single abstract `BaseService<...>` param; empty body.

**After (49 lines):** ctor extended to second `ProcessorService processorService` param stored in `_processorService` field (abstract base still flows to `: base(service)`); new `GetBySourceHash(string sourceHash, CancellationToken ct)` action decorated with `[HttpGet("by-source-hash/{sourceHash}")]` + `[ProducesResponseType(StatusCodes.Status200OK)]` + `[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]` returns `Ok(await _processorService.GetBySourceHashAsync(sourceHash, ct))`.

### AddProcessorFeature DI registration unchanged

`src/BaseApi.Service/Features/Processor/ProcessorServiceCollectionExtensions.cs` not modified. Verified intact:
```
services.AddScoped<ProcessorService>();
services.AddScoped<BaseService<ProcessorEntity, ProcessorCreateDto, ProcessorUpdateDto, ProcessorReadDto>>(
    sp => sp.GetRequiredService<ProcessorService>());
```
Both shapes resolve correctly. Mapper is auto-registered by `AddBaseApiMapping` (Phase 6 closed-generic reflection scan).

### IRepository<T> surface still 5 methods (Phase 3 D-04 preserved)

`src/BaseApi.Core/Persistence/Repositories/IRepository.cs` confirmed intact: `GetAsync`, `ListAsync`, `AddAsync`, `Update`, `DeleteAsync`. No new helper added.

### Both build commands exited 0 with zero warnings

- `dotnet build SK_P.sln -c Release --no-restore` → Build succeeded. 0 Warning(s). 0 Error(s). Time Elapsed 00:00:01.82
- `dotnet build SK_P.sln -c Debug --no-restore` → Build succeeded. 0 Warning(s). 0 Error(s). Time Elapsed 00:00:01.85

TreatWarningsAsErrors=true (project-wide via Directory.Build.props) means a single warning would have failed the build. Zero observed across BaseApi.Core, BaseApi.Service, and BaseApi.Tests.

## Decisions Made

None beyond the plan-specified key decisions (documented in frontmatter). Plan executed exactly as written.

## Deviations from Plan

None - plan executed exactly as written.

The plan's pre-baked source code was applied verbatim for both source-modifying tasks. No bugs encountered. No missing critical functionality. No blocking issues. No architectural decisions required.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Threat Model Verification

All `mitigate` dispositions from the plan's `<threat_model>` are honored:

| Threat ID | Disposition | Implementation Verification |
|-----------|-------------|----------------------------|
| T-09-01-INJECT-SQL | mitigate | EF Core LINQ predicate `p => p.SourceHash == sourceHash` is parameterized by Npgsql — no raw SQL, no string concatenation, no `FromSqlRaw` in the new method. Verified by reading ProcessorService.cs lines 53-60. |
| T-09-01-DOS-NOSCAN | mitigate | Postgres unique index `uq_processor_source_hash` (Phase 8 InitialCreate migration) makes the lookup O(log n). Predicate hits the indexed column directly. No `LIKE`, no client-side filter. |
| T-09-01-MEMORY-LEAK-DBCTX | mitigate | `.AsNoTracking()` present at ProcessorService.cs:56 (load-bearing per CONTEXT D-02 — read-only intent, no ChangeTracker footprint). |

`accept` dispositions (T-09-01-INFO-DISCLOSURE-404, T-09-01-ENUM-EXISTENCE, T-09-01-AUTHZ-MISSING) are explicit design accepts per SPEC.md "Out of scope" and require no implementation change.

## Next Phase Readiness

- Plan 09-02 (OrchestrationController + OrchestrationService — Start/Stop endpoints) can now reference this plan's patterns for concrete-service injection and direct DbContext.Set<T>() usage when it needs to read across all 5 entity mappers
- Plan 09-03 (integration tests for the new endpoint + Phase 1-8 regression replay) can now exercise the new route — `GET /api/v1/processors/by-source-hash/{sourceHash}` returns 200/404 as designed; the test fixture set up under Phase 8's Phase8WebAppFactory will need no schema/DI changes
- No blockers or concerns for downstream plans

## Self-Check: PASSED

**Created files:** none

**Modified files:**
- `src/BaseApi.Service/Features/Processor/ProcessorService.cs` — FOUND, contains literal `public async Task<ProcessorReadDto> GetBySourceHashAsync(string sourceHash, CancellationToken ct)` at line 53 + `DbContext.Set<ProcessorEntity>()` at line 55 + `throw new NotFoundException(nameof(ProcessorEntity), sourceHash);` at line 58
- `src/BaseApi.Service/Features/Processor/ProcessorController.cs` — FOUND, contains literal `[HttpGet("by-source-hash/{sourceHash}")]` at line 44 + `_processorService.GetBySourceHashAsync(sourceHash, ct)` at line 48 + inherits `BaseController<ProcessorEntity, ProcessorCreateDto, ProcessorUpdateDto, ProcessorReadDto>` at line 24

**Commits:**
- `f99974a` — FOUND in git log (Task 1 — feat(09-01): add ProcessorService.GetBySourceHashAsync)
- `f3872cd` — FOUND in git log (Task 2 — feat(09-01): add ProcessorsController.GetBySourceHash action)

**Builds:** Both `Release` and `Debug` confirmed 0 warnings / 0 errors with full output captured above.

---
*Phase: 09-add-getbysourcehash-to-processor-controller-and-new-orchestr*
*Completed: 2026-05-28*
