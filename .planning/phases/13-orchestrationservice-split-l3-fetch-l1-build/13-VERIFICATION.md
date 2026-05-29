---
phase: 13-orchestrationservice-split-l3-fetch-l1-build
verified: 2026-05-29T10:00:00Z
status: passed
score: 9/9
overrides_applied: 0
---

# Phase 13: OrchestrationService Split + L3 Fetch + L1 Build — Verification Report

**Phase Goal:** `OrchestrationService` becomes a thin orchestrator; a transient `WorkflowGraphSnapshot` (L1) is loaded from Postgres on every Start request and disposed deterministically — no validation gates, no Redis write yet.
**Verified:** 2026-05-29
**Status:** PASSED
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | `OrchestrationService.StartAsync` is a thin orchestrator (~80 LOC) delegating to 4 seams under `{Loading,Validation,Projection}/`, snapshot disposed via `using` declaration | VERIFIED | `OrchestrationService.cs` line 75-84: 6-step pipeline with `using var snapshot = await _loader.LoadL1Async(...)`. No `try`/`finally`. File is 144 LOC total (class body is thin). |
| 2 | `OrchestrationService.StopAsync` exists as a separate public method (no longer shares `ValidateWorkflowIdsAsync` with Start) | VERIFIED | `OrchestrationService.cs` lines 91-92: `public Task StopAsync(...)` defined separately from `StartAsync`. The name `ValidateWorkflowIdsAsync` only appears in a doc comment on line 29, not as a method definition. |
| 3 | All 4 seams exist as internal types under `Features/Orchestration/{Loading,Validation,Projection}/` and are registered Scoped in `AddOrchestrationFeature` | VERIFIED | `IWorkflowGraphLoader` + `WorkflowGraphLoader` in `Loading/`; `CycleDetector`, `SchemaEdgeValidator`, `PayloadConfigSchemaValidator` in `Validation/`; `IRedisProjectionWriter` + `RedisProjectionWriter` in `Projection/`. All 5 registrations confirmed in `OrchestrationServiceCollectionExtensions.cs` lines 60-64 as `AddScoped`. |
| 4 | `WorkflowGraphSnapshot` is an internal sealed record : IDisposable with 5 Dictionary members, public `IsDisposed` bool, injected `ILogger`, and idempotent `Dispose()` that clears all dictionaries AND emits `ILogger.LogDebug("L1 snapshot disposed")` (D-04) | VERIFIED | `WorkflowGraphSnapshot.cs` lines 32-53: `internal sealed record WorkflowGraphSnapshot(ILogger<WorkflowGraphSnapshot> Logger) : IDisposable`. Five `Dictionary<Guid, ...>` init properties, `IsDisposed { get; private set; }`, `Dispose()` calls `.Clear()` on all 5 then emits the literal `Logger.LogDebug("L1 snapshot disposed")` on line 51. No null-assignment to dictionary refs (CS8852 Pitfall 2 avoided). |
| 5 | `LoadL1Async` populates all 5 dictionaries via `AsNoTracking` batch queries on `BaseDbContext.Set<>()`, never via `Repository<TEntity>` (L1-BUILD-02/03) | VERIFIED | `WorkflowGraphLoader.cs` lines 70-126: eight `_db.Set<>().AsNoTracking().Where(...)` calls covering `WorkflowEntity`, `WorkflowEntrySteps`, `WorkflowAssignments`, `ProcessorEntity`, `SchemaEntity`, `AssignmentEntity`, `StepEntity`, `StepNextSteps`. No `Repository<` reference found in any orchestration file. |
| 6 | Step traversal walks every `Workflow.EntryStepIds[*]` and follows every `StepEntity.NextStepIds[*]` via `StepNextSteps` junction with a `List<Guid> visited` (not HashSet) guard that terminates on cyclic graphs | VERIFIED | `WorkflowGraphLoader.cs` lines 139-176: `LoadStepsBreadthFirstAsync` uses `var visited = new List<Guid>()`, iterative `while` loop over `StepNextSteps`, `visited.Contains()` guard before enqueueing, and `currentWave = nextRows.Select(j => j.NextStepId)...` for multi-child fan-out. No `HashSet`, no recursion, no `.Include()`. |
| 7 | `WorkflowGraphSnapshot.Dispose()` runs deterministically on BOTH success AND failure paths — proven by SC4 forced-throw integration test | VERIFIED | `StartCleanupFacts.cs` lines 111-154: fact registers a throwing `IRedisProjectionWriter` (last seam) + recording loader double, POSTs a Start request, asserts HTTP 500, then asserts `recorder.Captured!.IsDisposed == true` AND all 5 dictionaries `Empty`. |
| 8 | SC3 + SC5 proven by integration tests: snapshot contains all expected entities for a multi-workflow graph; multi-child fan-out; cyclic graph terminates | VERIFIED | `WorkflowGraphLoaderFacts.cs` lines 146-265: 3 facts — `LoadL1Async_PopulatesAllFiveDictionaries_ForMultiWorkflowGraph` (SC3 asserts all 5 dicts + enrichment), `LoadL1Async_IncludesAllChildren_ForMultiChildFanOut` (SC5 fan-out asserts both children in `Steps[P].NextStepIds`), `LoadL1Async_Terminates_ForCyclicGraph` (SC5 cycle guard with `Task.WhenAny(10s)`). |
| 9 | `InternalsVisibleTo("BaseApi.Tests")` is present; existing Start/Stop 204/400/404 contract is unaffected | VERIFIED | `AssemblyInfo.cs` line 3: `[assembly: InternalsVisibleTo("BaseApi.Tests")]`. `OrchestrationController.cs` unchanged on routing; controller calls `_service.StartAsync` and `_service.StopAsync`. Summaries report 181/181 tests green x3. |

