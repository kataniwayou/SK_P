---
phase: 13-orchestrationservice-split-l3-fetch-l1-build
plan: 01
subsystem: api
tags: [orchestration, dependency-injection, idisposable, mapperly, fluentvalidation, internalsvisibleto]

# Dependency graph
requires:
  - phase: 09-add-getbysourcehash-to-processor-controller-and-new-orchestr
    provides: "OrchestrationService (92 LOC, shared ValidateWorkflowIdsAsync), OrchestrationController (Start/Stop), WorkflowIdsValidator"
  - phase: 12-redis-infra-composition-healthcheck-di-registration
    provides: "Redis 5th compose tier + DI registration (consumed by Phase 15 when RedisProjectionWriter/StopAsync bodies land)"
provides:
  - "Thin OrchestrationService orchestrator: StartAsync (6-step locked pipeline) + StopAsync (separate method) + private ExistenceCheckAsync helper"
  - "WorkflowGraphSnapshot — internal sealed record : IDisposable, 5 Dictionary<Guid, ReadDto> members, injected ILogger, idempotent Dispose with D-04 'L1 snapshot disposed' log line"
  - "4 internal seams: IWorkflowGraphLoader/WorkflowGraphLoader (empty-snapshot impl, 5 mappers relocated), CycleDetector, SchemaEdgeValidator, PayloadConfigSchemaValidator (no-op), IRedisProjectionWriter/RedisProjectionWriter (no-op)"
  - "InternalsVisibleTo(\"BaseApi.Tests\") on BaseApi.Service for white-box loader + seam-double access"
affects: [13-02-loader-behavior, 13-03-cleanup-traversal-tests, phase-14-validators, phase-15-redis-projection-stop]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Internal-ctor + factory DI registration: public sealed class with an internal ctor (exposing internal seam types) registered via AddScoped<T>(sp => new T(...)) so the default container's ValidateOnBuild does not demand a public constructor"
    - "using-declaration disposal for transient request-scoped read-model (no hand-rolled try/finally); disposed on success AND on any throw in the pipeline"
    - "Snapshot owns an injected ILogger so the D-04 disposal diagnostic lives exactly at the point of disposal; logger is a positional record member excluded from data value-equality"

key-files:
  created:
    - src/BaseApi.Service/Properties/AssemblyInfo.cs
    - src/BaseApi.Service/Features/Orchestration/WorkflowGraphSnapshot.cs
    - src/BaseApi.Service/Features/Orchestration/Loading/IWorkflowGraphLoader.cs
    - src/BaseApi.Service/Features/Orchestration/Loading/WorkflowGraphLoader.cs
    - src/BaseApi.Service/Features/Orchestration/Validation/CycleDetector.cs
    - src/BaseApi.Service/Features/Orchestration/Validation/SchemaEdgeValidator.cs
    - src/BaseApi.Service/Features/Orchestration/Validation/PayloadConfigSchemaValidator.cs
    - src/BaseApi.Service/Features/Orchestration/Projection/IRedisProjectionWriter.cs
    - src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs
  modified:
    - src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs
    - src/BaseApi.Service/Features/Orchestration/OrchestrationController.cs
    - src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs

key-decisions:
  - "OrchestrationService ctor made internal (was public) — CS0051 forbids a public ctor from exposing the internal seam parameter types; class stays public sealed (Phase 9 D-06). Registered via explicit factory lambda so the default DI container's ValidateOnBuild does not require a public ctor."
  - "Loader holds ILogger<WorkflowGraphSnapshot> (not ILogger<WorkflowGraphLoader>) and passes it straight into the snapshot ctor — the D-04 disposal line lives in the snapshot, not the loader."
  - "5 IEntityMapper closed generics relocated from OrchestrationService to WorkflowGraphLoader (D-05); 5 [SuppressMessage IDE0052] attributes dropped (ctor-assignment keeps the analyzer quiet in 13-01; mappers are read in 13-02)."
  - "All 5 seams + snapshot registered Scoped (loader + service both depend on Scoped BaseDbContext; Singleton would be a captive-dependency bug). Snapshot NOT registered in DI — constructed by the loader."

patterns-established:
  - "Internal-ctor + DI factory registration: keeps a service public while accepting internal collaborators, without leaking the collaborators' visibility or requiring a public ctor for ValidateOnBuild."
  - "Bisect-friendly structural split lands before behavior: orchestrator body is structurally final while the loader returns an empty snapshot; loader logic + tests drop in next plans without re-architecting (Phase 10 precedent, D-01)."

