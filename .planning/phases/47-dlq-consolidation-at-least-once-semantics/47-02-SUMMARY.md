---
phase: 47-dlq-consolidation-at-least-once-semantics
plan: 02
subsystem: testing
tags: [masstransit, xunit-v3, nsubstitute, at-least-once, no-collapse, dedup, dlq, resilience]

# Dependency graph
requires:
  - phase: 46-keeper-5-state-recovery-orchestrator-per-item-consume
    provides: "TypedResultConsumer family (StepCompletedConsumer + RecordingDispatcher), ReinjectConsumer, RecoveryDeadLetterFacts harness, RecoveryTestKit.CapturingSendProvider"
  - phase: 43-message-contracts-l2-key-reshape
    provides: "RETIRE-01 removal of H/flag[H]/MessageIdentity from the execution path (the no-dedup precondition)"
provides:
  - "Duplicate_StepCompleted_reproduces_effect_no_collapse — RESIL-03 no-collapse proof (ONE dispatcher, double-Consume of the SAME StepCompleted, Calls.Count == 2)"
  - "Duplicate_Reinject_reproduces_effect_no_collapse — RESIL-03 no-collapse proof on the EntryStepDispatch-family (ReinjectConsumer) seam (ONE consumer, double-Consume, Sent.Count == 2)"
  - "Phase-47 re-tag on DataGone_reinject_faults_and_routes_to_dead_letter (RESIL-02 / R2 discoverable under --filter-trait Phase=47, cited not re-tested)"
affects: [47-03, 48-reactive-path-keeper-dlq-retirement, 49-live-realstack-dlq-at-least-once-proof]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "No-collapse proof shape: ONE consumer/dispatcher + double-Consume of the SAME message instance + Assert count == 2 (NOT the two-dispatcher indistinguishability shape)"
    - "Multiple [Trait(\"Phase\",N)] on one fact (xunit.v3) to re-tag an already-green proof into a new phase audit without duplicating the test"

key-files:
  created: []
  modified:
    - "tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs"
    - "tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs"

key-decisions:
  - "Duplicate_Reinject used the documented consumer-level double-Consume + RecoveryTestKit.CapturingSendProvider fallback (asserting Sent.Count == 2 on the SUCCESS re-injection path) rather than a harness double-publish over EmptyMux — EmptyMux would prove only the data-gone fault, not the success-effect reproduction the no-collapse invariant needs."
  - "ReinjectConsumer's presence gate is StringLengthAsync (STRLEN), not StringGetAsync, so the present-data substitute stubs StringLengthAsync -> 7L (non-zero) to drive the re-inject path."
  - "ExecutionId is deliberately excluded from the two-delivery equality asserts (regenerated per dispatch via NewId.NextGuid); StepId/ProcessorId/EntryId/queue-URI are pinned to prove no-lost-branch."

patterns-established:
  - "No-collapse fact = ONE dispatcher/consumer + double-Consume of the SAME instance + count == 2"
  - "Additive Phase-trait re-tag to fold an already-green proof into a later phase's audit"

requirements-completed: [RESIL-02, RESIL-03]

# Metrics
duration: 13min
completed: 2026-06-09
---

# Phase 47 Plan 02: At-Least-Once No-Collapse Facts + R2 Re-tag Summary

**Two duplicate-delivery no-collapse facts (StepCompleted via ONE RecordingDispatcher, KeeperReinject via ONE ReinjectConsumer + CapturingSendProvider) prove the v4 execution path reproduces effects on redelivery without dedup (RESIL-03), plus an additive Phase-47 trait makes the already-green data-gone proof (RESIL-02) discoverable for the audit.**

## Performance

