---
phase: 43-message-contracts-l2-key-reshape
plan: 05
subsystem: testing-phase-gate
tags: [phase-gate, full-suite-green, test-reshape, straight-through, guid-entryid, retire-h, retire-manifest, docs-reconciliation, wave-3, breaking-change]

# Dependency graph
requires:
  - phase: 43-message-contracts-l2-key-reshape
    plan: 02
    provides: "Reshaped Messaging.Contracts (no H, Guid EntryId, four Step* records, SourceStep, five Keeper records, CompositeBackup); ExecutionResult + MessageIdentity deleted"
  - phase: 43-message-contracts-l2-key-reshape
    plan: 03
    provides: "Straight-through Orchestrator/BaseProcessor consumers (ResultConsumer : IConsumer<StepCompleted> no Redis, IStepDispatcher.DispatchAsync 8-arg Guid entryId, EntryStepDispatchConsumer one-result=one-StepCompleted)"
  - phase: 43-message-contracts-l2-key-reshape
    plan: 04
    provides: "Keeper dark-path retarget (FaultExecutionResultConsumer -> Fault<StepCompleted>, L2ProbeRecovery.RunAsync Guid entryId, KeeperRecoveryHandler localKey from CompositeBackup)"
provides:
  - "Full hermetic test suite GREEN against the reshaped contracts — the explicit PHASE GATE (480 passed, 0 failed, RealStack excluded)"
  - "Surviving RETIRE-bucket tests actioned: ExecutionResultContractTests deleted, EntryStepDispatchTests reshaped (no H, Guid EntryId)"
  - "Shared harness (OrchestratorTestStubs, DispatchTestKit) constructs the new Step* shapes; no NoopRedis, no ExecutionResult"
  - "~18 consumer/scope test files reshaped to the straight-through contracts (one result = one Step* record, Guid EntryId, no H, no manifest, no flag[H] dedup)"
  - "Three obsolete RETIRE-01/02 E2E machinery tests deleted (FaultRecoverySpikeE2ETests, KeeperFaultIntakeE2ETests, KeeperRecoveryE2ETests)"
  - "ROADMAP Phase 43/48 descriptions + RETIRE-01/02 REQUIREMENTS traceability rows reconciled to the D-01/D-02 coupling"
affects: [Phase 44, Phase 46, Phase 48 (RETIRE-03 + remnant sweep)]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "MTP filter syntax: the hermetic suite excludes RealStack via `dotnet test -- --filter-not-trait Category=RealStack` (the VSTest `--filter`/VSTestTestCaseFilter is IGNORED under Microsoft.Testing.Platform — warning MTP0001; a bare `dotnet test` runs RealStack E2E too)"
    - "Straight-through test posture: a result-emitting consumer test asserts ONE typed Step* record per result (no manifest collapse), Guid EntryId, no .Outcome/.H wire field — IsType<StepCompleted/StepFailed/StepCancelled> replaces the old StepOutcome enum assertion"
    - "Capturing a (object)result Send: the consumer sends each Step* via endpoint.Send((object)result) so MassTransit routes the runtime type; the test CapturingSendProvider stubs the Send(object) overload and casts back to IStepResult"
    - "Cap-counter key parity: with H removed, the recover-attempt counter is keyed off CompositeBackup(corr,wf,proc,exec) — a cap test that shared a fixed-H counter must now pin all four ids so the derived localKey is stable across drives"

