# Phase 23: Orchestrator Lifecycle — L1 Hydration, Quartz Scheduling, Entry-Step Dispatch & Stop Teardown - Context

**Gathered:** 2026-05-31
**Status:** Ready for planning

<domain>
## Phase Boundary

The orchestrator's runtime lifecycle over the Phase 22 root-parent L2 structure: hydrate an in-memory **L1 dictionary** from L2 (read-only), schedule one in-memory **Quartz** job per workflow off its cron, fire entry-step `Send` messages to each step's processor queue while refreshing in-memory liveness, and tear the Quartz job + L1 entries down on stop. The orchestrator never writes to L2.

This discussion decided HOW to implement; WHAT/WHY are locked by `23-SPEC.md`.

</domain>

<spec_lock>
## Requirements (locked via SPEC.md)

**9 requirements are locked.** See `23-SPEC.md` for full requirements, boundaries, and acceptance criteria. Downstream agents MUST read `23-SPEC.md` before planning or implementing — requirements are NOT duplicated here.

IDs: ORCH-CONTRACT-01/02, ORCH-STARTUP-01, ORCH-SCHED-01, ORCH-FIRE-01, ORCH-CONSUME-01, ORCH-STOP-01, ORCH-SCALE-01, ORCH-ACK-01 (also registered in REQUIREMENTS.md under the v3.5.x Future block).

**In scope (from SPEC.md):** shared step-projection contract; entry-step dispatch message contract; startup L2→L1 hydration; in-memory Quartz job per workflow; job-fire dispatch + L1 liveness refresh; start-consume single-workflow hydrate+schedule; stop-consume jobId-addressed teardown + L1 cleanup; multi-replica-safe design (single active replica assumed); MSG-ACK-aligned error handling.

**Out of scope (from SPEC.md):** a real Processor console / consumer of `queue:{processorId}` (synthetic test consumer only); processor→orchestrator result round-trip; cross-replica duplicate-dispatch dedup; any orchestrator L2 write; WebApi writer refactor to consume the shared step contract; persistent Quartz store; non-entry-step / nextStepIds traversal at fire time.

</spec_lock>

<decisions>
## Implementation Decisions

### Carried forward from prior phases (LOCKED — not re-discussed)
- **D-01 (message contract):** `ORCH-CONTRACT-02` realizes the segregated **`IExecutionCorrelated : ICorrelated`** that Phase 19 D-01 deferred to "the Processor milestone where the ids are real" — that's now. Define `IExecutionCorrelated : ICorrelated` adding `{ ExecutionId, WorkflowId, StepId, ProcessorId, EntryId }`; the entry-step dispatch message implements it. `executionId`/`entryId` = `Guid.Empty` per SPEC. (`IExecutionCorrelated` confirmed not yet defined in code; `ICorrelated` = `{ Guid CorrelationId }`.)
- **D-02 (ack split):** Reuse Phase 19 D-07/D-08 verbatim — business failure → a `WorkflowRootNotFoundException`-style type that the retry pipeline `Ignore<>`s → log + ack; infra fault → bounded `UseMessageRetry` → `_error`. This is `ORCH-ACK-01`.
- **D-03 (keys):** Build/read keys via the shared `L2ProjectionKeys` (Phase 22, HARDEN-03). `OrchestratorL2Keys` today only forwards `Root` — add **`ParentIndex()` + `Step()`** reader forwarders for hydration.
- **D-04 (clock):** `TimeProvider` is the injected "now" for both the liveness `timestamp` refresh and the cron math (Phase 22 liveness pattern).
- **D-05 (per-fire correlation):** mint `correlationId` per job fire with `NewId.NextGuid()` (sequential — Phase 19 D-02).

### Area 1 — L1 store
- **D-06:** A **singleton thread-safe `IWorkflowL1Store`** wrapping `ConcurrentDictionary<Guid, WorkflowL1>`, keyed by workflowId. Each entry holds the root fields (`entryStepIds`, `cron`, `jobId`, `liveness`) plus a steps map (`stepId → step projection`). The SPEC's `{prefix}:wf` / `{prefix}:wf:step` namespacing becomes in-memory nesting (the workflow entry owns its steps). L1 holds NO processor keys and NOT the parent-index key.

