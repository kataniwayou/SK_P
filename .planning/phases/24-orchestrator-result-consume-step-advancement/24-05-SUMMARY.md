---
phase: 24-orchestrator-result-consume-step-advancement
plan: 05
subsystem: orchestrator-messaging
tags: [orchestrator, conditionless-reload, drain-stop, keep-l1, gate-closed-throw, scheduled-redelivery, masstransit]
dependency_graph:
  requires: [24-03, 24-04]
  provides: []
  affects: [Orchestrator.Consumers, Orchestrator.Hydration]
tech_stack:
  added: []
  patterns: [conditionless-consumer, gate-closed-throw, keep-l1-drain, scheduled-redelivery-before-retry, unschedule-only-split]
key-files:
  created: []
  modified:
    - src/Orchestrator/Hydration/WorkflowLifecycle.cs
    - src/Orchestrator/Consumers/StartOrchestrationConsumer.cs
    - src/Orchestrator/Consumers/StopOrchestrationConsumer.cs
    - src/Orchestrator/Consumers/StartOrchestrationConsumerDefinition.cs
    - src/Orchestrator/Consumers/StopOrchestrationConsumerDefinition.cs
    - tests/BaseApi.Tests/Orchestrator/StartConsumerLifecycleTests.cs
    - tests/BaseApi.Tests/Orchestrator/StopConsumerLifecycleTests.cs
    - tests/BaseApi.Tests/Orchestrator/AckSemanticsTests.cs
decisions:
  - "Stop split: added WorkflowLifecycle.UnscheduleOnlyAsync (unschedule, NO store.Remove) for the drain-stop path (D-07); kept the unchanged TeardownAsync (unschedule + store.Remove) for the conditionless Start reload pre-clean (Pitfall 4 — Start must delete the old Quartz job before re-scheduling; the immediate re-hydrate re-Upserts L1 so the transient remove is harmless)."
  - "Both Start/Stop consumers became conditionless: removed the per-workflow TryAcquire/continue/finally-Release stripe (D-05 — WebApi dedups in Plan 02) and dropped the now-unused IWorkflowL1Store store ctor param + Orchestrator.L1 using from BOTH consumers to keep the zero-warning Release build."
  - "Gate-closed INVERTED from ack-return (Phase 23 D-12) to throw new GateClosedException() in BOTH consumers (D-06) — the same exception ResultConsumer throws (Plan 04), reaching the redelivery middleware (NOT Ignore<>-listed)."
  - "Reused the existing redelivery wiring from 24-04 (Program.cs AddDelayedMessageScheduler + UseDelayedMessageScheduler) — did not recreate it. Added only the per-endpoint UseScheduledRedelivery(5s/15s/30s/60s) BEFORE UseMessageRetry(Immediate(3)) on the Start/Stop definitions (Pitfall 2 order, same policy as ResultConsumerDefinition A1)."
  - "Late-result drain test drives a real ExecutionResult through ResultConsumer AFTER a Stop and asserts the kept L1 entry still resolves + dispatches the matching next step (reuses the Plan 03 StepAdvancement/IStepDispatcher + NSubstitute dispatcher; no new harness)."
metrics:
  duration: ~25min
  completed: 2026-06-01
requirements: [ORCH-START-RELOAD-01, ORCH-STOP-DRAIN-01, ORCH-GATE-01]
---

# Phase 24 Plan 05: Conditionless Start/Stop Consumers + Gate-Closed Throw Summary

One-liner: Redesigned the orchestrator Start/Stop consumers to be conditionless and never-drop — gate-open Start unconditionally teardown→hydrate→reschedules (no existence skip, no stripe; revives a lingering post-stop workflow), gate-open Stop unconditionally unschedules the Quartz job but KEEPS the L1 entry (drain), and gate-closed Start AND Stop now THROW `GateClosedException` into the redelivery-before-retry middleware so they reprocess after `MarkReady`.

## What Was Built