requirements-completed: [ORCH-SPLIT-01, ORCH-SPLIT-02, ORCH-SPLIT-03, ORCH-SPLIT-04, L1-BUILD-01]

# Metrics
duration: ~9min
completed: 2026-05-29
---

# Phase 13 Plan 01: OrchestrationService Split + L1 Snapshot Scaffold Summary

**Decomposed the 92-LOC Phase 9 OrchestrationService into a thin StartAsync/StopAsync orchestrator over 4 internal seams (loader + 3 validators + projection writer) plus a transient IDisposable WorkflowGraphSnapshot L1 read-model — structural split first, with the loader returning an empty snapshot and all validator/writer seams as no-op stubs.**

## Performance

- **Duration:** ~9 min
- **Started:** 2026-05-29T08:25:58Z
- **Completed:** 2026-05-29T08:34:31Z
- **Tasks:** 3
- **Files modified:** 12 (9 created, 3 modified)

## Accomplishments
- `OrchestrationService` slimmed to a thin orchestrator: `StartAsync` runs the locked 6-step pipeline (existence check → loader → CycleDetector → SchemaEdgeValidator → PayloadConfigSchemaValidator → IRedisProjectionWriter) with the snapshot disposed via a `using` declaration; `StopAsync` is now a separate public method; the old shared `ValidateWorkflowIdsAsync` was deleted in favor of a private `ExistenceCheckAsync` helper.
- `WorkflowGraphSnapshot` landed as an internal sealed record : IDisposable with 5 `Dictionary<Guid, ReadDto>` members, a public `IsDisposed`, an injected `ILogger<WorkflowGraphSnapshot>`, and an idempotent `Dispose()` that clears all 5 dictionaries and emits the literal `LogDebug("L1 snapshot disposed")` (D-04).
- 4 internal seams created under `{Loading,Validation,Projection}/` and registered Scoped; `IWorkflowGraphLoader` carries the 5 relocated Mapperly mappers and returns an empty snapshot in this plan.
- `InternalsVisibleTo("BaseApi.Tests")` added so 13-03 can do white-box loader resolution + internal seam doubles.
- All 177 integration facts GREEN (Phase 9 Start/Stop 204/400/404 contract + locked NotFoundException error shape preserved; full-suite regression intact).

## Task Commits

Each task was committed atomically:

1. **Task 1: InternalsVisibleTo + 4 no-op seams + WorkflowGraphSnapshot** - `da0e3ed` (feat)
2. **Task 2: IWorkflowGraphLoader + WorkflowGraphLoader empty-snapshot impl** - `472117d` (feat)
3. **Task 3: Slim OrchestrationService, rewire controller + DI** - `9e56797` (refactor)

**Plan metadata:** (this commit) docs(13-01): complete plan

## Files Created/Modified
- `src/BaseApi.Service/Properties/AssemblyInfo.cs` - exposes internals to BaseApi.Tests
- `src/BaseApi.Service/Features/Orchestration/WorkflowGraphSnapshot.cs` - transient IDisposable L1 read-model record (D-04 disposal log)
- `src/BaseApi.Service/Features/Orchestration/Loading/IWorkflowGraphLoader.cs` - loader seam contract
- `src/BaseApi.Service/Features/Orchestration/Loading/WorkflowGraphLoader.cs` - loader impl, 5 mappers relocated, empty-snapshot in P13
- `src/BaseApi.Service/Features/Orchestration/Validation/CycleDetector.cs` - no-op validator (Phase 14)
- `src/BaseApi.Service/Features/Orchestration/Validation/SchemaEdgeValidator.cs` - no-op validator (Phase 14)
- `src/BaseApi.Service/Features/Orchestration/Validation/PayloadConfigSchemaValidator.cs` - no-op validator (Phase 14)
- `src/BaseApi.Service/Features/Orchestration/Projection/IRedisProjectionWriter.cs` - projection writer seam
- `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs` - no-op projection writer (Phase 15)
- `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` - slimmed to StartAsync + StopAsync + ExistenceCheckAsync
- `src/BaseApi.Service/Features/Orchestration/OrchestrationController.cs` - Start->StartAsync, Stop->StopAsync
- `src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs` - registers loader + 3 validators + writer; factory registration for OrchestrationService

