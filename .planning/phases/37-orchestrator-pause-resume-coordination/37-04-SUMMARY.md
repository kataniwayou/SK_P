---
phase: 37-orchestrator-pause-resume-coordination
plan: 04
subsystem: keeper
tags: [masstransit, publish, pause-resume, keeper, fault-recovery, tdd-green]

# Dependency graph
requires:
  - phase: 37
    plan: 01
    provides: KeeperPausePublishTests RED target (the executable contract these publish sites flip GREEN)
  - phase: 37
    plan: 02
    provides: PauseWorkflow + ResumeWorkflow contracts (Messaging.Contracts) + deterministic-TriggerKey scheduler
  - phase: 37
    plan: 03
    provides: orchestrator PauseWorkflowConsumer/ResumeWorkflowConsumer on the per-replica orchestrator-pauseresume endpoint (the fan-out target of these publishes)
  - phase: 36
    plan: 02
    provides: L2ProbeRecovery + ProbeOutcome + the two Keeper fault consumers (the recovery seam these publishes wrap)
provides:
  - Keeper publish half of PAUSE-01 — context.Publish(PauseWorkflow) at intake + context.Publish(ResumeWorkflow) on Recovered in both fault consumers
  - PAUSE-05/D-09 give-up semantics — GaveUp parks to keeper-dlq and publishes nothing (no re-pin, no reference-count)
  - The first end-to-end-compiling phase-37 BaseApi.Tests assembly (all 37-01..04 production symbols now exist)
affects: [keeper-recovery, orchestrator-scheduling, phase-39-close-gate]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "context.Publish (NOT GetSendEndpoint().Send) for control signals so MassTransit message-type binding fans out to the orchestrator's per-replica endpoint; CorrelationId carried from the inner message"
    - "Pause-before-probe / Resume-after-reinject — additive publish calls bracketing the preserved Phase-35/36 recovery body, never rewriting it"

key-files:
  created:
    - .planning/phases/37-orchestrator-pause-resume-coordination/deferred-items.md
  modified:
    - src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs
    - src/Keeper/Consumers/FaultExecutionResultConsumer.cs

key-decisions:
  - "Publish (not Send) — message-type binding fans Pause/Resume to the orchestrator's orchestrator-pauseresume-{instanceId} endpoint; the outbound correlation publish filter stamps the envelope from the body CorrelationId"
  - "Pause at intake BEFORE recovery.RunAsync (D-03) so the outage stops spreading while probing; Resume AFTER the re-inject Send inside the Recovered branch (D-04)"
  - "GaveUp else branch left byte-identical (D-09) — parks Fault<T> to keeper-dlq, publishes nothing; a workflow stays paused only if no recovery ever succeeds"

requirements-completed: []   # PAUSE-01/05 publish behavior code-complete + hermetically proven; live-gate traceability ticked by the operator/verifier on the Phase-39 GREEN run

# Metrics
duration: ~20min
completed: 2026-06-06
---

# Phase 37 Plan 04: Keeper Pause/Resume Publish Sites Summary

**Both Keeper fault consumers now `context.Publish(PauseWorkflow(inner.WorkflowId, inner.H))` at intake (before the L2 probe loop, D-03) and `context.Publish(ResumeWorkflow(...))` after the existing re-inject `Send` on `ProbeOutcome.Recovered` (D-04), carrying the inner message's `CorrelationId`; the GaveUp branch is untouched and publishes nothing (D-09) — flipping `KeeperPausePublishTests` RED→GREEN (2/2) and completing the Keeper publish half of the orchestrator pause/resume round-trip.**

## Performance

- **Duration:** ~20 min
- **Completed:** 2026-06-06
- **Tasks:** 1
- **Files modified:** 2 (src) + 1 created (deferred-items.md)

## TDD Gate Compliance

This is the **GREEN** plan that completes the phase-37 TDD cycle for the Keeper publish behavior.

- **RED gate:** owned by Plan 37-01 — `test(37-01): add ... Keeper publish-site RED` (`8622788`), `KeeperPausePublishTests`.
- **GREEN gate (this plan):** `feat(37-04): publish Pause-at-intake + Resume-on-Recovered in both Keeper fault consumers` (`bfecd38`) — `KeeperPausePublishTests` 2/2 GREEN in isolation.
- **REFACTOR gate:** none needed (additive 24-line change, no cleanup).

