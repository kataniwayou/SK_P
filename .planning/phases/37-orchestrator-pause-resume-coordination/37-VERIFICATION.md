---
phase: 37-orchestrator-pause-resume-coordination
verified: 2026-06-06T00:00:00Z
status: human_needed
score: 5/5 must-haves verified
overrides_applied: 0
human_verification:
  - test: "Live Keeper→Orchestrator pause/resume bus round-trip"
    expected: "On L2 outage: Keeper publishes PauseWorkflow → orchestrator Quartz job enters Paused state; on L2 recovery Keeper publishes ResumeWorkflow → orchestrator reschedules from L1 cron → Normal trigger with future StartAt. No duplicate fires."
    why_human: "Requires live RabbitMQ + Redis + rebuilt Keeper+Orchestrator containers (embedded SourceHash must match). Cannot verify programmatically without the running stack. Per Phase 35/36 precedent this is the Phase-39 close-gate signal."
  - test: "GaveUp path: workflow stays paused after Keeper parks to keeper-dlq, publishes no Resume"
    expected: "L2 stays down past max-attempts → Fault<T> in keeper-dlq, GetTriggerState(TriggerKey(jobId)) == Paused, no ResumeWorkflow published, no auto-resume."
    why_human: "Same dependency on live stack as above; hermetic proven via KeeperPausePublishTests.GaveUp_PublishesPause_ButNoResume (Quartz state stays Paused until next Resume signal, which is not sent — this can be confirmed hermetically in isolation but the end-to-end orchestrator state during a real GaveUp requires the live stack)."
---

# Phase 37: Orchestrator Pause/Resume Coordination — Verification Report