### Area 2 — Quartz + cron interpretation
- **D-07:** Use **raw Quartz.NET** (`Quartz.Extensions.Hosting`) with a DI-backed job factory + **RAMJobStore** (in-memory; rebuilt from L2 each startup). Job `JobKey` embeds the workflow's `jobId`; teardown is `DeleteJob(JobKey(jobId))`.
- **D-08 (load-bearing):** **Cronos computes fire times, NOT Quartz's cron parser.** The stored `cron` is a **5-field `Cronos.CronFormat.Standard`** expression (`WorkflowDtoValidator` VALID-19 explicitly rejects 6-field); Quartz's cron syntax is a different 6/7-field grammar, so feeding the stored cron into a Quartz `CronTrigger` would misinterpret or reject it. Instead: the job is a **one-shot scheduled at the next Cronos occurrence; on fire it computes the next occurrence and reschedules itself.** This keeps cron semantics identical to VALID-19 and makes `ORCH-SCHED-01`'s interval trivial.
- **D-09:** Liveness `interval` (ORCH-SCHED-01) = the delta **in whole seconds between the next two Cronos occurrences** (`GetNextOccurrence` ×2). Stored in L1 liveness.

### Area 3 — Outbound Send addressing + verification
- **D-10:** Dispatch via `ISendEndpointProvider.GetSendEndpoint(new Uri($"queue:{processorId:D}"))` then `Send(message)` — a load-balanced `Send` (FUT-SEND-01), NOT `Publish`. One message per entry step (`WorkflowRootProjection.EntryStepIds`); `stepId` = the entry step, `processorId`+`payload` from that step's L1 entry; entry condition irrelevant for entry steps.
- **D-11:** Since no processor consumes yet, verify with a **synthetic MassTransit receive endpoint** in the test harness asserting one message per entry step with correct fields, and `correlationId` differing across fires.

### Area 4 — Lifecycle gating, startup, concurrency
- **D-12 (global startup gate = the probe gate):** On boot, **drop** every consumed Start/Stop (ack, no work) while initial L2→L1 hydration + scheduling is in progress. The gate opens when initial hydration+scheduling completes — that moment **is `IStartupGate.MarkReady()`**, flipping `/health/startup`+`/health/ready` green (the existing `StartupHealthCheck` is tagged both). Move `MarkReady()` from `StartupCompletionService` (fires on bare host start today) to **initial-hydration-complete**. `/health/live` ("self") stays always-green — never gated.
- **D-13 (Redis-at-startup = probe-driven, parity with WebApi):** Hydration runs as a non-blocking `BackgroundService` against the shared `abortConnect=false` soft-dep multiplexer (no pre-warm). Redis down → hydration retries (bounded backoff) → the startup gate **never opens** → `/health/startup`+`/health/ready` stay Unhealthy → the container platform restarts on the **startup-probe failure threshold**; `/health/live` stays green (no self-inflicted crash loop). On Redis recovery → hydration completes → gate flips → ready. Mirrors the WebApi's degrade-then-recover Redis soft-dependency posture; never fail-fast/crash.
- **D-14 (per-workflowId gate, drop-if-held):** A **per-workflowId try-lock** serializes operations on the same workflowId; different workflowIds run concurrently. While wfX's gate is held, any consumed Start/Stop **for wfX** is **dropped** (first-in-flight wins). *(Accepted consequence: drop = first-writer-wins; a sub-second same-workflow collision discards the later intent. See Deferred — reconciliation.)*
- **D-15 (Start consume = replace, gated):** Acquire wfX's gate → **tolerant teardown** (unschedule job + clear L1 entry if present, silent if absent) → **hydrate L2→L1 + schedule** → release. So a re-Start re-applies the current L2 definition (no stale-definition drift), and a duplicate rebuilds identically. Reuses the Stop teardown as the first half (DRY). **Update contract:** re-Start applies the new definition — no "Stop-then-Start only" rule.
- **D-16 (Stop consume, gated):** Acquire wfX's gate → if present, unschedule + clean up L1; if absent, skip (business no-op) → release.
- **D-17 (ack timing):** Each gated operation runs inside `Consume`, so the message is acked only after it completes; transient infra fault throws → retry → `_error` (D-02). Note the receive endpoint is temporary/auto-delete (Phase 19 D-06) — control messages are NOT durably redelivered across a restart; cross-restart recovery is what startup hydration from L2 provides.

