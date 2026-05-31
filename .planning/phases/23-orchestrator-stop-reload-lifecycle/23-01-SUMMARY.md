---
phase: 23-orchestrator-stop-reload-lifecycle
plan: 01
subsystem: api
tags: [messaging-contracts, masstransit, system-text-json, orchestrator, l2-projection, correlation]

# Dependency graph
requires:
  - phase: 21-v3.4.0-closeout-hygiene
    provides: shared L2ProjectionKeys + WorkflowRootProjection read-shape in Messaging.Contracts.Projections
  - phase: 22-l2-root-parent-restructure
    provides: writer StepProjection (enum-typed) + StepEntryCondition int assignments + Messaging.Contracts leaf
provides:
  - "Reader-consumable StepProjection record (int EntryCondition) in Messaging.Contracts.Projections (ORCH-CONTRACT-01)"
  - "IExecutionCorrelated : ICorrelated segregated interface with the 5 execution Guids (D-01)"
  - "EntryStepDispatch 7-field orchestrator->processor dispatch message (ORCH-CONTRACT-02)"
affects: [orchestrator-runtime, fire-job, hydration, plan-03, plan-04, plan-05]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Reader/writer projection-record split: writer keeps enum (BaseApi.Service internal), reader hoists int-typed shape into Messaging.Contracts so the orchestrator (contracts-only ref) can deserialize byte-identical wire values (RESEARCH Pitfall 7)"
    - "Segregated correlation interface: IExecutionCorrelated extends ICorrelated with execution id-set; entry dispatch defaults ExecutionId/EntryId to Guid.Empty"

key-files:
  created:
    - src/Messaging.Contracts/Projections/StepProjection.cs
    - src/Messaging.Contracts/IExecutionCorrelated.cs
    - src/Messaging.Contracts/EntryStepDispatch.cs
    - tests/BaseApi.Tests/Orchestrator/StepProjectionReaderTests.cs
    - tests/BaseApi.Tests/Orchestrator/EntryStepDispatchTests.cs
  modified:
    - tests/BaseApi.Tests/Features/Orchestration/HappyPathE2EFacts.cs
    - tests/BaseApi.Tests/Features/Orchestration/RedisProjectionWriterFacts.cs
    - tests/BaseApi.Tests/Features/Orchestration/Projection/ProjectionRecordRoundTripTests.cs
    - tests/BaseApi.Tests/Features/Orchestration/StopCleanupFacts.cs

key-decisions:
  - "Reader StepProjection types EntryCondition as int (not the writer's StepEntryCondition enum) — no string-enum converter is registered, so the enum serializes as its underlying int and both records are byte-identical on the wire; also satisfies T-23-01 (out-of-range int never throws an enum-parse exception at the contract boundary)"
  - "Resolved the StepProjection name collision in 4 pre-existing test files via a using-alias (WriterStepProjection) rather than renaming either record — both records keep their canonical name StepProjection"

patterns-established:
  - "When hoisting a writer-internal projection into the shared contracts leaf, alias the writer type in any test that imports both namespaces to avoid CS0104 ambiguity"

requirements-completed: [ORCH-CONTRACT-01, ORCH-CONTRACT-02]

# Metrics
duration: ~12min
completed: 2026-05-31
---

# Phase 23 Plan 01: Orchestrator Lifecycle Leaf Contracts Summary

**Reader-consumable `StepProjection` record (int EntryCondition) plus the `IExecutionCorrelated` interface and 7-field `EntryStepDispatch` dispatch message — the two net-new Messaging.Contracts leaves the rest of Phase 23 builds against.**

## Performance

- **Duration:** ~12 min
- **Started:** 2026-05-31
- **Completed:** 2026-05-31
- **Tasks:** 2 (both TDD)
- **Files modified:** 9 (5 created, 4 modified)

## Accomplishments
- `Messaging.Contracts.Projections.StepProjection` — reader record with `int EntryCondition`, byte-identical on the wire to the writer's enum-typed shape; a writer value `{"entryCondition":1,...}` round-trips with all four camelCase fields bound (ORCH-CONTRACT-01).
- `IExecutionCorrelated : ICorrelated` — the segregated execution-correlation interface Phase 19 D-01 deferred, exposing `ExecutionId/WorkflowId/StepId/ProcessorId/EntryId`.
- `EntryStepDispatch` — 7-logical-field sealed record implementing `IExecutionCorrelated`, with `ExecutionId`/`EntryId` defaulting to `Guid.Empty` on entry-step dispatch (ORCH-CONTRACT-02).
- Two new pinning test classes (4 facts total) prove the int round-trip and the 7-field/Guid.Empty shape; `Messaging.Contracts` builds 0 Warning / 0 Error.

