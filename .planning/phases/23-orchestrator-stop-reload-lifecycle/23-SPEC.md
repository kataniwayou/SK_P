# Phase 23: Orchestrator Lifecycle — L1 Hydration, Quartz Scheduling, Entry-Step Dispatch & Stop Teardown — Specification

**Created:** 2026-05-31
**Ambiguity score:** 0.19 (gate: ≤ 0.20)
**Requirements:** 9 locked

## Goal

The orchestrator gains a real lifecycle over the Phase 22 root-parent L2 structure: it hydrates an in-memory **L1 dictionary** from L2 (read-only), schedules one in-memory **Quartz** job per workflow off its cron, fires entry-step messages to each step's processor queue while refreshing in-memory liveness, and tears the Quartz job + L1 entries down on stop — addressing Quartz jobs by `jobId` resolved from L1. The orchestrator never writes to L2.

## Background

Through Phases 17–22 the orchestrator was a **seam**: `StartOrchestrationConsumer` / `StopOrchestrationConsumer` read the L2 root per workflowId (`JsonSerializer.Deserialize<WorkflowRootProjection>`), log `"scheduler job start/stop (seam)"`, and explicitly perform **NO Redis writes and NO Quartz** scheduling. There is no in-memory L1 store, no startup hydration, no scheduler, and no outbound dispatch.

Current L2 shape (Phase 22, source of truth):
- Parent index SET at `skp:` (the bare `L2ProjectionKeys.Prefix`) holding all workflow ids.
- Root `skp:{workflowId}` → `WorkflowRootProjection { entryStepIds[], cron, jobId, liveness{timestamp,interval,status}, correlationId }` (shared in `Messaging.Contracts.Projections`).
- Per-step `skp:{workflowId}:{stepId}` → `StepProjection { entryCondition:int, processorId, payload, nextStepIds[] }` — currently `internal` to `BaseApi.Service` (the writer), **not** reader-visible.
- Processor `skp:{processorId}` → processor self-registration + liveness.

The WebApi already owns L2: `OrchestrationService.StartAsync` writes L2 then publishes `StartOrchestration(workflowIds)`; `StopAsync` gates on EXISTS, runs `RedisL2Cleanup` (SREM parent index + delete root + per-step keys), then publishes `StopOrchestration(workflowIds)`.

This phase pulls forward requirements previously locked as FUTURE (Processor milestone): `FUT-QUARTZ-01` (Quartz + per-trigger correlationId), `FUT-SEND-01` (`Send` to `queue:{processorId}`), and the dispatch-message half of `FUT-CONTRACTS-01`. The processor→orchestrator round-trip stays future.

## Requirements

1. **ORCH-CONTRACT-01 — Shared step-projection contract**: The orchestrator can deserialize the per-step L2 value.
   - Current: `StepProjection` is `internal` inside `BaseApi.Service`; the Orchestrator project (references only `Messaging.Contracts`) cannot see it. The orchestrator has never read step values.
   - Target: a reader-consumable step-projection record exists in `Messaging.Contracts.Projections` with shape `{ entryCondition:int, processorId:Guid, payload:string, nextStepIds:List<Guid> }` and load-bearing camelCase `[property: JsonPropertyName]` targets, mirroring `WorkflowRootProjection`. (Decision A — single source of truth per `HARDEN-03`; writer refactor to consume it is NOT required this phase.)
   - Acceptance: the orchestrator deserializes a real `skp:{workflowId}:{stepId}` value into this record with all four fields populated and `entryCondition` round-tripping as an int.

2. **ORCH-CONTRACT-02 — Entry-step dispatch message**: A typed orchestrator→processor message exists.
   - Current: no orchestrator→processor message contract exists; only the `StartOrchestration` / `StopOrchestration` control messages (each `WorkflowIds[]` + `ICorrelated`).
   - Target: a message record carrying `correlationId` (`ICorrelated`), `workflowId`, `stepId`, `processorId`, `executionId`, `entryId`, `payload`.
   - Acceptance: a constructed instance serializes with all seven fields; `executionId` and `entryId` are `Guid.Empty`; `correlationId` carries the per-fire-generated value.

