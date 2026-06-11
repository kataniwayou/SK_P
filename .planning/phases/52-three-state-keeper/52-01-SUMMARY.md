---
phase: 52-three-state-keeper
plan: 01
subsystem: infra
tags: [masstransit, redis, keeper, recovery, metrics, imeterfactory, nsubstitute]

# Dependency graph
requires:
  - phase: 50-contracts-slot-array-l2-key-reshape
    provides: "Final KeeperInject/KeeperReinject/KeeperDelete contracts + IKeeperRecoverable partition marker"
  - phase: 46-keeper-5-state-recovery-orchestrator-per-item-consume
    provides: "RecoveryConsumerBase + per-state consumer scaffolding + RecoveryConsumerDefinition partitioner"
provides:
  - "A18 three-state keeper bodies: REINJECT (drop-on-absent), INJECT (forward-only write->send->delete), DELETE (verify)"
  - "RecoveryConsumerBase stripped of gate-wait (D-09) — Consume dispatches straight to HandleAsync"
  - "KeeperMetrics (IMeterFactory) with keeper_reinject_dropped counter (D-07)"
  - "RecoveryOptions.ExhaustionPolicy enum (Dlq1 default / SustainedOutage) for Plan 02 policy wiring"
affects: [52-02, 53, keeper-recovery, exhaustion-policy, endpoint-pause-resume]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Endpoint-enforced gating (D-04) replaces in-Consume bounded gate-wait — base Consume is a one-line dispatch"
    - "IMeterFactory-built KeeperMetrics mirroring ProcessorMetrics (never static Meter)"
    - "REINJECT by-design silent drop on absent L2 data (A18 accepted silent loss) — Redis exception still routes to exhaustion"

key-files:
  created:
    - src/Keeper/Observability/KeeperMetrics.cs
    - tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs
  modified:
    - src/Keeper/Recovery/RecoveryConsumerBase.cs
    - src/Keeper/RecoveryOptions.cs
    - src/Keeper/Recovery/ReinjectConsumer.cs
    - src/Keeper/Recovery/InjectConsumer.cs
    - src/Keeper/Recovery/DeleteConsumer.cs
    - tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs
    - tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs
    - tests/BaseApi.Tests/Keeper/DeleteConsumerFacts.cs
    - tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs
    - tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs

key-decisions:
  - "Kept recoveryOptions in the base ctor (per plan signature) and exposed a protected ExhaustionPolicy property to resolve CS9113 (unread primary-ctor param)"
  - "Deleted RecoveryGateWaitFacts entirely — it tested the removed gate-wait (D-09), so it is obsolete, not adaptable"
  - "Repurposed RecoveryDeadLetterFacts data-gone fact to infra-fault->dead-letter (a Redis exception, not absent/empty) since data-gone is now a drop (D-06)"
  - "InjectConsumerFacts orders via Received.InOrder on the SE.Redis 2.13.1 Expiration/ValueCondition StringSetAsync overload (the one the 2-arg call binds to)"

patterns-established:
  - "Gating at the endpoint (Plan 02 pause/resume), not inside Consume — base Consume = HandleAsync dispatch"
  - "By-design observability drop: counter + structured {EntryId} log hole, never logging Payload/Data (T-52-01)"

requirements-completed: [KEEP-01, KEEP-02, KEEP-03]

# Metrics
duration: 30min
completed: 2026-06-11
---

# Phase 52 Plan 01: Three-State Keeper Bodies Summary

**A18 three-state keeper landed — REINJECT silently drops on absent L2 data (counter + log) and re-injects on present; INJECT is forward-only (write L2 -> send StepCompleted -> delete source, strict order); DELETE verifies drop-on-absent — with the obsolete gate-wait/exceptions stripped and a new IMeterFactory KeeperMetrics.**

## Performance

- **Duration:** ~30 min
- **Started:** 2026-06-11T18:51:27+03:00
- **Completed:** 2026-06-11T19:21:01+03:00
- **Tasks:** 3
- **Files modified:** 12 (2 created, 8 modified, 2 deleted)

