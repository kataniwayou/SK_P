---
phase: 09-add-getbysourcehash-to-processor-controller-and-new-orchestr
plan: 02
subsystem: api
tags: [aspnet-core, fluentvalidation, ef-core, npgsql, mapperly, dependency-injection]

# Dependency graph
requires:
  - phase: 07
    provides: BaseApi.Core composition root (AddBaseApi + AddBaseApiValidation auto-discovery + AddBaseApiMapping closed-generic scan + BaseDbContext Scoped alias)
  - phase: 08
    provides: WorkflowEntity persistence + AppDbContext WorkflowEntity DbSet + 5 entity Mapperly mappers + AppFeatures.AddAppFeatures aggregator
  - phase: 09-01
    provides: Established Phase 9 pattern of injecting concrete service into entity controller alongside abstract BaseService alias (now extended here with concrete-on-concrete)
provides:
  - Features/Orchestration/ feature folder (4 new files following design-mirror layout)
  - WorkflowIdsValidator (AbstractValidator<IReadOnlyList<Guid>>) auto-discovered for FluentValidation
  - OrchestrationService.ValidateWorkflowIdsAsync (validator + id-projection existence check)
  - POST /api/v1/orchestration/start endpoint returning 204 No Content
  - POST /api/v1/orchestration/stop endpoint returning 204 No Content
  - AddOrchestrationFeature DI extension registering concrete OrchestrationService Scoped
  - AppFeatures.AddAppFeatures aggregator wired with 6th call (post-Workflow)
affects: [09-03, phase-10, orchestration-runtime]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - Bare-primitive-collection validator (AbstractValidator<IReadOnlyList<Guid>>) — first non-DTO validator in the codebase
    - Concrete-on-concrete service injection (no interface, no abstract base alias) for non-CRUD service surface
    - v2-ready ctor surface stability (CONTEXT D-05) — inject all 5 entity mappers up-front even when v1 reads none
    - Id-projection existence check using EF Core Set<TEntity>().AsNoTracking().Where(.Contains).Select(id).ToListAsync
    - Per-feature DI extension simpler than entity equivalents — only AddScoped<ConcreteService>() with no abstract-base alias

key-files:
  created:
    - src/BaseApi.Service/Features/Orchestration/OrchestrationDtoValidator.cs
    - src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs
    - src/BaseApi.Service/Features/Orchestration/OrchestrationController.cs
    - src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs
  modified:
    - src/BaseApi.Service/Composition/AppFeatures.cs

key-decisions:
  - "OrchestrationService is concrete + sealed (NOT BaseService<...>-derived) per CONTEXT D-04 — no single entity to project, composes over WorkflowEntity directly"
  - "OrchestrationController injects concrete OrchestrationService (no IOrchestrationService interface, no abstract-base alias) per CONTEXT D-06 — Phase 7 Warning 7 pattern intentionally NOT applied"
  - "All 5 entity mappers ctor-injected up-front per CONTEXT D-05 with _ = _xxxMapper; suppressors to satisfy TreatWarningsAsErrors — v2 phases add methods, not ctor params"
  - "Class name OrchestrationController is singular (not OrchestrationsController) per CONTEXT D-13 — first and only singular controller name in the codebase; [controller] token resolves to orchestration"
  - "WorkflowIdsValidator targets IReadOnlyList<Guid> directly (one-of-a-kind validator) per CONTEXT D-08 + D-09 — bare JSON array body, no envelope DTO, no MaximumCount rule (deferred)"
  - "Existence check uses single SQL SELECT id WHERE id IN (...) projection (CONTEXT D-10) — AsNoTracking + Select(w => w.Id) hydrates only the id column, NO N-query loop, NO full entity materialization"
  - "AddOrchestrationFeature registers only concrete OrchestrationService Scoped — no abstract-base alias (CONTEXT D-06); validator + 5 mappers auto-discovered by Phase 6 AddBaseApiValidation + AddBaseApiMapping scans"
  - "Both endpoints (Start + Stop) delegate to the same OrchestrationService.ValidateWorkflowIdsAsync method per CONTEXT D-12 — v1 behavior is identical, only URL segment differs"