3. **ORCH-STARTUP-01 — Startup hydration into L1**: On boot, all workflows reload from L2 into an in-memory L1 dictionary.
   - Current: no L1 store and no startup hydration exist.
   - Target: a hosted startup path reads ALL workflow ids from the L2 parent index (`L2ProjectionKeys.ParentIndex()` = `skp:`) and, per workflowId, populates L1 with a workflow entry `{prefix}:{workflowId} → {entryStepIds[], cron, jobId, liveness}` plus one entry per step `{prefix}:{workflowId}:{stepId} → step projection`. L1 contains NEITHER processor keys NOR the parent-index key.
   - Acceptance: after startup against an L2 seeded with N workflows, L1 holds exactly N workflow entries plus each one's step entries; no processor key and no parent-index key are present in L1.

4. **ORCH-SCHED-01 — Quartz scheduling per workflow**: Each hydrated workflow gets one in-memory Quartz job.
   - Current: no Quartz dependency or scheduler; consumers comment "NO Quartz."
   - Target: using an in-memory RAMJobStore, for each hydrated workflow create a Quartz job whose `JobKey` embeds the workflow's `jobId`, build a cron trigger from the workflow's `cron`, compute the **interval as the delta in seconds between the next two scheduled fire times**, store that interval in the L1 liveness `interval`, then start the job.
   - Acceptance: after hydration the scheduler contains exactly one started (non-paused) job per workflow keyed by `jobId`; each workflow's L1 liveness `interval` equals the computed next-two-fire-times delta in seconds.

5. **ORCH-FIRE-01 — Job fire → entry-step dispatch + liveness refresh**: Firing dispatches to every entry step and refreshes liveness.
   - Current: nothing fires; no outbound dispatch.
   - Target: on each fire, in order — (a) generate a fresh `correlationId` GUID; (b) for EVERY entry step (`WorkflowRootProjection.EntryStepIds`), **`Send`** the ORCH-CONTRACT-02 message to `queue:{processorId}` (entry condition is irrelevant for entry steps), with `stepId` = the entry step, `processorId` + `payload` taken from that step's L1 entry, and `executionId` = `entryId` = `Guid.Empty`; (c) refresh that workflow's **L1** liveness `timestamp` to UTC-now (no L2 write).
   - Acceptance: a synthetic test consumer on `queue:{processorId}` receives one message per entry step on a fire with correct field values; `correlationId` differs between two consecutive fires; the workflow's L1 liveness `timestamp` advances after a fire; transport is `Send` (not `Publish`).

6. **ORCH-CONSUME-01 — Start consumer hydrates one workflow then schedules**: The start consumer reuses the lifecycle for a single workflow.
   - Current: `StartOrchestrationConsumer` reads the root and logs the seam; no L1, no scheduling.
   - Target: the start consumer hydrates ONLY the consumed workflowId(s) into L1 (same shape as ORCH-STARTUP-01), then runs ORCH-SCHED-01 + ORCH-FIRE-01 for it.
   - Acceptance: consuming `StartOrchestration([wfX])` yields an L1 entry for `wfX` only and a scheduled Quartz job for `wfX`; a synthetic consumer receives `wfX`'s entry-step messages on fire.

7. **ORCH-STOP-01 — Stop consumer tears down via jobId from L1**: Stop deletes the Quartz job and clears L1, never touching L2.
   - Current: `StopOrchestrationConsumer` reads the root and logs the seam; no teardown.
   - Target: the stop consumer consumes `workflowId`s; per workflowId it resolves the `jobId` from L1, calls `DeleteJob(JobKey(jobId))`, and removes that workflow's L1 entries (workflow + steps). It performs NO L2 mutation.
   - Acceptance: after stopping a scheduled `wfX`, the scheduler no longer contains the job and L1 has no `wfX` entries; an L2 snapshot is byte-identical before/after the stop (zero orchestrator L2 writes).

