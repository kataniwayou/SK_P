---
phase: 13-orchestrationservice-split-l3-fetch-l1-build
plan: 02
subsystem: api
tags: [orchestration, l1-build, ef-core, asnotracking, mapperly, bfs, traversal, cycle-termination]

# Dependency graph
requires:
  - phase: 13-orchestrationservice-split-l3-fetch-l1-build
    plan: 01
    provides: "Thin OrchestrationService orchestrator; WorkflowGraphSnapshot (transient IDisposable, owns D-04 disposal log + injected ILogger); WorkflowGraphLoader holding the 5 relocated Mapperly mappers + ILogger<WorkflowGraphSnapshot>; empty-snapshot LoadL1Async placeholder"
provides:
  - "Real WorkflowGraphLoader.LoadL1Async — staged L3→L1 build: AsNoTracking batch reads of 5 entity tables + 3 junction tables, iterative cycle-terminating BFS over StepNextSteps, Mapperly ToRead mapping, and with{} junction enrichment for Step.NextStepIds + Workflow.EntryStepIds/AssignmentIds"
  - "Private LoadStepsBreadthFirstAsync — iterative depth-wave BFS (List<Guid> visited guard, multi-child fan-out, no recursion/Include/HashSet) that terminates on cyclic graphs while the cycle-rejecting validator is still a no-op (T-13-05 mitigation)"
affects: [13-03-cleanup-traversal-tests, phase-14-validators, phase-15-redis-projection]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Staged L3→L1 loader: load roots + junction edges → BFS the reachable step graph → batch-load dependents (processors/schemas/assignments) → map+enrich. One AsNoTracking query per table/junction; round-trips in the BFS ≈ graph depth."
    - "Junction-backed enrichment via positional-record with{}: Mapperly ToRead returns the M2M collections null ([MapValue null]); the loader rebuilds StepReadDto.NextStepIds and WorkflowReadDto.EntryStepIds/AssignmentIds from the junction GroupBy lookups."
    - "Cycle-terminating iterative BFS with a List<Guid> visited guard (not HashSet, REQ-explicit): skip already-visited step ids before enqueuing the next wave → terminates on A→B→A without a cycle validator."

key-files:
  created: []
  modified:
    - src/BaseApi.Service/Features/Orchestration/Loading/WorkflowGraphLoader.cs

key-decisions:
  - "LoadL1Async made async; STAGE-ordered (workflows+edges → BFS steps → batch dependents → map+enrich) per the plan's topology-forced staging. Assignments collected via the WorkflowAssignments junction only (Pitfall 1 — AssignmentEntity has no WorkflowId; steps are NOT walked for assignments)."
  - "Mapping goes exclusively through the relocated _xxxMapper.ToRead seams (D-05) — no inline new StepReadDto(...)/new WorkflowReadDto(...) projection — preserving the single Mapperly drift-guard seam."
  - "Snapshot constructed via new WorkflowGraphSnapshot(_logger); the loader emits NO disposal log line itself (D-04 — the record owns the literal LogDebug(\"L1 snapshot disposed\") in its own Dispose())."
  - "visited is a List<Guid> keyed on StepId (NOT HashSet) per L1-BUILD-04; the !visited.Contains(childId) check before enqueuing the next wave is the cycle-termination guarantee (T-13-05)."

patterns-established:
  - "Within-plan two-commit split on a single file: Task 1 lands LoadL1Async calling a returns-empty BFS stub (build green); Task 2 replaces the stub body with the real traversal (build green). Keeps both task commits individually buildable + bisect-friendly while touching one file."

requirements-completed: [L1-BUILD-02, L1-BUILD-03, L1-BUILD-04]

# Metrics
duration: ~5min
completed: 2026-05-29
---

# Phase 13 Plan 02: WorkflowGraphLoader Real L3→L1 Build Summary

**Filled `WorkflowGraphLoader.LoadL1Async` with the real L3→L1 build — batched `AsNoTracking` reads of 5 entity tables + 3 junctions, an iterative cycle-terminating BFS over the `StepNextSteps` junction, Mapperly `ToRead` mapping, and `with{}` junction enrichment — producing a fully-populated transient `WorkflowGraphSnapshot` per Start request. Single file touched; 177/177 facts GREEN.**

## Performance

- **Duration:** ~5 min
- **Started:** 2026-05-29T08:38:14Z
- **Completed:** 2026-05-29T08:43:22Z
- **Tasks:** 2
- **Files modified:** 1 (0 created, 1 modified)