**Score:** 9/9 truths verified

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/BaseApi.Service/Properties/AssemblyInfo.cs` | `InternalsVisibleTo("BaseApi.Tests")` | VERIFIED | File exists, contains the attribute on line 3. |
| `src/BaseApi.Service/Features/Orchestration/WorkflowGraphSnapshot.cs` | Transient IDisposable record (5 dicts + IsDisposed + ILogger + D-04 Dispose) | VERIFIED | 53-line file, `record WorkflowGraphSnapshot : IDisposable`, literal `"L1 snapshot disposed"` on line 51. |
| `src/BaseApi.Service/Features/Orchestration/Loading/IWorkflowGraphLoader.cs` | Loader seam contract | VERIFIED | `internal interface IWorkflowGraphLoader` with `Task<WorkflowGraphSnapshot> LoadL1Async(IReadOnlyList<Guid> workflowIds, CancellationToken ct)`. |
| `src/BaseApi.Service/Features/Orchestration/Loading/WorkflowGraphLoader.cs` | Real LoadL1Async (177 LOC, batch reads + BFS + Mapperly) | VERIFIED | 177 LOC (exceeds min_lines 90), all 8 `Set<>()` reads present, `LoadStepsBreadthFirstAsync` helper, `dto with { ... }` enrichment, no inline ReadDto construction, no Repository. |
| `src/BaseApi.Service/Features/Orchestration/Validation/CycleDetector.cs` | No-op validator seam | VERIFIED | `internal sealed class CycleDetector`, `void Validate(WorkflowGraphSnapshot snapshot) { /* no-op in P13 */ }`. |
| `src/BaseApi.Service/Features/Orchestration/Validation/SchemaEdgeValidator.cs` | No-op validator seam | VERIFIED | Identical shape to CycleDetector. |
| `src/BaseApi.Service/Features/Orchestration/Validation/PayloadConfigSchemaValidator.cs` | No-op validator seam | VERIFIED | Identical shape to CycleDetector. |
| `src/BaseApi.Service/Features/Orchestration/Projection/IRedisProjectionWriter.cs` | Writer seam contract | VERIFIED | `internal interface IRedisProjectionWriter` with `Task UpsertAsync(WorkflowGraphSnapshot snapshot, CancellationToken ct)`. |
| `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionWriter.cs` | No-op writer | VERIFIED | `Task UpsertAsync(...) => Task.CompletedTask`. No StackExchange.Redis types. |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` | Thin orchestrator (StartAsync + StopAsync + ExistenceCheckAsync) | VERIFIED | 144 LOC, contains `StartAsync` + `StopAsync` + `private ExistenceCheckAsync`, no `ValidateWorkflowIdsAsync`, no mapper fields, no `SuppressMessage`, locked error shape (`NotFoundException(nameof(WorkflowEntity), string.Join(", ", missing))`) intact on lines 139-141. |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationController.cs` | Controller calls StartAsync and StopAsync | VERIFIED | Line 49: `await _service.StartAsync(workflowIds, ct)`. Line 65: `await _service.StopAsync(workflowIds, ct)`. |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs` | 5 Scoped registrations + factory for OrchestrationService | VERIFIED | Lines 52-64: factory lambda for OrchestrationService (internal ctor), plus `AddScoped<IWorkflowGraphLoader, WorkflowGraphLoader>()`, `AddScoped<CycleDetector>()`, `AddScoped<SchemaEdgeValidator>()`, `AddScoped<PayloadConfigSchemaValidator>()`, `AddScoped<IRedisProjectionWriter, RedisProjectionWriter>()`. |
| `tests/BaseApi.Tests/Features/Orchestration/WorkflowGraphLoaderFacts.cs` | SC3 + SC5 white-box tests | VERIFIED | 3 facts, `[Trait("Phase","13")]`, `GetRequiredService<IWorkflowGraphLoader>()` called 3 times, all 5 dictionaries asserted in SC3 fact, fan-out and cycle termination facts present. |
| `tests/BaseApi.Tests/Features/Orchestration/StartCleanupFacts.cs` | SC4 forced-throw disposal gate | VERIFIED | `[Trait("Phase","13")]`, recording loader double, throwing `IRedisProjectionWriter`, `ConfigureTestServices` wiring, `IsDisposed` assertion, all 5 `Assert.Empty` dict assertions. |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|---|-----|--------|---------|
| `OrchestrationController.cs Start` | `OrchestrationService.StartAsync` | `_service.StartAsync(workflowIds, ct)` | WIRED | Controller line 49 confirmed. |
| `OrchestrationController.cs Stop` | `OrchestrationService.StopAsync` | `_service.StopAsync(workflowIds, ct)` | WIRED | Controller line 65 confirmed. |
| `OrchestrationService.StartAsync` | `IWorkflowGraphLoader.LoadL1Async` | `using var snapshot = await _loader.LoadL1Async(...)` | WIRED | OrchestrationService.cs line 78 confirmed, `using` keyword present (no try/finally). |
| `WorkflowGraphSnapshot.Dispose` | `ILogger.LogDebug` | `Logger.LogDebug("L1 snapshot disposed")` | WIRED | WorkflowGraphSnapshot.cs line 51: exact D-04 literal. |
| `AddOrchestrationFeature` | 5 new seams | `AddScoped` registrations | WIRED | All 5 present in OrchestrationServiceCollectionExtensions.cs lines 60-64. |
| `WorkflowGraphLoader.LoadL1Async` | `BaseDbContext.Set<WorkflowEntity>()` | `AsNoTracking().Where(w => workflowIds.Contains(w.Id))` | WIRED | WorkflowGraphLoader.cs line 70-71. |
| `WorkflowGraphLoader BFS` | `StepNextSteps junction` | `Set<StepNextSteps>().Where(j => loadedIds.Contains(j.StepId))` | WIRED | WorkflowGraphLoader.cs line 162. |
| `WorkflowGraphLoader enrichment` | `StepReadDto / WorkflowReadDto` | `dto with { NextStepIds = ... }` / `with { EntryStepIds = ..., AssignmentIds = ... }` | WIRED | WorkflowGraphLoader.cs lines 114, 122. |
| `WorkflowGraphLoader assignment collection` | `WorkflowAssignments junction` | `Set<WorkflowAssignments>().Where(j => workflowIds.Contains(j.WorkflowId))` | WIRED | WorkflowGraphLoader.cs line 78. |
| `WorkflowGraphLoader.LoadL1Async` | `WorkflowGraphSnapshot (D-04 logger owner)` | `new WorkflowGraphSnapshot(_logger)` | WIRED | WorkflowGraphLoader.cs line 104. |