## Accomplishments
- Stripped the bounded gate-wait from `RecoveryConsumerBase` (D-09): dropped the `IL2HealthGate` ctor param, the `CancelAfter`/`WaitForOpenAsync` block, and the `RecoveryGateTimeoutException` class — `Consume` now dispatches straight to `HandleAsync` (gating moves to the endpoint in Plan 02).
- Flipped REINJECT's absent-data path from a thrown terminal to a BY-DESIGN silent drop (D-06): absent/empty `L2[entryId]` (STRLEN==0, no exception) acks with no send, increments `keeper_reinject_dropped`, and logs a structured `{EntryId}` warning; a Redis EXCEPTION on the read still routes to the exhaustion policy (T-52-02).
- Implemented the A18 forward-only INJECT body (KEEP-02): write `L2[entryId]=Data` -> send a reconstructed `StepCompleted` to `queue:orchestrator-result` -> delete `L2[deleteEntryId]`, in strict order (Pitfall 5 / T-52-03), proven by an `Received.InOrder` fact.
- Verified DELETE drops-on-absent (KEEP-03) and added a `Delete_absent_key_no_throws` fact.
- Created `KeeperMetrics` (IMeterFactory, `keeper_reinject_dropped`, no static Meter — T-52-04) and added `RecoveryOptions.ExhaustionPolicy` (default `Dlq1`, D-01/D-02) for Plan 02's policy wiring.
- Deleted `RecoveryDataGoneException` (D-06) and the obsolete `RecoveryGateWaitFacts` (D-09).

## Task Commits