key-files:
  created: []
  modified:
    - tests/BaseApi.Tests/Orchestrator/EntryStepDispatchTests.cs
    - tests/BaseApi.Tests/Orchestrator/OrchestratorTestStubs.cs
    - tests/BaseApi.Tests/Orchestrator/ResultConsumeTests.cs
    - tests/BaseApi.Tests/Orchestrator/ResultAckTests.cs
    - tests/BaseApi.Tests/Orchestrator/FireDispatchTests.cs
    - tests/BaseApi.Tests/Orchestrator/WorkflowFireJobScopeTests.cs
    - tests/BaseApi.Tests/Orchestrator/StopConsumerLifecycleTests.cs
    - tests/BaseApi.Tests/Console/ConsoleExecutionScopeFilterTests.cs
    - tests/BaseApi.Tests/Processor/DispatchTestKit.cs
    - tests/BaseApi.Tests/Processor/DispatchInputFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchInvokeFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchCorrelationFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchResultSendFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchAckSemanticsFacts.cs
    - tests/BaseApi.Tests/Processor/EntryStepDispatchScopeTests.cs
    - tests/BaseApi.Tests/Processor/EntryStepDispatchRuntimeScopeTests.cs
    - tests/BaseApi.Tests/Keeper/KeeperFaultConsumerScopeTests.cs
    - tests/BaseApi.Tests/Keeper/KeeperMetricsFacts.cs
    - tests/BaseApi.Tests/Keeper/KeeperProbeLoopTests.cs
    - tests/BaseApi.Tests/Keeper/KeeperPausePublishTests.cs
    - tests/BaseApi.Tests/Keeper/KeeperRecoverCapTests.cs
    - .planning/ROADMAP.md
    - .planning/REQUIREMENTS.md
  deleted:
    - tests/BaseApi.Tests/Orchestrator/ExecutionResultContractTests.cs
    - tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs
    - tests/BaseApi.Tests/Orchestrator/KeeperFaultIntakeE2ETests.cs
    - tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs

key-decisions:
  - "DELETED ExecutionResultContractTests (not split): Plan-01 StepResultContractTests fully covers the four Step* records (no-H, six ids, IStepResult, Guid.Empty defaults, diagnostic placement, present-zero-GUID serialization). The only uncovered assertions referenced the deleted ExecutionResult/StepOutcome-on-the-message shape, which no longer exists. Splitting would duplicate StepResultContractTests; deletion is the cleaner plan-sanctioned branch."
  - "DELETED three RETIRE-01/02 E2E machinery tests (FaultRecoverySpikeE2ETests, KeeperFaultIntakeE2ETests, KeeperRecoveryE2ETests) — under-inventoried by 43-RESEARCH §Test fallout. They validate the deterministic-H re-injection + flag[H] dedup-collapse + content-addressed manifest flow that RETIRE-01/02 REMOVE; they are the E2E siblings of the unit-level IdempotentExactlyOnceE2ETests the RESEARCH already classified DELETE (the file docs literally call them 'CLONED from IdempotentExactlyOnceE2ETests'). They cannot be reshaped to the new contracts without re-designing the whole scenario (the H/manifest/collapse behavior is gone). Treated as a Rule-1/3 in-scope deviation, same category as Plan-01's authorized deletions."
  - "The hermetic phase gate is `dotnet test -- --filter-not-trait Category=RealStack` (MTP syntax). A first run with the VSTest-style `--filter` showed 3 failures because MTP IGNORES that flag (warning MTP0001) and ran the live RealStack E2E tests without a compose stack; the correct MTP `--filter-not-trait` run is 480/480 GREEN."
  - "ConsoleExecutionScopeFilterTests.ExecProbeMessage dropped its `string H => \"\"` member and retyped EntryId to Guid — IExecutionCorrelated no longer declares H (CS0738 was the surfacing error), and the EntryId scope value is now Guid.ToString() with Guid.Empty skipped via SourceStep.IsSource."
  - "KeeperRecoverCapTests pins a fixed four-tuple (CapCorr/CapWf/CapProc/CapExec) and computes CapH = CompositeBackup(those) so every drive shares the recover-attempt counter key — the cap counter is now keyed off the CompositeBackup-derived localKey (D-14), not the removed wire H."

patterns-established:
  - "When a milestone removes a wire field that a counter/identity key was derived from, a test that pinned a fixed value for that field must pin the SURVIVING inputs of the replacement key-derivation instead (here: pin the four-tuple so CompositeBackup is stable)"

