---
phase: 36-l2-health-probe-recovery-loop-dlqs
plan: 01
subsystem: Keeper / Messaging.Contracts (contract surface + Wave-0 test scaffolding)
tags: [keeper, dlq, probe-loop, contracts, config, test-double, wave-0]
requires:
  - "Messaging.Contracts.KeeperQueues (FaultRecovery const, Phase 34)"
  - "Messaging.Contracts.Projections.L2ProjectionKeys (Prefix/Flag, Phase 21/22)"
  - "Messaging.Contracts.Configuration.RetryOptions (shape analog)"
  - "Keeper console + Program.cs RetryOptions bind (Phase 34/35)"
  - "tests/BaseApi.Tests references Keeper.csproj (Phase 34)"
provides:
  - "KeeperQueues.DeadLetter = keeper-dlq (DLQ-2, terminal probe give-up queue)"
  - "L2ProjectionKeys.KeeperProbe(h) => skp:keeper:probe:{h} (probe scratch key)"
  - "Keeper.ProbeOptions (DelaySeconds=5, MaxAttempts=12) + Program.cs Configure bind + appsettings Probe section"
  - "tests/BaseApi.Tests/Keeper/FakeRedis.cs (down/half-open/up IConnectionMultiplexer+IDatabase double)"
  - "tests/BaseApi.Tests/Keeper/ProbeOptionsBoundTests.cs (ProbeOptions_Bound constraint test)"
affects:
  - "Plan 02 (probe loop + consumers — consumes ProbeOptions, KeeperProbe, FakeRedis, DeadLetter)"
  - "Plan 04 (RealStack E2E — consumes the contracts + scratch-key family in net-zero scan)"
tech-stack:
  added: []
  patterns:
    - "Keeper-local sealed-options class (ProbeOptions) mirroring shared RetryOptions shape"
    - "Stateful NSubstitute-backed test double with mutable health flip (FakeRedis)"
    - "Bind-time config constraint asserted as a hermetic Fact (ProbeOptions_Bound)"
key-files:
  created:
    - "src/Keeper/ProbeOptions.cs"
    - "tests/BaseApi.Tests/Keeper/FakeRedis.cs"
    - "tests/BaseApi.Tests/Keeper/ProbeOptionsBoundTests.cs"
  modified:
    - "src/Messaging.Contracts/KeeperQueues.cs"
    - "src/Messaging.Contracts/Projections/L2ProjectionKeys.cs"
    - "src/Keeper/Program.cs"
    - "src/Keeper/appsettings.json"
decisions:
  - "ProbeOptions is Keeper-local (a Keeper-only knob), NOT in shared Messaging.Contracts (RESEARCH Open Q #3)"
  - "FakeRedis is NSubstitute-backed (the repo's established mocking lib, no new package) wrapped in a stateful flip API, rather than a full hand-implemented interface (impractical for the large IConnectionMultiplexer/IDatabase surface)"
  - "No second AddBaseConsoleRedis — IConnectionMultiplexer is already a singleton via AddBaseConsole (PATTERNS correction 1)"
metrics:
  duration: "~25 min"
  completed: 2026-06-05
  tasks: 3
  files: 7
  commits: 4
---

# Phase 36 Plan 01: L2-Probe Contract Surface & Wave-0 Test Scaffolding Summary

Landed the Phase-36 interface-first contract surface and Wave-0 hermetic test scaffolding that Plans 02 and 04 build against: the `keeper-dlq` queue const (DLQ-2), the `KeeperProbe(h)` scratch-key builder, the Keeper-local `ProbeOptions` config class + its appsettings binding, plus the reusable stateful `FakeRedis` down-then-up test double and the `ProbeOptions_Bound` consumer-timeout constraint test.

## What Was Built

