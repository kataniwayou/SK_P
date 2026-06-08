---
phase: 43-message-contracts-l2-key-reshape
plan: 01
subsystem: testing
tags: [xunit-v3, contract-tests, golden-tests, messaging-contracts, l2-keys, redis, nyquist, red-tests]

# Dependency graph
requires:
  - phase: 22-l2-root-restructure
    provides: "L2ProjectionKeys single-source-of-truth key builder + L2ProjectionKeysTests golden-pin template"
  - phase: 24-orchestrator-result-consume
    provides: "ExecutionResultContractTests + EntryStepDispatchTests contract-pin templates"
  - phase: 36-l2-probe-recovery
    provides: "ProbeOptions / ProbeOptionsBoundTests options-class + bound-test template"
provides:
  - "Wave-0 RED Nyquist proofs (SC-1..SC-4 + D-10) authored BEFORE the production symbols exist"
  - "StepResultContractTests: four Step* records, six ids, no H, IStepResult, Guid.Empty defaults, diagnostic placement, present-zero-GUID serialization"
  - "SourceStepTests: D-07 single-predicate proof"
  - "KeeperContractTests: five Keeper records + IKeeperRecoverable 4-tuple, StepId-not-on-marker"
  - "BackupOptionsBoundTests: D-10 default TtlDays==2"
  - "L2ProjectionKeysTests extended with CompositeBackup golden + Guid ExecutionData pin"
  - "Eight RETIRE-01/02 machinery test files removed (stale-green coverage cannot mask a partial reshape)"
affects: [43-02 (production symbols turn these GREEN), 43-05 (full-suite gate), Phase 44, Phase 46]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "RED-first golden/contract tests (Nyquist proof): pin the contract as executable tests BEFORE the symbols exist; build is deliberately red until the implementing plan lands"
    - "Pre-delete stale-green machinery tests in the same wave that authors their replacement, so a partial reshape surfaces as compile-red instead of hidden by passing legacy tests"

key-files:
  created:
    - tests/BaseApi.Tests/Contracts/StepResultContractTests.cs
    - tests/BaseApi.Tests/Contracts/SourceStepTests.cs
    - tests/BaseApi.Tests/Contracts/KeeperContractTests.cs
    - tests/BaseApi.Tests/Keeper/BackupOptionsBoundTests.cs
  modified:
    - tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs

key-decisions:
  - "No string-overload ExecutionData fact existed in L2ProjectionKeysTests (the file already used the Guid overload as of Phase 22), so Task 2's 'delete the string fact if present' was a no-op — only the two new facts were added"
  - "Used [Trait(\"Phase\",\"43\")] on all four new fixtures to match the repo trait convention (L2ProjectionKeysTests carries [Trait(\"Phase\",\"22\")] and was left unchanged)"

patterns-established:
  - "Nyquist RED proof: contract tests reference not-yet-created production symbols; the build failing ONLY on those symbols IS the gate"

requirements-completed: [MSG-01, MSG-02, MSG-03]

# Metrics
duration: 18min
completed: 2026-06-08
---

# Phase 43 Plan 01: Wave-0 Message-Contract RED Proofs + RETIRE Test Sweep Summary

**Authored the Wave-0 RED Nyquist proofs (four Step* records, the SourceStep predicate, five Keeper records, BackupOptions, and the two L2 key strings) BEFORE Plan 02 creates the symbols, and deleted the eight RETIRE-01/02 machinery test files so a partial reshape cannot hide behind stale-green tests.**

## Performance

- **Duration:** ~18 min
- **Started:** 2026-06-08T11:47:24Z (approx, plan execution start)
- **Completed:** 2026-06-08
- **Tasks:** 3
- **Files modified:** 5 created/edited + 8 deleted