8. **ORCH-SCALE-01 — Multi-replica-safe design (single active replica assumed)**: No single-instance shortcuts.
   - Current: the orchestrator binds a per-replica fan-out temporary queue (`orchestrator-{instanceId}`, `Temporary = true`); only seam logging exists today.
   - Target: all new state (L1 dictionary, Quartz RAMJobStore) is per-instance; the implementation introduces NO global-uniqueness or "exactly one orchestrator" assumption that would break when a second replica starts. Cross-replica duplicate-dispatch coordination is explicitly NOT implemented this phase (a single active replica is assumed at runtime).
   - Acceptance: code review confirms no static/global singleton lock or process-uniqueness assumption gates the lifecycle; a single replica produces correct end-to-end behavior.

9. **ORCH-ACK-01 — Ack/error semantics mirror MSG-ACK**: New flows follow the existing business-vs-infra split.
   - Current: existing consumers split business failure (log + ack, never throw — MSG-ACK-01) from infra fault (propagate → bounded retry → `_error` — MSG-ACK-02).
   - Target: the new consumers and startup hydration follow the same split — business failures (absent workflow in L2, missing root/step, stop for an unknown workflowId) log + ack/skip; infra faults (Redis/broker unreachable) propagate to the bounded retry pipeline; startup hydration logs + skips a missing/corrupt entry without crashing the host.
   - Acceptance: a stop/start for an absent workflow acks with no `_error` message produced; a simulated Redis-unreachable during a consume propagates (does not ack-swallow); startup with one corrupt workflow entry still hydrates the others and the host stays up.

## Boundaries

**In scope:**
- Reader-consumable step-projection record in `Messaging.Contracts.Projections` (decision A).
- Orchestrator→processor entry-step dispatch message contract (7 fields, `ICorrelated`).
- Startup hydration of all parent-index workflows into an in-memory L1 dictionary (workflow + step entries only).
- In-memory (RAMJobStore) Quartz job per workflow; `JobKey` embeds `jobId`; interval = next-two-fire-times delta in seconds → L1 liveness `interval`.
- Job fire: fresh `correlationId`, `Send` to `queue:{processorId}` for every entry step, L1 liveness `timestamp` refresh.
- Start consumer: single-workflow hydrate + schedule + fire.
- Stop consumer: `jobId`-addressed Quartz `DeleteJob` + L1 cleanup, no L2 mutation.
- Multi-replica-safe design (single active replica assumed at runtime).
- MSG-ACK-aligned business-vs-infra error handling across the new flows + startup.

**Out of scope:**
- A Processor console / real consumer of `queue:{processorId}` — none exists yet; dispatch is verified via a synthetic test consumer.
- Processor→orchestrator result round-trip (`FUT-SEND-02`, `FUT-REQRESP-01`, and the round-trip half of `FUT-CONTRACTS-01`) — no result handling this phase.
- Cross-replica duplicate-dispatch dedup/coordination — a separate future solution; design must merely not preclude it.
- Any orchestrator L2 write — liveness refresh stays L1-only; the WebApi owns all L2 teardown.
- Refactoring the WebApi writer to consume the shared step contract — optional plan-time choice, not required here.
- Persistent Quartz job store (ADO/clustered) — RAMJobStore only; jobs are rebuilt from L2 on each startup.
- Firing non-entry steps / `nextStepIds` traversal at fire time — entry steps only (workflow start); downstream step execution is the processor's concern.

## Constraints

- In-memory RAMJobStore only — no persistent/clustered Quartz store; the scheduler is rebuilt from L2 on every startup.
- Outbound transport is `Send` to `queue:{processorId}` (load-balanced competing consumers, `FUT-SEND-01`), NOT `Publish`.
- The orchestrator performs ZERO L2 writes (read-only L2 access preserved from prior phases).
- `entryCondition` serializes as an int (no string-enum converter is registered anywhere) — must match the writer's `StepProjection`.
- Liveness `interval` = delta between the next two cron fire times, expressed in whole seconds.
- `correlationId` is minted fresh per job fire (`FUT-QUARTZ-01` — the bus-world correlation source); `executionId` and `entryId` are `Guid.Empty` on entry-step dispatch.
- No single-instance/global-uniqueness assumption may gate the lifecycle (must remain multi-replica-compatible).