patterns-established:
  - "Cross-entity composition service: concrete + sealed + ctor-injects BaseDbContext (Scoped) + IValidator (auto-discovered) + N entity mappers (Singleton, auto-discovered); not BaseService-derived"
  - "Bare-primitive-collection validation: AbstractValidator<IReadOnlyList<T>> with RuleFor/Distinct + RuleForEach/.NotEqual; auto-registered by AddValidatorsFromAssembly without subclassing BaseDtoValidator"
  - "204 No Content endpoint shape: thin controller body = await _service.MethodAsync(payload, ct); return NoContent(); — single line per endpoint, no business logic in controller"
  - "Singular controller naming: when the controller targets a verb-noun action rather than an entity collection, the class is singular (OrchestrationController) and [controller] resolves to the lowercase singular noun"
  - "v2 ctor surface stability via unused-mapper injection + _ = _field; suppressors — accepts known smell to keep ctor stable across v1→v2"

requirements-completed: [REQ-2, REQ-3, REQ-4, REQ-5, REQ-6]

# Metrics
duration: ~10min
completed: 2026-05-28
---

# Phase 09 Plan 02: Orchestration Feature Folder Summary

**New Features/Orchestration/ folder with 4 files (WorkflowIdsValidator + OrchestrationService injecting BaseDbContext + 5 entity mappers + OrchestrationController Start/Stop returning 204 No Content + AddOrchestrationFeature DI extension), wired into AppFeatures aggregator as the 6th feature — POST /api/v1/orchestration/start and /stop endpoints accept bare List<Guid> body, validate (duplicates + Guid.Empty + NotEmpty), perform single SQL id-projection existence check against WorkflowEntity, return 204 on success.**

## Performance

- **Duration:** ~10 min
- **Started:** 2026-05-28T05:28:00Z (approx)
- **Completed:** 2026-05-28T05:38:00Z (approx)
- **Tasks:** 5 (all type=auto, no checkpoints)
- **Files modified:** 5 (4 created + 1 modified)

## Accomplishments

- New `src/BaseApi.Service/Features/Orchestration/` folder with the 4 mandated files exactly matching the design-mirror layout.
- `POST /api/v1/orchestration/start` and `POST /api/v1/orchestration/stop` HTTP routes registered in the compiled assembly route table.
- `WorkflowIdsValidator` (bare `IReadOnlyList<Guid>` validator) auto-discovered by `AddBaseApiValidation`'s `AddValidatorsFromAssembly` scan — zero manual DI in `AddOrchestrationFeature`.
- `OrchestrationService` ctor injects BaseDbContext + IValidator<IReadOnlyList<Guid>> + all 5 entity mappers up-front per CONTEXT D-05 for v2 ctor surface stability.
- Existence check implemented as a single SQL `SELECT id WHERE id IN (...)` projection — no N-query loop, no full entity materialization.
- AppFeatures aggregator extended with the 6th call `services.AddOrchestrationFeature();` (Phase 9 REQ-2) — `Program.cs` UNCHANGED (it already invokes `AddAppFeatures()`).
- `dotnet build SK_P.sln -c Release --no-restore` and `-c Debug --no-restore` both exit 0 with zero warnings.

## Task Commits

Each task was committed atomically:

1. **Task 1: Create WorkflowIdsValidator** — `f431fec` (feat)
2. **Task 2: Create OrchestrationService with ValidateWorkflowIdsAsync** — `e73760e` (feat)
3. **Task 3: Create OrchestrationController with Start + Stop endpoints** — `fef430a` (feat)
4. **Task 4: Create AddOrchestrationFeature DI extension** — `01f1aa9` (feat)
5. **Task 5: Wire AddOrchestrationFeature into AppFeatures aggregator** — `46e5240` (feat)