## Accomplishments

- `FaultEntryStepDispatchConsumer.cs` + `FaultExecutionResultConsumer.cs`: identical two-insert change —
  1. **Pause at intake** (before `recovery.RunAsync`): `await context.Publish(new PauseWorkflow(inner.WorkflowId, inner.H) { CorrelationId = inner.CorrelationId }, context.CancellationToken);` — D-03.
  2. **Resume on Recovered** (after the re-inject `await endpoint.Send(inner, ...)`, still inside the `if (outcome == ProbeOutcome.Recovered)` block): `await context.Publish(new ResumeWorkflow(inner.WorkflowId, inner.H) { CorrelationId = inner.CorrelationId }, context.CancellationToken);` — D-04.
- The GaveUp `else` branch in BOTH files is byte-identical (parks the original `Fault<T>` to `keeper-dlq`, publishes nothing — D-09).
- `L2ProbeRecovery.cs` is untouched; the Phase-35/36 consumer bodies (double-unwrap, manual `CorrelationId` scope, `BuildState` scope, structured intake log, re-inject/park) are preserved verbatim.
- The full `BaseApi.Tests` assembly now compiles end-to-end for the first time across phase 37 (all 37-01..04 production symbols exist).

## Task Commits

1. **Task 1: Publish Pause-at-intake + Resume-on-Recovered in both Keeper fault consumers (PAUSE-01/05)** — `bfecd38` (feat), 2 files / 24 insertions / 0 deletions.

**Plan metadata:** this SUMMARY + STATE + ROADMAP + deferred-items committed in the final docs commit.

## Files Created/Modified

- `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs` — Pause at intake + Resume on Recovered (Send-origin `queue:{inner.ProcessorId:D}`).
- `src/Keeper/Consumers/FaultExecutionResultConsumer.cs` — Pause at intake + Resume on Recovered (Send-origin `queue:{OrchestratorQueues.Result}`).
- `.planning/phases/37-orchestrator-pause-resume-coordination/deferred-items.md` — logs the out-of-scope FireDispatch/Quartz collision + the two RealStack operator-pending E2E reds.

## Decisions Made

- **`context.Publish` (not `GetSendEndpoint().Send`):** message-type binding fans Pause/Resume out to the orchestrator's per-replica `orchestrator-pauseresume-{instanceId}` endpoint (Plan 03); the outbound correlation publish filter stamps the envelope from the body `CorrelationId` for any `ICorrelated` message.
- **`CorrelationId = inner.CorrelationId`:** continuity from the faulted inner message (which itself restored the propagated correlationId via the Phase-35 manual scope).
- **`inner.WorkflowId` / `inner.H`:** `WorkflowId` resolves off `IExecutionCorrelated`; `H` resolves off the concrete inner type (already referenced by the preserved log line + `recovery.RunAsync`).

## Deviations from Plan

None — plan executed exactly as written. No auto-fixes, no architectural decisions, no auth gates, no scope creep.

## Threat Surface

No new threat surface. The two records travel the existing trusted internal bus (boundary already in the threat register). T-37-08/09/10 dispositions hold: log holes stay structured (no new interpolated lines), a publish failure un-acks → bounded retry re-runs probe + re-publishes Resume (fails OPEN to Running, never stuck Paused), and duplicate Pause from sibling faults is absorbed by the orchestrator's `ConcurrentMessageLimit=1` + idempotent `PauseJob`.

## Known Stubs

None. The publish sites are fully wired to the live contracts (Plan 02) and the live orchestrator consumers (Plan 03).

## Verification

- **`dotnet build src/Keeper/Keeper.csproj -c Release`:** Build succeeded, **0 Warning / 0 Error**.
- **`KeeperPausePublishTests` (MTP `--filter-class`, isolation):** **2/2 GREEN** — `Recovered_PublishesPauseThenResume_ForInnerWorkflow` + `GaveUp_PublishesPause_ButNoResume`.
- **Full hermetic BaseApi.Tests assembly:** compiles end-to-end (first time across phase 37). Full-suite run: **480 passed / 6 failed of 486** (Duration ~11m30s).
- **`git diff --diff-filter=D HEAD~1 HEAD`:** clean — no file deletions.

### The 6 failing tests are ALL out-of-scope for this Keeper-only plan