## Acceptance Criteria

- [ ] A step-projection record exists in `Messaging.Contracts.Projections` and the orchestrator deserializes a real `skp:{wf}:{step}` value into it with `entryCondition` as int.
- [ ] An entry-step dispatch message record exists with all 7 fields; `executionId` and `entryId` are `Guid.Empty`.
- [ ] Startup hydrates all parent-index workflows into L1 (workflow + step entries); L1 contains no processor key and no parent-index key.
- [ ] After hydration the scheduler holds one started job per workflow keyed by `jobId`; L1 liveness `interval` = next-two-fire-times delta in seconds.
- [ ] On a fire, a synthetic consumer on `queue:{processorId}` receives one `Send` message per entry step with correct fields; `correlationId` differs across two consecutive fires.
- [ ] On a fire, the workflow's L1 liveness `timestamp` advances to UTC-now; no L2 write occurs.
- [ ] `StartOrchestration([wfX])` hydrates only `wfX` into L1 and schedules only `wfX`.
- [ ] `StopOrchestration([wfX])` resolves `wfX`'s `jobId` from L1, deletes the Quartz job, removes `wfX`'s L1 entries, and leaves an L2 snapshot byte-identical before/after.
- [ ] A start/stop for an absent workflow logs + acks (no `_error` message); a simulated Redis-unreachable during consume propagates; startup with one corrupt entry hydrates the rest and stays up.
- [ ] Code review confirms no single-instance/global-uniqueness assumption gates the lifecycle.

## Ambiguity Report

| Dimension          | Score | Min  | Status | Notes                                                        |
|--------------------|-------|------|--------|--------------------------------------------------------------|
| Goal Clarity       | 0.88  | 0.75 | ✓      | Full lifecycle specified; pulls forward FUT-QUARTZ/SEND/CONTRACTS |
| Boundary Clarity   | 0.78  | 0.70 | ✓      | Dispatch-only; no round-trip; no cross-replica dedup; no L2 writes |
| Constraint Clarity | 0.80  | 0.65 | ✓      | RAMJobStore, Send, cron→interval delta, L1-only liveness     |
| Acceptance Criteria| 0.72  | 0.70 | ✓      | 10 pass/fail checks incl. synthetic-consumer dispatch checks |
| **Ambiguity**      | 0.19  | ≤0.20| ✓      | Gate passed after 2 rounds                                   |

Status: ✓ = met minimum, ⚠ = below minimum (planner treats as assumption)

## Interview Log

| Round | Perspective         | Question summary                                  | Decision locked                                                            |
|-------|---------------------|---------------------------------------------------|----------------------------------------------------------------------------|
| 1     | Researcher          | Dispatch target reality — full round-trip or send-only? | Dispatch-only: `Send` to `queue:{processorId}` fire-and-forget; round-trip future |
| 1     | Researcher          | Multi-replica duplicate dispatch?                 | 1 active replica assumed, but NO single-instance shortcuts (multi-replica-safe design) |
| 1     | Researcher          | Quartz job store — in-memory or persistent?       | In-memory RAMJobStore; rebuilt from L2 each startup                         |
| 1     | Researcher          | Quartz stop addressing — by workflowId or jobId?  | `DeleteJob(JobKey(jobId))`; workflowId is inbound key → resolve jobId from L1 |
| 2     | Simplifier          | Liveness refresh target — L1 or L2?               | L1-only (in-memory); preserves the no-L2-mutation invariant                |
| 2     | Researcher          | Cron → "interval in seconds" derivation?          | Delta between the next two scheduled fire times, in seconds                |
| 2     | Boundary Keeper     | Reader access to step values (new contract)?      | Decision A — hoist a shared step-projection record into `Messaging.Contracts` |

---

*Phase: 23-orchestrator-stop-reload-lifecycle*
*Spec created: 2026-05-31*
*Next step: /gsd-discuss-phase 23 — implementation decisions (how to build what's specified above)*