- **WorkflowLifecycle.UnscheduleOnlyAsync (D-07 split):** a new method that resolves the jobId from L1 and `UnscheduleAsync(jobId)` (jobId-addressed `DeleteJob`) but does NOT call `store.Remove` — the L1 entry is KEPT so a late `ExecutionResult` for the stopped workflow still resolves in L1 and drains. The existing `TeardownAsync` (unschedule + `store.Remove`) is unchanged and is still used by the conditionless Start reload pre-clean (Pitfall 4). `HydrateAndScheduleAsync` and `IsInfra`/`IsBusiness` are untouched.
- **StartOrchestrationConsumer (ORCH-START-RELOAD-01):** conditionless reload. For each `WorkflowId` (gate open) it unconditionally `TeardownAsync` (unschedule old job + clear L1) then `HydrateAndScheduleAsync` (re-apply current L2 + schedule) — no `TryGet` existence skip, no `TryAcquire`/`Release` stripe. Gate-closed → `throw new GateClosedException()` (D-06, inverts the Phase 23 ack-drop). The now-unused `IWorkflowL1Store store` ctor param and `Orchestrator.L1` using were removed (zero-warning build).
- **StopOrchestrationConsumer (ORCH-STOP-DRAIN-01):** conditionless drain-stop. For each `WorkflowId` (gate open) it calls `UnscheduleOnlyAsync` (delete job, KEEP L1) — NOT `TeardownAsync`, no stripe. Gate-closed → `throw new GateClosedException()`. Same ctor-param / using cleanup.
- **Start/StopOrchestrationConsumerDefinition (ORCH-GATE-01):** added `UseScheduledRedelivery(5s/15s/30s/60s)` BEFORE the existing `UseMessageRetry(Immediate(3))` (Pitfall 2 / GitHub #1575 order — outer redelivery, inner immediate retry). Kept `Ignore<WorkflowRootNotFoundException>`; `GateClosedException` is deliberately NOT `Ignore<>`-listed so it reaches the redelivery layer. Shared `EndpointName = "orchestrator"` (fan-out) unchanged. The bus-level scheduler that backs `UseScheduledRedelivery` was already wired by 24-04 (`AddDelayedMessageScheduler` + `UseDelayedMessageScheduler` in `Program.cs`) and was reused, not recreated.
- **Reconciled lifecycle tests:**
  - `StartConsumerLifecycleTests` (4 facts): hydrate+schedule the consumed workflow; **already-in-L1 → re-hydrates+reschedules (no skip)**; **stop→start revives a live job**; **gate-closed → `Assert.ThrowsAsync<GateClosedException>`** (no hydrate/schedule). (Replaced the Phase 23 ack-drop + single happy-path facts.)
  - `StopConsumerLifecycleTests` (3 facts): **stop deletes the job but the L1 entry REMAINS** (`store.TryGet` true) + zero L2 writes; **a late `ExecutionResult` after Stop still resolves in L1 and dispatches its matching next step** (driven through `ResultConsumer` over the kept L1 entry, NSubstitute `IStepDispatcher`); **gate-closed → `Assert.ThrowsAsync<GateClosedException>`** (no teardown, job+L1 survive). (Replaced the Phase 23 L1-clear + ack-drop facts.)

## How It Works

Duplicate-suppression moved to the WebApi (Plan 02), so the orchestrator's Start/Stop consumers carry no idempotency logic. With the gate open, a Start re-applies the current L2 definition every time (idempotent reload: teardown removes the old Quartz job, hydrate re-Upserts L1 with a fresh jobId from the re-read root and reschedules), and a Stop deletes the Quartz job while leaving the L1 entry in place so in-flight executions drain — a late processor result for the stopped workflow still matches its next steps in L1 and dispatches the continuation. With the gate closed (hydration in flight), both consumers throw `GateClosedException`; `UseScheduledRedelivery` removes-and-reschedules the control message on the 5s/15s/30s/60s ladder so it is reprocessed once `MarkReady` opens the gate, after which a genuine outage exhausts to `_error`.

## Verification

- `dotnet build src/Orchestrator/Orchestrator.csproj -c Release` — Build succeeded, 0 Warning(s) / 0 Error(s).
- `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug` — Build succeeded, 0 Warning(s) / 0 Error(s).
- Orchestrator namespace slice (`--filter-namespace BaseApi.Tests.Orchestrator`) — total: 78, failed: 0, passed: 78 (71 from 24-04 + the net-new lifecycle facts). No regression.
- Full suite `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` (run AFTER deleting `obj/bin` for an honest from-scratch compile) — total: 309, failed: 0, passed: 309, skipped: 0 (phase-merge gate).

Note (extends the 24-04 stale-DLL memory): the per-task slice was run with `--filter-namespace` (unquoted); `--filter-class` returns "Zero tests ran" in this environment. Incremental `dotnet build` masked the `AckSemanticsTests` ctor break (Deviation 2) — the trustworthy build/test numbers above are from a from-scratch rebuild after `rm -rf */obj */bin`.

## Acceptance Criteria

Task 1:
- `WorkflowLifecycle.cs` contains `UnscheduleOnlyAsync` and that method does NOT contain `store.Remove` — yes.
- `WorkflowLifecycle.cs` `TeardownAsync` STILL contains `store.Remove` — yes (unchanged, Start reload pre-clean).
- `StartOrchestrationConsumer.cs` contains `throw new GateClosedException()` and does NOT contain `TryAcquire` / `store.Release` — yes (stripe removed; store param dropped entirely).
- `StopOrchestrationConsumer.cs` contains `throw new GateClosedException()`, contains `UnscheduleOnlyAsync`, and does NOT contain `TeardownAsync` / `TryAcquire` / `store.Remove` — yes.
- `dotnet build src/Orchestrator/Orchestrator.csproj -c Release` exits 0 with 0 Warning(s) / 0 Error(s) — yes.

Task 2:
- `StartOrchestrationConsumerDefinition.cs` AND `StopOrchestrationConsumerDefinition.cs` each contain `UseScheduledRedelivery` appearing BEFORE `UseMessageRetry` — yes.
- Neither definition contains `Ignore<GateClosedException>` — verified.
- `StopConsumerLifecycleTests.cs` asserts the L1 entry REMAINS after a Stop (no L1-clear assertion survives) — yes (`Assert.Equal(1, store.Count)` + `Assert.True(store.TryGet(...))` post-Stop; the late-result-drain fact further proves it).
- `StartConsumerLifecycleTests.cs` asserts gate-closed Start `ThrowsAsync<GateClosedException>` and an already-in-L1 Start re-hydrates (no skip) — yes.
- Lifecycle test slice exits 0 — yes (within the 78/0 slice).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] CS1574 doc-cref on `ExecutionResult` (warnings-as-errors)**
- **Found during:** Task 1 build.
- **Issue:** the new `<see cref="Messaging.Contracts.ExecutionResult"/>` doc references in `WorkflowLifecycle.UnscheduleOnlyAsync` and `StopOrchestrationConsumer` failed to resolve (CS1574) because `ExecutionResult` is not a using-imported/aliased type in those source files (the alias only exists in `ResultConsumer.cs`), and the project promotes doc warnings to errors.
- **Fix:** replaced the two `<see cref="...ExecutionResult"/>` references with plain `<c>ExecutionResult</c>` text (no behavior change). Folded into the Task 1 commit (amended after the first commit carried the broken cref).
- **Files modified:** src/Orchestrator/Hydration/WorkflowLifecycle.cs, src/Orchestrator/Consumers/StopOrchestrationConsumer.cs
- **Commit:** 8c1e3f4 (Task 1, amended).