## Decisions Made
- **Internal ctor + factory DI registration (deviation, see below):** the plan kept `OrchestrationService` ctor public, but a public ctor cannot expose the internal seam parameter types (CS0051). Resolved by making the ctor internal (class stays `public sealed`, Phase 9 D-06) and registering it with an explicit `AddScoped<OrchestrationService>(sp => new OrchestrationService(...))` factory, because the default container's `ValidateOnBuild` reflects for a *public* ctor and would otherwise fail at app boot.
- Loader logger typed `ILogger<WorkflowGraphSnapshot>` and passed straight into the snapshot ctor (D-04 wiring) — exactly as the plan specified.
- 5 mappers relocated to the loader with null-guards and no `[SuppressMessage]` (plan-specified, D-05).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] OrchestrationService ctor visibility (CS0051) + DI registration**
- **Found during:** Task 3 (slim OrchestrationService, rewire DI)
- **Issue:** The plan's `OrchestrationService` ctor signature exposes the internal seam types (`IWorkflowGraphLoader`, `CycleDetector`, `SchemaEdgeValidator`, `PayloadConfigSchemaValidator`, `IRedisProjectionWriter`) which the plan locks as `internal`. A `public` ctor cannot expose `internal` parameter types — `dotnet build` failed with 5× CS0051 ("inconsistent accessibility"). First remediation (making the ctor `internal`) compiled but broke the entire app's DI graph: the default DI container's `ValidateOnBuild` requires a *public* constructor, so 91/177 tests failed at WebApplicationFactory boot with "A suitable constructor ... could not be located."
- **Fix:** Kept the ctor `internal` (class stays `public sealed`, satisfying Phase 9 D-06 concrete-injection) and switched the registration from the typed-implementation overload to an explicit factory lambda `AddScoped<OrchestrationService>(sp => new OrchestrationService(...))` that resolves each dependency and invokes the internal ctor directly within the assembly — bypassing the public-ctor reflection requirement.
- **Files modified:** `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs`, `src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs`
- **Verification:** `dotnet build` exits 0 / zero warnings; full suite 177/177 GREEN (was 86/177 under the broken internal-ctor + typed registration).
- **Committed in:** `9e56797` (Task 3 commit)

---

**Total deviations:** 1 auto-fixed (1 blocking)
**Impact on plan:** Necessary for the code to compile AND for the app to boot. The plan's intent (internal seams, public-sealed OrchestrationService, controller injects concrete) is fully preserved; only the ctor accessibility + registration mechanism changed. No scope creep. Establishes a reusable internal-ctor + factory-registration pattern for any future public service that takes internal collaborators.

## Known Stubs

These are intentional, plan-documented no-op stubs that later plans/phases fill (D-01 bisect-friendly structural split). None block this plan's goal — the orchestrator shape is structurally final and the existing 204/400/404 contract is green.

| Stub | File | Reason / Resolved by |
|------|------|----------------------|
| `LoadL1Async` returns empty snapshot | `Loading/WorkflowGraphLoader.cs` | Plan 13-02 fills it with batch reads + BFS + Mapperly enrichment |
| `CycleDetector.Validate` empty body | `Validation/CycleDetector.cs` | Phase 14 throws 422 on detected cycle |
| `SchemaEdgeValidator.Validate` empty body | `Validation/SchemaEdgeValidator.cs` | Phase 14 throws 422 on schema-edge mismatch |
| `PayloadConfigSchemaValidator.Validate` empty body | `Validation/PayloadConfigSchemaValidator.cs` | Phase 14 throws 422 on Payload↔ConfigSchema failure |
| `RedisProjectionWriter.UpsertAsync` returns `Task.CompletedTask` | `Projection/RedisProjectionWriter.cs` | Phase 15 writes the L2 Redis projection |

## Issues Encountered
- See Deviation 1 (CS0051 + DI ValidateOnBuild). Resolved within Task 3.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Orchestrator shape is structurally final; the 6 ordered seam calls + `using` disposal are in place.
- Plan 13-02 drops loader behavior into `LoadL1Async` (no re-architecting); Plan 13-03 adds cleanup/traversal tests using `InternalsVisibleTo` + internal seam doubles.
- No blockers. Build green / zero warnings; 177/177 facts GREEN.

---
*Phase: 13-orchestrationservice-split-l3-fetch-l1-build*
*Completed: 2026-05-29*

## Self-Check: PASSED

All 9 created source files + SUMMARY.md present on disk; all 3 task commits (da0e3ed, 472117d, 9e56797) present in git log.
