---
phase: 24-orchestrator-result-consume-step-advancement
plan: 03
subsystem: orchestrator-messaging
tags: [orchestrator, dispatch, step-advancement, entry-condition, tdd, masstransit]
dependency_graph:
  requires: [24-01]
  provides: [24-04, 24-05]
  affects: [Orchestrator.Dispatch, Orchestrator.Scheduling, Orchestrator.Consumers]
tech_stack:
  added: []
  patterns: [single-owner-dispatch, pure-match-helper, sealed-exception, tdd-red-green]
key_files:
  created:
    - src/Orchestrator/Dispatch/IStepDispatcher.cs
    - src/Orchestrator/Dispatch/StepDispatcher.cs
    - src/Orchestrator/Dispatch/StepAdvancement.cs
    - src/Orchestrator/Consumers/GateClosedException.cs
    - tests/BaseApi.Tests/Orchestrator/StepAdvancementTests.cs
  modified:
    - src/Orchestrator/Scheduling/WorkflowFireJob.cs
    - tests/BaseApi.Tests/Orchestrator/FireDispatchTests.cs
  decisions: []
metrics:
  duration: ~20min
  completed: 2026-05-31
requirements: [ORCH-ADVANCE-01, ORCH-ADVANCE-02]
---

# Phase 24 Plan 03: Step Dispatch Extraction + Pure Step Advancement Summary

One-liner: Extracted the EntryStepDispatch build+Send into a single-owner `IStepDispatcher`/`StepDispatcher` (refactoring `WorkflowFireJob` to call it), added the pure I/O-free `StepAdvancement.SelectNext` outcome->entry-condition match (`== (int)outcome || == Always(4)`, `Never(5)` never selected), and the gate-closed `GateClosedException` type — the dispatch/match/throw contracts Plan 04's result consumer builds on.

## What Was Built

- **`IStepDispatcher` / `StepDispatcher`** (D-01): the single owner of the `EntryStepDispatch` build-and-Send shape — `Send` (not Publish) to `queue:{processorId:D}` with `executionId`/`entryId` parameterized (not forced `Guid.Empty`) so the result-continuation path (Plan 04) can carry real values. Infra fault on `Send` propagates unchanged.
- **`WorkflowFireJob` refactor**: ctor now injects `IStepDispatcher dispatcher` (replacing `ISendEndpointProvider sendProvider`); the inline `new EntryStepDispatch(...)` + `GetSendEndpoint` + `Send` block is replaced by one `dispatcher.DispatchAsync(..., Guid.Empty, Guid.Empty, ct)` call (initial fire). The liveness-refresh + self-reschedule block is untouched. `using MassTransit;` stays (`NewId` at line 53); `using Messaging.Contracts;` stays (`LivenessProjection`).
- **`StepAdvancement.SelectNext`** (D-02 / SPEC req 3): pure match+traversal — `private const int Always = 4`; predicate `next.EntryCondition == (int)outcome || next.EntryCondition == Always`; `Never(5)` falls out of the predicate (never selected); a dangling `NextStepIds` id is skipped via `TryGetValue` (no throw). No Redis/store reference — step map passed as argument.
- **`GateClosedException`** (D-06): sealed `Exception` subclass for the gate-closed throw, mirroring `WorkflowRootNotFoundException`. CRITICAL: NOT added to any `r.Ignore<>()` list — it must flow to redelivery.
- **`StepAdvancementTests`** (pure, no harness): table-driven `[Theory]` matrix over all four outcomes asserting matched-condition+Always selection, Never-never, non-matching-conditions-excluded, dangling-skip, and no-I/O signature.

## How It Works

`WorkflowFireJob` (cron initial fire) and the future `ResultConsumer` (continuation) now share one dispatch implementation — a single change to the queue convention or message shape lands in `StepDispatcher` only. The advancement match is a free function: given an outcome (0-3), the completed step, and the L1 step map, it yields the next steps to dispatch. `Always(4)` is an orchestrator-side constant (not on the `StepOutcome` enum, which is contracts-only 0-3); `Never(5)` is structurally unreachable because `(int)outcome` is 0-3 and `Always` is 4.

## Verification