**2. [Rule 3 - Blocking] `AckSemanticsTests.cs` referenced the removed 4-arg ctor**
- **Found during:** post-Task-2 self-check (the incremental `dotnet build` had masked this — a from-scratch rebuild after deleting `obj/bin` surfaced it; the stale-build trap from project memory struck again).
- **Issue:** dropping the `IWorkflowL1Store store` ctor param made both consumers 3-arg, but `AckSemanticsTests.Build()` still constructed them as `new StartOrchestrationConsumer(gate, store, lifecycle, logger)` / `new StopOrchestrationConsumer(gate, store, lifecycle, logger)` — a genuine CS-level break that incremental builds were not recompiling.
- **Fix:** updated both construction sites to the new 3-arg `(gate, lifecycle, logger)` signature (the local `store` is still used to construct the `WorkflowLifecycle`, so it stays). `AckSemanticsTests` is in the plan's Wave 0 reconcile list precisely because it consumes the changed ctor.
- **Files modified:** tests/BaseApi.Tests/Orchestrator/AckSemanticsTests.cs
- **Commit:** 2630c20 (Task 2, amended).

> Build-trust note (extends the 24-04 stale-DLL memory): incremental `dotnet build` reported `0/0` even with the broken `AckSemanticsTests` call. Only deleting `src/Orchestrator/obj|bin` + `tests/BaseApi.Tests/obj|bin` and rebuilding from scratch produced an honest compile. The final verification numbers below are from that from-scratch rebuild + full `dotnet test`.

