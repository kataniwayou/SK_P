# Phase 45: Keeper BIT Health Gate + Global Pause/Resume - Context

**Gathered:** 2026-06-08
**Status:** Ready for planning

<domain>
## Phase Boundary

The Keeper runs a proactive background **BIT (built-in test) loop** that probes L2 (read + write-then-delete scratch key) on a configurable delay and drives two outputs:
1. A **global pause-all / resume-all broadcast** to all orchestrator replicas (unhealthy → pause all jobs; healthy → resume all jobs).
2. A local in-Keeper **health gate** the Phase-46 recovery consumer will await before performing any L2 op.

The orchestrator's pause-all/resume-all is idempotent: pause via scheduler-wide `PauseAll()`, resume per-job via the existing `TriggerState == Paused` guard.

**Requirements (locked):** KEEP-01, KEEP-02, KEEP-03, ORCH-02.

**NOT in this phase:**
- The recovery consumer that *processes* UPDATE/REINJECT/INJECT/DELETE/CLEANUP — **Phase 46**. Phase 45 only builds the gate *mechanism* (`IL2HealthGate` + `WaitForOpenAsync`); Phase 46 is its first consumer.
- `_DLQ1` consolidation + at-least-once semantics — **Phase 47**.
- Removal of the reactive `Fault<T>` recovery path + per-workflow `PauseWorkflow`/`ResumeWorkflow` + `keeper-dlq` — **Phase 48 (RETIRE-03)**.

</domain>

<decisions>
## Implementation Decisions

### Orchestrator pause-all / resume-all mechanism (ORCH-02)
- **D-01:** **Pause-all = Quartz native `scheduler.PauseAll()`** — one scheduler-wide call, idempotent (re-pausing all groups is a no-op). It holds all the in-flight one-shot triggers in place.
- **D-02:** **Resume-all = per-job, NOT native `ResumeAll()`.** The `ResumeAll` consumer iterates the `IWorkflowL1Store` snapshot and calls the existing `WorkflowLifecycle.ResumeAsync(workflowId)` for each. Rationale: the orchestrator does NOT use repeating Quartz cron triggers — it uses self-perpetuating **one-shot `SimpleSchedule` triggers** with `WithMisfireHandlingInstructionFireNow()` (`WorkflowScheduler.cs:47`). Native `ResumeAll()` would un-pause every past-due one-shot and **fire them all immediately** = a cross-workflow catch-up burst. `ResumeAsync` instead does `UnscheduleAsync` + fresh-from-now `ScheduleAsync` off each workflow's cron → **skips missed fires, resumes at the next scheduled occurrence, no burst.** This is the only model that honors "skip to next occurrence" given the one-shot trigger design.
- **D-03:** **Resume idempotency is already provided** by `ResumeAsync`'s `state != TriggerState.Paused → ignore` guard (resume only if Paused). Pause idempotency is provided by `PauseAll()` at the scheduler level. Together these satisfy ORCH-02 — no new per-job pause guard needed.
- **D-04:** **`[DisallowConcurrentExecution]` on `WorkflowFireJob` (`WorkflowFireJob.cs:29`) is relied upon as-is, unchanged** — it already guarantees a single jobKey never double-fires (same-workflow overlap). The skip-to-next resume (D-02) is what prevents the *cross-workflow* herd, which `[DisallowConcurrentExecution]` does not cover.
- **D-05 (success-criteria reword):** ROADMAP Phase-45 **SC#4** currently reads "idempotent per job via Quartz `TriggerState` — pause only if Running, resume only if Paused." Reword to reflect D-01/D-02/D-03: pause-all is scheduler-wide idempotent (`PauseAll()`); resume-all is per-job idempotent via the existing `TriggerState == Paused` guard with a fresh-from-now reschedule. The verification target must match what is built.