1. **Task 1: Strip gate-wait from base, delete exceptions, add ExhaustionPolicy + KeeperMetrics** - `cfb5006` (refactor)
2. **Task 2: REINJECT absent-path flip (drop + log + metric) and rewrite its facts** - `e71a477` (test — TDD RED->GREEN folded; the absent fact's drop assertion drove the source flip)
3. **Task 3: INJECT forward-only body + DELETE verify, with ordering facts** - `6e64364` (feat)

**Hygiene cleanup:** `003e117` (chore: clean stale gate-wait config + comments after D-09 removal)

## Files Created/Modified
- `src/Keeper/Observability/KeeperMetrics.cs` (created) - IMeterFactory KeeperMetrics with `keeper_reinject_dropped` Counter<long>
- `tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs` (created) - KEEP-02 write->send->delete in-order fact
- `src/Keeper/Recovery/RecoveryConsumerBase.cs` - removed gate-wait + IL2HealthGate param + RecoveryGateTimeoutException; exposed ExhaustionPolicy
- `src/Keeper/RecoveryOptions.cs` - removed GateWaitSeconds; added ExhaustionPolicy enum (Dlq1 default)
- `src/Keeper/Recovery/ReinjectConsumer.cs` - absent-path drop + counter + log (KEEP-01)
- `src/Keeper/Recovery/InjectConsumer.cs` - A18 forward-only body (KEEP-02)
- `src/Keeper/Recovery/DeleteConsumer.cs` - ctor de-gated; drop-on-absent verified (KEEP-03)
- `tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs` - Recovery() de-gated; Metrics() helper
- `tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs` - present-path ctor update + rewritten absent-drop fact (MeterListener)
- `tests/BaseApi.Tests/Keeper/DeleteConsumerFacts.cs` - ctor update + absent-key no-throw fact
- `tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs` - repurposed to infra-fault->dead-letter; duplicate-reinject ctor update
- `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs` - STATE2 -> by-design drop; STATE3 -> A18 INJECT effect asserts
- `src/Keeper/Recovery/RecoveryDataGoneException.cs` (deleted) - D-06, REINJECT was its only thrower
- `tests/BaseApi.Tests/Keeper/RecoveryGateWaitFacts.cs` (deleted) - D-09, tested the removed gate-wait

## Decisions Made
- **CS9113 resolution:** the plan's specified base ctor keeps `recoveryOptions`, but after removing the gate-wait it was unread (CS9113 with warnings-as-errors). Resolved by exposing a `protected ExhaustionPolicy` property that reads it — also forward-useful for Plan 02's policy branch. (Alternative — dropping the param — would have cascaded the unread-param error onto the three subclass ctors.)
- **INJECT ordering assertion:** SE.Redis 2.13.1 binds the consumer's 2-arg `StringSetAsync(key, value)` to the `Expiration`/`ValueCondition` overload (not the 6-arg keepTtl overload the test kit stubs). The `Received.InOrder` matcher targets that overload, mirroring `DispatchTestKit`'s per-overload robustness.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Resolved CS9113 unread-primary-ctor-param on RecoveryConsumerBase**
- **Found during:** Task 1
- **Issue:** Removing the gate-wait left `recoveryOptions` unread on the base ctor; warnings-as-errors failed the build (CS9113).
- **Fix:** Exposed a `protected ExhaustionPolicy => recoveryOptions.Value.ExhaustionPolicy` property (observes the param; sets up Plan 02's policy branch).
- **Files modified:** src/Keeper/Recovery/RecoveryConsumerBase.cs
- **Verification:** `dotnet build src/Keeper/Keeper.csproj` 0 warnings/0 errors.
- **Committed in:** e71a477 (folded with the Task 2 group, base change)

**2. [Rule 3 - Blocking] Deleted obsolete RecoveryGateWaitFacts**
- **Found during:** Task 1/2 (compile cascade)
- **Issue:** `RecoveryGateWaitFacts` tests the removed bounded gate-wait + `RecoveryGateTimeoutException` (5-param base ctor). Both are deleted by D-09, so the file no longer compiles and tests retired behavior.
- **Fix:** `git rm` the file (the behavior it guarded no longer exists).
- **Files modified:** tests/BaseApi.Tests/Keeper/RecoveryGateWaitFacts.cs (deleted)
- **Verification:** Test project builds 0/0; Keeper namespace 16/16 facts green.
- **Committed in:** e71a477

**3. [Rule 1 - Bug] Repurposed RecoveryDeadLetterFacts data-gone assertion (now contradicted D-06)**
- **Found during:** Task 2 (compile cascade)
- **Issue:** `DataGone_reinject_faults_and_routes_to_dead_letter` asserted data-gone -> `RecoveryDataGoneException` -> dead-letter; under D-06 data-gone is now a DROP (no fault, no dead-letter), and `RecoveryDataGoneException` is deleted.
- **Fix:** Repurposed to `InfraFault_reinject_faults_and_routes_to_dead_letter` (a Redis EXCEPTION on the read -> exhaustion -> consolidated `skp-dlq-1`), preserving the Dlq1-mode consolidated-route proof; updated the duplicate-reinject fact to the new ctor. (52-PATTERNS line 408 prescribes this repurpose.)
- **Files modified:** tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs
- **Verification:** Both facts green (Keeper namespace 16/16).
- **Committed in:** e71a477

**4. [Rule 1 - Bug] Updated SC2RecoveryPathsE2ETests for the new REINJECT-drop + implemented INJECT**
- **Found during:** Task 3 (compile cascade)
- **Issue:** The RealStack E2E referenced the deleted `RecoveryDataGoneException` in a `<see cref>` (CS1574 -> error with doc-warnings-as-errors), and its STATE 2 asserted data-gone -> DLQ (now a drop), STATE 3 assumed INJECT was a no-op stub.
- **Fix:** Removed the `<see cref>`; STATE 2 now asserts a silent drop (origin queue empty, DLQ not incremented); STATE 3 asserts the A18 INJECT effect (data key written, source key deleted) with a new `PollForKeyValueAsync` helper + net-zero key registration.
- **Files modified:** tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs
- **Verification:** Full solution Release build 0 warnings/0 errors (the RealStack fact is hermetically excluded; its live run is operator-gated).
- **Committed in:** 6e64364

**5. [Rule 1 - Bug] Cleaned stale gate-wait config + comments**
- **Found during:** Final verification (grep `GateWaitSeconds`/`RecoveryGateTimeoutException` in src/)
- **Issue:** `appsettings.json` still carried `Recovery:GateWaitSeconds`, and `Program.cs`/`ReinjectConsumerDefinition.cs` comments referenced the deleted symbols.
- **Fix:** Replaced the appsettings key with `Recovery:ExhaustionPolicy=Dlq1`; corrected the two comments. (52-PATTERNS line 263 prescribes the optional config cleanup; the policy/pause-resume WIRING stays Plan 02.)
- **Files modified:** src/Keeper/appsettings.json, src/Keeper/Program.cs, src/Keeper/Recovery/ReinjectConsumerDefinition.cs
- **Verification:** src/ grep clean (only the RecoveryOptions "REMOVED" doc-note remains); Keeper Release build 0/0.
- **Committed in:** 003e117

---

**Total deviations:** 5 auto-fixed (3 blocking compile-cascade, 2 bug/hygiene)
**Impact on plan:** All deviations are forced consequences of the plan's own D-06/D-09 removals (the plan's `files_modified` list under-counted the test/E2E cascade). No scope creep — every change is the minimal fix to keep the solution building and the retired behavior out of the test suite. Endpoint policy/pause-resume wiring and the meter DI registration remain deferred to Plan 02 as scoped (D-08).

## Issues Encountered
- **NSubstitute `Received.InOrder` saw only `KeyDeleteAsync`:** the consumer's 2-arg `StringSetAsync` binds to SE.Redis 2.13.1's `Expiration`/`ValueCondition` overload, not the 6-arg keepTtl overload the matcher first used. Resolved by matching the correct overload (per `DispatchTestKit`'s documented per-overload approach).
- **`--filter` ignored under Microsoft.Testing.Platform:** the VSTest `--filter` flag emits MTP0001 and runs the whole suite; used the MTP `--filter-class`/`--filter-namespace`/`--filter-not-trait` flags against the built exe instead.

## Threat Surface Scan
No new trust-boundary surface introduced beyond the plan's `<threat_model>`. T-52-01 (no Payload/Data in the drop log — structured `{EntryId}` hole only), T-52-02 (Redis exception still routes to exhaustion, not swallowed), T-52-03 (`Received.InOrder` locks write->send->delete), and T-52-04 (IMeterFactory, no cross-test leakage) are all satisfied by the implementation.

## Known Stubs
None. INJECT is now fully implemented (no longer the Phase-50 no-op stub). The two other recovery `ConsumerDefinition`s remain intentional endpoint-scoped no-ops (unchanged, by design). The endpoint pause/resume + ExhaustionPolicy-conditional wiring + the `AddSingleton<KeeperMetrics>`/`AddMeter` registration are deferred to Plan 02 by D-08 (the meter type is created and consumed here; its DI registration lands in Plan 02's Program.cs edit).

## Verification
- Keeper namespace facts: **16/16 green** (REINJECT present+absent-drop, INJECT in-order, DELETE delete+absent, RecoveryDeadLetter infra-fault + duplicate, partitioner, BitHealthLoop).
- Hermetic suite (`--filter-not-trait Category=RealStack --filter-not-trait Category=E2E`): **518/518 green, 0 failed**.
- `dotnet build SK_P.sln -c Release`: **0 warnings, 0 errors**.
- grep in `src/`: `RecoveryDataGoneException`/`RecoveryGateTimeoutException`/`GateWaitSeconds` absent (only the RecoveryOptions "REMOVED" doc-note remains).
- The 6 failures in the unfiltered full run were the RealStack/E2E tests (require a live docker stack) — out of scope; hermetically excluded.

## Next Phase Readiness
- The three A18 bodies + `KeeperMetrics` + `ExhaustionPolicy` are ready for **Plan 02** (KEEP-04 endpoint pause/resume + KEEP-05 policy-conditional wiring + meter DI registration).
- Plan 02 must add `AddSingleton<KeeperMetrics>()` + `ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(KeeperMetrics.MeterName))` in `Program.cs` so `ReinjectConsumer` resolves `KeeperMetrics` at runtime (the unit facts construct it directly, so they are self-sufficient).
- Phase 53 retains the global `UseMessageRetry=none` rule, the processor-side latch removal, and the Model-B remnant sweep (RETIRE-03), per D-08.

## Self-Check: PASSED

- Created files exist: `src/Keeper/Observability/KeeperMetrics.cs`, `tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs`, `.planning/phases/52-three-state-keeper/52-01-SUMMARY.md` — all FOUND.
- Task commits exist: `cfb5006`, `e71a477`, `6e64364`, `003e117` — all FOUND.
- Intentional deletions confirmed: `RecoveryDataGoneException.cs`, `RecoveryGateWaitFacts.cs` — both removed.

---
*Phase: 52-three-state-keeper*
*Completed: 2026-06-11*