**Phase Goal:** Add the `PauseWorkflow`/`ResumeWorkflow` contracts and the orchestrator-side consumers
that halt a workflow's cron via Quartz `PauseJob` (D-08) and reschedule it from L1 on any successful
recovery (D-09), with Quartz `GetTriggerState` as the single source of truth for the
Running/Paused/Stopped state (no L1 state field, no pending-recovery set); idempotent under
duplicate/concurrent signals via `ConcurrentMessageLimit = 1` + idempotent transitions + redelivery (D-07).
**Verified:** 2026-06-06
**Status:** human_needed — all hermetic must-haves verified; live bus round-trip is operator-pending (Phase-39 close gate)
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | `PauseWorkflow` and `ResumeWorkflow` contracts exist in `Messaging.Contracts`, implement `ICorrelated`, and carry `WorkflowId` + `H` + body `CorrelationId` | ✓ VERIFIED | `src/Messaging.Contracts/PauseWorkflow.cs` and `ResumeWorkflow.cs` — `public sealed record PauseWorkflow(Guid WorkflowId, string H) : ICorrelated { public Guid CorrelationId { get; init; } }`. Both files byte-identical mirror of `StartOrchestration`. PauseResumeContractTests 4/4 GREEN. |
| 2 | Orchestrator halts future cron fires via Quartz `PauseJob` keyed by a deterministic `TriggerKey(jobId.ToString("D"))` — Quartz `GetTriggerState` is the sole source of truth (no L1 state field, no pending-recovery set) | ✓ VERIFIED | `WorkflowScheduler.PauseAsync` wraps `scheduler.PauseJob(KeyFor(jobId))`. `TriggerKeyFor(jobId)` helper stamps `.WithIdentity(TriggerKeyFor(jobId))` on BOTH `ScheduleAsync` (line 44) and `RescheduleAsync` (line 69 via intermediate `var triggerKey`). `GetTriggerStateAsync` wraps `scheduler.GetTriggerState(TriggerKeyFor(jobId))`. No L1 state field exists on `WorkflowL1` or `WorkflowL1Store`. PauseSuppressesFire test asserts `TriggerState.Normal` (fresh) → `TriggerState.Paused` (after Pause) at the same key. |
| 3 | On any successful recovery the orchestrator reschedules from L1 — `ResumeAsync` deletes the stale paused job then `ScheduleAsync` a fresh from-now trigger; `None` (Stopped) and `Normal` (Running) are no-ops | ✓ VERIFIED | `WorkflowLifecycle.ResumeAsync` (lines 177–194): TryGet L1 → `GetTriggerStateAsync` → `if (state != TriggerState.Paused) return` → `UnscheduleAsync` → `ScheduleAsync`. ResumeReschedulesFresh test: Paused → one Normal trigger, future next-fire, count==1. ResumeIgnoresStoppedAndRunning test: None branch returns without action, Normal branch returns without action. |
| 4 | Duplicate/concurrent pause/resume signals are absorbed by `ConcurrentMessageLimit=1` + idempotent Quartz transitions + redelivery-on-crash; no dedicated lock, no per-`workflowId` semaphore, no reference-counting set | ✓ VERIFIED | `PauseWorkflowConsumerDefinition`: `consumerConfigurator.ConcurrentMessageLimit = 1` on `EndpointName = "orchestrator-pauseresume"`. `ResumeWorkflowConsumerDefinition`: same endpoint + limit. No `TryAcquire`/`Wait(0)` in either consumer or the two new lifecycle seams (grep confirms — only doc-comment negations). PauseResumeIdempotent test: Pause×2 then Resume×2 → one Normal trigger, `GetJobKeys.Count == 1` (no orphans). |
| 5 | Keeper publishes `PauseWorkflow` at intake (before probe) and `ResumeWorkflow` on Recovered (after re-inject); `GaveUp` parks to `keeper-dlq` and publishes nothing — a workflow stays paused only if no recovery ever succeeds | ✓ VERIFIED | `FaultEntryStepDispatchConsumer` and `FaultExecutionResultConsumer` both have `await context.Publish(new PauseWorkflow(...), ct)` before `recovery.RunAsync`, and `await context.Publish(new ResumeWorkflow(...), ct)` inside the `if (outcome == ProbeOutcome.Recovered)` block only. The `else` (GaveUp) branch has only the DLQ `dlq.Send(context.Message, ...)` — no Publish. KeeperPausePublishTests 2/2 GREEN: Recovered_PublishesPauseThenResume_ForInnerWorkflow + GaveUp_PublishesPause_ButNoResume. |