### Broadcast cadence + transport (KEEP-01 / KEEP-02)
- **D-06:** **Edge-triggered broadcast.** The BIT loop tracks the previous health state and publishes `PauseAll` once on healthy→unhealthy and `ResumeAll` once on unhealthy→healthy — NOT on every tick. Avoids per-tick L1 iteration + log noise on the orchestrator while steady; ORCH-02 idempotency still covers any redelivery. Costs one bool of previous-state in the loop.
- **D-07 (success-criteria reword):** ROADMAP Phase-45 **SC#2** currently reads "Each BIT result fans out a global broadcast." Reword to "each health **transition** fans out a global broadcast" to match the edge-triggered decision (D-06). The design doc's literal "each BIT result fans out a broadcast" (§BIT health gate) is read as descriptive of intent, not a mandate for per-tick publishing — idempotency exists as a safety net, not a license to spam.
- **D-08:** **Transport = MassTransit `Publish` to a NEW dedicated per-replica fan-out endpoint** (e.g. `orchestrator-global-pause`, `InstanceId` + `Temporary = true`), mirroring the existing per-workflow pattern (`Orchestrator/Program.cs:41–50`) but **independent** from `orchestrator-pauseresume`. Every orchestrator replica receives its own copy of the broadcast and pauses/resumes its own in-memory Quartz scheduler + L1 (both are per-instance state). Keeping it on a separate endpoint lets Phase 48 drop the old per-workflow endpoint + consumers with zero entanglement.

### BIT health gate primitive (KEEP-03)
- **D-09:** **Dedicated `IL2HealthGate` DI-singleton**, injected into both the BIT loop (writer) and the Phase-46 recovery consumer (reader). API: `Open()` / `Close()` (called by the loop on health transitions) and `WaitForOpenAsync(CancellationToken)` (awaited by the consumer).
- **D-10:** **Async reset-event implementation** — a swappable `TaskCompletionSource` (open = completed task, close = fresh pending task); `WaitForOpenAsync` awaits the current task. **No polling, no busy-wait, no `volatile` flag loop.**
- **D-11:** **The consumer owns the bound.** `WaitForOpenAsync` is awaited inside `Consume` (holding the broker delivery un-acked), so the Phase-46 consumer applies its own bounded timeout via a linked `CancellationTokenSource` kept **well under the RabbitMQ 30-min `consumer_timeout`**. Phase 45 builds the gate + wait primitive; Phase 46 exercises it with the actual bound (the exact timeout value is a Phase-46 concern).
- **D-12:** **Gate starts CLOSED** until the first successful BIT probe opens it (fail-safe). A recovery message arriving before the first probe waits (bounded) rather than acting on L2 whose health has not yet been confirmed.

### Old per-workflow pause/resume path
- **D-13:** **Phase 45 is purely ADDITIVE on the pause path — defer all removal to Phase 48.** The per-workflow `PauseWorkflow`/`ResumeWorkflow` contracts + their orchestrator consumers (`PauseWorkflowConsumer`/`ResumeWorkflowConsumer` + definitions) stay live and untouched. Their **sole sender** is `KeeperRecoveryHandler` (`KeeperRecoveryHandler.cs:102,158`), which is the reactive `Fault<T>` recovery path scheduled for removal in **Phase 48 / RETIRE-03**. Deleting the contracts now would break that still-live handler's compile and pull Phase-48 teardown forward (risking a non-buildable intermediate). RETIRE-03 sweeps handler + per-workflow contracts + consumers + `keeper-dlq` together.

