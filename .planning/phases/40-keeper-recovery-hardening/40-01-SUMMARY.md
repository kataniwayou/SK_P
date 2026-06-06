---
phase: 40-keeper-recovery-hardening
plan: 01
subsystem: keeper
tags: [keeper, recovery, refactor, dedup, kharden, generic-extraction]
requires:
  - "IExecutionCorrelated (Messaging.Contracts) — extended with H"
  - "L2ProbeRecovery + KeeperMetrics (Keeper) — injected into the new handler"
provides:
  - "KeeperRecoveryHandler — the single shared generic recovery body both fault consumers delegate to (the one place KHARD-01's cap lands in Plan 02)"
  - "IExecutionCorrelated.H — H hoisted onto the shared inner interface"
affects:
  - "src/Keeper/Consumers/* (both fault consumers now one-line delegators)"
  - "src/Keeper/Program.cs (handler DI registration)"
  - "tests/BaseApi.Tests/Keeper/* + Console/ConsoleExecutionScopeFilterTests.cs"
tech-stack:
  added: []
  patterns:
    - "Injected sealed-singleton generic helper (mirrors L2ProbeRecovery) absorbing two verbatim-duplicate consumer bodies; per-type deltas passed as method params (fault_type tag, reinject-endpoint selector, ct)"
    - "Interface member added as { get; } satisfied by the records' existing { get; init; } — zero record edits"
key-files:
  created:
    - "src/Keeper/Recovery/KeeperRecoveryHandler.cs"
  modified:
    - "src/Messaging.Contracts/IExecutionCorrelated.cs"
    - "src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs"
    - "src/Keeper/Consumers/FaultExecutionResultConsumer.cs"
    - "src/Keeper/Program.cs"
    - "tests/BaseApi.Tests/Keeper/KeeperProbeLoopTests.cs"
    - "tests/BaseApi.Tests/Keeper/KeeperFaultConsumerScopeTests.cs"
    - "tests/BaseApi.Tests/Keeper/KeeperPausePublishTests.cs"
    - "tests/BaseApi.Tests/Keeper/KeeperMetricsFacts.cs"
    - "tests/BaseApi.Tests/Keeper/KeeperRoundRobinTests.cs"
    - "tests/BaseApi.Tests/Keeper/KeeperHostBootFixture.cs"
    - "tests/BaseApi.Tests/Console/ConsoleExecutionScopeFilterTests.cs"
decisions:
  - "D-A6 choice (a): added `string H { get; }` to IExecutionCorrelated; both records' existing `{ get; init; }` satisfy it with no record edits"
  - "Dropped the now-unused ILogger ctor param from both consumers (the handler owns all logging) — required to keep the 0-warning TreatWarningsAsErrors build (CS9113 unread-parameter)"
metrics:
  duration: "~22 min"
  completed: "2026-06-06"
  tasks: 3
  files: 11
  commits: 5
---

# Phase 40 Plan 01: KeeperRecoveryHandler Extraction (KHARD-03 keystone) Summary

Collapsed the two verbatim-duplicate Keeper fault-consumer bodies into ONE injected generic
`KeeperRecoveryHandler` both consumers delegate to in a single expression, hoisted `H` onto
`IExecutionCorrelated` so the generic body reads `inner.H` through the bound, and wired the handler
into DI + every Keeper test harness — the full hermetic suite stays GREEN (491/491) at 0-warning Release.

## What Was Built

- **Task 1 — `IExecutionCorrelated.H`** (`119b84f`): added `string H { get; }` after `EntryId` (D-A6 choice a).
  Both concrete records (`EntryStepDispatch`, `ExecutionResult`) already expose `public string H { get; init; } = "";`,
  which satisfies the `{ get; }` member — **zero record edits**. `Messaging.Contracts` builds 0-warning Release.
- **Task 2 — `KeeperRecoveryHandler` + delegating consumers** (`8b8eec6`): new
  `public sealed class KeeperRecoveryHandler(ILogger<KeeperRecoveryHandler>, L2ProbeRecovery, KeeperMetrics)`
  in `Keeper.Recovery` with one generic method
  `HandleAsync<T>(ConsumeContext<Fault<T>>, string faultTypeTag, Func<T,Uri> reinjectEndpoint, CancellationToken ct) where T : class, IExecutionCorrelated`.
  The body is byte-identical to the source consumer with exactly the three documented data deltas substituted:
  `typeof(T).Name` for the intake log, `faultTypeTag` at the 3 counter sites, `reinjectEndpoint(inner)` at the
  re-inject. The double-unwrap, manual CorrelationId scope + `ExecutionLogScope.BuildState`, intake log,
  `Publish(PauseWorkflow)`, awaited `recovery.RunAsync`, the Recovered branch (re-inject + `Publish(ResumeWorkflow)`
  + recovered metrics) and the GaveUp branch (park original `Fault<T>` to `keeper-dlq` + gave-up metrics) are all
  preserved exactly. Both `Consume` methods are now single expression-body delegations supplying their per-type
  deltas (`FaultTypeDispatch` + `queue:{ProcessorId:D}` / `FaultTypeResult` + `queue:{OrchestratorQueues.Result}`).
  The two `*ConsumerDefinition.cs` retry-owner files are **byte-unchanged** (not in `git diff`).
