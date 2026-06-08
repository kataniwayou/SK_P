---
phase: 43-message-contracts-l2-key-reshape
plan: 02
subsystem: messaging-contracts
tags: [messaging-contracts, wire-contracts, l2-keys, keeper-states, source-sentinel, options-binding, wave-1, breaking-change]

# Dependency graph
requires:
  - phase: 43-message-contracts-l2-key-reshape
    plan: 01
    provides: "Wave-0 RED contract/key/predicate/options tests (StepResultContractTests, SourceStepTests, KeeperContractTests, BackupOptionsBoundTests, L2ProjectionKeysTests CompositeBackup+Guid pins) that this plan turns GREEN"
provides:
  - "IExecutionCorrelated + EntryStepDispatch reshaped: six ids, no H, Guid EntryId (D-04/D-05)"
  - "IStepResult marker + four typed Step* records (StepCompleted/StepFailed/StepCancelled/StepProcessing) with D-06a/D-06b id-set + diagnostic placement"
  - "SourceStep.IsSource single source-step sentinel predicate (D-07)"
  - "IKeeperRecoverable partition 4-tuple marker + five Keeper-state records (Update/Reinject/Inject/Delete/Cleanup) (D-11/D-12)"
  - "L2ProjectionKeys.ExecutionData(Guid) sole overload + CompositeBackup builder; ExecutionData(string)/Flag/MessageIdentity/ExecutionResult deleted (D-08/D-09)"
  - "KeeperQueues.Recovery const (D-13)"
  - "BackupOptions { TtlDays = 2 } bound from Keeper appsettings (D-10)"
affects: [43-03 (consumers adapt to new contracts), 43-04 (dark-path rebind), 43-05 (full-suite GREEN gate), Phase 44, Phase 46]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Single-source sentinel predicate (SourceStep.IsSource) — every Guid.Empty source-step branch routes through ONE helper, never inline == Guid.Empty"
    - "Marker-interface 4-tuple declared directly (NOT via base-interface inheritance) so reflection-based contract tests using interface GetProperties() surface the full member set"
    - "Wave-boundary compile state: Messaging.Contracts + new symbols compile clean; consumer projects (Keeper/Orchestrator/BaseProcessor.Core) + their sibling tests stay red on the OLD contracts until Plans 03/04/05 — the intended Nyquist-GREEN-pending boundary"

key-files:
  created:
    - src/Messaging.Contracts/IStepResult.cs
    - src/Messaging.Contracts/StepCompleted.cs
    - src/Messaging.Contracts/StepFailed.cs
    - src/Messaging.Contracts/StepCancelled.cs
    - src/Messaging.Contracts/StepProcessing.cs
    - src/Messaging.Contracts/SourceStep.cs
    - src/Messaging.Contracts/IKeeperRecoverable.cs
    - src/Messaging.Contracts/KeeperUpdate.cs
    - src/Messaging.Contracts/KeeperReinject.cs
    - src/Messaging.Contracts/KeeperInject.cs
    - src/Messaging.Contracts/KeeperDelete.cs
    - src/Messaging.Contracts/KeeperCleanup.cs
    - src/Keeper/BackupOptions.cs
  modified:
    - src/Messaging.Contracts/IExecutionCorrelated.cs
    - src/Messaging.Contracts/EntryStepDispatch.cs
    - src/Messaging.Contracts/ExecutionLogScope.cs
    - src/Messaging.Contracts/Projections/L2ProjectionKeys.cs
    - src/Messaging.Contracts/KeeperQueues.cs
    - src/Keeper/Program.cs
    - src/Keeper/appsettings.json
  deleted:
    - src/Messaging.Contracts/ExecutionResult.cs
    - src/Messaging.Contracts/Hashing/MessageIdentity.cs

