---
phase: 48-v3-x-teardown
plan: 01
subsystem: infra
tags: [keeper, masstransit, redis, recovery, teardown, dead-code-removal]

# Dependency graph
requires:
  - phase: 46-keeper-5-state-recovery-orchestrator-per-item-consume
    provides: the v4 5-state keeper-recovery consumers + RecoveryConsumerBase that the reactive path now defers to entirely
  - phase: 45-keeper-bit-health-gate-global-pause-resume
    provides: BitHealthLoop + L2HealthGate (the live consumer of L2ProbeRecovery.ProbeOnceAsync — the surviving member)
  - phase: 43-message-contracts-l2-key-reshape
    provides: the no-H reshaped contracts + the 5 Keeper-state contracts that made the reactive Fault<T> path dark
provides:
  - The v3.x reactive Fault<EntryStepDispatch>/Fault<ExecutionResult> recovery path is deleted from src/Keeper/
  - KeeperRecoveryHandler + the orphaned KeeperMetrics meter are deleted
  - L2ProbeRecovery reduced to the v4 BIT-probe helper (ProbeOnceAsync only, single IConnectionMultiplexer ctor)
  - The keeper-dlq (DeadLetter) + keeper-fault-recovery (FaultRecovery) queue consts removed; KeeperQueues.Recovery is the sole surviving Keeper queue
  - Dead config (ProbeOptions.RecoverAttemptCap + appsettings) + the KeeperRecoverAttempts L2 key builder removed
  - Solution builds 0-warning in Debug on the v4 path alone (SC-3 + SC-4 build-half)
affects: [48-02, 48-03, keeper, recovery, validation]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Atomic delete-wave: source deletes + their same-compilation-unit dependent test deletes/edits land in one build-green change (no intermediate non-compiling state)"

key-files:
  created: []
  modified:
    - src/Keeper/Program.cs
    - src/Keeper/Recovery/L2ProbeRecovery.cs
    - src/Keeper/ProbeOptions.cs
    - src/Keeper/appsettings.json
    - src/Messaging.Contracts/KeeperQueues.cs
    - src/Messaging.Contracts/Projections/L2ProjectionKeys.cs
    - tests/BaseApi.Tests/Keeper/KeeperHostBootFixture.cs
    - tests/BaseApi.Tests/Keeper/KeeperDependencyFirewallTests.cs
    - tests/BaseApi.Tests/Keeper/Health/BitHealthLoopTests.cs
    - tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs
    - tests/BaseApi.Tests/BaseApi.Tests.csproj

key-decisions:
  - "Open-Q1 fixture decision: KeeperHostBootFixture uses the MINIMAL form — drop the reactive registrations, register NO consumers in the messaging seam (KeeperHostBootTests only asserts IBusControl resolves, which the no-consumers form satisfies)."
  - "KeeperMetrics whole-file delete (D-02 deviation): there is no v4 meter usage to preserve, so both KeeperMetrics and KeeperMetricTags were deleted, not retained."
  - "RecoveryOptions.cs + BackupOptions.cs + the appsettings Recovery/Backup sections left untouched (confirmed v4-shared by the 5-state recovery consumers)."

patterns-established:
  - "Same-compilation-unit teardown: deleting a type that a test references requires editing/deleting that test in the SAME commit, since tests/BaseApi.Tests is one compilation unit."

requirements-completed: [RETIRE-03]

# Metrics
duration: 18min
completed: 2026-06-09
---

# Phase 48 Plan 01: v3.x Reactive Recovery Teardown Summary

**Deleted the v3.x reactive Fault<T> Keeper recovery path (consumers, KeeperRecoveryHandler, the orphaned KeeperMetrics meter), reduced L2ProbeRecovery to the v4 BIT-probe helper, removed the keeper-dlq/keeper-fault-recovery queue consts + dead config/key remnants, and swept every dependent test — leaving the solution 0-warning green on the v4 recovery path alone.**

## Performance

- **Duration:** ~18 min
- **Started:** 2026-06-09
- **Completed:** 2026-06-09
- **Tasks:** 2
- **Files modified:** 11 modified + 12 deleted