## Accomplishments
- `LoadL1Async` rewritten from the Plan 13-01 empty-snapshot placeholder (`Task.FromResult(new WorkflowGraphSnapshot(_logger))`) into the real staged loader: STAGE 1 loads the requested workflows + their `WorkflowEntrySteps`/`WorkflowAssignments` junction edges; STAGE 2 walks the reachable step graph BFS; STAGE 3 batch-loads processors → schemas (via Input/Output/Config FKs) → assignments; STAGE 4 maps every entity via its Mapperly `ToRead` seam and enriches `Step.NextStepIds` + `Workflow.EntryStepIds/AssignmentIds` via `with{}`. All 5 snapshot dictionaries are populated.
- Added the private `LoadStepsBreadthFirstAsync` — iterative depth-wave BFS over `StepNextSteps`, with a `List<Guid> visited` guard (keyed on StepId, not HashSet) that terminates on cyclic graphs, honoring multi-child fan-out (next wave = ALL `NextStepId`s of the wave). No recursion, no `.Include()`, no `HashSet`.
- All reads use `BaseDbContext.Set<>().AsNoTracking().Where(ids.Contains(...))`; zero `Repository<TEntity>` usage (L1-BUILD-02). Assignments collected from the `WorkflowAssignments` junction only — steps are never walked for assignments (Pitfall 1).
- Snapshot constructed with the injected `ILogger<WorkflowGraphSnapshot>` (D-04) — the loader emits no disposal log line itself; the record owns the literal `LogDebug("L1 snapshot disposed")` in its `Dispose()`.
- Build green / zero warnings (Debug); the relocated mappers + `_logger` are now all read (no IDE0052). Full integration suite 177/177 GREEN (no regression — Start still returns 204 because the populated snapshot is consumed by no-op validator/writer seams).

## Task Commits

Each task was committed atomically:

1. **Task 1: Implement LoadL1Async — entity batch reads + Mapperly mapping + enrichment** - `b1584da` (feat)
2. **Task 2: Implement the private iterative BFS step-traversal helper (visited list, cycle-terminating)** - `db7249b` (feat)

**Plan metadata:** (this commit) docs(13-02): complete plan

## Files Created/Modified
- `src/BaseApi.Service/Features/Orchestration/Loading/WorkflowGraphLoader.cs` - added `using Microsoft.EntityFrameworkCore;`; replaced the empty-snapshot `LoadL1Async` body with the staged loader; added the private iterative-BFS `LoadStepsBreadthFirstAsync` helper. 177 LOC (≥ min_lines 90).

## Decisions Made
- **Within-plan two-commit split on one file (no deviation, plan-as-written intent):** Task 1's plan body calls `LoadStepsBreadthFirstAsync`, which Task 2 implements. To keep BOTH task commits individually build-green (per each task's `dotnet build` acceptance gate), Task 1 landed the real `LoadL1Async` plus a returns-empty BFS *stub* helper; Task 2 replaced the stub body with the real iterative traversal. No behavior shipped in the stub commit beyond the empty traversal, and the final state is exactly the plan's specified code. Not a scope/behavior deviation — purely a commit-boundary mechanic to honor "each task committed individually" + "dotnet build exits 0" simultaneously.
- All four STRIDE `mitigate` dispositions satisfied: T-13-05 (cyclic-graph DoS) via the `visited` guard; T-13-08 (info disclosure on the disposal log) inherited from Plan 13-01's data-free literal. T-13-06 (parameterized `Contains` → `= ANY(@ids)`) and T-13-07 (unbounded graph size — out of scope) are `accept`.

## Deviations from Plan

None — plan executed exactly as written. The two-commit mechanic above is a commit-boundary choice (documented under Decisions), not a change to the planned code or scope; the final `WorkflowGraphLoader.cs` matches the plan's specified `LoadL1Async` + `LoadStepsBreadthFirstAsync` bodies verbatim.

## Issues Encountered
None. Build green / zero warnings on both task commits; full suite 177/177 GREEN.

## Known Stubs
None introduced by this plan. This plan REMOVED the prior `LoadL1Async` empty-snapshot stub (tracked in Plan 13-01's Known Stubs) by filling it with real behavior. The validator/writer no-op seams remain stubbed per Plan 13-01 (Phase 14/15 fill them) — unchanged by this plan.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- The loader now produces a fully-populated transient snapshot per Start; Plan 13-03 proves its CONTENTS (SC3) and cycle-termination (SC5) via white-box tests using `InternalsVisibleTo` + internal seam doubles.
- No blockers. Build green / zero warnings; 177/177 facts GREEN. No package change; no schema change (read-only).

---
*Phase: 13-orchestrationservice-split-l3-fetch-l1-build*
*Completed: 2026-05-29*

## Self-Check: PASSED

Modified file `WorkflowGraphLoader.cs` + `13-02-SUMMARY.md` present on disk; both task commits (b1584da, db7249b) present in git log.