### Plan-anticipated cleanups (not deviations)

- Both consumers' `IWorkflowL1Store store` ctor param became unused once the stripe was removed; per the plan's explicit instruction ("drop the `IWorkflowL1Store store` ctor param to avoid an unused-dependency warning") it was removed from both, along with the now-unused `using Orchestrator.L1;`. All three consumer-constructing test files (`StartConsumerLifecycleTests`, `StopConsumerLifecycleTests`, `AckSemanticsTests`) had their `Build()` helpers updated to the new 3-arg ctor.

## Out of Scope (NOT touched)

- WEBAPI-SUPPRESS-01 (WebApi first-win) is Plan 02's scope, not this plan.
- L1 eviction of stopped workflows (FUTURE-STOP-EVICTION) remains deferred — stopped workflows linger in L1 by design (drain; T-24-13 accept).
- The large set of pre-existing `D` (deleted) `.planning/phases/01..16/*` entries in `git status` were present in the session's initial snapshot and left untouched.
- FLAG-24-04-SCHEDULER (the RabbitMQ delayed-exchange-plugin caveat for live gate-closed deferral) carries forward unchanged — this plan reuses the same 24-04 scheduler wiring and adds the same redelivery policy to Start/Stop; the live-broker plugin decision remains an architectural follow-up for review.

## Threat Model Outcome

- T-24-11 (DoS / redelivery storm during long hydration/Redis outage): mitigated by design — finite `5s/15s/30s/60s` interval set on the Start/Stop endpoints exhausts to `_error` (no infinite loop), subject to FLAG-24-04-SCHEDULER for live deferral.
- T-24-12 (Tampering / V5 — malformed/absent L2 during conditionless hydrate): mitigated — `WorkflowLifecycle.IsBusiness` split unchanged; malformed/absent L2 is logged+skipped inside `HydrateAndScheduleAsync`, only infra (Redis) faults propagate to bounded retry.
- T-24-13 (DoS / lingering L1 from keep-L1-on-stop): accepted — stopped workflows linger this phase (eviction deferred); memory bounded by workflow population.

## Requirements Satisfied

- ORCH-START-RELOAD-01 — conditionless Start (hydrate+reschedule, no existence skip; stop→start revives) — supersedes Phase 23 ORCH-CONSUME-01.
- ORCH-STOP-DRAIN-01 — conditionless Stop (delete job, KEEP L1 for drain; late result still dispatches) — supersedes Phase 23 ORCH-STOP-01.
- ORCH-GATE-01 — gate-closed never-drop for Start AND Stop (throw GateClosedException → scheduled redelivery) — supersedes Phase 23 D-12 gate-drop. (Completes the Start/Stop slice; the result-consumer slice landed in 24-04. Live-broker deferral subject to FLAG-24-04-SCHEDULER.)

## Commits

- 8c1e3f4 feat(24-05): conditionless Start/Stop consumers + gate-closed throw; split WorkflowLifecycle (Task 1, amended to fold the Rule 3 CS1574 cref fix)
- 2630c20 feat(24-05): redelivery-before-retry on Start/Stop definitions; reconcile lifecycle tests (Task 2, amended to fold the AckSemanticsTests ctor reconciliation)

## Self-Check: PASSED

All 7 modified source/test files present on disk; both task commits (8c1e3f4 Task 1, 2630c20 Task 2) present in git log. Content checks: WorkflowLifecycle has UnscheduleOnlyAsync (no store.Remove) + unchanged TeardownAsync (keeps store.Remove); Start/Stop consumers each `throw new GateClosedException()` with no stripe (Start: 0 TryAcquire; Stop: 0 TeardownAsync); both definitions place UseScheduledRedelivery BEFORE UseMessageRetry and neither Ignore<GateClosedException>. From-scratch (obj/bin deleted) build: Orchestrator Release 0/0; full suite 309 passed / 0 failed / 0 skipped.