## Accomplishments
- Removed the entire reactive `Fault<EntryStepDispatch>`/`Fault<ExecutionResult>` recovery surface (4 consumer/definition files), `KeeperRecoveryHandler`, and the now-orphaned `KeeperMetrics` + `KeeperMetricTags`.
- Reduced `L2ProbeRecovery` to `ProbeOnceAsync` only with a single `IConnectionMultiplexer` ctor param (dropped `RunAsync`, `ProbeOutcome`, and the `IOptions<ProbeOptions>`/`KeeperMetrics` ctor deps) — the v4 `BitHealthLoop` is its sole live caller.
- Unwired `Program.cs`: removed the reactive consumer registration, the `KeeperRecoveryHandler` + `KeeperMetrics` + meter-provider wiring; kept the five `keeper-recovery` consumers + `BitHealthLoop` + `L2HealthGate` verbatim.
- Removed the `keeper-dlq` (`DeadLetter`) + `keeper-fault-recovery` (`FaultRecovery`) consts, `ProbeOptions.RecoverAttemptCap` (source + appsettings), and the `KeeperRecoverAttempts` L2 key builder.
- Deleted/edited every test referencing the retired symbols; solution builds 0-warning in Debug; the hermetic suite (RealStack-excluded) is 503/503 green.

## Task Commits

Each task was committed atomically:

1. **Task 1: Delete the reactive source + orphaned metrics + their dependent tests** - `5f0e210` (refactor)
2. **Task 2: Remove the now-unreferenced consts + dead config/key, with their last test referencer** - `384bde5` (refactor)

## Files Created/Modified

**Deleted (Task 1):**
- `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs` + `...Definition.cs`
- `src/Keeper/Consumers/FaultExecutionResultConsumer.cs` + `...Definition.cs`
- `src/Keeper/Recovery/KeeperRecoveryHandler.cs`
- `src/Keeper/Observability/KeeperMetrics.cs` (incl. `KeeperMetricTags`)
- `tests/BaseApi.Tests/Keeper/{KeeperFaultConsumerScopeTests,KeeperRecoverCapTests,KeeperRoundRobinTests,KeeperPausePublishTests,KeeperProbeLoopTests,KeeperMetricsFacts}.cs`

**Modified (Task 1):**
- `src/Keeper/Recovery/L2ProbeRecovery.cs` - reduced to `ProbeOnceAsync` only, single-param ctor
- `src/Keeper/Program.cs` - unwired reactive surface; kept the v4 keep-set
- `tests/BaseApi.Tests/Keeper/KeeperHostBootFixture.cs` - minimal form (no reactive registrations, no consumers)
- `tests/BaseApi.Tests/Keeper/KeeperDependencyFirewallTests.cs` - re-anchored to `BitHealthLoop`
- `tests/BaseApi.Tests/Keeper/Health/BitHealthLoopTests.cs` - adopted single-param ctor, dropped `KeeperMetrics` (deviation; see below)
- `tests/BaseApi.Tests/BaseApi.Tests.csproj` - refreshed the stale Keeper comment

