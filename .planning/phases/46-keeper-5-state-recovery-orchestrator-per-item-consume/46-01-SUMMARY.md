---
phase: 46-keeper-5-state-recovery-orchestrator-per-item-consume
plan: 01
subsystem: infra
tags: [masstransit, dotnet, redis, keeper, recovery, retry, contracts, tdd]

# Dependency graph
requires:
  - phase: 43-message-contracts-l2-key-reshape
    provides: KeeperReinject record + IKeeperRecoverable partition marker (now gains Payload)
  - phase: 44-processor-pre-in-post-process-pipeline
    provides: RetryLoop + ProcessorPipeline.BuildReinject send site
  - phase: 45-keeper-bit-health-gate-global-pause-resume
    provides: IL2HealthGate primitive (consumed by the Wave-1 recovery base)
provides:
  - RetryLoop + RetryOutcome<T> relocated to BaseConsole.Core.Resilience (reusable by Keeper + BaseProcessor.Core)
  - KeeperReinject.Payload field for faithful EntryStepDispatch reconstruction (D-01)
  - ProcessorPipeline.BuildReinject stamps Payload onto KeeperReinject
  - KeeperReinject golden contract test pins the Payload field
  - 11 RED Phase-46 test stubs (8 files) as automated targets for Waves 1-2
affects: [46-02, 46-03, 46-04, 47-resilience-dlq1, 48-retire-reactive-recovery]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Shared A3 retry helper lives in BaseConsole.Core so both processor and console (Keeper) reuse one implementation"
    - "Recovery-envelope contract carries the step Payload so a recovered run is config-faithful to a direct dispatch"
    - "Wave-0 RED stubs under [Trait(\"Phase\",\"46\")] give every downstream task a discoverable --filter target"

key-files:
  created:
    - src/BaseConsole.Core/Resilience/RetryLoop.cs
    - tests/BaseApi.Tests/Keeper/UpdateConsumerFacts.cs
    - tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs
    - tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs
    - tests/BaseApi.Tests/Keeper/DeleteConsumerFacts.cs
    - tests/BaseApi.Tests/Keeper/CleanupConsumerFacts.cs
    - tests/BaseApi.Tests/Keeper/RecoveryGateWaitFacts.cs
    - tests/BaseApi.Tests/Keeper/RecoveryPartitionFacts.cs
    - tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs
  modified:
    - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
    - src/Messaging.Contracts/KeeperReinject.cs
    - tests/BaseApi.Tests/Processor/RetryLoopFacts.cs
    - tests/BaseApi.Tests/Contracts/KeeperContractTests.cs

key-decisions:
  - "D-05: relocate RetryLoop (move, not duplicate) so there is ONE A3 retry implementation Keeper can reference past its csproj firewall"
  - "D-01: add Payload as a string init-only property mirroring KeeperUpdate.ValidatedData; stamp it in BuildReinject in the same commit as the contract + test (Pitfall 5 atomicity)"
  - "KeyAbsentException stays in BaseProcessor.Core; ProcessorPipeline keeps both usings (BaseConsole for RetryLoop, BaseProcessor for KeyAbsentException)"

patterns-established:
  - "Pattern: shared resilience primitives belong in BaseConsole.Core (lowest common project both consoles + processor reference)"
  - "Pattern: contract + producer + golden test change in one atomic commit (no silent contract drift)"
  - "Pattern: Wave-0 RED stubs reference no unbuilt production types so the test project keeps compiling"

requirements-completed: [KEEP-04, KEEP-05, KEEP-06, KEEP-07, KEEP-08, KEEP-09, ORCH-01]

# Metrics
duration: 33min
completed: 2026-06-08
---

# Phase 46 Plan 01: Recovery Foundation Summary

**Relocated the shared RetryLoop into BaseConsole.Core, added Payload to KeeperReinject (contract + BuildReinject + golden test atomically), and scaffolded 11 RED Phase-46 test stubs so every Keeper/Orchestrator consumer in later waves has a discoverable automated target.**

## Performance

- **Duration:** ~33 min
- **Started:** 2026-06-08T20:08Z
- **Completed:** 2026-06-08T20:42Z
- **Tasks:** 3
- **Files modified:** 12 (1 renamed/relocated, 3 edited, 8 created)

## Accomplishments
- D-05: `RetryLoop` + `RetryOutcome<T>` moved from `BaseProcessor.Core.Resilience` to `BaseConsole.Core.Resilience` (git-tracked as a rename) so Keeper — whose csproj firewall references only `BaseConsole.Core` + `Messaging.Contracts` — can reuse the single A3 retry helper. `KeyAbsentException` deliberately left behind in `BaseProcessor.Core`.
- D-01: `KeeperReinject` gained `public string Payload { get; init; } = "";`; `ProcessorPipeline.BuildReinject` now stamps `Payload = d.Payload`; the `KeeperContractTests` golden test pins Payload as a string init-only property — all three in ONE commit (Pitfall 5).
- Created 8 new test files holding 11 RED stubs under `[Trait("Phase","46")]`, each `Assert.Fail`-ing and referencing no unbuilt production type so the test project compiles.