## Task Commits

Each task was committed atomically:

1. **Task 1: Hoist the reader StepProjection record** - `2405d07` (feat)
2. **Task 2: IExecutionCorrelated + EntryStepDispatch** - `d1e6b65` (feat)

_Both tasks are TDD; for pure-contract records the failing-test → minimal-record cycle collapses into a single atomic feat commit (the test cannot compile without the type)._

## Files Created/Modified
- `src/Messaging.Contracts/Projections/StepProjection.cs` - Reader L2 per-step projection record (int EntryCondition)
- `src/Messaging.Contracts/IExecutionCorrelated.cs` - Segregated execution-correlation interface (5 Guids)
- `src/Messaging.Contracts/EntryStepDispatch.cs` - 7-field orchestrator->processor dispatch message
- `tests/BaseApi.Tests/Orchestrator/StepProjectionReaderTests.cs` - Writer-value int round-trip + camelCase-bind pins
- `tests/BaseApi.Tests/Orchestrator/EntryStepDispatchTests.cs` - 7-field shape + Guid.Empty defaults + interface assignability pins
- `tests/BaseApi.Tests/Features/Orchestration/HappyPathE2EFacts.cs` - Aliased writer StepProjection (ambiguity fix)
- `tests/BaseApi.Tests/Features/Orchestration/RedisProjectionWriterFacts.cs` - Aliased writer StepProjection (ambiguity fix)
- `tests/BaseApi.Tests/Features/Orchestration/Projection/ProjectionRecordRoundTripTests.cs` - Aliased writer StepProjection (ambiguity fix)
- `tests/BaseApi.Tests/Features/Orchestration/StopCleanupFacts.cs` - Aliased writer StepProjection (ambiguity fix)

## Decisions Made
- Reader `EntryCondition` is `int`, not the writer enum: no `JsonStringEnumConverter` is registered anywhere, so the writer enum serializes as its underlying int and both records are byte-identical on the wire (RESEARCH Pitfall 7). This also realizes the T-23-01 mitigation — an out-of-range int surfaces as a plain int for the Plan 04 consumer to range-check, never an enum-parse throw at the contract boundary.
- Resolved the new `StepProjection` name collision via a `using WriterStepProjection = ...` alias in each affected test file, keeping both records named `StepProjection` (writer internal vs. reader public).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] StepProjection name collision in 4 pre-existing test files**
- **Found during:** Task 1 (Hoist the reader StepProjection record)
- **Issue:** The new `Messaging.Contracts.Projections.StepProjection` collided with the writer's `BaseApi.Service.Features.Orchestration.Projection.StepProjection` in 4 test files that import both namespaces, producing CS0104 "ambiguous reference" build errors that blocked the whole test project (and thus Task 1's own verification).
- **Fix:** Added a `using WriterStepProjection = BaseApi.Service.Features.Orchestration.Projection.StepProjection;` alias to each affected file and changed the 8 ambiguous unqualified usages (all of which construct/deserialize the writer's enum-typed record) to the alias. No behavior change — the tests still exercise the writer record exactly as before.
- **Files modified:** HappyPathE2EFacts.cs, RedisProjectionWriterFacts.cs, ProjectionRecordRoundTripTests.cs, StopCleanupFacts.cs
- **Verification:** Test project compiles; StepProjectionReaderTests passes 2/2; the touched writer-projection tests still build and are unaffected.
- **Committed in:** `2405d07` (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (1 blocking)
**Impact on plan:** The alias fix was required for the test project to compile at all once the second `StepProjection` entered scope. No scope creep — the change is a mechanical disambiguation confined to test files that already referenced the writer record.

## Issues Encountered
- The plan's combined verification idiom `--filter-class A|B` matched 0 tests (the MTP `--filter-class` flag does not accept a `|`-alternation). Ran each class separately instead — both pass (2/2 each). Not a regression; purely a filter-syntax limitation.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- ORCH-CONTRACT-01/02 leaves are in place; Plans 03/04 (orchestrator runtime + hydration) and Plan 05 (tests) can now build against `StepProjection`, `IExecutionCorrelated`, and `EntryStepDispatch`.
- No blockers.

## Self-Check: PASSED

- All 6 created files verified present on disk.
- Both task commits verified in git history (`2405d07`, `d1e6b65`).

---
*Phase: 23-orchestrator-stop-reload-lifecycle*
*Completed: 2026-05-31*