requirements-completed: [MSG-01, MSG-02, MSG-03]

# Metrics
duration: ~95min
completed: 2026-06-08
---

# Phase 43 Plan 05: Full-Suite-Green Phase Gate + Docs Reconciliation Summary

**Closed the phase to FULL-SUITE-GREEN against the reshaped contracts: deleted ExecutionResultContractTests + three obsolete RETIRE-01/02 E2E machinery tests, reshaped EntryStepDispatchTests (no H, Guid EntryId) + the two shared harnesses + ~18 consumer/scope test files to the straight-through model (one result = one typed Step* record, Guid EntryId, no H/manifest/flag[H]-dedup), then ran the explicit phase gate — the hermetic suite is 480 passed / 0 failed and Release builds 0-warning. Finally reconciled ROADMAP Phase 43/48 + the RETIRE-01/02 REQUIREMENTS traceability rows to the D-01/D-02 coupling (docs only).**

## Performance

- **Duration:** ~95 min (large blast radius — the test-side fallout was ~24 files, far larger than the 4 the plan's `files_modified` listed)
- **Completed:** 2026-06-08
- **Tasks:** 3
- **Files:** 23 modified + 4 deleted

## Accomplishments

- **Task 1:** Deleted `ExecutionResultContractTests.cs` (fully covered by Plan-01 `StepResultContractTests`); reshaped `EntryStepDispatchTests` to assert `GetProperty("H") == null` + `EntryId` is a `Guid` defaulting to `Guid.Empty` (D-04/D-05). Commit `b94c86d`.
- **Task 2 (the gate):** Reshaped the shared harness (`OrchestratorTestStubs` dropped `NoopRedis`; `DispatchTestKit` Guid entryId + `IStepResult` capture + typed-record result harness) and ~18 consumer/scope test files to the straight-through contracts, deleted three obsolete E2E machinery tests, and ran the FULL hermetic suite as the explicit PHASE GATE: **480 passed, 0 failed** (`--filter-not-trait Category=RealStack`). `dotnet build SK_P.sln -c Release` succeeds **0-warning, 0-error**. Commit `4995a42`.
- **Task 3 (docs):** Reconciled `REQUIREMENTS.md` (RETIRE-01/02 → Phase 43 coupled per D-01; footer clarified, count unchanged at 31) and `ROADMAP.md` (Phase 43 table row + Goal + Requirements no longer reads as "just vocabulary" — notes the coupled RETIRE-01/02 landing; Phase 48 shrinks to RETIRE-03 + remnant sweep, SC-1/2 annotated landed-in-43). Build order (43→…→49) and phase numbering untouched. Commit `0734e24`.

## Task Commits

1. **Task 1: delete ExecutionResultContractTests; reshape EntryStepDispatchTests** — `b94c86d` (test)
2. **Task 2: reshape shared harness + consumer tests; full hermetic suite GREEN** — `4995a42` (test)
3. **Task 3: docs reconciliation — ROADMAP 43/48 + RETIRE-01/02 traceability** — `0734e24` (docs)

## Deviations from Plan

### [Scope — Rule 1/3] The test-side blast radius was ~24 files, not the 4 in `files_modified`

The plan's `files_modified` listed only `ExecutionResultContractTests`, `EntryStepDispatchTests`, `OrchestratorTestStubs`, `DispatchTestKit` (+ 2 docs). In reality ~20 additional sibling consumer/scope test files still referenced the OLD contracts (`ExecutionResult`, `Messaging.Contracts.Hashing`, `.H`, string `EntryId`, `StepOutcome` on the message, `ResultConsumer(...,redis,...)`, 7-arg string-entryId `DispatchAsync`, 3-arg `StepDispatcher`). This is exactly the "compiles-but-red — actually doesn't even compile" failure mode the plan's gate exists to close. All were reshaped to the straight-through model (one result = one typed Step* record, Guid EntryId, no H/manifest/dedup). Tracked as a Rule-1/3 in-scope fix (the gate is non-negotiable; the plan under-inventoried, mirroring 43-RESEARCH §Test fallout admitting "the test-side fallout is larger").

### [Scope — Rule 1/3] Deleted three obsolete RETIRE-01/02 E2E machinery tests

`FaultRecoverySpikeE2ETests`, `KeeperFaultIntakeE2ETests`, `KeeperRecoveryE2ETests` were NOT in the RESEARCH §Test fallout DELETE/RESHAPE/VERIFY-ONLY buckets (an inventory gap). They validate the deterministic-`H` re-injection + `flag[H]` dedup-collapse + content-addressed manifest flow that RETIRE-01/02 remove — the E2E siblings of the unit-level `IdempotentExactlyOnceE2ETests` the RESEARCH already classified DELETE (and which Plan 01 deleted). The file docs literally describe them as "CLONED from IdempotentExactlyOnceE2ETests." They cannot be reshaped to the new contracts (the scenario they prove no longer exists), so they were deleted — same category and rationale as the already-authorized Plan-01 deletions. Documented here as the load-bearing scoping deviation.

### [Plan-sanctioned choice, not a deviation] Deleted ExecutionResultContractTests rather than splitting it

Task 1 offered EITHER split-into-four OR delete (cleaner per 43-PATTERNS). Chose delete: `StepResultContractTests` already covers all four records' no-H/six-id/IStepResult/Guid.Empty-default/diagnostic/round-trip assertions; the only non-duplicated assertions referenced the deleted `ExecutionResult`/on-the-message `StepOutcome` shape.

## Authentication Gates

None.

## Issues Encountered

**MTP ignores the VSTest `--filter` (warning MTP0001).** A first gate run with `--filter "Category!=RealStack"` reported 3 failures — the MTP runner ignored the flag and executed the live RealStack E2E tests (`KeeperRecovery_*`, etc.) without a compose stack. The correct Microsoft.Testing.Platform syntax is `dotnet test -- --filter-not-trait "Category=RealStack"`, which excludes the RealStack bucket and yields **480 passed, 0 failed**. The hermetic suite is fully green; the only "failures" were infra-dependent E2E tests that are out of scope for the hermetic phase gate (they are operator-gated against the live compose stack per the Phase-39 precedent).

## Known Stubs

None. The straight-through test posture is the deliberate D-03 milestone shape (single result = one Step* record, no dedup, no manifest), the final shape for this phase — not a placeholder. The reactive Keeper recovery path stays DARK-but-compiling per D-14 (its tests assert the present-and-dormant shape, not future behavior).

## Threat Flags

None. T-43-11 (gate integrity) is honored: the gate runs the unmodified full hermetic suite; no assertion was weakened to force green — failing tests were reshaped to the new contracts or (for the obsolete machinery proofs) deleted alongside the machinery they tested, and the Plan-01 contract/key/predicate/options proofs + ExecutionLogScopeKeyTests remain as independent coverage. T-43-12 (doc drift) is the accepted docs-only reconciliation. No new network endpoint, auth path, file-access pattern, or schema change at a trust boundary was introduced.

## Self-Check: PASSED

- `ExecutionResultContractTests.cs` + the three E2E files are gone from disk (confirmed via `git rm` + `git status`).
- `EntryStepDispatchTests` + the two shared harnesses + ~18 consumer/scope files exist and the test project builds 0/0.
- All three task commits exist in git history (b94c86d, 4995a42, 0734e24).
- Phase gate: `dotnet test -- --filter-not-trait Category=RealStack` → 480 passed, 0 failed. `dotnet build SK_P.sln -c Release` → Build succeeded, 0 warnings.

---
*Phase: 43-message-contracts-l2-key-reshape*
*Completed: 2026-06-08*
