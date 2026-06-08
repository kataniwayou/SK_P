---
phase: 45-keeper-bit-health-gate-global-pause-resume
plan: "00"
subsystem: messaging-contracts + test-scaffold
tags: [wave-0, contracts, nyquist, red-stubs, no-h, keeper, orchestrator]
requires: [Messaging.Contracts.ICorrelated]
provides:
  - Messaging.Contracts.PauseAll        # no-H global pause broadcast contract
  - Messaging.Contracts.ResumeAll       # no-H global resume broadcast contract
  - BaseApi.Tests.Keeper.Health.L2HealthGateTests       # KEEP-03 RED stubs
  - BaseApi.Tests.Keeper.Health.BitHealthLoopTests      # KEEP-01/02 RED stubs
  - BaseApi.Tests.Orchestrator.Consumers.PauseAllConsumerTests   # ORCH-02 RED stubs
  - BaseApi.Tests.Orchestrator.Consumers.ResumeAllConsumerTests  # ORCH-02 RED stubs
  - BaseApi.Tests.Orchestrator.Consumers.ResumeNoBurstTests      # ORCH-02 no-herd negative RED stub
affects:
  - 45-01  # Keeper BIT loop + L2 health gate (turns Keeper stubs green; consumes PauseAll/ResumeAll)
  - 45-02  # Orchestrator PauseAll/ResumeAll consumers (turns Orchestrator stubs green; consumes PauseAll/ResumeAll)
tech-stack:
  added: []          # no new NuGet packages, no new test project (D — uses shared BaseApi.Tests)
  patterns:
    - "no-H ICorrelated broadcast record (copied from StartOrchestration shape; RETIRE-01 posture)"
    - "type-free Assert.Fail RED stub (one named [Fact] per requirement behavior, compiles before production type exists)"
key-files:
  created:
    - src/Messaging.Contracts/PauseAll.cs
    - src/Messaging.Contracts/ResumeAll.cs
    - tests/BaseApi.Tests/Keeper/Health/L2HealthGateTests.cs
    - tests/BaseApi.Tests/Keeper/Health/BitHealthLoopTests.cs
    - tests/BaseApi.Tests/Orchestrator/Consumers/PauseAllConsumerTests.cs
    - tests/BaseApi.Tests/Orchestrator/Consumers/ResumeAllConsumerTests.cs
    - tests/BaseApi.Tests/Orchestrator/Consumers/ResumeNoBurstTests.cs
  modified: []
decisions:
  - "PauseAll/ResumeAll carry ONLY a tracing Guid CorrelationId — no H, no WorkflowId, no dedup key (pure broadcast, RETIRE-01)."
  - "Wave-0 test stubs are deliberately type-free (no reference to not-yet-built Keeper.Health.* / Orchestrator.Consumers.*) so the shared BaseApi.Tests project compiles NOW; bodies are Assert.Fail placeholders that 45-01/45-02 replace with real assertions against the production types."
metrics:
  duration: ~9m
  completed: 2026-06-08
requirements: [KEEP-01, KEEP-02, KEEP-03, ORCH-02]
---

# Phase 45 Plan 00: Wave-0 Foundation (Contracts + RED Test Scaffold) Summary

Two no-`H` global control contracts (`PauseAll`/`ResumeAll`) plus the failing test scaffold mapping every Phase-45 requirement (KEEP-01/02/03, ORCH-02) to a named, runnable RED test — the shared interface-first contract both downstream branches (45-01 Keeper BIT loop, 45-02 Orchestrator consumers) compile and assert against.

## What Was Built

**Task 1 — Contracts (`cbbf904`):**
- `src/Messaging.Contracts/PauseAll.cs` — `public sealed record PauseAll : ICorrelated`, carrying only `Guid CorrelationId { get; init; }`. Byte-for-byte the `StartOrchestration` no-`H` shape; explicitly NOT the legacy `PauseWorkflow(Guid WorkflowId, string H)` shape.
- `src/Messaging.Contracts/ResumeAll.cs` — identical shape for resume.
- `dotnet build src/Messaging.Contracts/Messaging.Contracts.csproj -c Debug` = 0 warnings / 0 errors. Grep confirms neither file contains `string H` or `WorkflowId`.

**Task 2 — Keeper Wave-0 RED stubs (`dc6fe6d`):**
- `tests/BaseApi.Tests/Keeper/Health/L2HealthGateTests.cs` — 6 KEEP-03 gate behaviors (`Gate_Starts_Closed_WaitForOpenAsync_Blocks_Until_Open`, `Open_Completes_The_Wait`, `Close_After_Open_Re_Blocks`, `WaitForOpenAsync_Throws_OperationCanceledException_On_Cancel`, `Open_Is_Idempotent`, `Close_Is_Idempotent`).
- `tests/BaseApi.Tests/Keeper/Health/BitHealthLoopTests.cs` — 6 KEEP-01/02 behaviors (`Probe_RedisException_Reports_Unhealthy_Loop_Survives`, `Probe_NonRedis_Throw_Propagates_Not_Swallowed`, `StoppingToken_Ends_Loop_Cleanly`, `Edge_Trigger_Publishes_PauseAll_Once_On_Healthy_To_Unhealthy`, `Edge_Trigger_Publishes_ResumeAll_Once_On_Unhealthy_To_Healthy`, `Same_State_Ticks_Publish_Nothing`).