### Claude's Discretion
- **Probe primitive reuse (grounded default, not a fork):** extract a single-probe `ProbeOnceAsync() → bool healthy` core from the read (`L2[entryId]` / `ExecutionData`) + write-then-delete-scratch (`KeeperProbe`) logic already in `L2ProbeRecovery.RunAsync` (`L2ProbeRecovery.cs:36–48`), catching `RedisException` → unhealthy. The v4.0 BIT loop is the *outer* `while` calling `ProbeOnceAsync` once per tick — distinct shape from the v3.x bounded `MaxAttempts` loop, so this is a refactor-extract, not a wholesale reuse. Do **not** catch `Exception` (a genuine bug must not masquerade as "down" — preserve the existing `RedisException`-only discipline).
- **BIT loop hosting:** the obvious .NET pattern is a `BackgroundService`/`IHostedService` in the Keeper composition root (`Keeper/Program.cs`). Exact class shape, the `Probe:DelaySeconds` reuse vs a new knob, and graceful-shutdown wiring are Claude's to design.
- **`IL2HealthGate` namespace/placement** and the exact `TaskCompletionSource` swap mechanics (e.g. `Interlocked.Exchange`, `TaskCreationOptions.RunContinuationsAsynchronously`) are Claude's within D-09/D-10.
- **New contract shape** for `PauseAll`/`ResumeAll`: follow the v4.0.0 no-`H` posture (RETIRE-01) — these carry no `H`/dedup key; a `CorrelationId` for tracing is acceptable. Mirror the existing control-message conventions minus `H`.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Locked design (source of truth)
- `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` — LOCKED 2026-06-08. **§Keeper → "BIT health gate (background)"** (lines 82–85) is the authoritative spec for the BIT loop, the global pause/resume broadcast, and the per-job TriggerState idempotency. **§Locked decisions** A14 (line 111, global pause-all replaces per-workflow) and A4 (line 112, single `_DLQ1` — Phase 47). The recovery-consumer gate semantics (line 90, "performs the L2 op only when the gate is open; gate-closed → waits bounded under the broker consumer timeout") are what `IL2HealthGate` realizes.

### Requirements
- `.planning/REQUIREMENTS.md` — KEEP-01 (line 40), KEEP-02 (line 41), KEEP-03 (line 42), ORCH-02 (line 37). Every id must be accounted for in the plan.
- `.planning/ROADMAP.md` §"Phase 45: Keeper BIT Health Gate + Global Pause/Resume" (lines 467–476) — the 4 success criteria are the verification target. **NOTE the SC rewords:** SC#2 → "each health transition broadcasts" (D-07); SC#4 → scheduler-wide pause-all idempotent + per-job resume idempotency (D-05).

### Existing code this phase builds on / reuses (read before planning)
- `src/Orchestrator/Scheduling/WorkflowScheduler.cs` — `PauseAsync`/`GetTriggerStateAsync`/`ScheduleAsync`; the one-shot `SimpleSchedule` + `WithMisfireHandlingInstructionFireNow()` trigger model (line 47) that forces D-02. The new `PauseAll` consumer calls `scheduler.PauseAll()` (the raw `IScheduler`); a thin `PauseAllAsync`/`ResumeAll` seam here is reasonable.
- `src/Orchestrator/Hydration/WorkflowLifecycle.cs` — `ResumeAsync` (lines 178–207): the fresh-from-now per-job resume with the `TriggerState == Paused` guard that `ResumeAll` iterates (D-02/D-03).
- `src/Orchestrator/L1/IWorkflowL1Store.cs` — the "snapshot of currently-held workflow ids" (line ~22) that `ResumeAll` enumerates.
- `src/Orchestrator/Scheduling/WorkflowFireJob.cs` — `[DisallowConcurrentExecution]` (line 29), relied on unchanged (D-04).
- `src/Orchestrator/Program.cs` — the per-replica fan-out endpoint pattern (`InstanceId` + `Temporary = true`, lines 41–50) the new global consumers mirror on a NEW endpoint (D-08).
- `src/Keeper/Recovery/L2ProbeRecovery.cs` — the read + write-then-delete probe logic (lines 36–48) to extract `ProbeOnceAsync` from; `RedisException`-only discipline.
- `src/Keeper/ProbeOptions.cs` — `Probe:DelaySeconds` knob (the BIT delay); already bound in `Keeper/Program.cs`.
- `src/Keeper/Program.cs` — the Keeper composition root where the `BackgroundService` BIT loop + `IL2HealthGate` singleton register.

