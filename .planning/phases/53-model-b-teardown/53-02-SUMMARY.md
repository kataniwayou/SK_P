---
phase: 53-model-b-teardown
plan: 02
subsystem: orchestrator-messaging
tags: [masstransit, usemessageretry, teardown, model-b, D-01, D-07, RETIRE-03, 0-warning]

# Dependency graph
requires:
  - phase: 53-model-b-teardown
    plan: 01
    provides: "Standing RED guards FACT 6 (D-01 source-scan) + FACT 8 (D-07 Ignore<> removed) that verify this plan's removals"
provides:
  - "All 5 orchestrator UseMessageRetry owners stripped (Start, Stop, StepCompleted, PauseWorkflow, PauseAll) — A18 no-bus-retry end-state on every orchestrator endpoint"
  - "Dual-owner orchestrator endpoint retry removed from BOTH Start AND Stop (RESEARCH Pitfall 3)"
  - "Dead Ignore<WorkflowRootNotFoundException> removed from Start/Stop (D-07, no DLQ seam added) -> FACT 8 GREEN"
  - "FACT 6 reduced from 5 orchestrator+processor offenders to 1 (processor-half ProcessorStartupOrchestrator only) — orchestrator-source offenders gone"
affects: [53-03, model-b-teardown verification, close-gate]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Collapse retry-owner ConsumerDefinition to the no-op sibling shape (parameterless ctor, EndpointName only, intentional no-op ConfigureConsumer) when the only thing it did was register bus retry"
    - "KEEP ConcurrentMessageLimit=1 (serialization) while removing UseMessageRetry (retry) — orthogonal concerns on the same ConfigureConsumer"
    - "Drop dead IOptions<RetryOptions> field+ctor-param+using together with the retry block to stay 0-warning (SC-3)"

key-files:
  created: []
  modified:
    - "src/Orchestrator/Consumers/StartOrchestrationConsumerDefinition.cs"
    - "src/Orchestrator/Consumers/StopOrchestrationConsumerDefinition.cs"
    - "src/Orchestrator/Consumers/StepCompletedConsumerDefinition.cs"
    - "src/Orchestrator/Consumers/PauseWorkflowConsumerDefinition.cs"
    - "src/Orchestrator/Consumers/PauseAllConsumerDefinition.cs"
    - "src/Orchestrator/Consumers/ResumeAllConsumerDefinition.cs"

key-decisions:
  - "WorkflowRootNotFoundException needs no using removal — it lives in the Orchestrator.Consumers namespace (same as the definitions), so it was referenced unqualified; only `using Microsoft.Extensions.Options;` was dead and removed"
  - "Followed the plan exactly: ResumeWorkflowConsumerDefinition's now-stale doc-comment (still mentions UseMessageRetry) was LEFT UNCHANGED — the plan scoped Task 2 to 4 files and explicitly said leave ResumeWorkflow's definition untouched; the D-01 guard scans the CALL pattern, not the bare word, so the surviving doc-comment does not break the guard"

patterns-established:
  - "No-op sibling shape is the canonical post-removal target for a retry-only ConsumerDefinition"

requirements-completed: [RETIRE-03]

# Metrics
duration: 3min
completed: 2026-06-11
---

# Phase 53 Plan 02: Model-B Teardown — Orchestrator Retry Strip Summary

**Stripped the outer-bus `UseMessageRetry` (and the dead `Ignore<WorkflowRootNotFoundException>` + dead `IOptions<RetryOptions>` injections) from all 5 orchestrator consumer definitions — including the dual-owner `orchestrator` endpoint (Start AND Stop) — collapsing each to the no-op sibling shape; `ConcurrentMessageLimit=1` kept on Pause/PauseAll; 0-warning Release+Debug; FACT 8 GREEN and FACT 6's orchestrator-source offenders eliminated.**

## Performance

- **Duration:** ~3 min
- **Started:** 2026-06-11T19:58:58Z
- **Completed:** 2026-06-11T20:01:30Z
- **Tasks:** 2
- **Files modified:** 6

## Accomplishments

- **Task 1 — dual-owner strip (Start + Stop):** removed the full `UseMessageRetry(r => { r.Immediate(...); r.Ignore<WorkflowRootNotFoundException>(); })` block from BOTH `StartOrchestrationConsumerDefinition` and `StopOrchestrationConsumerDefinition` (the `orchestrator` endpoint is shared by two owners — RESEARCH Pitfall 3). Dropped the `_retryOptions` field + ctor param + `using Microsoft.Extensions.Options;` and collapsed each ctor to parameterless, matching `StepFailedConsumerDefinition`'s no-op shape (EndpointName only, intentional no-op ConfigureConsumer). The dead `Ignore<WorkflowRootNotFoundException>` died with the block (D-07 — `WorkflowLifecycle` handles an absent root as a logged no-op/ACK; the exception is never thrown). **No DLQ/catch seam added; consumers and WorkflowLifecycle untouched.**
- **Task 2 — single-owner strips (StepCompleted, PauseWorkflow, PauseAll) + ResumeAll comment:** removed the `UseMessageRetry(r => r.Immediate(...))` call + dead `IOptions<RetryOptions>` from all three; `StepCompleted` collapsed to the no-op shape; `PauseWorkflow`/`PauseAll` retained `ConcurrentMessageLimit = 1` (serialization, orthogonal to retry). Reconciled `ResumeAllConsumerDefinition`'s trailing comment so it no longer references a retry that no longer exists on its sibling.
- **Doc-comment reconciliation:** rewrote every stale xml-doc that described a `UseMessageRetry(Immediate(N)) -> _error` call to the A18 end-state — "NO bus retry; a send that exhausts the in-code RetryLoop throws -> RabbitMQ nack-requeue (broker redelivery); no _error, no dead-letter (Phase-53 D-01)."