**Score:** 5/5 truths verified (hermetically)

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/Messaging.Contracts/PauseWorkflow.cs` | PauseWorkflow control contract (ICorrelated, WorkflowId+H+CorrelationId) | ✓ VERIFIED | 7 lines; `sealed record PauseWorkflow(Guid WorkflowId, string H) : ICorrelated { public Guid CorrelationId { get; init; } }` |
| `src/Messaging.Contracts/ResumeWorkflow.cs` | ResumeWorkflow control contract (byte-identical sibling) | ✓ VERIFIED | 7 lines; same shape with ResumeWorkflow name and summary |
| `src/Orchestrator/Scheduling/WorkflowScheduler.cs` | Deterministic TriggerKey stamping + PauseAsync + GetTriggerStateAsync | ✓ VERIFIED | `TriggerKeyFor` helper; `.WithIdentity(TriggerKeyFor(jobId))` in ScheduleAsync (line 44); `.WithIdentity(triggerKey)` (where `triggerKey = TriggerKeyFor(jobId)`) in RescheduleAsync (line 69); `PauseAsync` wraps `PauseJob`; `GetTriggerStateAsync` wraps `GetTriggerState(TriggerKeyFor(jobId))`; `RescheduleJob`-replace fix (571498f) |
| `src/Orchestrator/Consumers/PauseWorkflowConsumer.cs` | Pause consumer (PauseJob via lifecycle seam) | ✓ VERIFIED | `IConsumer<PauseWorkflow>`; delegates to `lifecycle.PauseOnlyAsync`; structured log holes `{WorkflowId}` / `{H}` |
| `src/Orchestrator/Consumers/ResumeWorkflowConsumer.cs` | Resume consumer (guard-on-Paused, delete + fresh schedule) | ✓ VERIFIED | `IConsumer<ResumeWorkflow>`; delegates to `lifecycle.ResumeAsync`; structured log holes |
| `src/Orchestrator/Consumers/PauseWorkflowConsumerDefinition.cs` | ConcurrentMessageLimit=1 + dedicated endpoint + retry ownership | ✓ VERIFIED | `EndpointName = "orchestrator-pauseresume"`, `ConcurrentMessageLimit = 1`, `UseMessageRetry` registered; no `WorkflowRootNotFoundException` |
| `src/Orchestrator/Consumers/ResumeWorkflowConsumerDefinition.cs` | ConcurrentMessageLimit=1 + same endpoint, no second retry | ✓ VERIFIED | `EndpointName = "orchestrator-pauseresume"`, `ConcurrentMessageLimit = 1`, no `UseMessageRetry` (per-endpoint ownership held by Pause def) |
| `src/Orchestrator/Hydration/WorkflowLifecycle.cs` | PauseOnlyAsync + ResumeAsync lifecycle seams | ✓ VERIFIED | `PauseOnlyAsync` (lines 163–172): TryGet → `scheduler.PauseAsync`; `ResumeAsync` (lines 177–194): TryGet → `GetTriggerStateAsync` → guard `!= Paused` return → `UnscheduleAsync` → `ScheduleAsync` |
| `src/Orchestrator/Program.cs` | Both consumers registered per-replica fan-out | ✓ VERIFIED | Lines 47–50: `AddConsumer<PauseWorkflowConsumer, PauseWorkflowConsumerDefinition>().Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; })` + same for Resume |
| `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs` | PauseWorkflow at intake + ResumeWorkflow on Recovered | ✓ VERIFIED | `context.Publish(new PauseWorkflow(...))` before `recovery.RunAsync`; `context.Publish(new ResumeWorkflow(...))` inside Recovered branch; GaveUp else unchanged |
| `src/Keeper/Consumers/FaultExecutionResultConsumer.cs` | Same pattern (sibling consumer) | ✓ VERIFIED | Identical two-insert pattern; only difference is re-inject endpoint URI (`queue:OrchestratorQueues.Result`) |
| `tests/BaseApi.Tests/Messaging/PauseResumeContractTests.cs` | PAUSE-01 contract-shape assertions | ✓ VERIFIED | 4 facts: `PauseWorkflow_ImplementsICorrelated`, `ResumeWorkflow_ImplementsICorrelated`, `PauseWorkflow_BodyCarriesWorkflowId_H_AndCorrelationId`, `ResumeWorkflow_BodyCarriesWorkflowId_H_AndCorrelationId` |
| `tests/BaseApi.Tests/Keeper/KeeperPausePublishTests.cs` | PAUSE-01 Keeper publish-site assertions | ✓ VERIFIED | 2 facts: `Recovered_PublishesPauseThenResume_ForInnerWorkflow` + `GaveUp_PublishesPause_ButNoResume`; uses real `L2ProbeRecovery` over `FakeRedis` (Up/Down) |
| `tests/BaseApi.Tests/Orchestrator/Scheduling/PauseResumeSchedulingTests.cs` | PAUSE-02/03/05 RAM-scheduler state-model assertions | ✓ VERIFIED | 3 facts: `PauseSuppressesFire`, `ResumeReschedulesFresh`, `ResumeIgnoresStoppedAndRunning`; real RAMJobStore; unique `quartz.scheduler.instanceName = test-{Guid:N}`; all Quartz calls pass `TestContext.Current.CancellationToken` |
| `tests/BaseApi.Tests/Orchestrator/PauseResumeConsumerTests.cs` | PAUSE-04 serial-replay idempotency | ✓ VERIFIED | 1 fact: `PauseResumeIdempotent`; Pause×2 then Resume×2 → `Assert.Single(jobKeys)` + `TriggerState.Normal` |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `FaultEntryStepDispatchConsumer.cs` | orchestrator Pause/Resume consumers | `context.Publish(new PauseWorkflow(...))` before `recovery.RunAsync`; `context.Publish(new ResumeWorkflow(...))` inside Recovered | ✓ WIRED | Lines 50–52 (Pause at intake) and 66–68 (Resume on Recovered). CorrelationId carried from inner. |
| `FaultExecutionResultConsumer.cs` | orchestrator Pause/Resume consumers | Same two-insert pattern | ✓ WIRED | Lines 51–53 (Pause at intake) and 67–69 (Resume on Recovered). |
| `PauseWorkflowConsumer.cs` | `WorkflowLifecycle.PauseOnlyAsync` → `WorkflowScheduler.PauseAsync` | `lifecycle.PauseOnlyAsync(m.WorkflowId, ct)` | ✓ WIRED | `PauseOnlyAsync` calls `scheduler.PauseAsync(wf.JobId, ct)` which calls `scheduler.PauseJob(KeyFor(jobId), ct)`. Chain complete. |
| `ResumeWorkflowConsumer.cs` | `WorkflowLifecycle.ResumeAsync` → guard on `TriggerState.Paused` → `UnscheduleAsync` → `ScheduleAsync` | `lifecycle.ResumeAsync(m.WorkflowId, ct)` | ✓ WIRED | ResumeAsync checks `state != TriggerState.Paused` (exact guard); on Paused: `scheduler.UnscheduleAsync` then `scheduler.ScheduleAsync`. |
| `WorkflowScheduler.RescheduleAsync` | deterministic trigger identity preserved on self-reschedule | `.WithIdentity(triggerKey)` where `triggerKey = TriggerKeyFor(jobId)`; `RescheduleJob(triggerKey, trigger)` | ✓ WIRED | 571498f fix: uses `RescheduleJob` (replace) not `ScheduleJob` (add), preventing `ObjectAlreadyExistsException` while keeping the deterministic key on every self-reschedule. |
| `Program.cs` | dedicated per-replica fan-out endpoint `orchestrator-pauseresume-{instanceId}` | `.AddConsumer<PauseWorkflowConsumer, PauseWorkflowConsumerDefinition>().Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; })` | ✓ WIRED | Lines 47–50 register both consumers on the dedicated shared endpoint. Matches `EndpointName = "orchestrator-pauseresume"` in both definitions. |

---

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|---------------|--------|--------------------|--------|
| `PauseWorkflowConsumer` | `m.WorkflowId` (from consumed `PauseWorkflow` message) | Keeper's `context.Publish(new PauseWorkflow(inner.WorkflowId, inner.H) { CorrelationId = ... })` | Yes — `inner.WorkflowId` is the concrete `EntryStepDispatch.WorkflowId` from the faulted message, not a static value | ✓ FLOWING |
| `WorkflowLifecycle.PauseOnlyAsync` | `wf.JobId` (from L1 store) | `store.TryGet(workflowId, out var wf)` — reads `WorkflowL1Store` seeded at `HydrateAndScheduleAsync` time | Yes — real in-memory L1 store | ✓ FLOWING |
| `WorkflowLifecycle.ResumeAsync` | `state` (Quartz trigger state) + `wf.Cron` (from L1) | `scheduler.GetTriggerStateAsync(wf.JobId, ct)` → live Quartz RAMJobStore; `wf.Cron` from hydrated L1 | Yes — live Quartz state and real L1 cron | ✓ FLOWING |

---

### Behavioral Spot-Checks

Step 7b skipped — no runnable entry points testable without the live RabbitMQ+Redis stack. Core behavioral assertions are covered by the hermetic test suite (477 passed / 0 failed).

---

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|-------------|-------------|--------|----------|
| PAUSE-01 | 37-02 (contracts), 37-04 (Keeper publish) | New `PauseWorkflow` and `ResumeWorkflow` contracts in `Messaging.Contracts`; Keeper fans them to the orchestrator | ✓ SATISFIED (hermetic) | Both contracts exist and implement `ICorrelated`. Both Keeper consumers publish them at the correct call sites. 4 contract tests + 2 Keeper publish tests GREEN. Live bus fan-out is human_needed (Phase-39 gate). |
| PAUSE-02 | 37-02 (scheduler), 37-03 (consumer) | Orchestrator halts future cron fires via Quartz `PauseJob`; pause-state is Quartz-owned via `GetTriggerState(TriggerKey(jobId))`; L1 preserved | ✓ SATISFIED (hermetic) | `PauseAsync` wraps `PauseJob`; deterministic `TriggerKey` stamped on both builder sites; no L1 state field. PauseSuppressesFire GREEN. |
| PAUSE-03 | 37-03 | On recovery, orchestrator reschedules from L1 (`ScheduleAsync` with `wf.jobId` + `wf.Cron`) | ✓ SATISFIED (hermetic) | `ResumeAsync`: Paused → `UnscheduleAsync` → `ScheduleAsync`. ResumeReschedulesFresh GREEN (one Normal trigger, future StartAt). |
| PAUSE-04 | 37-03 | Duplicate/concurrent signals serialized; no double-count; `ConcurrentMessageLimit=1` | ✓ SATISFIED (hermetic) | Definitions set limit=1; idempotent Quartz transitions; no lock/stripe. PauseResumeIdempotent GREEN. |
| PAUSE-05 | 37-03 (guard), 37-04 (GaveUp unchanged) | Resume acts only when `GetTriggerState == Paused`; GaveUp parks to DLQ, publishes no Resume; workflow stays paused with no recovery | ✓ SATISFIED (hermetic) | `state != TriggerState.Paused` guard in `ResumeAsync`; GaveUp else branch has no Publish. ResumeIgnoresStoppedAndRunning GREEN. KeeperPausePublishTests.GaveUp GREEN. |

**Note:** REQUIREMENTS.md shows PAUSE-01..05 as unchecked by executor convention — requirement IDs are ticked when the end-to-end behavior is proven at the Phase-39 live close gate. This is consistent with the Phase-35/36 precedent and is not a gap.

---

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| `src/Orchestrator/Hydration/WorkflowLifecycle.cs` | 186 | Resume guard `state != TriggerState.Paused` returns silently — no log on the ignore path | ⚠️ Warning | Pre-existing WR-01 from 37-REVIEW.md: a Resume delivered during the narrow fire window (trigger transiently None/Normal mid-fire) is silently dropped. Adding an informational log on the ignore branch would make a dropped Resume diagnosable. Non-blocking for hermetic correctness. |
| `src/Orchestrator/Scheduling/WorkflowScheduler.cs` | 84–85 | `RescheduleAsync` fallback `ScheduleJob(trigger)` assumes the non-durable job still exists | ⚠️ Warning | Pre-existing WR-02 from 37-REVIEW.md: the fallback (only reached when `RescheduleJob` returns null, i.e. no prior trigger exists) would throw if the non-durable job was purged first. Latent — current callers only invoke `RescheduleAsync` from inside `WorkflowFireJob.Execute` where the trigger is still live. Non-blocking. |
| `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs` | body | Near-total duplication with `FaultExecutionResultConsumer.cs` — pause/probe/resume/DLQ logic copied verbatim | ℹ️ Info | IN-01 from 37-REVIEW.md: future fixes to the pause/resume discipline must be applied in two places, risking drift. Not a functional defect. |

No stub patterns found. No `TODO`/`FIXME`/`placeholder` markers in any new file. No string interpolation of `WorkflowId`/`H` in log statements (structured holes verified in both consumers). No `return null` / `return {}` / `return []` in production code paths.

---

### Human Verification Required

#### 1. Live Pause/Resume Bus Round-Trip (PAUSE-01 fan-out + PAUSE-02/03 Quartz state)

**Test:** In a live stack (Keeper + Orchestrator containers rebuilt to current SourceHash, RabbitMQ + Redis running): induce an L2 (Redis) outage that causes a `Fault<EntryStepDispatch>` (or `Fault<ExecutionResult>`). Observe that:
1. Keeper publishes `PauseWorkflow` to the bus.
2. The orchestrator's `PauseWorkflowConsumer` receives it, calls `PauseOnlyAsync`, and the workflow's Quartz trigger enters `Paused` state (no further cron fires).
3. When L2 recovers, Keeper re-injects the original message and publishes `ResumeWorkflow`.
4. The orchestrator's `ResumeWorkflowConsumer` receives it, calls `ResumeAsync` (which reads `GetTriggerState == Paused`), deletes the stale job, and schedules a fresh trigger — `GetTriggerState` returns `Normal`, `GetNextFireTimeUtc()` is in the future.
5. No duplicate fires occur downstream.

**Expected:** The workflow is paused during the outage and correctly resumed afterward with a fresh from-now trigger. Keeper logs show the `PauseWorkflow`/`ResumeWorkflow` publish; orchestrator logs show `Pause WorkflowId={...} H={...}` and `Resume WorkflowId={...} H={...}` from the two consumers.

**Why human:** Requires running RabbitMQ + Redis + rebuilt Keeper and Orchestrator containers. Cannot be asserted programmatically without the full live stack. This is the Phase-39 close-gate signal per the Phase-35/36 precedent.

#### 2. GaveUp End-State Verification (PAUSE-05)

**Test:** Same live stack; configure Keeper with a very low `MaxAttempts` and keep Redis down past the limit. Verify that:
1. `keeper-dlq` receives the original `Fault<T>` envelope.
2. No `ResumeWorkflow` is published (only `PauseWorkflow` at intake).
3. The orchestrator's workflow trigger remains `Paused` — the workflow is stranded until an operator intervenes.
4. `GetTriggerState(TriggerKey(jobId))` returns `Paused` (not `None` or `Normal`).

**Expected:** Workflow stays paused; `keeper-dlq` depth = 1; no `ResumeWorkflow` in the bus; orchestrator Quartz state is `Paused`. `keeper_dlq_pushed` counter increments (Phase-39 metric).

**Why human:** Same live-stack dependency. The hermetic test `GaveUp_PublishesPause_ButNoResume` proves the publish behavior, but the Quartz end-state in a real orchestrator and the `keeper-dlq` queue depth require the live environment.

---

### Gaps Summary

No hermetic gaps found. All 5 observable truths are verified against the actual codebase:
- Both contracts exist and implement `ICorrelated` correctly.
- Deterministic `TriggerKey(jobId.ToString("D"))` is stamped on both `WorkflowScheduler` trigger builder sites (the 37-02 regression that used `ScheduleJob`/add instead of `RescheduleJob`/replace was fixed in commit `571498f`).
- `PauseAsync`/`GetTriggerStateAsync` wrappers are present and correctly delegate to Quartz.
- Both orchestrator consumers delegate to lifecycle seams; `ConcurrentMessageLimit=1` is set on the dedicated `"orchestrator-pauseresume"` endpoint; Program.cs registers both per-replica.
- Both Keeper fault consumers publish `PauseWorkflow` at intake and `ResumeWorkflow` on Recovered; the GaveUp branch publishes nothing and is byte-identical to Phase 36.
- Hermetic suite: 477 passed / 0 failed (full `BaseApi.Tests` with `--filter-not-trait "Category=RealStack"`).

The two human verification items are not implementation gaps — they are live-stack integration tests consistent with the Phase-35/36 operator-pending precedent, explicitly routed to the Phase-39 close gate.

---

_Verified: 2026-06-06T00:00:00Z_
_Verifier: Claude (gsd-verifier)_