### Task 1 — `keeper-dlq` const + `KeeperProbe` key builder (commit `52d2b67`)
- `KeeperQueues.DeadLetter = "keeper-dlq"` (DLQ-2, D-08): plain durable, NO x-message-ttl — its depth is the primary operator alert (Phase 39), so it persists until drained (contrast DLQ-1's TTL).
- `L2ProjectionKeys.KeeperProbe(string h) => "skp:keeper:probe:{h}"` (D-03): short-TTL write-then-delete scratch key, built off the existing `Prefix = "skp:"` const. The transitional `ExecutionData(Guid)` overload was left untouched — the string overload remains the probe-read path Plan 02 binds.
- Verify: `dotnet build src/Messaging.Contracts -c Debug` → 0/0; greps `DeadLetter = "keeper-dlq"`==1, `KeeperProbe`==1, `keeper:probe:{h}`==1.

### Task 2 — `ProbeOptions` class + Program.cs bind + appsettings Probe section (commit `85c526a`)
- `src/Keeper/ProbeOptions.cs` (Keeper-local, namespace `Keeper`): sealed class `DelaySeconds=5`, `MaxAttempts=12`. The load-bearing D-04 constraint: `DelaySeconds × MaxAttempts` (60s) stays 30× under RabbitMQ's 30-min `consumer_timeout` because the loop is awaited inside `Consume`, holding the delivery un-acked.
- `Program.cs`: added `builder.Services.Configure<ProbeOptions>(builder.Configuration.GetSection("Probe"));` immediately after the existing `RetryOptions` bind, plus `using Keeper;` (Program.cs top-level statements are in the global namespace; `ProbeOptions` lives in `Keeper`). Did NOT add `AddBaseConsoleRedis` — `IConnectionMultiplexer` is already a registered singleton via `AddBaseConsole` (PATTERNS correction 1).
- `appsettings.json`: added a `"Probe": { "DelaySeconds": 5, "MaxAttempts": 12 }` block before `"ConsoleHealth"` (JSON comma-correct).
- Verify: `dotnet build src/Keeper -c Release` → 0 Warning / 0 Error; greps `Configure<ProbeOptions>`==1, `AddBaseConsoleRedis`==0, `"Probe"`==1, `"DelaySeconds": 5`==1, `"MaxAttempts": 12`==1.

### Task 3 — Wave-0 FakeRedis double + ProbeOptions_Bound constraint test (commit `8f386c6`, tdd=true)
- `tests/BaseApi.Tests/Keeper/FakeRedis.cs`: a stateful `IConnectionMultiplexer`/`IDatabase` double with a `RedisHealth { Down, HalfOpen, Up }` model and flip methods (`BringUp`/`BringDown`/`HalfOpen`/`SetFailuresBeforeUp(n)`). While Down, every probe op (`StringGetAsync`/`StringSetAsync`/`KeyDeleteAsync`) throws `RedisConnectionException(ConnectionFailureType.UnableToConnect, "fake-down")` — a `RedisException`-derived type (the superset the loop's `catch (RedisException)` catches, RESEARCH A2). HalfOpen = read OK, write/delete throw (PROBE-02). Up = read returns `RedisValue.Null`, write/delete return `true`. `SetFailuresBeforeUp(n)` arms the canonical fail-n-times-then-recover shape for the bounded retry loop. NSubstitute-backed (no new package). Consumed by Plan 02's `KeeperProbeLoopTests`.
- `tests/BaseApi.Tests/Keeper/ProbeOptionsBoundTests.cs`: `ProbeOptions_Bound_Under_RabbitMq_ConsumerTimeout` asserts `DelaySeconds × MaxAttempts` (60s) is `> 0` and `< 30*60` (the consumer_timeout). Named so `--filter ProbeOptions_Bound` matches (VALIDATION.md PROBE-01 command).
- Verify: targeted run (xUnit-v3/MTP `--filter-method "*ProbeOptions_Bound*"`) → 1 passed / 0 failed; full hermetic suite (`--filter-not-trait "Category=RealStack"`) → **458 passed / 0 failed** (was 457 in Phase 35; +1 = the new test, zero regression).

## TDD Gate Compliance

Task 3 was `tdd="true"`. There is no compiling RED state possible: the SUT for `ProbeOptions_Bound` (`Keeper.ProbeOptions`) already shipped in Task 2, so the constraint test was GREEN on first run — it is a bind-time invariant assertion against an existing config class, not a behavior-first feature. The `test(...)` GREEN commit `8f386c6` lands the test alongside the `FakeRedis` double (whose behavior is exercised by Plan 02). This is the same RED-collapse precedent recorded for Phase 35 Plan 02 (a test that references already-compiled SUT types cannot have a failing-to-compile RED). The feature work (`feat(...)`) for the contract surface is in commits `52d2b67`/`85c526a`. No GREEN-skipping-RED bug occurred — the assertion exercises real defaults and would fail if `ProbeOptions` were mis-defaulted.

## Deviations from Plan

None of substance — plan executed as written. Two minor, in-scope adjustments:

**1. [Rule 3 — Blocking issue] Added `using Keeper;` to Program.cs**
- **Found during:** Task 2.
- **Issue:** `ProbeOptions` lives in namespace `Keeper`; Program.cs uses top-level statements (global namespace) and only imported `Keeper.Consumers`, so `ProbeOptions` was unresolved.
- **Fix:** added `using Keeper;` next to the existing `using Keeper.Consumers;`. Build then clean 0/0.
- **Files modified:** src/Keeper/Program.cs (already in scope). **Commit:** `85c526a`.

**2. [PATTERNS-driven] FakeRedis backed by NSubstitute (the repo standard) wrapped in a hand-rolled stateful flip API**
- The plan said "hand-rolled test double ... StackExchange.Redis interfaces are mockable; no new package" and to throw `NotImplementedException` for unused members. A fully hand-implemented `IConnectionMultiplexer`/`IDatabase` (100+ members) is impractical and unmaintainable. Per the repo's established pattern (`OrchestratorTestStubs.cs`), the double is NSubstitute-backed (no new package — NSubstitute is already a dependency) wrapped in a stateful `FakeRedis` class exposing the down/half-open/up flip API and configurable failure counter the `<behavior>` block specifies. The minimal-surface intent is preserved: only the three probe ops + `GetDatabase` are configured; any other call returns NSubstitute defaults.
- **Files:** tests/BaseApi.Tests/Keeper/FakeRedis.cs. **Commit:** `8f386c6`.

No bugs, no missing-critical-functionality, no architectural decisions, no auth gates, no scope creep.

## Threat Model Compliance

Per the plan's `<threat_model>`: T-36-01 (ProbeOptions tampering) is the accepted-with-test disposition — the `ProbeOptions_Bound` test is the named mitigation and now exists GREEN. T-36-02 (scratch-key namespace) / T-36-03 (queue-name disclosure) are config-only constants with no new untrusted-input surface. No new security-relevant surface beyond the planned contracts.

## Git Hygiene

Each task committed atomically with explicit per-file `git add` (no `git add -A`/`.`). No file deletions in any of the 3 task commits. The ~242 pre-existing `.planning/` archive deletions were left UNtouched (NOT staged, NOT reverted) — verified `git status` still shows 242 ` D .planning/...` entries after all commits.

## Verification Summary

| Check | Result |
|-------|--------|
| `dotnet build src/Messaging.Contracts -c Debug` | 0 Warning / 0 Error |
| `dotnet build src/Keeper -c Release` | 0 Warning / 0 Error |
| `dotnet build SK_P.sln -c Release` | 0 Warning / 0 Error |
| `--filter-method "*ProbeOptions_Bound*"` | 1 passed / 0 failed |
| Full hermetic suite (`Category!=RealStack`) | 458 passed / 0 failed (was 457; +1, zero regression) |
| Task 1/2/3 acceptance greps | all met |

## Requirements

PROBE-01 (bounded probe-loop knobs + constraint test), PROBE-02 (half-open read-OK/write-fail in FakeRedis), PROBE-04 (DeadLetter give-up queue const), DLQ-03 (terminal `keeper-dlq` const) — contract/scaffolding slices landed this plan; full runtime behavior (probe loop, re-inject, park) is Plan 02. These are interface-first foundations; the requirements complete across Plans 02/04.

## Self-Check: PASSED

All 5 source/test files + SUMMARY.md exist on disk; all 3 task commits (52d2b67, 85c526a, 8f386c6) present in git history.