| Test | Category | Disposition |
|------|----------|-------------|
| `Keeper.KeeperRecoveryE2ETests.KeeperRecovery_RecoversBothPaths` | RealStack/live | Operator-pending (needs live RabbitMQ+Redis+Keeper container) — Phase 35/36 precedent |
| `Orchestrator.KeeperFaultIntakeE2ETests.LiveWrongTypeTrip_KeeperContainer_EmitsCorrelatedIntakeLog` | RealStack/live | Operator-pending (`Connection Failed: rabbitmq://rabbitmq/`) |
| `Orchestrator.FireDispatchTests.OneMessagePerEntryStep_CorrectFields` | hermetic (Orchestrator) | Pre-existing Quartz RAMJobStore DEFAULT-group collision (D1 in deferred-items.md) |
| `Orchestrator.FireDispatchTests.CorrelationIdDiffersAcrossTwoFires` | hermetic (Orchestrator) | Same — `ObjectAlreadyExistsException` |
| `Orchestrator.FireDispatchTests.LivenessTimestampAdvancesOnFire_NoL2Write` | hermetic (Orchestrator) | Same — `ObjectAlreadyExistsException` |
| `Orchestrator.WorkflowFireJobScopeTests.PostMint_FireLogs_Carry_CorrelationId_And_WorkflowId_Scope` | hermetic (Orchestrator) | Same — `ObjectAlreadyExistsException` |

The 4 hermetic reds fail with `Quartz.ObjectAlreadyExistsException: Unable to store Trigger: 'DEFAULT.{guid}'` — a cross-class RAMJobStore collision in older Orchestrator tests (last touched Phase 31) that schedule into the shared DEFAULT trigger group without the per-test unique `quartz.scheduler.instanceName` discipline the new 37-01 `PauseResumeSchedulingTests` adopt. They reproduce in isolation (3/3) so they are deterministic, not load-flakiness — but they never reference Keeper, so they cannot be caused by this plan's change. Logged to `deferred-items.md` (D1), NOT fixed per the executor scope boundary; candidate for a 37 cleanup / Phase-38 hygiene pass.

The 2 RealStack reds are the standing live E2E guards (operator-gated per the Phase 35/36 precedent); the Phase-39 close gate is the authoritative live signal.

## Deferred Issues

See `deferred-items.md` (D1 — FireDispatch/WorkflowFireJobScope Quartz collision; D2 — two RealStack operator-pending E2E). Neither is in scope for plan 37-04 (Keeper consumers only).

## User Setup Required

None for the hermetic close. The live pause/resume bus round-trip (Keeper fault → PauseWorkflow → orchestrator pauses Quartz job → recovery → ResumeWorkflow → orchestrator reschedules) requires a rebuilt v3.7.0 stack (Keeper + Orchestrator containers, embedded SourceHash must match) and is the operator's Phase-39 close-gate run.

## Next Phase Readiness

- **Phase 37 = 4/4 plans complete** — pause/resume coordination is code-complete + hermetically proven (orchestrator `GetTriggerState`-as-truth Quartz Pause/Resume from Plans 02/03 + the Keeper publish half here).
- PAUSE-01 (publish half) + PAUSE-05/D-09 behavior satisfied; full PAUSE-01..05 traceability is ticked by the verifier/operator on the Phase-39 live GREEN run.
- Milestone v3.7.0 continues: **Phase 38** (uniform `service_name` + instance metric labels) + **Phase 39** (Keeper observability + RealStack E2E + close gate) remain.

## Self-Check: PASSED

- Files modified — all present:
  - FOUND: src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs (contains `context.Publish(` × Pause + Resume)
  - FOUND: src/Keeper/Consumers/FaultExecutionResultConsumer.cs (contains `context.Publish(` × Pause + Resume)
  - FOUND: .planning/phases/37-orchestrator-pause-resume-coordination/deferred-items.md
- Commit exists: FOUND `bfecd38`.
- KeeperPausePublishTests 2/2 GREEN in isolation; Keeper Release build 0/0; the 6 full-suite reds are all out-of-scope (2 RealStack operator-pending, 4 pre-existing Orchestrator Quartz collisions logged to deferred-items.md).

---
*Phase: 37-orchestrator-pause-resume-coordination*
*Completed: 2026-06-06*