key-decisions:
  - "IKeeperRecoverable declares the 4-tuple DIRECTLY (dropped the leaning-yes : ICorrelated): C# interface reflection GetProperties() does NOT surface base-interface members, so KeeperContractTests' Assert.Contains(\"CorrelationId\", typeof(IKeeperRecoverable).GetProperties()) required CorrelationId to be declared on the marker itself"
  - "ExecutionLogScope.cs (a Task-3 file) was edited in the Task-1 commit because it lives in the Messaging.Contracts assembly and the EntryId string->Guid change made the whole assembly non-compiling — a Rule-3 blocking-issue fix bundled with the change that necessitated it"
  - "Task verify commands run with -p:BuildProjectReferences=false and verification is by isolated Messaging.Contracts/Keeper build + binary reflection probe: the shared BaseApi.Tests assembly cannot LINK because sibling consumer-test files still reference the OLD contracts (reshaped in Plans 03/04/05), so the xUnit runner cannot execute — the contract-test SOURCE compiles clean against the new symbols (zero errors in any Plan-02 contract test file)"

patterns-established:
  - "Reflection contract tests on marker interfaces must declare (not inherit) the pinned member set"

requirements-completed: [MSG-01, MSG-02, MSG-03]

# Metrics
duration: 7min
completed: 2026-06-08
---

# Phase 43 Plan 02: Message-Contract & L2-Key Reshape (production symbols) Summary

**Reshaped the shared wire vocabulary in Messaging.Contracts — dropped H and made entryId a Guid; replaced ExecutionResult with four typed Step* records; added the SourceStep sentinel, IKeeperRecoverable + five Keeper-state records, the ExecutionData(Guid)/CompositeBackup key builders, KeeperQueues.Recovery, and the BackupOptions class bound in Keeper; deleted MessageIdentity + ExecutionResult — turning every Plan-01 contract/key/predicate/options proof GREEN at the symbol level.**

## Performance

- **Duration:** ~7 min
- **Started:** 2026-06-08T11:55:27Z
- **Completed:** 2026-06-08T12:02:42Z
- **Tasks:** 3
- **Files:** 13 created + 7 modified + 2 deleted

## Accomplishments

- **SC-1/SC-2 (Task 1):** `IExecutionCorrelated` + `EntryStepDispatch` now carry the six ids, no `H`, `Guid EntryId`. Four typed records `StepCompleted`/`StepFailed`/`StepCancelled`/`StepProcessing`, each `: IStepResult : IExecutionCorrelated`. `StepCompleted.EntryId` is the real data key (no default); the other three hard-default `EntryId = Guid.Empty`. `ErrorMessage` on `StepFailed` only, `CancellationMessage` on `StepCancelled` only, neither on Completed/Processing. `SourceStep.IsSource` is the single sentinel predicate. `ExecutionResult.cs` deleted.
- **SC-3/SC-4 (Task 2):** `IKeeperRecoverable` exposes exactly the corr/wf/proc/exec partition 4-tuple (StepId NOT on the marker); five `Keeper*` records each implement it, all carry `StepId`, with `ValidatedData` on `KeeperUpdate`, `EntryId` on `KeeperReinject`/`KeeperDelete`. `L2ProjectionKeys.ExecutionData(Guid)` is the sole data overload; `CompositeBackup(corr,wf,proc,exec)` added; `ExecutionData(string)`/`Flag(string)` removed. `KeeperQueues.Recovery = "keeper-recovery"` added (FaultRecovery + DeadLetter kept). `MessageIdentity.cs` deleted.
- **D-10 (Task 3):** `ExecutionLogScope` EntryId skip routes through `SourceStep.IsSource` (Guid skip + `.ToString()`); five const keys unchanged. `BackupOptions { TtlDays = 2 }` created and bound via `Configure<BackupOptions>(GetSection("Backup"))` with a `"Backup"` appsettings section.
- **Binary-level GREEN proof:** a reflection probe against the built `Messaging.Contracts.dll` confirmed every assertion the Plan-01 tests pin — six ids + IStepResult + no-H + Guid.Empty default + diagnostic placement on all four Step* records, IsSource(Empty)==true/IsSource(new)==false, and the five Keeper records' IKeeperRecoverable/StepId/ValidatedData/EntryId shape.

## Task Commits