- `dotnet build src/Orchestrator/Orchestrator.csproj -c Debug` — Build succeeded, 0 Warning(s) / 0 Error(s).
- `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug` — Build succeeded, 0 Warning(s) / 0 Error(s) (after the FireDispatchTests reconcile, Rule 1 below).
- `dotnet test --filter "FullyQualifiedName~StepAdvancement"` — Passed: 17, Failed: 0 (full outcome x entry-condition matrix incl. Never-never + dangling-skip).
- `dotnet test --filter "FullyQualifiedName~Orchestrator"` — Passed: 56, Failed: 0 (no regression after the WorkflowFireJob refactor; the `~Orchestrator` namespace filter spans the 17 StepAdvancement + 39 fire/lifecycle/contract tests).

## TDD Gate Compliance

Plan task 2 was `tdd="true"`. Gate sequence held: RED commit `83b75d7` (`test(24-03): ...`) — test failed to compile because `StepAdvancement` did not exist (a genuine RED, not a false-pass); GREEN commit `ddd73b1` (`feat(24-03): implement pure StepAdvancement...`) — 17/17 pass. No REFACTOR commit needed (implementation clean as written).

## Acceptance Criteria

Task 1:
- IStepDispatcher contains `interface IStepDispatcher` with `DispatchAsync(` taking `executionId`/`entryId` — yes.
- StepDispatcher contains `GetSendEndpoint(new Uri($"queue:{processorId:D}"))` and `.Send(msg, ct)` — yes.
- WorkflowFireJob contains `dispatcher.DispatchAsync(` and `IStepDispatcher dispatcher`; does NOT contain `new EntryStepDispatch(` — verified (0 matches).
- GateClosedException contains `class GateClosedException` and `: Exception(` — yes; NOT ignore-listed.
- Orchestrator build exits 0 — yes.

Task 2:
- StepAdvancement contains `private const int Always = 4` and `next.EntryCondition == (int)outcome || next.EntryCondition == Always` — yes.
- StepAdvancement does NOT contain `StepEntryCondition` / `IDatabase` / `IConnectionMultiplexer` / `IWorkflowL1Store` — yes (pure).
- Tests assert Never(5) never selected for any outcome — yes (`NeverIsNeverSelected_ForAnyOutcome` Theory x4).
- Tests do NOT contain `AddMassTransitTestHarness` — yes (pure test).
- StepAdvancement filter exits 0 — yes.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Reconciled `FireDispatchTests` to the new `WorkflowFireJob` ctor**
- **Found during:** Task 2 verification (the `~Orchestrator` regression slice did not compile).
- **Issue:** the existing `tests/BaseApi.Tests/Orchestrator/FireDispatchTests.cs` constructs `WorkflowFireJob` at 3 sites passing `harness.Bus` (an `IBus`) into the 2nd ctor slot, which was `ISendEndpointProvider`. Task 1 changed that slot to `IStepDispatcher`, so the test assembly failed to compile (`CS1503: cannot convert IBus to IStepDispatcher`) — which also masked the StepAdvancement test result (the whole assembly was broken).
- **Fix:** the 3 sites now pass `new StepDispatcher(harness.Bus)` (the real dispatcher wrapping the harness bus — `IBus` satisfies `ISendEndpointProvider`), preserving the test's intent (harness captures the `Send`). Added `using Orchestrator.Dispatch;`.
- **Files modified:** `tests/BaseApi.Tests/Orchestrator/FireDispatchTests.cs`
- **Commit:** `8a3149c`
- **Result:** test assembly builds 0/0; StepAdvancement 17/17; Orchestrator slice 56/56 (no regression).

The KNOWN GOTCHA from 24-02 (BOM / off test paths) did not apply: the new `StepAdvancementTests.cs` is plain ASCII at the verified path `tests/BaseApi.Tests/Orchestrator/` with namespace `BaseApi.Tests.Orchestrator`.

## Commits

- `24f6668` feat(24-03): extract IStepDispatcher; refactor WorkflowFireJob; add GateClosedException (Task 1)
- `83b75d7` test(24-03): add failing table-driven test for StepAdvancement.SelectNext (Task 2 RED)
- `ddd73b1` feat(24-03): implement pure StepAdvancement.SelectNext match + traversal (Task 2 GREEN)
- `8a3149c` fix(24-03): reconcile FireDispatchTests to IStepDispatcher ctor change (Rule 1)

## Self-Check: PASSED

All 6 key files present on disk; all 4 commits (24f6668, 83b75d7, ddd73b1, 8a3149c) present in git log.