**Modified (Task 2):**
- `src/Messaging.Contracts/KeeperQueues.cs` - removed `FaultRecovery` + `DeadLetter`; `Recovery` survives
- `src/Keeper/ProbeOptions.cs` + `src/Keeper/appsettings.json` - removed `RecoverAttemptCap`
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` - removed `KeeperRecoverAttempts`
- `tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs` - dropped the two `keeper-dlq` assertions + scrubbed the DLQ-2 doc-comment

## Decisions Made
- **Open-Q1 (fixture):** `KeeperHostBootFixture` uses the minimal form — drop the reactive registrations and register NO consumers in the `AddBaseConsoleMessaging` seam. `KeeperHostBootTests` only asserts `IBusControl` resolves, which the no-consumers form satisfies; this keeps the fixture edit minimal/low-risk for a deletion phase.
- **`KeeperMetrics` whole-file delete** (the planned deviation from D-02's literal "keep the v4 meter" wording): grep confirmed no v4 meter usage to preserve, so both `KeeperMetrics` and `KeeperMetricTags` were deleted.
- **`RecoveryOptions.cs` / `BackupOptions.cs` + their appsettings sections left untouched** — confirmed v4-shared by the 5-state recovery consumers (`PartitionCount`=5 grep, `GateWaitSeconds` present).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Updated `BitHealthLoopTests.cs` to the reduced `L2ProbeRecovery` ctor + removed `KeeperMetrics`**
- **Found during:** Task 1 (reducing `L2ProbeRecovery` + deleting `KeeperMetrics`)
- **Issue:** `tests/BaseApi.Tests/Keeper/Health/BitHealthLoopTests.cs` (NOT in the plan's test inventory) constructed `new L2ProbeRecovery(redis.Multiplexer, ZeroDelay(), NewMetrics())` (the 3-param ctor) and a `NewMetrics()` helper using the deleted `KeeperMetrics`. As part of the single `BaseApi.Tests` compilation unit, this would have broken the build the moment Task 1's ctor reduction + `KeeperMetrics` delete landed — exactly the same-compilation-unit trap the plan's §7 warns about, but for a file the plan's inventory missed.
- **Fix:** Removed the `KeeperMetrics` import + `NewMetrics()` helper and the `System.Diagnostics.Metrics`/`Microsoft.Extensions.DependencyInjection`/`Keeper.Observability` usings; updated all six `new L2ProbeRecovery(...)` call sites to the single-param ctor. The tests still validate `BitHealthLoop` edge-trigger behavior (the v4 keep-set).
- **Files modified:** `tests/BaseApi.Tests/Keeper/Health/BitHealthLoopTests.cs`
- **Verification:** `dotnet build SK_P.sln -c Debug` 0-warning green; the 6 `BitHealthLoop` facts pass in the hermetic run.
- **Committed in:** `5f0e210` (Task 1 commit)

**2. [Rule 1 - Bug/Hygiene] Refreshed the stale Keeper comment in `BaseApi.Tests.csproj`**
- **Found during:** Task 1
- **Issue:** The csproj `<ProjectReference>` comment named the now-deleted consumers + test classes (`KeeperRoundRobinTests`, `FaultEntryStepDispatchConsumer`, …) — a dead remnant (SC-4 spirit).
- **Fix:** Reworded to describe the surviving v4 keep-set tests. Comment-only, no build impact.
- **Files modified:** `tests/BaseApi.Tests/BaseApi.Tests.csproj`
- **Committed in:** `5f0e210` (Task 1 commit)

---

**Total deviations:** 2 auto-fixed (1 blocking, 1 hygiene)
**Impact on plan:** The blocking fix was required for the build-before-teardown invariant (the plan's test inventory missed `Health/BitHealthLoopTests.cs`). No scope creep — both changes are confined to test/comment surface directly orphaned by the planned deletes.

## Issues Encountered
- **Full-suite run shows 2 RealStack failures:** the full `dotnet test` reports 505 passed / 2 failed; the 2 failures are the pre-existing docker-dependent RealStack E2E tests (no live stack in this environment), documented in PROJECT.md (Phase 46/47: "full hermetic suite green except the 2 pre-existing docker-dependent E2E tests"). Confirmed unrelated: `dotnet test ... -- --filter-not-trait Category=RealStack` is **503/503 green, 0 failed**. The ×3 green + Release gate is owned by Plan 03 per VALIDATION sampling.

## Known Stubs
None — this is a pure-deletion plan; no placeholder/stub data was introduced.

## Threat Flags
None — net security posture is surface-reducing (one fewer consumer, one fewer queue; no new input/auth/crypto/data flow), matching the plan's threat register (T-48-01 mitigate / T-48-02 accept).

## Info Notes (for the verifier)
- `tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs:99` still excludes `KeeperRecoveryHandler.cs` by filename in its source-scan; that file is now deleted, so the exclusion is a harmless no-op and the RESIL-02 fact still passes (no remaining source under `src/Keeper/Recovery/` or `src/BaseProcessor.Core/Processing/` references `keeper-dlq`). Left untouched — it is a Phase-47 verification fact outside this plan's `files_modified`.
- Prose mentions of the retired path remain in non-build artifacts (`compose.yaml` comment, `docs/design/...`, `scripts/phase-39-close.ps1`, `.planning/MILESTONES.md`) and surviving definition doc-comments (`<c>FaultEntryStepDispatchConsumerDefinition</c>` crefs in the keeper-recovery definitions). These are plain inline-code prose (not `<see cref>` compile targets) and out of this plan's file scope.

## Next Phase Readiness
- v4 recovery path is the sole mechanism; the Phase-48 negative guards (Plan 02) now have a meaningful, removed target to enforce against.
- Solution is Debug 0-warning green; Plan 03 owns the ×3 RealStack green + Release build close gate.

## Self-Check: PASSED

- All modified files present on disk (Program.cs, L2ProbeRecovery.cs, KeeperQueues.cs, L2ProjectionKeys.cs, 48-01-SUMMARY.md).
- All deleted files absent (KeeperRecoveryHandler.cs, KeeperMetrics.cs, the 4 Fault consumer/definition files, the 6 dependent tests).
- Both task commits present in git log: `5f0e210`, `384bde5`.

---
*Phase: 48-v3-x-teardown*
*Completed: 2026-06-09*