### Claude's Discretion
- Per-workflowId lock implementation (per-key `SemaphoreSlim` / lock striping inside `IWorkflowL1Store`) and its lifecycle (no semaphore leak).
- Exact shape of the hydration `BackgroundService` retry/backoff and how the startup gate is awaited/dropped-against inside the consumers.
- `WorkflowL1` record shape and where `IExecutionCorrelated` + the dispatch message record live in `Messaging.Contracts`.
- Quartz job factory wiring details; misfire policy for the self-rescheduling one-shot.
- Whether bulk startup hydration runs sequentially or with bounded parallelism (store is thread-safe either way).
- Test project placement (extend `tests/BaseApi.Tests/Orchestrator/` vs a dedicated project).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Locked requirements
- `.planning/phases/23-orchestrator-stop-reload-lifecycle/23-SPEC.md` — Locked requirements, boundaries, acceptance criteria. MUST read before planning.
- `.planning/ROADMAP.md` §"Phase 23" — goal + 6 success criteria.
- `.planning/REQUIREMENTS.md` §"Phase 23 — Orchestrator Lifecycle" (v3.5.x Future block) — the 9 ORCH-* requirements + the FUT-QUARTZ-01 / FUT-SEND-01 / FUT-CONTRACTS-01 items they realize.

### Prior-phase decisions that bind this phase
- `.planning/phases/19-orchestrator-console-webapi-bus-wiring-rabbitmq-tier/19-CONTEXT.md` — D-01 correlation model + the deferred `IExecutionCorrelated`; D-06 InstanceId fan-out (temporary/auto-delete queue); D-07/D-08 ack split.
- `.planning/phases/22-l2-root-parent-restructure-processor-self-registration/22-CONTEXT.md` — final L2 structure; `L2ProjectionKeys` const prefix + `ParentIndex()`/`Root`/`Step`/`Processor`; liveness `timestamp+interval*2>now` + `TimeProvider`.

### Code touchpoints (read before changing)
- `src/Messaging.Contracts/ICorrelated.cs` — `{ Guid CorrelationId }`; `IExecutionCorrelated` to be added here.
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — key builders (add `ParentIndex()`/`Step()` reader forwarders to `src/Orchestrator/Messaging/OrchestratorL2Keys.cs`).
- `src/Messaging.Contracts/Projections/WorkflowRootProjection.cs`, `LivenessProjection.cs` — root + liveness read shapes.
- `src/BaseApi.Service/Features/Orchestration/Projection/StepProjection.cs` — the writer-internal step shape to HOIST into `Messaging.Contracts` (ORCH-CONTRACT-01).
- `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs`, `StopOrchestrationConsumer.cs`, `WorkflowRootNotFoundException.cs` — the seams to replace with the real lifecycle.
- `src/Orchestrator/Program.cs` — Generic Host composition (add hydration `BackgroundService`, Quartz, L1 store, gate wiring).
- `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` — WebApi publish (workflowIds) + StopAsync; rewrites L2 on re-Start (delete-then-write).
- `src/BaseApi.Service/Features/Orchestration/Projection/RedisL2Cleanup.cs` — WebApi owns ALL L2 teardown.
- `src/BaseConsole.Core/Health/IStartupGate.cs`, `StartupHealthCheck.cs`, `StartupCompletionService.cs` — the gate to repoint at hydration-complete (D-12/D-13).
- `src/BaseConsole.Core/DependencyInjection/ConsoleRedisServiceCollectionExtensions.cs` — `abortConnect=false` soft-dep multiplexer.
- `src/BaseApi.Service/Features/Workflow/WorkflowDtoValidator.cs` — VALID-19: cron is 5-field Cronos Standard (drives D-08).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`IStartupGate`/`StartupHealthCheck`/`StartupCompletionService`** (BaseConsole.Core): the startup latch backing `/health/startup`+`/health/ready` — repoint `MarkReady()` at hydration-complete (D-12/D-13).
- **`L2ProjectionKeys`** + `OrchestratorL2Keys` forwarder: shared key source of truth (add `ParentIndex()`/`Step()` reader forwarders).
- **`WorkflowRootProjection` / `LivenessProjection`** (Messaging.Contracts): root + liveness read shapes already shared.
- **`StepProjection`** (BaseApi.Service-internal): exact step shape to hoist to Messaging.Contracts.
- **Cronos** (already a dependency): the validated cron engine — reuse for fire-time computation (D-08).
- **`ICorrelated`** + the slim/derived interface-segregation pattern (Phase 19): extend with `IExecutionCorrelated`.
- **`RedisL2Cleanup`** BFS GET-and-follow pattern: reference for how the orchestrator walks entry→next steps (though orchestrator hydration reads steps into L1, not deletes).