## Files Created/Modified

### Created (4 files, all under `src/BaseApi.Service/Features/Orchestration/`)

- **`OrchestrationDtoValidator.cs`** (45 lines) — `public sealed class WorkflowIdsValidator : AbstractValidator<IReadOnlyList<Guid>>` with 3 rule groups (NotNull/NotEmpty; Distinct.Count == Count; RuleForEach NotEqual Guid.Empty). No `MaximumCount`, no `Include(new BaseDtoValidator<...>())`, no `IOrchestrationService`.
- **`OrchestrationService.cs`** (106 lines) — `public sealed class OrchestrationService` (NOT BaseService-derived). 7-param ctor injecting `BaseDbContext _db` + `IValidator<IReadOnlyList<Guid>> _idsValidator` + 5 `IEntityMapper<TEntity, TCreate, TUpdate, TRead>` mappers (Schema, Processor, Step, Assignment, Workflow). One method: `public async Task ValidateWorkflowIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct)` — validate first, then `_db.Set<WorkflowEntity>().AsNoTracking().Where(w => ids.Contains(w.Id)).Select(w => w.Id).ToListAsync(ct)`, then throw `new NotFoundException(nameof(WorkflowEntity), string.Join(", ", missing))` if any unresolved. The 4 mappers v1 doesn't read are kept alive via `_ = _xxxMapper;` suppressors (TreatWarningsAsErrors=true tolerance).
- **`OrchestrationController.cs`** (69 lines) — `public sealed class OrchestrationController : ControllerBase` with `[ApiController] + [ApiVersion("1.0")] + [Route("api/v{version:apiVersion}/[controller]")]`. Ctor injects concrete `OrchestrationService` (no interface, no abstract-base alias). Two endpoints: `[HttpPost("start")]` and `[HttpPost("stop")]`, both `[FromBody] List<Guid> workflowIds`, both bodies = `await _service.ValidateWorkflowIdsAsync(workflowIds, ct); return NoContent();`. Each decorated with `[ProducesResponseType]` for 204, 400 (ValidationProblemDetails), and 404 (ProblemDetails).
- **`OrchestrationServiceCollectionExtensions.cs`** (37 lines) — `internal static class OrchestrationServiceCollectionExtensions` with one extension `public static IServiceCollection AddOrchestrationFeature(this IServiceCollection services)` that calls `services.AddScoped<OrchestrationService>(); return services;`. Simplest per-feature extension in the codebase — no abstract-base alias because controller injects concrete service directly (CONTEXT D-06).

### Modified

- **`src/BaseApi.Service/Composition/AppFeatures.cs`** (+7 / -2 lines) — added `using BaseApi.Service.Features.Orchestration;` (alphabetically between Assignment and Processor) and appended `services.AddOrchestrationFeature();   // Phase 9 REQ-2` as the 6th call in `AddAppFeatures()` body (after `AddWorkflowFeature()`, before `return services;`). XML doc-comment updated to mention "5 per-entity DI extensions plus the Phase 9 Orchestration feature".

### Unchanged (verified)

- **`src/BaseApi.Service/Program.cs`** — `git diff HEAD~5 HEAD -- src/BaseApi.Service/Program.cs` is empty. The Program.cs body already invokes `AddAppFeatures()` (line 8), so the new Orchestration feature lands in DI automatically through the aggregator change.

## Diff of AppFeatures.cs