- **Task 3 — DI + harness wiring** (`d6cd8ae`, `c7b6c4f`): `Program.cs` registers
  `AddSingleton<Keeper.Recovery.KeeperRecoveryHandler>()` beside `L2ProbeRecovery`. All five Keeper test harnesses
  that stand up the fault consumers now resolve the handler; the two direct-construction test sites build the
  handler and pass it to the consumers' new single-arg ctor.

## Verification

- `dotnet build src/Messaging.Contracts` + `src/Keeper` + `tests/BaseApi.Tests` — all **0 Warning / 0 Error** Release (TreatWarningsAsErrors).
- **Full hermetic suite** `BaseApi.Tests.exe --filter-not-trait Category=RealStack` = **491 passed / 0 failed / 0 skipped**.
- `KeeperProbeLoopTests` 6/6 GREEN (the plan's Task-3 acceptance gate); all 26 touched-class facts GREEN.
- Acceptance greps: 1 `class KeeperRecoveryHandler`; 1 `handler.HandleAsync` per consumer; 0 `recovery.RunAsync`
  in `src/Keeper/Consumers/`; 1 `KeeperQueues.DeadLetter` site in the handler; 1 `AddSingleton<...KeeperRecoveryHandler>`
  in Program.cs; 1 in `KeeperProbeLoopTests`. Neither `*ConsumerDefinition.cs` in `git diff`.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Dropped the unused `ILogger` ctor param from both consumers**
- **Found during:** Task 2 (first Keeper Release build)
- **Issue:** The plan specified the delegating consumers keep `(ILogger<...> logger, KeeperRecoveryHandler handler)`.
  Once the body delegates, `logger` is never read → **CS9113 (unread parameter)**, which fails the 0-warning
  `TreatWarningsAsErrors` gate.
- **Fix:** Removed the `logger` param (and the now-unused `Microsoft.Extensions.Logging` using) from both consumers;
  the handler owns all logging. Behavior unchanged — the consumers never logged anything in the delegating shape.
- **Files modified:** `FaultEntryStepDispatchConsumer.cs`, `FaultExecutionResultConsumer.cs`
- **Commit:** `8b8eec6`

**2. [Rule 3 - Blocking] Added `H` to the `ExecProbeMessage` test stub**
- **Found during:** Task 3 (full test build after the Task-1 interface change)
- **Issue:** `ConsoleExecutionScopeFilterTests.ExecProbeMessage` implements `IExecutionCorrelated` directly →
  **CS0535** (does not implement the new `H` member).
- **Fix:** Added `public string H => "";` to the probe record (the test never exercises `H`). The only other
  `: IExecutionCorrelated` implementers are the two production records, which already carry `H`.
- **Files modified:** `tests/BaseApi.Tests/Console/ConsoleExecutionScopeFilterTests.cs`
- **Commit:** `d6cd8ae`

**3. [Rule 1 - Stale assertion] Rewrote `KeeperMetricsFacts.Both_Fault_Consumers_Take_KeeperMetrics_In_Ctor`**
- **Found during:** Task 3 (touched-class test run)
- **Issue:** This Phase-39 structural fact reflectively asserted both consumer ctors take `KeeperMetrics`.
  After KHARD-03 the metrics moved one hop — into `KeeperRecoveryHandler` — so the assertion is now false by design.
- **Fix:** Renamed to `..._Delegate_To_KeeperRecoveryHandler_Which_Owns_KeeperMetrics` and assert the relocated
  wiring: both consumers take `KeeperRecoveryHandler`; the handler takes `KeeperMetrics`. Same intent (the metrics
  wiring point still exists structurally), correct location.
- **Files modified:** `tests/BaseApi.Tests/Keeper/KeeperMetricsFacts.cs`
- **Commit:** `d6cd8ae`

**4. [Rule 3 - Blocking] Registered the handler in two harnesses the plan did not name**
- **Found during:** full hermetic-suite verification (after Task 3 commit)
- **Issue:** The plan named only `KeeperProbeLoopTests` (and implicitly `KeeperFaultConsumerScopeTests`). But
  `KeeperRoundRobinTests` and `KeeperHostBootFixture` also stand up the fault consumers; without the handler the
  MassTransit consumer factory cannot resolve the consumer at runtime, so the message is never consumed
  (round-robin `count==0`; boot-fixture mirror gap).
- **Fix:** Added `.AddSingleton<KeeperRecoveryHandler>()` to both harnesses (the boot fixture mirrors `Program.cs`).
- **Files modified:** `tests/BaseApi.Tests/Keeper/KeeperRoundRobinTests.cs`, `tests/BaseApi.Tests/Keeper/KeeperHostBootFixture.cs`
- **Commit:** `c7b6c4f`

### Direct-construction test fixes (in-scope, not deviations)
`KeeperPausePublishTests` and `KeeperMetricsFacts` constructed the consumer via `new` with the old 3-arg signature.
Updated each to `new KeeperRecoveryHandler(NullLogger<KeeperRecoveryHandler>.Instance, recovery, metrics)` then
`new FaultEntryStepDispatchConsumer(handler)` — behavior identical (the consumer delegates to the handler).

## Authentication Gates

None.

## Known Stubs

None. The extraction is behavior-preserving; no placeholder/empty data paths introduced.

## Self-Check: PASSED

- `src/Keeper/Recovery/KeeperRecoveryHandler.cs` — FOUND
- Commit `119b84f` (Task 1) — FOUND
- Commit `8b8eec6` (Task 2) — FOUND
- Commit `d6cd8ae` (Task 3) — FOUND
- Commit `c7b6c4f` (harness fixes) — FOUND
