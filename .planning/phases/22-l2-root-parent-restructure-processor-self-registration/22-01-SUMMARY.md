---
phase: 22-l2-root-parent-restructure-processor-self-registration
plan: 01
subsystem: infra
tags: [redis, l2-projection, key-builder, messaging-contracts, hardening]

# Dependency graph
requires:
  - phase: 21-v3.4.0-closeout-hygiene
    provides: shared L2ProjectionKeys single-source-of-truth (Root/Step/Processor) + thin writer/reader forwarders (HARDEN-03)
provides:
  - "L2ProjectionKeys.Prefix compile-time const (\"skp:\") — prefix no longer a builder parameter or host config"
  - "L2ProjectionKeys.ParentIndex() builder returning the bare prefix (the parent-index SET key)"
  - "Parameterless no-prefix Root/Step/Processor builders on the shared key class"
  - "Writer forwarder (RedisProjectionKeys) no-prefix signatures + a ParentIndex() forwarder"
  - "Reader forwarder (OrchestratorL2Keys) no-prefix Root signature"
affects: [22-02, 22-03, parent-index, redis-projection-writer, redis-l2-cleanup, orchestration-service, orchestrator-consumers]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Compile-time const key prefix owned in the leaf contract (no config-injection path into key names)"
    - "Bare-prefix ParentIndex() SET-key builder colocated with the GUID key builders"

key-files:
  created: []
  modified:
    - src/Messaging.Contracts/Projections/L2ProjectionKeys.cs
    - src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionKeys.cs
    - src/Orchestrator/Messaging/OrchestratorL2Keys.cs
    - tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs

key-decisions:
  - "Prefix is a compile-time const \"skp:\" on L2ProjectionKeys (D-01) — reversed Phase 21's 'prefix stays a parameter (D-05)' XML doc."
  - "ParentIndex() returns the bare Prefix const verbatim — the parent-index SET key (D-02)."
  - "Writer forwarder gains ParentIndex() (the only side that SADD/SREMs); reader forwarder keeps only Root(Guid) (D-04)."
  - "L2ProjectionKeysTests Phase trait bumped 21 -> 22; per-class-prefix fact deleted since prefix is no longer configurable (D-24)."

patterns-established:
  - "Const key prefix in the shared leaf eliminates any caller-supplied input feeding key construction (T-22-01 mitigation)."
  - "No-prefix builder signatures enforced in lockstep across both thin forwarders (HARDEN-03 preserved)."

requirements-completed: [L2PREFIX-01, L2IDX-01]

# Metrics
duration: 2min
completed: 2026-05-31
---

# Phase 22 Plan 01: L2 Key Prefix Const + ParentIndex() Builder Summary

**Made the L2 key prefix a compile-time `const Prefix = "skp:"` on the shared `L2ProjectionKeys`, dropped the `string prefix` parameter from every builder, added the `ParentIndex()` bare-prefix SET-key builder, and updated both thin forwarders + golden tests to the new parameterless signatures in lockstep.**

## Performance

- **Duration:** 2 min
- **Started:** 2026-05-31T08:46:16Z
- **Completed:** 2026-05-31T08:48:19Z
- **Tasks:** 3
- **Files modified:** 4

## Accomplishments
- `L2ProjectionKeys` now owns a `public const string Prefix = "skp:"` (D-01) — the prefix is no longer a per-host config value nor a builder parameter, removing any config-injection path into key names (T-22-01).
- Added `ParentIndex() => Prefix` (D-02) — the bare-prefix parent-index SET key that Plans 02/03 will `SADD`/`SREM` against.
- All three builders (`Root`/`Step`/`Processor`) dropped their `string prefix` parameter and now interpolate the const; `Root` keeps its `:D` (hyphenated) specifier.
- Writer forwarder `RedisProjectionKeys` updated to no-prefix signatures and gained a `ParentIndex()` forwarder; reader forwarder `OrchestratorL2Keys` updated to no-prefix `Root(Guid)`.
- `L2ProjectionKeysTests` rewritten to the new signatures, added a `ParentIndex()` golden asserting `"skp:"`, and dropped the per-class-prefix fact (D-24).

## Task Commits

Each task was committed atomically:

1. **Task 1: Const prefix + ParentIndex() + no-prefix builders in L2ProjectionKeys** - `d6e7c3b` (feat)
2. **Task 2: Update both forwarders to no-prefix signatures (HARDEN-03)** - `ebedb02` (refactor)
3. **Task 3: Update L2ProjectionKeysTests golden strings** - `112d580` (test)

_Note: Tasks 1 and 3 were declared `tdd="true"` but this plan is a behavior-preserving restructure of pre-existing golden tests rather than greenfield RED/GREEN; the test signatures track the production signatures directly. No separate RED commit was produced because the test had to change in lockstep with the dropped builder parameter._

## Files Created/Modified
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` - Added `const Prefix`, `ParentIndex()`, dropped `string prefix` from all builders, reversed the XML doc.
- `src/BaseApi.Service/Features/Orchestration/Projection/RedisProjectionKeys.cs` - No-prefix forwarders + new `ParentIndex()` forwarder (writer side).
- `src/Orchestrator/Messaging/OrchestratorL2Keys.cs` - No-prefix `Root(Guid)` forwarder (reader side).
- `tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs` - New parameterless golden facts + `ParentIndex()` golden; per-class-prefix fact removed; Phase trait 21 -> 22.

## Decisions Made
- None beyond the plan — D-01/D-02/D-04/D-24 implemented exactly as specified.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

**Task 3 test execution is blocked mid-wave (expected and documented by the plan).** The `dotnet test --filter-class L2ProjectionKeysTests` verify could not run because `tests/BaseApi.Tests` transitively references `BaseApi.Service` and `Orchestrator`, both of which have consuming call sites (`RedisProjectionWriter`, `RedisL2Cleanup`, `OrchestrationService`, `StartOrchestrationConsumer`, `StopOrchestrationConsumer`) that still call the old 2-/3-arg builders. The build fails with exactly 9 `CS1501: No overload for method 'Root'/'Step'/'Processor' takes N arguments` errors — precisely the call sites the plan's Task 2 NOTE names as "fixed in Plans 02 and 03." The plan's `<verification>` explicitly states: *"the full solution does NOT build at the end of this plan ... this is expected and acceptable mid-wave."* The `Messaging.Contracts` project (which owns the changed contract) builds clean with 0 warnings / 0 errors. The test source itself is correct against the new signatures (verified by acceptance greps); it will go GREEN once Plans 02/03 land.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- The stable no-prefix `L2ProjectionKeys` contract (+ `ParentIndex()`) is in place for the rest of Wave 1+ to build against.
- Plans 02/03 MUST fix the 5 consuming call sites (writer/cleanup/service/2 consumers) and the dependent test files (`RedisProjectionWriterFacts`, `StopCleanupFacts`, `GateNoWriteFacts`, plus config-removal test/`appsettings`) before the solution and the `L2ProjectionKeysTests` class can build and run GREEN.
- No blockers; the mid-wave non-building solution is the intended hand-off state.

## Self-Check: PASSED

All 4 modified files present on disk; all 3 task commits (`d6e7c3b`, `ebedb02`, `112d580`) found in git history.

---
*Phase: 22-l2-root-parent-restructure-processor-self-registration*
*Completed: 2026-05-31*
