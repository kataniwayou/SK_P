---
phase: 46-keeper-5-state-recovery-orchestrator-per-item-consume
plan: 02
subsystem: keeper
tags: [masstransit, dotnet, redis, keeper, recovery, retry, health-gate, tdd]

# Dependency graph
requires:
  - phase: 46-01
    provides: RetryLoop in BaseConsole.Core.Resilience + KeeperReinject.Payload + RED Phase-46 stubs
  - phase: 45-keeper-bit-health-gate-global-pause-resume
    provides: IL2HealthGate primitive (first consumed here)
  - phase: 43-message-contracts-l2-key-reshape
    provides: Keeper recovery contracts + L2ProjectionKeys + IKeeperRecoverable + OrchestratorQueues
provides:
  - RecoveryConsumerBase (gate-wait once at entry + RetryLoop Guard) — abstract per-state body
  - RecoveryOptions (PartitionCount=8, GateWaitSeconds=300) knobs
  - RecoveryDataGoneException (terminal) + RecoveryGateTimeoutException (transient) markers
  - Five sealed per-state recovery consumers (Update/Reinject/Inject/Delete/Cleanup)
  - Six Wave-0 Keeper-body facts turned GREEN
affects: [46-03, 46-04, 47-resilience-dlq1, 48-retire-reactive-recovery]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Recovery base awaits IL2HealthGate.WaitForOpenAsync ONCE at Consume entry under a GateWaitSeconds-bounded linked CTS; on bound throws a TRANSIENT marker (Pattern A / D-03 LOCKED)"
    - "Each per-state body is the inverse of a Phase-44 ProcessorPipeline SendKeeper site: one L2 op (or read→Send) via the Guard/RetryLoop re-throw path"
    - "Deliberate data-gone terminal (RecoveryDataGoneException) thrown INSIDE the retried read so an absent key surfaces as a thrown terminal, not a silent ack (D-04)"

key-files:
  created:
    - src/Keeper/RecoveryOptions.cs
    - src/Keeper/Recovery/RecoveryDataGoneException.cs
    - src/Keeper/Recovery/RecoveryConsumerBase.cs
    - src/Keeper/Recovery/UpdateConsumer.cs
    - src/Keeper/Recovery/DeleteConsumer.cs
    - src/Keeper/Recovery/CleanupConsumer.cs
    - src/Keeper/Recovery/ReinjectConsumer.cs
    - src/Keeper/Recovery/InjectConsumer.cs
    - tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs
  modified:
    - tests/BaseApi.Tests/Keeper/RecoveryGateWaitFacts.cs
    - tests/BaseApi.Tests/Keeper/UpdateConsumerFacts.cs
    - tests/BaseApi.Tests/Keeper/DeleteConsumerFacts.cs
    - tests/BaseApi.Tests/Keeper/CleanupConsumerFacts.cs
    - tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs
    - tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs

key-decisions:
  - "D-03 (LOCKED Pattern A): single bounded gate-wait at entry; RecoveryGateTimeoutException (transient) on bound, distinct from RecoveryDataGoneException (terminal)"
  - "D-04: every L2 op + Send wrapped in Guard(RetryLoop) which re-throws .Error on exhaustion → skp-dlq-1 via the inherited ConsolidatedErrorTransportFilter; no per-consumer ConfigureError"
  - "TTL applied ONLY at the UPDATE StringSetAsync call site (BackupOptions.TtlDays); INJECT data write has NO TTL; INJECT order read→write→send→delete asserted via Received.InOrder"

patterns-established:
  - "Pattern: a five-subclass + shared-base recovery family where the base owns gate-wait + retry and each subclass is a one-op HandleAsync override (D-02)"
  - "Pattern: a RecoveryTestKit (OpenGate / Db / Mux / CapturingSendProvider + option helpers) mirroring DispatchTestKit for the Keeper consumer facts"