- **Duration:** ~13 min
- **Started:** 2026-06-09T06:53:03Z
- **Completed:** 2026-06-09T07:05:58Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments
- `Duplicate_StepCompleted_reproduces_effect_no_collapse` (R3): ONE `StepCompletedConsumer` + ONE `RecordingDispatcher`, the SAME `StepCompleted` instance Consumed twice -> `Assert.Equal(2, dispatcher.Calls.Count)`, no throw, both deliveries advance the SAME matched successor (StepId/ProcessorId/EntryId pinned; ExecutionId excluded as regenerated). Phase-47-tagged, green.
- `Duplicate_Reinject_reproduces_effect_no_collapse` (R3): ONE `ReinjectConsumer` over a present-L2 substitute + `RecoveryTestKit.CapturingSendProvider`, the SAME `KeeperReinject` instance Consumed twice -> `Assert.Equal(2, send.Sent.Count)`, both re-inject an `EntryStepDispatch` to the SAME origin `queue:{ProcessorId:D}`, no throw. Phase-47-tagged, green.
- R2 re-tag (additive): added `[Trait("Phase", "47")]` alongside the existing `[Trait("Phase", "46")]` on `DataGone_reinject_faults_and_routes_to_dead_letter` — now discoverable under `--filter-trait "Phase=47"`, body untouched (still asserts `RecoveryDataGoneException` AND `Consumed.Any<ConsolidatedFault>()`).

## Task Commits

Each task was committed atomically:

1. **Task 1: StepCompleted duplicate-delivery no-collapse fact (R3)** - `29002c9` (test)
2. **Task 2: KeeperReinject duplicate no-collapse fact + R2 Phase-47 re-tag** - `f6139d7` (test)

_Note: both tasks are `tdd="true"`; each test was authored, run green under its method/trait filter, then committed in one step (no RED-first failing commit was warranted — these are verification facts asserting already-shipped no-collapse behavior, which the fail-fast rule explicitly anticipates passing immediately)._

## Files Created/Modified
- `tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs` - added `Duplicate_StepCompleted_reproduces_effect_no_collapse` (62 lines)
- `tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs` - added `Duplicate_Reinject_reproduces_effect_no_collapse` + `PresentMux()`/`ContextFor()` helpers (66 lines); added second `[Trait("Phase","47")]` on the data-gone fact

## No-Collapse Shape Confirmation (plan output requirement)
- Both new facts use the **ONE-dispatcher / double-Consume of the SAME instance** shape (asserting `Count == 2`), NOT the two-dispatcher indistinguishability shape of `Injected_StepCompleted_indistinguishable_from_direct`. Pitfall 5 / T-47-04 mitigated: each fact creates exactly one `RecordingDispatcher` / `CapturingSendProvider`, calls `Consume` exactly twice on one message variable.
- The R2 re-tag is **additive**: both `[Trait("Phase","46")]` and `[Trait("Phase","47")]` are present on the data-gone fact and its body is byte-unchanged. T-47-05 mitigated (trait added, not replaced).

## Decisions Made
- See `key-decisions` frontmatter. Principal call: the Reinject duplicate fact proves no-collapse on the **success** re-injection path (present L2, `Sent.Count == 2`) using the plan's documented consumer-level fallback, because the data-gone harness (EmptyMux) would only prove the fault route, not reproduction.

## Deviations from Plan

None - plan executed exactly as written. No production source change (verify-only, as the plan specified). No defect surfaced: the recovery path does NOT collapse/dedup a redelivery (T-47-06 check passed — both deliveries reproduced their effect).

## Issues Encountered
- `ReinjectConsumer` confirms presence via `StringLengthAsync` (STRLEN), not `StringGetAsync` — so `RecoveryTestKit.Db` (which stubs only StringGetAsync) would have driven the data-gone path. Resolved by a small inline `PresentMux()` stubbing `StringLengthAsync -> 7L`. Not a deviation — a seam detail resolved within the planned consumer-level approach.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- RESIL-03 no-collapse now has standing facts on BOTH the StepCompleted (orchestrator-consume) and EntryStepDispatch-family (Keeper-reinject) seams; RESIL-02 data-gone proof is Phase-47-discoverable. Ready for 47-03 (the `47-DLQ-AUDIT.md` traceability ledger + design-doc at-least-once amendment) to cite these.
- Full hermetic suite 530/530 green; `dotnet build SK_P.sln` 0/0.

## Self-Check: PASSED
- Files exist: TypedResultConsumerFacts.cs, RecoveryDeadLetterFacts.cs, 47-02-SUMMARY.md (all FOUND).
- Commits exist: 29002c9, f6139d7 (all FOUND).
- SUMMARY encoding: BOM=false, mojibake=0.
- Phase=47 trait facts: 6/6 green; DataGone method-filter: green; full hermetic suite 530/530; build 0/0.

---
*Phase: 47-dlq-consolidation-at-least-once-semantics*
*Completed: 2026-06-09*