```diff
 using BaseApi.Service.Features.Assignment;
+using BaseApi.Service.Features.Orchestration;
 using BaseApi.Service.Features.Processor;
 using BaseApi.Service.Features.Schema;
 using BaseApi.Service.Features.Step;
 using BaseApi.Service.Features.Workflow;
 using Microsoft.Extensions.DependencyInjection;

 ...

 public static IServiceCollection AddAppFeatures(this IServiceCollection services)
 {
     services.AddSchemaFeature();
     services.AddProcessorFeature();
     services.AddStepFeature();
     services.AddAssignmentFeature();
     services.AddWorkflowFeature();
+    services.AddOrchestrationFeature();   // Phase 9 REQ-2
     return services;
 }
```

(Doc-comment text was also adjusted minimally to mention the new feature; no other prose was removed or rewritten.)

## Confirmation of No-Introductions

- `grep IOrchestrationService` returns 0 hits in source (only in plan/context/patterns docs) — no interface introduced per CONTEXT D-06.
- `grep OrchestrationEntity` returns 0 hits — no entity introduced per CONTEXT D-16 (Orchestration is not CRUD-over-an-entity).
- `grep OrchestrationReadDto` returns 0 hits — no response DTO introduced per SPEC.md amended Out of Scope ("A response body of any kind on Orchestration endpoints").
- `grep AddOrchestrationFeature` returns exactly 2 source hits: definition in `OrchestrationServiceCollectionExtensions.cs` + invocation in `AppFeatures.cs`.
- `grep ValidateWorkflowIdsAsync` returns exactly 3 source hits: declaration in `OrchestrationService.cs` + 2 invocations in `OrchestrationController.cs` (Start + Stop).
- `grep WorkflowIdsValidator` returns 1 source-class definition hit + XML `<see cref>` doc-comment references in the 3 sibling files.

## Build Verification

| Command | Exit code | Warnings | Errors |
|---|---|---|---|
| `dotnet build SK_P.sln -c Release --no-restore /clp:ErrorsOnly` | 0 | 0 | 0 |
| `dotnet build SK_P.sln -c Debug --no-restore /clp:ErrorsOnly` | 0 | 0 | 0 |

TreatWarningsAsErrors=true is active globally via Directory.Build.props (Plan 01-01 D-02). Zero warnings means zero suppressed lint complaints across the 4 new files plus the 1 modified file. The `_ = _xxxMapper;` suppressor pattern in `OrchestrationService` is what keeps the IDE0052 "field is assigned but never used" diagnostic from firing on the 4 v1-unused mappers under TreatWarningsAsErrors.

## DI Graph Note

`OrchestrationService` is registered via `services.AddScoped<OrchestrationService>()` in `AddOrchestrationFeature()`. Its 7 ctor dependencies all resolve from pre-existing registrations:

| Ctor param | Type | Lifetime | Registered by |
|---|---|---|---|
| `db` | `BaseDbContext` | Scoped (alias of `AppDbContext`) | Phase 7 D-14 (`AddBaseApiPersistence`) via `sp.GetRequiredService<TDbContext>()` |
| `idsValidator` | `IValidator<IReadOnlyList<Guid>>` | Scoped | Phase 6 (`AddBaseApiValidation` → `AddValidatorsFromAssembly` auto-scan over `BaseApi.Service` assembly) |
| `schemaMapper` | `IEntityMapper<SchemaEntity, SchemaCreateDto, SchemaUpdateDto, SchemaReadDto>` | Singleton | Phase 6 (`AddBaseApiMapping` closed-generic scan) — implemented by Phase 8 Mapperly partial class |
| `processorMapper` | `IEntityMapper<ProcessorEntity, ProcessorCreateDto, ProcessorUpdateDto, ProcessorReadDto>` | Singleton | Phase 6 + Phase 8 |
| `stepMapper` | `IEntityMapper<StepEntity, StepCreateDto, StepUpdateDto, StepReadDto>` | Singleton | Phase 6 + Phase 8 |
| `assignmentMapper` | `IEntityMapper<AssignmentEntity, AssignmentCreateDto, AssignmentUpdateDto, AssignmentReadDto>` | Singleton | Phase 6 + Phase 8 |
| `workflowMapper` | `IEntityMapper<WorkflowEntity, WorkflowCreateDto, WorkflowUpdateDto, WorkflowReadDto>` | Singleton | Phase 6 + Phase 8 |