requirements-completed: [KEEP-04, KEEP-05, KEEP-06, KEEP-07, KEEP-08]

# Metrics
duration: 20min
completed: 2026-06-08
---

# Phase 46 Plan 02: Keeper Recovery State Bodies Summary

**Built the five sealed Keeper recovery-state consumers and their shared gate/retry base — the base awaits IL2HealthGate once at entry under a 300s-bounded linked CTS (throwing a transient marker on bound, Pattern A / D-03 LOCKED), and a Guard helper wraps every L2 op + Send in the relocated RetryLoop re-throwing on exhaustion (D-04). UPDATE writes the composite with the BackupOptions TTL; REINJECT re-injects an EntryStepDispatch carrying the D-01 Payload (or throws the data-gone terminal); INJECT does read→write(no TTL)→send→delete in strict order producing a StepCompleted indistinguishable from a direct completion; DELETE/CLEANUP delete the data/composite keys. Six Wave-0 Keeper-body facts are GREEN.**

## Performance

- **Duration:** ~20 min
- **Started:** 2026-06-08T20:47Z
- **Completed:** 2026-06-08T~21:07Z
- **Tasks:** 3
- **Files created/modified:** 15 (9 created, 6 test stubs turned green)

## Accomplishments
- **RecoveryConsumerBase (D-02/D-03/D-04):** abstract `IConsumer<TMessage> where TMessage : class, IKeeperRecoverable`. Awaits `IL2HealthGate.WaitForOpenAsync` ONCE at Consume entry under a `GateWaitSeconds`-bounded linked CTS; on bound (gate-CTS canceled while the inbound token is not) throws the TRANSIENT `RecoveryGateTimeoutException`. Exposes `Db`/`Send`/`RetryLimit`/`TtlDays` accessors and `Guard`/`Guard<T>` helpers that wrap `RetryLoop.ExecuteAsync` and re-throw `.Error` on exhaustion.
- **RecoveryOptions (D-03/D-06):** `PartitionCount = 8`, `GateWaitSeconds = 300`.
- **Markers (D-04):** `RecoveryDataGoneException` (deliberate terminal) and `RecoveryGateTimeoutException` (transient) — distinct types.
- **UPDATE (KEEP-04):** `StringSetAsync(CompositeBackup(4-tuple), ValidatedData, expiry: TimeSpan.FromDays(TtlDays))`.
- **REINJECT (KEEP-05):** retried read of `ExecutionData(entryId)` throwing `RecoveryDataGoneException` on absent/empty INSIDE the op; present → Send reconstructed `EntryStepDispatch(..., m.Payload)` to `queue:{ProcessorId:D}`.
- **INJECT (KEEP-06):** read composite (terminal if gone) → `NewId.NextGuid()` → write `ExecutionData(entryId)` NO TTL → Send `StepCompleted` to `queue:orchestrator-result` → delete composite — strict order.
- **DELETE (KEEP-07) / CLEANUP (KEEP-08):** `KeyDeleteAsync(ExecutionData(entryId))` / `KeyDeleteAsync(CompositeBackup(4-tuple))`.
- **Tests:** `RecoveryTestKit` (OpenGate / Db / Mux / CapturingSendProvider + option helpers); six body facts green including the INJECT `Received.InOrder` order check and the ORCH-01 indistinguishability assertions.

## Task Commits

1. **Task 1: RecoveryOptions, markers, gate/retry RecoveryConsumerBase** - `375e00a` (feat)
2. **Task 2: UPDATE, DELETE, CLEANUP bodies (KEEP-04/07/08)** - `1739eff` (feat)
3. **Task 3: REINJECT and INJECT bodies (KEEP-05/06)** - `48d8487` (feat)