1. **Task 1: reshape interfaces + four Step* records + SourceStep** - `79f4fe3` (feat)
2. **Task 2: IKeeperRecoverable + five Keeper records + L2 key builders + KeeperQueues.Recovery + delete MessageIdentity** - `85d23d1` (feat)
3. **Task 2 fix: declare the 4-tuple directly on IKeeperRecoverable** - `094a05b` (fix)
4. **Task 3: BackupOptions + Keeper binding + appsettings** - `f9e10b2` (feat)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] IKeeperRecoverable inheriting ICorrelated hid CorrelationId from reflection**
- **Found during:** Task 2 (post-commit verification)
- **Issue:** PATTERNS guidance leaned toward `IKeeperRecoverable : ICorrelated` (inherit CorrelationId, don't redeclare). But `KeeperContractTests.IKeeperRecoverable_exposes_exactly_the_partition_four_tuple_and_not_StepId` asserts `Assert.Contains("CorrelationId", typeof(IKeeperRecoverable).GetProperties()...)`. C# interface `GetProperties()` does NOT surface base-interface members, so the marker reflected only WorkflowId/ProcessorId/ExecutionId — the test would have failed on CorrelationId.
- **Fix:** Declared all four members (CorrelationId/WorkflowId/ProcessorId/ExecutionId) directly on `IKeeperRecoverable`, dropped `: ICorrelated`. Verified via a binary reflection probe that `GetProperties()` now returns exactly the 4-tuple with StepId still absent.
- **Files modified:** src/Messaging.Contracts/IKeeperRecoverable.cs
- **Commit:** `094a05b`

### Task-ordering adjustment (Rule 3 — blocking issue)

`ExecutionLogScope.cs` is listed under Task 3 but lives in the `Messaging.Contracts` assembly. Changing `IExecutionCorrelated.EntryId` from `string` to `Guid` (Task 1) made `ExecutionLogScope.BuildState`'s `string.IsNullOrEmpty(ec.EntryId)` a compile error (CS1503), so the assembly could not build at all. The `ExecutionLogScope` -> `SourceStep.IsSource` edit was therefore applied and committed in **Task 1** (the change that necessitated it) rather than Task 3. The Task-3 commit covers the remaining BackupOptions + binding + appsettings work. Net deliverable is identical to the plan; only the commit grouping shifted by one file.

## Authentication Gates

None.

## Issues Encountered

**Verification mechanism at the wave boundary:** The plan's per-task `dotnet test --filter` commands cannot reach the xUnit runner because the shared `BaseApi.Tests` assembly will not LINK — sibling consumer-test files (KeeperRecoveryE2ETests, FaultRecoverySpikeE2ETests, ResultConsumeTests, the processor Dispatch* facts, ConsoleExecutionScopeFilterTests, etc.) still reference the OLD contracts (`ExecutionResult`, `Messaging.Contracts.Hashing`, `string EntryId`, `H`). These are the consumer tests reshaped in Plans 03/04/05. This is the intended Wave-1 boundary state (the plan's own `<verification>` says "solution-wide build remains red ONLY on the consumer files reshaped in Plan 03/04"). Verification was performed instead by (a) an isolated `dotnet build src/Messaging.Contracts` — 0/0; (b) confirming ZERO compile errors originate in any Plan-02 contract test file (StepResultContractTests/SourceStepTests/KeeperContractTests/L2ProjectionKeysTests/BackupOptionsBoundTests) — the contract-test source compiles clean against the new symbols; (c) a binary reflection probe against `Messaging.Contracts.dll` proving every assertion those tests pin. The tests will execute GREEN once Plan 05's full-suite gate runs after the consumer reshape.

## Known Stubs

None. All symbols are fully shaped with their final wire contract; no placeholder or empty-data flow introduced. The wave-boundary red build is the intended Nyquist-pending state, not a stub.

## Self-Check: PASSED

- All 13 created files + the SUMMARY exist on disk.
- Both deleted files (ExecutionResult.cs, Hashing/MessageIdentity.cs) are gone.
- All four commits exist in git history (79f4fe3, 85d23d1, 094a05b, f9e10b2).
- `git diff --diff-filter=D` per commit showed only the two intended deletions, no accidental file loss.

---
*Phase: 43-message-contracts-l2-key-reshape*
*Completed: 2026-06-08*