### Prior-phase contracts (consumed, not modified here)
- `.planning/phases/43-message-contracts-l2-key-reshape/43-CONTEXT.md` — the five `Keeper*` records + `KeeperQueues`, `L2ProjectionKeys`, no-`H` posture (RETIRE-01). The new `PauseAll`/`ResumeAll` follow the same no-`H` convention.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`WorkflowLifecycle.ResumeAsync`** — already does fresh-from-now per-job resume with the `TriggerState == Paused` guard. `ResumeAll` iterates the L1 snapshot calling this (D-02). No new resume logic needed.
- **`IWorkflowL1Store` snapshot** — the enumeration source for `ResumeAll`.
- **`WorkflowScheduler` / raw `IScheduler`** — `PauseAll()` is the native pause-all (D-01); `ResumeAsync` reuses the existing per-job scheduler ops.
- **`L2ProbeRecovery` probe core** — the read + write-then-delete + `RedisException` handling to extract `ProbeOnceAsync` from for the BIT loop.
- **Per-replica fan-out endpoint pattern** (`Orchestrator/Program.cs:41–50`) — copy the `InstanceId` + `Temporary = true` idiom for the new global consumers on a new endpoint.

### Established Patterns
- **One-shot `SimpleSchedule(FireNow)` triggers, self-perpetuating off Cronos** — NOT repeating Quartz cron triggers. This is the load-bearing fact behind D-02 (native `ResumeAll()` would fire all past-due one-shots immediately).
- **Per-replica fan-out vs shared competing-consumer** — pause/resume broadcasts use per-replica fan-out (each replica acts on its own scheduler); result/work queues use shared competing-consumer. The new global pause/resume is fan-out (D-08).
- **`RedisException`-only infra discipline** — never `catch (Exception)` in the probe (a bug must not look like "L2 down").

### Integration Points (files this phase adds/rewrites)
- **Add (Keeper):** the BIT-loop `BackgroundService`; `IL2HealthGate` + its implementation; `ProbeOnceAsync` (extracted from `L2ProbeRecovery`). Register all in `Keeper/Program.cs`.
- **Add (Messaging.Contracts):** `PauseAll` / `ResumeAll` global control contracts (no `H`).
- **Add (Orchestrator):** `PauseAllConsumer` (→ `scheduler.PauseAll()`) + `ResumeAllConsumer` (→ iterate L1 snapshot → `ResumeAsync` each) + their `ConsumerDefinition`s bound to the new per-replica fan-out endpoint; register in `Orchestrator/Program.cs`.
- **Untouched (defer to Phase 48):** `PauseWorkflow`/`ResumeWorkflow` + their consumers + `KeeperRecoveryHandler`.

</code_context>

<specifics>
## Specific Ideas

- The resume path must NEVER produce a catch-up burst: after an L2 blip, resumed workflows fire at their *next* scheduled occurrence, not immediately. This is the explicit user intent behind choosing skip-to-next, and the reason resume is per-job (D-02) rather than native `ResumeAll()`.
- The gate is fail-safe: closed until the first healthy probe (D-12) — never act on L2 before BIT has confirmed it up at least once.
- Edge-triggered broadcast (D-06): a steadily-healthy system emits zero pause/resume traffic; broadcasts happen only at the two transition edges.

</specifics>

<deferred>
## Deferred Ideas

- **Removal of the per-workflow `PauseWorkflow`/`ResumeWorkflow` path** + the reactive `KeeperRecoveryHandler` that sends it — **Phase 48 / RETIRE-03** (D-13). Phase 45 leaves it live and additive.
- **Transition-window coexistence (Phases 45–47):** during the migration both recovery mechanisms run — the reactive `KeeperRecoveryHandler` issues per-workflow `PauseWorkflow`/`ResumeWorkflow`, and the new BIT loop issues global `PauseAll`/`ResumeAll`. Both can pause the orchestrator concurrently. This is the planned migration overlap; the reactive path's switch-off is Phase 47/48 territory, not Phase 45. Noted so a downstream phase consciously decides *when* the reactive recovery driver is disabled.
- **Exact Phase-46 gate wait bound** (the concrete `WaitForOpenAsync` timeout value under the broker `consumer_timeout`) — Phase 46 sets it; Phase 45 only builds the mechanism (D-11).

</deferred>

---

*Phase: 45-keeper-bit-health-gate-global-pause-resume*
*Context gathered: 2026-06-08*