## Accomplishments
- SC-1/SC-2 pinned: StepResultContractTests asserts H-absent on all four Step* records, the six-id set, IStepResult/IExecutionCorrelated layering, Guid.Empty EntryId defaults on StepFailed/StepCancelled/StepProcessing (no default asserted for StepCompleted's real-key EntryId), diagnostic-field placement (ErrorMessage on StepFailed only, CancellationMessage on StepCancelled only), and Pitfall-1 present-zero-GUID serialization.
- SC-2 pinned: SourceStepTests proves IsSource is true only for Guid.Empty.
- SC-3 pinned: KeeperContractTests proves five records implement IKeeperRecoverable exposing exactly the corr/wf/proc/exec 4-tuple (StepId NOT on the marker), StepId present as a record property on all five, ValidatedData on KeeperUpdate only, EntryId on KeeperReinject/KeeperDelete only.
- D-10 pinned: BackupOptionsBoundTests asserts default TtlDays==2.
- SC-4 pinned: L2ProjectionKeysTests now pins CompositeBackup => skp:{corr:D}:{wf:D}:{proc:D}:{exec:D} and the Guid ExecutionData overload => skp:data:{guid:D}.
- Eight RETIRE-01/02 machinery test files removed (git-staged as deletions).
- Build confirmed RED on exactly the 14 Plan-02 symbols, no collateral breakage.

## Task Commits

Each task was committed atomically:

1. **Task 1: Four contract/predicate/keeper/options golden tests (RED)** - `cb70765` (test)
2. **Task 2: Extend L2ProjectionKeysTests with CompositeBackup + Guid ExecutionData pin** - `b485923` (test)
3. **Task 3: Delete the eight RETIRE-01/02 machinery test files** - `1c50a05` (test)

**Plan metadata:** (final docs commit — this SUMMARY + STATE/ROADMAP)

## Files Created/Modified
- `tests/BaseApi.Tests/Contracts/StepResultContractTests.cs` - SC-1/SC-2 proofs for the four Step* records (created)
- `tests/BaseApi.Tests/Contracts/SourceStepTests.cs` - SC-2 single-predicate proof (created)
- `tests/BaseApi.Tests/Contracts/KeeperContractTests.cs` - SC-3 five-record + IKeeperRecoverable 4-tuple proof (created)
- `tests/BaseApi.Tests/Keeper/BackupOptionsBoundTests.cs` - D-10 default TtlDays==2 proof (created)
- `tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs` - +CompositeBackup golden, +Guid ExecutionData pin (modified)
- Deleted: `Processor/EffectFirstDedupFacts.cs`, `Processor/CheckAndDropFacts.cs`, `Processor/DispatchOutputWriteFacts.cs`, `Orchestrator/ResultCheckAndDropFacts.cs`, `Orchestrator/ManifestFanoutFacts.cs`, `Orchestrator/MergeCollapseFacts.cs`, `Orchestrator/IdempotentExactlyOnceE2ETests.cs`, `Contracts/HashHelperGoldenFacts.cs`

## Decisions Made
- L2ProjectionKeysTests already used `ExecutionData(Guid)` (Phase 22) and carried no string-literal `ExecutionData("...")` fact, so the plan's conditional "delete the string-overload fact if present" was a verified no-op; only the two new facts were appended. The existing `[Trait("Phase","22")]` on that fixture was left unchanged.
- The four new fixtures carry `[Trait("Phase","43")]` per the repo trait convention.

## Deviations from Plan

None - plan executed exactly as written. The conditional in Task 2 ("delete the string-overload fact if any exists") resolved to a no-op because no such fact was present; this is the plan's documented branch, not a deviation.

## Issues Encountered
None.

## RED-State Verification (the Nyquist gate)

`dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj` fails, and the distinct missing-symbol set is EXACTLY the Plan-02 symbols and nothing else:

- `IStepResult`, `StepCompleted`, `StepFailed`, `StepCancelled`, `StepProcessing` (CS0246)
- `SourceStep` (CS0103)
- `IKeeperRecoverable`, `KeeperUpdate`, `KeeperReinject`, `KeeperInject`, `KeeperDelete`, `KeeperCleanup` (CS0246)
- `BackupOptions` (CS0246)
- `L2ProjectionKeys.CompositeBackup` (CS0117)

Error files are confined to the four new test files + the extended L2ProjectionKeysTests. No collateral breakage from the eight deletions; the four keep-list files (ExecutionResultContractTests, EntryStepDispatchTests, ExecutionLogScopeKeyTests, L2ProjectionKeysTests) remain present. This RED state is expected and turns GREEN in Plan 02.

## Known Stubs
None. The RED build state is the intended Nyquist proof, not a stub — Plan 02 creates the referenced production symbols.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Plan 02 (production symbols) is the direct consumer: creating the four Step* records, SourceStep, the five Keeper records + IKeeperRecoverable, BackupOptions, and the CompositeBackup builder will flip every fact authored here from compile-red to GREEN.
- The 8 obsolete RETIRE tests are gone, so Plan 05's full-suite gate measures only the new shapes.

## Self-Check: PASSED

- All four created test files + the SUMMARY exist on disk.
- All three task commits exist in git history (cb70765, b485923, 1c50a05).
- SUMMARY is BOM-less UTF-8, zero mojibake.

---
*Phase: 43-message-contracts-l2-key-reshape*
*Completed: 2026-06-08*