## Task Commits

1. **Task 1: Relocate RetryLoop to BaseConsole.Core (D-05)** - `f4f1ce5` (refactor)
2. **Task 2: D-01 Payload ripple — contract + BuildReinject + golden test** - `c9ec1e9` (feat)
3. **Task 3: 11 Wave-0 RED test stubs** - `84b8f91` (test)

## Files Created/Modified
- `src/BaseConsole.Core/Resilience/RetryLoop.cs` - relocated RetryLoop + RetryOutcome<T> (namespace BaseConsole.Core.Resilience)
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` - dual using (BaseConsole + BaseProcessor Resilience); BuildReinject stamps Payload
- `src/Messaging.Contracts/KeeperReinject.cs` - added Payload init-only property (D-01)
- `tests/BaseApi.Tests/Processor/RetryLoopFacts.cs` - using points at the new namespace
- `tests/BaseApi.Tests/Contracts/KeeperContractTests.cs` - pins KeeperReinject.Payload (string, init-only)
- `tests/BaseApi.Tests/Keeper/{Update,Reinject,Inject,Delete,Cleanup}ConsumerFacts.cs` + `RecoveryGateWaitFacts.cs` + `RecoveryPartitionFacts.cs` - RED stubs (KEEP-04..09)
- `tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs` - RED stubs (ORCH-01)

## Decisions Made
- Relocation surfaced as a git rename (97% similarity) — clean, no dangling old file.
- ProcessorPipeline.cs keeps BOTH `using BaseConsole.Core.Resilience;` (RetryLoop) and `using BaseProcessor.Core.Resilience;` (KeyAbsentException line 78) — the plan anticipated this coexistence and it was required.
- Stub method/class names match the VALIDATION filters verbatim so downstream `<automated>` targets resolve.

## Deviations from Plan

None - plan executed exactly as written. The plan's Task-1 contingency ("if ProcessorPipeline also uses KeyAbsentException, ADD a second using back") was the realized path, not a deviation — it was an explicit branch in the plan action.

## Issues Encountered
- A leftover background test run (PID 445360) held a file lock on the test bin DLLs, causing one `dotnet build` of the test project to fail with MSB3021/MSB3027 (file-in-use). Resolved when that background run completed and released the lock (Rule 3 — blocking, transient); the rebuild then succeeded cleanly. No code change required.

## Verification
- `dotnet build` (full solution `SK_P.sln`): **Build succeeded, 0 errors**.
- RetryLoopFacts (MTP filter `*RetryLoopFacts*`): **5/5 passed**.
- KeeperContractTests (MTP filter `*KeeperContractTests*`): **8/8 passed** (Payload pin green).
- Phase-46 stubs (MTP filter trait `Phase=46`): **11 discovered, 11 RED** (expected/acceptable for scaffolding; ≥11 target met).

Note: the test project uses Microsoft.Testing.Platform (xunit.v3), which ignores the VSTest `--filter` property used by `dotnet test`; filters were applied via `dotnet run -- --filter-method` / `--filter-trait`. A full `dotnet test` run shows 2 unrelated failures (`SampleRoundTripE2ETests`, `MetricsRoundTripE2ETests`) — both are LIVE E2E tests requiring the docker compose processor-sample container, which is not up in this environment (out of scope per SCOPE BOUNDARY; not caused by these changes).

## Next Phase Readiness
- D-05 + D-01 prerequisites are in place: Wave-1 Keeper bodies can call `BaseConsole.Core.Resilience.RetryLoop` and read `KeeperReinject.Payload`.
- 11 RED `[Trait("Phase","46")]` stubs give Waves 1-2 concrete automated targets to turn green.
- **D-03 OQ-1 downstream awareness (carried for 46-02/03):** gate-wait on bound exhaustion must throw a TRANSIENT marker that the endpoint `UseMessageRetry` re-attempts (MassTransit v8 routes the thrown delivery to `skp-dlq-1`; it does NOT broker-requeue). Mirror the `L2ProbeRecovery` await-inside-Consume precedent.
- Deferred (user-owned, not a blocker): design-doc amendment recording Payload-on-KeeperReinject (D-01).

## Self-Check: PASSED

- All 9 created files verified present on disk; old `src/BaseProcessor.Core/Resilience/RetryLoop.cs` confirmed removed.
- All 3 task commits verified in git history: `f4f1ce5`, `c9ec1e9`, `84b8f91`.

---
*Phase: 46-keeper-5-state-recovery-orchestrator-per-item-consume*
*Completed: 2026-06-08*