## Decisions Made
- **StringSetAsync overload (test-only):** the production `expiry:`-named UPDATE call binds to SE.Redis 2.13's `Expiration`/`ValueCondition` overload (TimeSpan implicitly converts to `Expiration`). The UPDATE fact asserts the TTL against THAT overload — a Rule-1 fix after the first assertion (against the 6-arg `keepTtl` overload) found "no matching call". Production code unchanged; only the test assertion targets the correct overload.
- **Gate-wait test fake:** `BlockingGate` (a TaskCompletionSource that completes on Release or cancels on the linked CTS) proves both blocks-until-open and bound-exhaustion-throws-transient without a real timer dependency (1s `GateWaitSeconds` for the bound test).
- **INJECT order across two substitutes:** the InjectConsumerFacts uses a single captured `ISendEndpoint` substitute (not the CapturingSendProvider) so `Received.InOrder` can interleave the db read/write/delete with the Send.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] UPDATE TTL assertion targeted the wrong StringSetAsync overload**
- **Found during:** Task 2 (UpdateConsumerFacts first run)
- **Issue:** The fact asserted the TTL against the 6-arg `StringSetAsync(..., bool keepTtl, When, CommandFlags)` overload, but the production `expiry:`-named call binds to the `Expiration`/`ValueCondition` overload in SE.Redis 2.13.1 — NSubstitute reported "no matching call".
- **Fix:** Re-targeted the assertion to `StringSetAsync(key, value, (Expiration)TimeSpan.FromDays(ttlDays), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>())`. No production change.
- **Files modified:** tests/BaseApi.Tests/Keeper/UpdateConsumerFacts.cs
- **Commit:** 1739eff

## Issues Encountered
- xUnit analyzer rule xUnit1051 required `Task.Delay(50, TestContext.Current.CancellationToken)` in the gate-wait blocking test (a CancellationToken-accepting call). Trivial fix during Task 1.

## Verification
- **Build (`dotnet build SK_P.sln`):** Build succeeded, 0 warnings, 0 errors.
- **Phase-46 trait run (`dotnet run --project tests/BaseApi.Tests -- --filter-trait "Phase=46"`):** 8 / 11 green. The 8 green are exactly this plan's targets — the two gate-wait facts + the five state-body facts (Update/Delete/Cleanup/Reinject(x2)/Inject). The 3 still RED — `RecoveryPartitionFacts` (1) and `TypedResultConsumerFacts` (2) — are the partition + typed-consumer stubs, deferred to Plans 03/04 exactly as the plan's `<verification>` states.
- **Discipline checks:** no `catch (Exception)` in any state body; no per-consumer `ConfigureError`/`SetQueueArgument` (Grep clean — only a doc-comment reference stating the consolidated error route is inherited).

Note: a bare full `dotnet test` would show the 2 pre-existing unrelated E2E failures (`SampleRoundTripE2ETests`, `MetricsRoundTripE2ETests`) that need a docker container — NOT regressions from this plan (per the phase-notes test-runner caveat).

## Next Phase Readiness
- Plan 03 can now register the five consumers + five `ConsumerDefinition`s on `queue:keeper-recovery` with `UsePartitioner(RecoveryOptions.PartitionCount)` keyed on the `IKeeperRecoverable` 4-tuple, and the endpoint `UseMessageRetry` (single-owner definition) that re-attempts the transient `RecoveryGateTimeoutException`. The base + bodies + options are all in place; `RecoveryPartitionFacts` becomes the Plan-03 target.
- Plan 04 builds the orchestrator `TypedResultConsumer<T>` family (`TypedResultConsumerFacts`).

## Self-Check: PASSED

- All 9 created files verified present on disk (RecoveryOptions, RecoveryDataGoneException, RecoveryConsumerBase, Update/Delete/Cleanup/Reinject/InjectConsumer, RecoveryTestKit) plus this SUMMARY.
- All 3 task commits verified in git history: `375e00a`, `1739eff`, `48d8487`.
- No accidental file deletions in any task commit; no stubs introduced (the six Wave-0 stubs were turned into real passing facts).