All dependencies exist before this plan runs — `WebAppFactory<Program>`-based integration tests in Plan 09-03 will resolve the full graph through the same registration path.

## Decisions Made

All decisions matched the plan exactly — see the `key-decisions` frontmatter for the 8 locked-by-CONTEXT decisions reflected in the code. No new decisions emerged during execution.

## Deviations from Plan

None — plan executed exactly as written.

The plan body contained literal verbatim source for all 4 new files plus the 2-edit AppFeatures patch. The verbatim text was used as-is with zero auto-fix adjustments. Build exited 0 warnings 0 errors on first attempt for every task.

## Issues Encountered

None.

## Known Stubs

None. The 4 v1-unused mappers (`_schemaMapper`, `_processorMapper`, `_stepMapper`, `_assignmentMapper`) are **not stubs** — they are intentional v2 ctor surface stability per CONTEXT D-05 (the plan calls this out as a "known smell accepted for design-mirror stability"). The single `_workflowMapper` is also currently unread but kept for symmetry. All are suppressed via `_ = _xxxMapper;` assignments so TreatWarningsAsErrors does not flag them. Future Phase 10+ phases that add Orchestration methods over these entities will read the corresponding mappers.

The `Stop` endpoint is **not a stub** either — it intentionally shares behavior with `Start` per CONTEXT D-12 (both delegate to the same service method in v1; only the URL segment differs). The plan and SPEC.md both acknowledge this as the intentional v1 surface.

## Threat Flags

None. No new network endpoint type or trust boundary was introduced beyond what the plan's `<threat_model>` already covers (T-09-02-MASS-ASSIGN through T-09-02-INTERFACE-INJECTION — all 9 STRIDE entries). The 2 new POST endpoints fit entirely within the existing threat surface analysis (anonymous endpoints accepting JSON-array body, parameterized EF Core query, RFC 7807 error mapping).

## User Setup Required

None — no external service configuration required. The new endpoints use only existing Postgres connectivity (already wired since Phase 2 / 7 / 8).

## Next Phase Readiness

- **Plan 09-03 unblocked at file-availability level:** All 4 Orchestration files exist, AppFeatures wires them in, DI graph is complete. Plan 09-03 can author `WebAppFactory<Program>`-based runtime acceptance tests (204 happy / 400 dup-empty-Guid.Empty / 404 missing-id) against the running pipeline.
- **Build state:** Release + Debug both 0/0 under TreatWarningsAsErrors. Phase 8's 128/128 fact suite remains unaffected — no behavioral change to any existing entity surface, no schema change, no migration.
- **Phase 9 progress:** 2 of 3 plans complete (09-01 ProcessorService.GetBySourceHash + 09-02 Orchestration feature folder). Plan 09-03 is the integration-test plan that closes Phase 9.

## Self-Check: PASSED

Files verified to exist:
- FOUND: src/BaseApi.Service/Features/Orchestration/OrchestrationDtoValidator.cs
- FOUND: src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs
- FOUND: src/BaseApi.Service/Features/Orchestration/OrchestrationController.cs
- FOUND: src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs
- FOUND: src/BaseApi.Service/Composition/AppFeatures.cs (modified)

Commits verified in `git log`:
- FOUND: f431fec (Task 1 — WorkflowIdsValidator)
- FOUND: e73760e (Task 2 — OrchestrationService)
- FOUND: fef430a (Task 3 — OrchestrationController)
- FOUND: 01f1aa9 (Task 4 — AddOrchestrationFeature)
- FOUND: 46e5240 (Task 5 — AppFeatures wiring)

---
*Phase: 09-add-getbysourcehash-to-processor-controller-and-new-orchestr*
*Completed: 2026-05-28*