---

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|---------------|--------|--------------------|--------|
| `WorkflowGraphLoader.LoadL1Async` | `snapshot` (5 dicts) | `_db.Set<>().AsNoTracking().Where(...)` queries on all 5 entity tables + 3 junctions | Yes — EF Core LINQ batch queries, no static returns | FLOWING |
| `WorkflowGraphSnapshot` | 5 `Dictionary<Guid, ReadDto>` members | Populated by `WorkflowGraphLoader` via Mapperly `ToRead` + `with {}` enrichment | Yes — each dict populated in STAGE 4 | FLOWING |
| `StartCleanupFacts` (SC4) | `recorder.Captured` | `RecordingWorkflowGraphLoader` wraps real `WorkflowGraphLoader` | Yes — real loader called, snapshot is a populated real object | FLOWING |

---

### Behavioral Spot-Checks

Step 7b: SKIPPED — test suite requires Postgres + Redis fixtures (integration tests cannot run without running services). Behavioral verification is covered by the 181-test suite documented in summaries (green x3).

---

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| ORCH-SPLIT-01 | 13-01 | OrchestrationService split into orchestrator + 4 internal seams under `{Loading,Validation,Projection}/` | SATISFIED | All 4 seam directories + 7 seam files confirmed in codebase. |
| ORCH-SPLIT-02 | 13-01 | Seams stay `internal` to `BaseApi.Service.Features.Orchestration` | SATISFIED | All seam files use `internal sealed class/interface` keyword, confirmed by reading each file. |
| ORCH-SPLIT-03 | 13-01 | `OrchestrationService.StartAsync` becomes the orchestrator (~80 LOC), with ordered seam pipeline | SATISFIED | StartAsync body is 9 lines (lines 77-84); class is 144 LOC total. 6 seam calls + 1 comment in correct order. |
| ORCH-SPLIT-04 | 13-01 | `OrchestrationService.StopAsync` extracted as a NEW method | SATISFIED | `public Task StopAsync(...)` on line 91 is a distinct method; `ValidateWorkflowIdsAsync` does not exist as a method in the source file. |
| L1-BUILD-01 | 13-01 | `WorkflowGraphSnapshot` is a transient record implementing `IDisposable` | SATISFIED | `internal sealed record WorkflowGraphSnapshot : IDisposable` confirmed, `IsDisposed` + 5 dicts + D-04 disposal log. |
| L1-BUILD-02 | 13-02 | `LoadL1Async` uses `BaseDbContext.Set<>().AsNoTracking()` — NOT `Repository<TEntity>` | SATISFIED | All 8 batch reads use `_db.Set<>().AsNoTracking()`. No `Repository<` in orchestration code. |
| L1-BUILD-03 | 13-02 / 13-03 | Loader populates flat `Dictionary<Guid, EntityDto>` for each entity type, keyed by Id, using v3.2.0 ReadDtos | SATISFIED | STAGE 4 of `LoadL1Async` populates `snapshot.Workflows`, `.Assignments`, `.Steps`, `.Processors`, `.Schemas` dictionaries. SC3 integration test asserts all 5 contain expected ids. |
| L1-BUILD-04 | 13-02 / 13-03 | Step traversal walks every collection (multi-child fan-out) with `List<Guid> visited` keyed on StepId | SATISFIED | `LoadStepsBreadthFirstAsync` uses `var visited = new List<Guid>()`, multi-child fan-out via `nextRows.Select(j => j.NextStepId)`. SC5 fan-out and SC5 cycle-termination facts green. |
| L1-BUILD-05 | 13-01 / 13-03 | L1 cleanup contract: `Dispose()` clears all dicts; called via `using` declaration on success AND failure paths | SATISFIED | `WorkflowGraphSnapshot.Dispose()` clears 5 dicts + sets `IsDisposed = true`. `using var snapshot` in `StartAsync` line 78. SC4 forced-throw fact asserts `IsDisposed == true` + all dicts empty after a throw. |