### Established Patterns
- **Ack split**: business=log+ack (Ignore<>'d exception), infra=throw→retry→`_error` (Phase 19 D-07/08).
- **Redis soft-dependency**: `abortConnect=false`, lazy connect, never crash on Redis-down; `/health/live` independent of deps; Redis not a readiness probe (orchestrator readiness is hard-on-broker).
- **`TimeProvider`** for testable "now" (Phase 22 liveness).
- **Body-carried correlation**, fresh per stage; `NewId.NextGuid()` for minting.
- **Temporary/auto-delete fan-out receive endpoint** (`orchestrator-{InstanceId}`) hosting both consumers (Phase 19 D-06).

### Integration Points
- Orchestrator `Program.cs` Generic Host: add Quartz hosted service + job factory, the `IWorkflowL1Store` singleton, the hydration `BackgroundService`, and the gate repoint.
- The two existing consumers (`Start`/`StopOrchestrationConsumer`) graduate from log-seams to the real gated lifecycle.
- `Messaging.Contracts`: new `IExecutionCorrelated`, the entry-step dispatch message record, and the hoisted step-projection record.

</code_context>

<specifics>
## Specific Ideas

- **Cronos-not-Quartz-cron** (D-08) is a hard, deliberate choice — cron interpretation must stay consistent with the WebApi's VALID-19 5-field Cronos validation; do NOT translate to Quartz cron syntax.
- **One unified gate**: the startup barrier, the Redis-at-startup behavior, and the existing `IStartupGate`/probe latch are the SAME mechanism (D-12) — don't build a second gate.
- **Drop, not wait** for the per-workflowId gate (D-14) — owner chose first-in-flight-wins simplicity over last-writer-wins serialization, accepting the narrow same-workflow-collision tail (covered by deferred reconciliation).
- **Start = stop-then-start** (D-15) — re-Start re-applies the current L2 definition by tolerant-teardown-then-rebuild.

</specifics>

<deferred>
## Deferred Ideas

- **Periodic reconciliation pass (hardening)** — the gated design removes the common startup/concurrency races but does NOT cover steady-state control-message loss: the receive endpoint is temporary/auto-delete, so a Start/Stop published while the orchestrator is briefly disconnected is lost with no redelivery, and the per-workflow drop discards a later same-workflow intent in a sub-second collision. A lost Stop → an orphan Quartz job firing forever; a lost Start → an unscheduled workflow until its next event. Fix = a periodic reconciliation tick that re-reads the parent index + roots and converges (schedule missing, delete orphans), using the `liveness {timestamp, interval}` signal already refreshed on every fire. **Owner accepted the gated design as-is for Phase 23 and recorded reconciliation as explicit deferred hardening.**
- **Cross-replica duplicate-dispatch dedup** — with >1 replica, every replica hydrates all workflows and fires every job → N× dispatch. Owner has a separate solution; out of scope here (the design must merely not preclude it — ORCH-SCALE-01). 
- **Processor→orchestrator result round-trip** — FUT-SEND-02, FUT-REQRESP-01, and the round-trip half of FUT-CONTRACTS-01; this phase is dispatch-only.
- **WebApi guard against bare re-Start of an active workflow** — optional: have the WebApi reject or Stop-first a re-Start; not needed since the orchestrator's Start=replace already applies updates.

</deferred>

---

*Phase: 23-orchestrator-stop-reload-lifecycle*
*Context gathered: 2026-05-31*
*Next step: /gsd-plan-phase 23 — implementation decisions are locked; planning turns them into tasks.*