## Task Commits

1. **Task 1: Strip dual-owner retry from Start + Stop** — `0fca8d4` (refactor)
2. **Task 2: Strip retry from StepCompleted + PauseWorkflow + PauseAll; reconcile ResumeAll** — `8b2db63` (refactor)

**Plan metadata:** _(final docs commit — SUMMARY/STATE/ROADMAP/REQUIREMENTS)_

## Files Created/Modified

- `StartOrchestrationConsumerDefinition.cs` — collapsed to no-op shape; retry+Ignore+IOptions removed; doc reconciled.
- `StopOrchestrationConsumerDefinition.cs` — collapsed to no-op shape; retry+Ignore+IOptions removed; doc reconciled.
- `StepCompletedConsumerDefinition.cs` — collapsed to no-op shape; retry+IOptions removed; doc reconciled.
- `PauseWorkflowConsumerDefinition.cs` — retry+IOptions removed; `ConcurrentMessageLimit=1` KEPT; doc reconciled.
- `PauseAllConsumerDefinition.cs` — retry+IOptions removed; `ConcurrentMessageLimit=1` KEPT; doc reconciled.
- `ResumeAllConsumerDefinition.cs` — comment-only reconcile (no longer references a removed sibling retry).

## Verification

- `grep "endpointConfigurator.UseMessageRetry(" / "cfg.UseMessageRetry("` across `src/Orchestrator/Consumers/` -> **0 CALLs** (the surviving bare-word mentions in StepFailed/StepCancelled/StepProcessing/ResumeWorkflow are doc-comment text in unchanged no-op siblings; the D-01 guard scans the CALL pattern, so they are inert).
- `grep "Ignore<WorkflowRootNotFoundException>"` across the consumers -> **0**.
- `grep "ConcurrentMessageLimit = 1"` on PauseWorkflow + PauseAll -> present (KEPT).
- `dotnet build SK_P.sln -c Release` -> **0 Warning / 0 Error**; `-c Debug` -> **0 Warning / 0 Error** (SC-3).
- `dotnet test tests/BaseApi.Tests -- --filter-trait "Phase=53"` -> **Passed: 2, Failed: 2, Total: 4** (was 1/3 before this plan). FACT 8 (D-07) moved RED->GREEN. FACT 6 (D-01) is still RED but now names ONLY `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` — the orchestrator-source offenders are ALL gone, exactly as Task 2's acceptance criterion specifies.
- `git diff --name-only HEAD~2 HEAD` touches ONLY the 6 definition files; `git diff --diff-filter=D` clean (no file deletions). StartOrchestrationConsumer.cs / StopOrchestrationConsumer.cs / WorkflowLifecycle.cs unchanged.

## Expected-Remaining-RED State (NOT a failure — Plan 03 scope)

| Fact | Test | State after this plan | Owner |
|------|------|-----------------------|-------|
| FACT 5 | `Keeper_registers_exactly_three_recovery_consumers` | GREEN | (already, Plan 01) |
| FACT 8 | `Dead_WorkflowRootNotFound_ignore_removed_from_start_stop_definitions` | **GREEN (this plan)** | 53-02 |
| FACT 6 | `No_bus_retry_or_error_transport_on_execution_path_endpoints` | RED — names ONLY `ProcessorStartupOrchestrator.cs` (processor-half latch) | **53-03** |
| FACT 7 | `ConfigureError_is_keeper_local_only` | RED — global `ConfigureError` still in BaseConsole.Core | **53-03** |

The 2 remaining RED facts are wholly Plan-03 scope (processor keep-latch strip + keeper-local ConfigureError move). This plan delivered exactly its half: every orchestrator-source D-01 offender is gone and the D-07 dead-Ignore guard is GREEN.

## Deviations from Plan

None — plan executed exactly as written. No code outside the 6 listed definition files was touched; no DLQ/catch seam added (pure teardown per D-07); `WorkflowLifecycle` and both consumers left unchanged.

## Known Stubs

None — this plan only removes wiring and reconciles doc-comments. No placeholder values, no unwired data sources.

## Threat Flags

None — no new network endpoint, auth path, file access, or schema surface introduced. The intended residual (unbounded nack-requeue spin on a permanently-failing poison send) is the deliberately-accepted T-53-DoS from the plan's threat register, not new surface.

## Self-Check: PASSED

- FOUND: `.planning/phases/53-model-b-teardown/53-02-SUMMARY.md`
- FOUND: `src/Orchestrator/Consumers/StartOrchestrationConsumerDefinition.cs` (no-op shape, 0 UseMessageRetry calls)
- FOUND commit: `0fca8d4` (Task 1)
- FOUND commit: `8b2db63` (Task 2)

---
*Phase: 53-model-b-teardown*
*Completed: 2026-06-11*