All 9 phase requirements (ORCH-SPLIT-01..04, L1-BUILD-01..05) are satisfied.

---

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| `Validation/CycleDetector.cs` | 8 | Empty `Validate` body | INFO (intentional stub) | Documented in Plan 13-01 Known Stubs; Phase 14 fills it. No impact on phase goal. |
| `Validation/SchemaEdgeValidator.cs` | 8 | Empty `Validate` body | INFO (intentional stub) | Same — Phase 14. |
| `Validation/PayloadConfigSchemaValidator.cs` | 8 | Empty `Validate` body | INFO (intentional stub) | Same — Phase 14. |
| `Projection/RedisProjectionWriter.cs` | 7 | `=> Task.CompletedTask` no-op | INFO (intentional stub) | Phase 15 fills it. No impact on phase goal. |

No blocker anti-patterns found. All stubs are intentional, plan-documented, and scoped to later phases.

---

### Human Verification Required

None. All must-haves are fully verifiable from the codebase and commit history.

The following items are expected to require human or live-environment verification in later phases:
- Real 204/400/404 contract regression (test suite must run against live Postgres + Redis fixtures — covered by 181/181 green x3 reported in summaries but not re-verified here without running the suite).

---

### Gaps Summary

No gaps. All 9 ROADMAP Success Criteria and all 9 phase requirement IDs are fully satisfied by the actual codebase artifacts, key links, and test coverage.

The validator/writer no-op stubs (CycleDetector, SchemaEdgeValidator, PayloadConfigSchemaValidator, RedisProjectionWriter) are intentional per the phase plan and are addressed by Phases 14 and 15 in the milestone.

---

_Verified: 2026-05-29T10:00:00Z_
_Verifier: Claude (gsd-verifier)_