**Task 3 — Orchestrator Wave-0 RED stubs (`06b4490`):**
- `tests/BaseApi.Tests/Orchestrator/Consumers/PauseAllConsumerTests.cs` — `Consume_Calls_Scheduler_PauseAll`, `Redelivery_Is_Idempotent_No_Op`.
- `tests/BaseApi.Tests/Orchestrator/Consumers/ResumeAllConsumerTests.cs` — `Consume_Enumerates_WorkflowIds_And_Calls_ResumeAsync_Each`, `Resume_Of_Non_Paused_Trigger_Is_Ignored`.
- `tests/BaseApi.Tests/Orchestrator/Consumers/ResumeNoBurstTests.cs` (the load-bearing negative) — `Native_ResumeAll_Is_Never_Called`, `Resume_Reschedules_Fresh_From_Now_StartAt_Ge_Now`.

Each stub carries a top-of-file `// TODO(45-01)` / `// TODO(45-02)` header naming the production types and the fakes the real bodies will need (IScheduler spy, fake IWorkflowL1Store, WorkflowLifecycle/WorkflowScheduler), and the intended use of `var ct = TestContext.Current.CancellationToken;`.

## Verification

- `dotnet build src/Messaging.Contracts/Messaging.Contracts.csproj -c Debug` → 0/0.
- `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug` → 0/0 (all stubs compile).
- `dotnet build SK_P.sln -c Release` → 0 warnings / 0 errors (full solution with new contracts + stubs).
- MTP runner (`BaseApi.Tests.exe --filter-query`) per class — all RED as expected for Wave 0:
  - `L2HealthGateTests` total 6 / failed 6.
  - `BitHealthLoopTests` total 6 / failed 6.
  - `PauseAllConsumerTests` total 2 / failed 2.
  - `ResumeAllConsumerTests` total 2 / failed 2.
  - `ResumeNoBurstTests` total 2 / failed 2.
- Total Wave-0 scaffold = 18 named RED stubs across 5 files, mapping KEEP-01/02/03 + ORCH-02.

## Deviations from Plan

None — plan executed exactly as written. The plan offered a choice between guarded-real-bodies vs. type-free `Assert.Fail` stubs and explicitly directed the type-free path "for THIS task" (so the shared project compiles before 45-01/45-02 land the production types); that directed path was taken.

### Tooling note (not a content deviation)

`dotnet test --filter "FullyQualifiedName~..."` is the VSTest filter idiom; this project uses xUnit v3 under Microsoft.Testing.Platform (MTP), where the filter idiom is `--filter-query "/asm/ns/class/method"`. Verification was run by invoking the built MTP executable directly with `--filter-query` per test class. The plan's `<automated>` verify lines name `dotnet test --filter` for documentation; the equivalent MTP invocation was used to produce the RED evidence above. No code/behavior change.

## Known Stubs

By design — this is the Wave-0 RED-stub plan. All five test files contain `Assert.Fail("RED — 45-0x must implement ...")` bodies. These are intentional and tracked: 45-01 replaces the Keeper stub bodies (against `Keeper.Health.IL2HealthGate`/`L2HealthGate`/`BitHealthLoop`) and 45-02 replaces the Orchestrator stub bodies (against `Orchestrator.Consumers.PauseAllConsumer`/`ResumeAllConsumer`). No production stubs (no hardcoded empty values flowing to UI/runtime) were introduced — the two new contracts are complete, not stubbed.

## Threat Flags

None. The two contracts carry only a `Guid CorrelationId` (T-45-01 mitigated: no free-text payload; T-45-02 accepted: nothing sensitive on the wire). This plan introduces no logging, no network endpoints, no auth paths, no schema changes. No new threat surface beyond the plan's `<threat_model>`.

## Self-Check: PASSED

- FOUND: src/Messaging.Contracts/PauseAll.cs
- FOUND: src/Messaging.Contracts/ResumeAll.cs
- FOUND: tests/BaseApi.Tests/Keeper/Health/L2HealthGateTests.cs
- FOUND: tests/BaseApi.Tests/Keeper/Health/BitHealthLoopTests.cs
- FOUND: tests/BaseApi.Tests/Orchestrator/Consumers/PauseAllConsumerTests.cs
- FOUND: tests/BaseApi.Tests/Orchestrator/Consumers/ResumeAllConsumerTests.cs
- FOUND: tests/BaseApi.Tests/Orchestrator/Consumers/ResumeNoBurstTests.cs
- FOUND commit: cbbf904 (feat 45-00 contracts)
- FOUND commit: dc6fe6d (test 45-00 Keeper stubs)
- FOUND commit: 06b4490 (test 45-00 Orchestrator stubs)
