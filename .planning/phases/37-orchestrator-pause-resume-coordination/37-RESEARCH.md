# Phase 37: Orchestrator Pause/Resume Coordination - Research

**Researched:** 2026-06-06
**Domain:** Quartz.NET scheduling mechanics + MassTransit control-message contracts/consumers (.NET 8 / C#)
**Confidence:** HIGH (all primary claims grounded in repo source; Quartz semantics cross-verified against official docs/source)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01:** New `PauseWorkflow` and `ResumeWorkflow` sealed records in `Messaging.Contracts`, shaped exactly like `StartOrchestration` (implement `ICorrelated`, body-carried `CorrelationId`). Each carries `WorkflowId` + `H`. The inner fault messages already expose both via `IExecutionCorrelated` (`WorkflowId`, `H`), so Keeper reads them straight off `context.Message.Message`.
- **D-02:** `H` is carried for **correlation / observability only** — NOT for counting or reference-tracking. PAUSE-04's "do not double-count" holds trivially because there is no count to keep.
- **D-03:** In *both* Keeper consumers (`FaultEntryStepDispatchConsumer`, `FaultExecutionResultConsumer`): publish `PauseWorkflow(workflowId)` **at intake, before** the `L2ProbeRecovery.RunAsync` loop starts.
- **D-04:** On `ProbeOutcome.Recovered` — after the existing re-inject `Send` — publish `ResumeWorkflow(workflowId)`. On `ProbeOutcome.GaveUp` — park the original `Fault<T>` to `keeper-dlq` (existing Phase-36 behavior) and publish **nothing**.
- **D-05:** A workflow's scheduling state is **derived from Quartz**, via `IScheduler.GetTriggerState(triggerKey)` — **no separate L1 state field, no in-memory marker**: `Normal`→Running, `Paused`→Paused, `None`→Stopped.
- **D-06:** Mechanism per state transition: Stop = `DeleteJob`→`None` (unchanged); Pause = `PauseJob`→`Paused` (L1 preserved); Resume = read `GetTriggerState`, **only if `Paused`** delete the stale paused trigger and `ScheduleAsync` a **fresh** trigger recomputed from L1's `cron`; if `None` or `Normal` → **ignore**. Start = schedule→`Normal` (unchanged).
- **D-07:** **No dedicated lock and no reference-counting set.** Pause/resume consumer runs at **`ConcurrentMessageLimit = 1`** (serial); every handler is **idempotent**; crash before ack → redelivered → reprocessed. Deliberately does **NOT** reuse the `IWorkflowL1Store` drop-if-held (`Wait(0)`) stripe.
- **D-08 (revises PAUSE-02):** Pause halts cron via **`PauseJob`** (Quartz-native), NOT `UnscheduleOnlyAsync`/`DeleteJob`.
- **D-09 (revises PAUSE-05):** Orchestrator resumes on **any** successful recovery regardless of siblings. Give-up → `keeper-dlq`, publishes nothing, does NOT re-pin paused. A workflow stays paused only if NO recovery ever succeeds.

### Claude's Discretion
- The deterministic per-workflow `TriggerKey` derivation needed for `GetTriggerState` (scheduler currently addresses jobs by `JobKey(jobId)`). **See §1 — resolved below.**
- Exact placement of the two new orchestrator consumers (endpoint topology mirroring Start/Stop fan-out) and the new contracts' file layout. **See §5 — resolved below.**

### Deferred Ideas (OUT OF SCOPE)
- Quartz-native `ResumeJob` on the stale trigger (rejected in favor of delete-stale + recompute-from-now).
- Durable pause-state across orchestrator restart (HydrationBackgroundService reschedules all → paused workflow resumes on restart). Accepted, documented, out of scope.
- Auto-resume after give-up — explicitly an operator action (FUTURE-KEEPER-SWEEP).
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description (with D-08/D-09 revisions applied) | Research Support |
|----|-------------------------------------------------|------------------|
| PAUSE-01 | `PauseWorkflow`/`ResumeWorkflow` contracts + Keeper publish sites | §5 (contract shape mirrors `StartOrchestration`), §6 (publish sites have `WorkflowId`/`H` in scope via `inner` = `IExecutionCorrelated`) |
| PAUSE-02 (rev D-08) | Pause halts cron via Quartz `PauseJob`; L1 preserved | §1–§2 (TriggerKey scheme + confirmation PauseJob suppresses the next self-reschedule) |
| PAUSE-03 | Resume reschedules from L1 cron (recompute-from-now) | §3 (delete-stale + fresh `ScheduleAsync` sidesteps misfire), §7 (L1 carries `JobId`+`Cron`) |
| PAUSE-04 | No double-count under duplicate/concurrent signals | §5 (`ConcurrentMessageLimit = 1`), D-02/D-07 (no count to keep; idempotent transitions) |
| PAUSE-05 (rev D-09) | Resume on any successful recovery; give-up parks to DLQ, no re-pin | §6 (publish `ResumeWorkflow` on `Recovered` only; `GaveUp` already parks to `keeper-dlq`) |
</phase_requirements>

## Summary

This phase adds two `ICorrelated` control contracts and two orchestrator consumers that drive a Quartz-owned three-state model (Running/Paused/Stopped) read via `IScheduler.GetTriggerState`. The decisions are locked; the open mechanics were the deterministic `TriggerKey` scheme and the `PauseJob`/resume/misfire interaction. **All four primary research questions are resolved with HIGH confidence from repo source plus Quartz official docs.**

**The one load-bearing implementation fact the planner MUST know:** the current scheduler creates triggers with **no explicit identity** — `TriggerBuilder.Create()` is called without `.WithIdentity(...)` in both `ScheduleAsync` (`WorkflowScheduler.cs:41`) and `RescheduleAsync` (`WorkflowScheduler.cs:64`), so Quartz auto-generates a random GUID trigger name on every fire. There is therefore **no deterministic TriggerKey to query today.** `GetTriggerState(triggerKey)` cannot be made deterministic without the scheduler first stamping a stable per-workflow `TriggerKey` on every trigger it builds. This is a required change to `WorkflowScheduler.ScheduleAsync` **and** `RescheduleAsync` (the self-reschedule path), not just a new method.

**Primary recommendation:** Derive a deterministic `TriggerKey` from the same `jobId` already used for `JobKey` — `new TriggerKey(jobId.ToString("D"))` — and stamp it via `.WithIdentity(triggerKey)` in `ScheduleAsync`, `RescheduleAsync`, and the new resume reschedule. Add `PauseJob(JobKey)` and `GetTriggerState(TriggerKey)` wrapper methods to `WorkflowScheduler`. Mirror the `StartOrchestration` record and the Start/Stop consumer + definition + per-replica fan-out endpoint exactly; set `ConcurrentMessageLimit = 1` on the new consumers' shared definition.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| `PauseWorkflow`/`ResumeWorkflow` contracts | Messaging.Contracts | — | Leaf shared vocabulary; both Keeper (publisher) and Orchestrator (consumer) reference it (mirrors `StartOrchestration`) |
| Publish pause-at-intake / resume-on-recovery | Keeper (fault consumers + recovery loop) | — | `workflowId`/`H` only in scope inside the fault consumers; recovery outcome is Keeper-owned |
| Halt/reschedule cron (PauseJob, GetTriggerState, fresh trigger) | Orchestrator (`WorkflowScheduler`) | Orchestrator consumers | Quartz `IScheduler` is an Orchestrator-only singleton; only the Orchestrator project touches scheduling |
| Three-state derivation (Running/Paused/Stopped) | Orchestrator (Quartz job-store) | — | D-05: Quartz is the single source of truth; no L1/in-memory state field |
| Serial idempotent consume | Orchestrator consumer definition | — | `ConcurrentMessageLimit = 1` + redelivery-on-crash is the concurrency model (D-07) |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Quartz | 3.18.1 | Scheduler: `PauseJob`, `GetTriggerState`, `ScheduleJob`, `DeleteJob` | Already the orchestrator scheduler (ORCH-SCHED-01); RAMJobStore single-replica `[VERIFIED: Directory.Packages.props:96]` |
| Quartz.Extensions.Hosting | 3.18.1 | Hosted scheduler + MS-DI job factory | Already wired in `Orchestrator/Program.cs:53-54` `[VERIFIED: Directory.Packages.props:97]` |
| MassTransit | (repo-pinned) | Consumers, `IConsumer<T>`, `ConsumerDefinition<T>`, publish/consume | Bus tier for all control messages `[VERIFIED: src/*/Program.cs]` |

**No new packages.** This phase reuses the existing Quartz + MassTransit stack. `[VERIFIED: Orchestrator.csproj:39 has only Quartz.Extensions.Hosting; no new ref needed]`

### Version verification
`[VERIFIED: Directory.Packages.props:96-97]` Quartz + Quartz.Extensions.Hosting both 3.18.1 (CPM-pinned). `dotnet --version` = 8.0.421 `[VERIFIED: Bash]`.

## Architecture Patterns

### System Architecture Diagram

```
  Keeper fault consumer (FaultEntryStepDispatch / FaultExecutionResult)
        │  inner = context.Message.Message  (IExecutionCorrelated: WorkflowId, H)
        │
        ├─[INTAKE, before RunAsync]──► Publish PauseWorkflow(WorkflowId, H)
        │                                          │
        ▼                                          │   (bus: orchestrator per-replica
   L2ProbeRecovery.RunAsync(entryId, H)            │    fan-out endpoint)
        │                                          ▼
        ├─ Recovered ─► Send(inner) to origin      Orchestrator PauseWorkflowConsumer
        │              ─► Publish ResumeWorkflow ─► scheduler.PauseJob(JobKey(jobId))
        │                                          └─► GetTriggerState → Paused
        │
        └─ GaveUp ────► Send(Fault<T>) to keeper-dlq   (publishes NOTHING — D-09)


   Orchestrator ResumeWorkflowConsumer
        │  resolve jobId+cron from L1 (store.TryGet)
        ▼
   state = scheduler.GetTriggerState(TriggerKey(jobId))
        ├─ Paused  ─► delete stale trigger + ScheduleAsync(wf, jobId, cron) [fresh, from-now] → Normal
        ├─ None    ─► ignore (operator-Stopped)
        └─ Normal  ─► ignore (already Running)
```

File-to-implementation mapping is in Component Responsibilities (below), not the diagram.

### Component Responsibilities
| File | Change | Reuses |
|------|--------|--------|
| `src/Messaging.Contracts/PauseWorkflow.cs` (new) | `sealed record PauseWorkflow(Guid WorkflowId, string H) : ICorrelated` | `StartOrchestration.cs` shape |
| `src/Messaging.Contracts/ResumeWorkflow.cs` (new) | same shape | `StartOrchestration.cs` shape |
| `src/Orchestrator/Scheduling/WorkflowScheduler.cs` | Add deterministic `TriggerKey` stamping to `ScheduleAsync`+`RescheduleAsync`; add `PauseAsync(jobId)` + `GetTriggerStateAsync(jobId)` + resume reschedule | existing `KeyFor`, `ScheduleAsync` |
| `src/Orchestrator/Consumers/PauseWorkflowConsumer.cs` (new) | mirror `StopOrchestrationConsumer` | `WorkflowLifecycle` / `WorkflowScheduler` |
| `src/Orchestrator/Consumers/ResumeWorkflowConsumer.cs` (new) | new resume logic | `WorkflowScheduler`, L1 `JobId`/`Cron` |
| `src/Orchestrator/Consumers/PauseResume*Definition.cs` (new) | `ConcurrentMessageLimit = 1`; shared `EndpointName = "orchestrator"` | `Start/StopOrchestrationConsumerDefinition` |
| `src/Orchestrator/Program.cs` | register the two consumers with `.Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; })` | lines 40-43 |
| `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs` | publish `PauseWorkflow` at intake; `ResumeWorkflow` on `Recovered` | `inner.WorkflowId`, `inner.H` |
| `src/Keeper/Consumers/FaultExecutionResultConsumer.cs` | same | same |

### Pattern: Deterministic TriggerKey (the resolution to the research flag)
**What:** Stamp every trigger built by `WorkflowScheduler` with a stable identity derived from `jobId`.
**Why:** `GetTriggerState(triggerKey)` requires a known key; the current code never sets one.
```csharp
// Source: derived from WorkflowScheduler.cs:19 (KeyFor) + Quartz TriggerBuilder.WithIdentity
private static JobKey     JobKeyFor(Guid jobId)     => new(jobId.ToString("D"));
private static TriggerKey TriggerKeyFor(Guid jobId) => new(jobId.ToString("D")); // 1:1 with the job

// In ScheduleAsync AND RescheduleAsync, change:
//   TriggerBuilder.Create().ForJob(jobKey)...
// to:
//   TriggerBuilder.Create().WithIdentity(TriggerKeyFor(jobId)).ForJob(jobKey)...
```
This matches the convention `23-REVIEW.md:104` already documented: `var triggerKey = new TriggerKey(jobId.ToString("D"));`. `[VERIFIED: .planning/phases/23-orchestrator-stop-reload-lifecycle/23-REVIEW.md:104]`

### Anti-Patterns to Avoid
- **Querying GetTriggerState without first stamping a deterministic TriggerKey** — returns a random auto-key's state or `None`; the whole three-state model breaks silently. The scheduler change to `WithIdentity` is a PREREQUISITE, not optional.
- **Quartz `ResumeJob` on the stale one-shot trigger** — explicitly deferred (CONTEXT.md). The stale trigger's `StartAt` is in the past while paused; `ResumeJob` would hit one-shot misfire semantics. Use delete-stale + fresh `ScheduleAsync` instead.
- **Reusing the `IWorkflowL1Store` `Wait(0)` drop-if-held stripe** — it *drops* a contended caller, silently losing a Resume and stranding a workflow paused forever (D-07).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Pause/resume state tracking | A custom L1 `bool IsPaused` field or in-memory set | `IScheduler.GetTriggerState` (D-05) | Quartz already tracks Normal/Paused/None atomically in its job store; a parallel field desyncs under crash/restart |
| Concurrency control on the consumer | A custom lock / reference count | `ConcurrentMessageLimit = 1` + idempotent handlers + redelivery (D-07) | Serial consume + Quartz idempotency makes the transition a no-op on replay; a lock adds drop-if-held loss risk |
| Halting the cron | A flag the fire job reads to skip | Quartz `PauseJob` | `PauseJob` prevents the trigger from firing at the source, so `WorkflowFireJob.Execute` never runs and never re-adds a trigger (§2) |

**Key insight:** This phase's correctness comes entirely from *deferring to Quartz's own job-store state machine* rather than maintaining any parallel orchestrator state.

## Primary Research Questions — Resolved

### §1. TriggerKey derivation (RESOLVED — HIGH)
**Current keying:** Jobs are keyed `JobKey(jobId.ToString("D"))` via `WorkflowScheduler.KeyFor` `[VERIFIED: WorkflowScheduler.cs:19]`. Triggers, however, are built **without any identity** — `TriggerBuilder.Create()` with no `.WithIdentity(...)` in both `ScheduleAsync` `[VERIFIED: WorkflowScheduler.cs:41-45]` and `RescheduleAsync` `[VERIFIED: WorkflowScheduler.cs:64-68]`. Quartz auto-assigns a random GUID trigger name when none is given. The existing test confirms triggers are only ever discovered indirectly via `GetTriggersOfJob(jobKey)`, never by a known TriggerKey `[VERIFIED: SchedulingTests.cs:61-66]`.

**Required scheme:** `new TriggerKey(jobId.ToString("D"))` — a 1:1 deterministic key with the job, identical string to the `JobKey`. Since there is exactly one live trigger per job at any moment (one-shot self-reschedule), a single deterministic key is sufficient and never collides. The scheduler must call `.WithIdentity(TriggerKeyFor(jobId))` in `ScheduleAsync`, `RescheduleAsync`, **and** the new resume reschedule so the key stays stable across self-reschedules.

> **Planner note — this is a modification to existing methods, not purely additive.** `RescheduleAsync` (the self-reschedule on every fire, `WorkflowFireJob.cs:103`) must also stamp the deterministic key, otherwise after the first fire the trigger reverts to a random key and `GetTriggerState(TriggerKey(jobId))` returns `None`. `SchedulingTests.cs` will keep passing (it queries via `GetTriggersOfJob`), so add an explicit new test asserting `GetTriggerState(TriggerKey(jobId)) == Normal` after a fresh schedule.

### §2. PauseJob vs the one-shot self-rescheduling model (RESOLVED — HIGH)
`PauseJob(JobKey)` "pauses the IJobDetail with the given key by pausing all of its current ITriggers" `[CITED: github.com/quartznet/quartznet IScheduler.cs]`. The single live one-shot trigger is therefore moved to `Paused`. When that trigger's `StartAt` time arrives, Quartz does **not** fire a paused trigger — so `WorkflowFireJob.Execute` never runs, and the self-reschedule at `WorkflowFireJob.cs:101-104` (which is the ONLY thing that re-adds a trigger) never executes. The cron chain halts. `[VERIFIED: WorkflowFireJob.cs:100-104 — RescheduleAsync is reached only from inside Execute]`

**Edge case — fire already in-flight when PauseJob is called:** `WorkflowFireJob` is `[DisallowConcurrentExecution]` `[VERIFIED: WorkflowFireJob.cs:30]`. If a fire is mid-flight when Pause arrives, that fire completes and **will** call `RescheduleAsync`, adding one more future trigger. That trigger is added to the (now paused) job; Quartz applies the job's paused state, so the freshly-added trigger does not fire either. Net effect: at most one extra trigger is created but it is also paused — the cron is still halted. The deterministic `TriggerKey` means this in-flight reschedule **overwrites the same key** (Quartz replaces a trigger scheduled with an existing identity), so no orphan accumulates. This is acceptable and requires no extra handling; document it. `[ASSUMED: in-flight reschedule lands paused — based on PauseJob pausing the job's triggers; verify with a test in Validation §b]`

### §3. Resume misfire avoidance (RESOLVED — HIGH)
On `GetTriggerState == Paused`: delete the stale paused trigger and `ScheduleAsync` a fresh trigger recomputed from now off L1's `cron`. The stale one-shot trigger's `StartAt` is a past time (it was due while paused), so resuming it directly would trigger Quartz's one-shot misfire policy. Building a **fresh** trigger via the existing `ScheduleAsync(workflowId, jobId, cron, ct)` recomputes `CronInterval.NextOccurrence(cron, nowUtc)` `[VERIFIED: WorkflowScheduler.cs:28-29]` — a future `StartAt` — so misfire never applies.

**Primitive to reuse:** `WorkflowScheduler.ScheduleAsync(workflowId, jobId, cron, ct)` `[VERIFIED: WorkflowScheduler.cs:26]`. Note `ScheduleAsync` calls `scheduler.ScheduleJob(job, trigger)` which **re-creates the job**; the resume path must first delete the stale paused trigger (and may delete the job) so re-`ScheduleAsync` does not collide with the existing paused job. **Cleanest resume sequence:**
1. `GetTriggerState(TriggerKey(jobId))`; act only if `Paused`.
2. `DeleteJob(JobKey(jobId))` (atomically removes the paused job + its stale trigger).
3. `ScheduleAsync(workflowId, jobId, cron, ct)` — fresh job + fresh future trigger → `Normal`.

This reuses `UnscheduleAsync` (`DeleteJob`, `WorkflowScheduler.cs:76-77`) + `ScheduleAsync` exactly as the Start reload already does (`StartOrchestrationConsumer.cs:36-37` calls Teardown then HydrateAndSchedule) — a proven delete-then-reschedule idiom. `[VERIFIED: StartOrchestrationConsumer.cs:36-37]`

> Alternative the planner may prefer: delete only the stale trigger (`UnscheduleTrigger(TriggerKey)`) and re-add a fresh trigger to the existing (still-paused) job — but the job stays Paused, so a freshly-added trigger inherits Paused and won't fire. **Therefore the delete-job + ScheduleAsync route above is required** (it produces a non-paused job). Do NOT keep the paused job and merely re-trigger it.

### §4. GetTriggerState return values (RESOLVED — HIGH)
TriggerState enum values: `Normal, Paused, Complete, Error, Blocked, None` `[CITED: github.com/quartznet/quartznet IScheduler.cs]`. Mapping per D-05:
- `Normal` → Running `[VERIFIED: SchedulingTests.cs:66 asserts a started trigger is Normal]`
- `Paused` → Paused
- `None` → Stopped — **`GetTriggerState` returns `TriggerState.None` for a non-existent trigger key** `[CITED: Quartz IScheduler.cs remarks list None as a possible return]`. So a Stopped (DeleteJob'd) workflow's `TriggerKey(jobId)` query returns `None` cleanly; no exception. `[ASSUMED: no exception thrown for unknown key — Quartz returns None rather than throwing; confidence HIGH from docs but flagged for the Validation test in §e]`

**Other states (`Complete`/`Error`/`Blocked`):** Resume must treat them the same as the "not Paused" branch — **ignore** (only `Paused` resumes). For this phase's one-shot self-rescheduling model none of these are expected steady states: `Complete` would only appear transiently, `Error` requires a job-store error, `Blocked` is the `[DisallowConcurrentExecution]` mid-fire state. The locked rule "**resume only if `Paused`**" (D-06) already covers them — a non-`Paused` state is a no-op. Recommend the resume code switch on `== TriggerState.Paused` explicitly (not `!= None`), so `Blocked`/`Error` correctly fall through to ignore. `[VERIFIED: D-06 locked rule + GitHub issue #193 notes paused-while-blocked can read as Paused_Blocked — guarding on exact `Paused` is safest]`

## Secondary Research — Resolved

### §5. Consumer/contract patterns to mirror
**Contract shape** `[VERIFIED: StartOrchestration.cs:4-7]`:
```csharp
public sealed record StartOrchestration(Guid[] WorkflowIds) : ICorrelated
{
    public Guid CorrelationId { get; init; }
}
```
New contracts (D-01 — single `WorkflowId` + `H`, not an array, since Keeper publishes per-workflow):
```csharp
// src/Messaging.Contracts/PauseWorkflow.cs
public sealed record PauseWorkflow(Guid WorkflowId, string H) : ICorrelated
{
    public Guid CorrelationId { get; init; }
}
// src/Messaging.Contracts/ResumeWorkflow.cs — identical shape
```
`ICorrelated` is body-carried; the outbound publish filter stamps the envelope from the ambient accessor for any `ICorrelated` message `[VERIFIED: OutboundCorrelationPublishFilter.cs:17-18]`.

**Consumer shape:** mirror `StopOrchestrationConsumer` `[VERIFIED: StopOrchestrationConsumer.cs:23-38]` — primary-ctor DI of the lifecycle/scheduler + logger, `Consume` returns normally → ACK.

**Endpoint/topology wiring** `[VERIFIED: Program.cs:35-50]`: Start and Stop share `EndpointName = "orchestrator"` `[VERIFIED: StartOrchestrationConsumerDefinition.cs:27, StopOrchestrationConsumerDefinition.cs:22]` and are registered with `.Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; })` `[VERIFIED: Program.cs:40-43]` → a per-replica auto-delete fan-out queue `orchestrator-{instanceId}` (broadcast, NOT competing-consumer). The two new consumers join this same fan-out: same `EndpointName = "orchestrator"`, same `.Endpoint(... InstanceId = instanceId; Temporary = true)` block.

**`ConcurrentMessageLimit = 1` placement** (D-07): the existing definitions only set `UseMessageRetry` in `ConfigureConsumer` `[VERIFIED: StartOrchestrationConsumerDefinition.cs:38-42]`. Set the limit on the **consumer configurator** inside `ConfigureConsumer`:
```csharp
protected override void ConfigureConsumer(
    IReceiveEndpointConfigurator endpointConfigurator,
    IConsumerConfigurator<PauseWorkflowConsumer> consumerConfigurator,
    IRegistrationContext context)
{
    consumerConfigurator.ConcurrentMessageLimit = 1;   // D-07 serial
    endpointConfigurator.UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit));
}
```
> **Endpoint-sharing caveat (mirror the Keeper Pitfall-3 pattern):** `UseMessageRetry` is **per-endpoint**, not per-consumer `[VERIFIED: FaultEntryStepDispatchConsumerDefinition.cs:14-19 documents this]`. If both new consumers (and Start/Stop) share `EndpointName = "orchestrator"`, only ONE definition may register `UseMessageRetry` for that endpoint; Start/Stop already own it. **Decide:** either (a) put Pause/Resume on the SAME `"orchestrator"` endpoint and make their `ConfigureConsumer` set only `ConcurrentMessageLimit` (no second `UseMessageRetry`), or (b) give Pause/Resume their OWN shared endpoint name (e.g. `"orchestrator-pauseresume"`) so they own their retry + limit independently. **Recommendation: (b)** — a separate fan-out endpoint keeps `ConcurrentMessageLimit = 1` from throttling the unrelated Start/Stop/Result traffic, and avoids the per-endpoint retry-ownership conflict. Both Pause and Resume can share one new endpoint name (like Start+Stop share `"orchestrator"`). `[ASSUMED: separate endpoint is preferable — judgement call within Claude's-discretion topology; planner may choose (a)]`

### §6. Keeper publish sites (RESOLVED — HIGH)
Both fault consumers unwrap `inner = context.Message.Message` which is the verbatim inner `IExecutionCorrelated` `[VERIFIED: FaultEntryStepDispatchConsumer.cs:36, FaultExecutionResultConsumer.cs:37]`. `IExecutionCorrelated` exposes `WorkflowId` `[VERIFIED: IExecutionCorrelated.cs:14]` and `H` (via `EntryStepDispatch.H` / the inner record) `[VERIFIED: EntryStepDispatch.cs:17]`, so both `workflowId` and `H` are in scope at intake — no new plumbing.

**Pause at intake (D-03):** publish `PauseWorkflow(inner.WorkflowId, inner.H)` immediately **before** `await recovery.RunAsync(...)` at `FaultEntryStepDispatchConsumer.cs:49` / `FaultExecutionResultConsumer.cs:50`.

**Resume on Recovered (D-04):** after the existing re-inject `Send` (`FaultEntryStepDispatchConsumer.cs:55-56` / `FaultExecutionResultConsumer.cs:56-57`), publish `ResumeWorkflow(inner.WorkflowId, inner.H)`. On `GaveUp` the existing `else` branch parks `context.Message` to `keeper-dlq` `[VERIFIED: FaultEntryStepDispatchConsumer.cs:60-64]` and must publish nothing (D-09 — leave that branch unchanged).

**How to publish:** Keeper consumers currently use `context.GetSendEndpoint(uri).Send(...)` for the re-inject `[VERIFIED: FaultEntryStepDispatchConsumer.cs:55-56]`. For Pause/Resume, use **`context.Publish(new PauseWorkflow(...))`** — Publish (not Send) targets the orchestrator's per-replica fan-out endpoint by message-type binding, mirroring how the WebApi publishes `StartOrchestration`/`StopOrchestration` via `IPublishEndpoint.Publish` `[VERIFIED: OrchestrationService.cs:263, 377]`. `context.Publish` runs through the same outbound correlation publish filter `[VERIFIED: OutboundCorrelationPublishFilter.cs:17-18]`. Set `CorrelationId` on the body from `inner.CorrelationId` for continuity (the Keeper consumer already opens a manual scope from `inner.CorrelationId` `[VERIFIED: FaultEntryStepDispatchConsumer.cs:39]`).

> **Idempotency note (PAUSE-04 / D-02):** both fault consumers can fire for the SAME workflow concurrently (sibling faults). Each publishes its own `PauseWorkflow`; the orchestrator's `ConcurrentMessageLimit = 1` serial consume + idempotent `PauseJob` (re-pausing an already-paused job is a no-op) absorbs the duplicates. No count is kept (D-02). Resume on the first `Recovered` regardless of siblings (D-09).

### §7. L1 resume source (RESOLVED — HIGH)
`WorkflowL1` carries `Cron` and `JobId` as ctor params `[VERIFIED: WorkflowL1.cs:17-21]` — exactly what resume needs, no new field (D-05). The resume consumer resolves them via `store.TryGet(workflowId, out var wf)` then uses `wf.JobId` + `wf.Cron`, mirroring `WorkflowFireJob.cs:103` (`scheduler.RescheduleAsync(wf.JobId, wf.Cron, ...)`) and `WorkflowLifecycle.UnscheduleOnlyAsync` (`store.TryGet` → `scheduler.UnscheduleAsync(wf.JobId)`) `[VERIFIED: WorkflowLifecycle.cs:149-158]`.

**Seam note:** `HydrateAndScheduleAsync` `[VERIFIED: WorkflowLifecycle.cs:36-118]` reads L2 and re-hydrates L1 before scheduling — heavier than resume needs (resume's L1 is already populated). Resume should NOT call `HydrateAndScheduleAsync` (it would re-read L2 and reset liveness); it should call the lighter `WorkflowScheduler` reschedule directly off the in-memory `wf.JobId`/`wf.Cron`. `UnscheduleOnlyAsync` is the Stop seam (keeps L1) and is the correct sibling to model the resume's "resolve jobId from L1" lookup on. `[VERIFIED: WorkflowLifecycle.cs:149-158]`

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit (`tests/BaseApi.Tests`), with NSubstitute for mocks `[VERIFIED: StopConsumerLifecycleTests.cs:5,13]` |
| Config file | repo CPM; xUnit analyzer enforces `xUnit1051` — every Quartz call taking a CancellationToken must pass `TestContext.Current.CancellationToken` `[VERIFIED: 23-03-SUMMARY.md:101]` |
| Quartz under test | real `StdSchedulerFactory` RAMJobStore, unique `quartz.scheduler.instanceName = test-{Guid:N}` per test to avoid the shared process-wide repository collision `[VERIFIED: SchedulingTests.cs:21-29, StopConsumerLifecycleTests.cs:34-41]` |
| Quick run command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~Orchestrator.Scheduling|FullyQualifiedName~PauseResume"` |
| Full suite command | `dotnet test` (solution) + the live `scripts/phase-NN-close.ps1` 3×GREEN real-stack gate |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| PAUSE-01 | `PauseWorkflow`/`ResumeWorkflow` records implement `ICorrelated`, body-carry `CorrelationId`+`WorkflowId`+`H` | unit | `dotnet test --filter "~PauseWorkflowContract"` | ❌ Wave 0 |
| PAUSE-01 | Keeper publishes `PauseWorkflow` at intake + `ResumeWorkflow` on Recovered (fan-out via Publish) | unit (mock `ConsumeContext.Publish`) | `dotnet test --filter "~KeeperPausePublish"` | ❌ Wave 0 |
| PAUSE-02 (D-08) | Pause → `GetTriggerState(TriggerKey(jobId)) == Paused`; next self-reschedule suppressed (no new live trigger fires) | unit (real RAM scheduler) | `dotnet test --filter "~PauseSuppressesFire"` | ❌ Wave 0 |
| PAUSE-03 | Resume on `Paused` → fresh trigger scheduled, `GetTriggerState == Normal`, future `StartAt` (no misfire) | unit (real RAM scheduler) | `dotnet test --filter "~ResumeReschedulesFresh"` | ❌ Wave 0 |
| PAUSE-04 | Duplicate/concurrent Pause then Resume at `ConcurrentMessageLimit=1` → idempotent end state (one Normal trigger) | unit (serial replay) | `dotnet test --filter "~PauseResumeIdempotent"` | ❌ Wave 0 |
| PAUSE-05 (D-09) | `None` (Stopped) and `Normal` (already Running) workflows ignore Resume (no-op) | unit (real RAM scheduler) | `dotnet test --filter "~ResumeIgnoresStoppedAndRunning"` | ❌ Wave 0 |

### Validation tactics (per the secondary-research §8 asks)
- **(a) contracts exist & fan out:** assert the two records implement `ICorrelated` and carry the fields; assert Keeper publishes them — model on the existing `OrchestratorTestStubs.Context(...)` harness `[VERIFIED: StopConsumerLifecycleTests.cs:93]` and mock `context.Publish<PauseWorkflow>(...)`. Real fan-out delivery is covered by the live close-gate E2E.
- **(b) Pause → Paused & fire suppressed:** schedule via `WorkflowScheduler`, `PauseAsync`, assert `GetTriggerState(TriggerKey(jobId)) == TriggerState.Paused`. To prove the next self-reschedule is suppressed: use a controllable `TimeProvider` and a short-cron trigger, advance past `StartAt`, assert `WorkflowFireJob.Execute` did NOT dispatch (NSubstitute `dispatcher.DidNotReceive()`), mirroring the dispatch-assertion idiom at `StopConsumerLifecycleTests.cs:187-189`. Also covers the in-flight-fire edge (§2).
- **(c) Resume → fresh & Normal:** from a Paused state, run the resume consumer, assert exactly one trigger, `GetTriggerState == Normal`, and its `StartAt` is in the future (no misfire). Reuse the `GetTriggersOfJob` + `GetTriggerState` pattern `[VERIFIED: SchedulingTests.cs:61-66]`.
- **(d) idempotency at `ConcurrentMessageLimit=1`:** invoke the Pause consumer twice then the Resume consumer twice serially over the same scheduler; assert the end state is one `Normal` trigger and no orphan triggers (`GetJobKeys` count == 1). The serial limit is a definition property — assert it via the definition or rely on the integration close-gate; the unit test proves the **transition** is a no-op on replay.
- **(e) Stopped/Running ignore Resume:** `DeleteJob` → `GetTriggerState == None` → run resume → assert still no job (ignored). Schedule (Normal) → run resume → assert the trigger key unchanged / still one `Normal` trigger (ignored). Confirms §4's `None`-returns-cleanly claim.

### Hermetic vs real-stack boundary
- **Hermetic (unit, RAMJobStore + mocks):** all six rows above. Real Quartz RAM scheduler is in-process, fast, deterministic — this is where the TriggerKey/PauseJob/misfire mechanics are proven. This is the bulk of the phase's validation.
- **Real-stack E2E (`scripts/phase-NN-close.ps1`, RabbitMQ + Redis + live consoles):** prove the bus path end-to-end — a Keeper fault intake against a poisoned L2 publishes `PauseWorkflow`, the orchestrator pauses, recovery publishes `ResumeWorkflow`, the orchestrator resumes. Model on `KeeperRecoveryE2ETests.cs` / `FaultRecoverySpikeE2ETests.cs` `[VERIFIED: tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs, tests/BaseApi.Tests/Orchestrator/FaultRecoverySpikeE2ETests.cs exist]`. **Close-gate caveats from MEMORY:** rebuild orchestrator/keeper containers before the live gate (embedded SourceHash); the close gate's 3×GREEN may surface stale prior-phase flaky tests (treat as prior-phase, not this phase).

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Orchestrator/Scheduling/PauseResumeSchedulingTests.cs` — covers PAUSE-02/03/05 (b,c,e) against a RAM scheduler
- [ ] `tests/BaseApi.Tests/Orchestrator/PauseResumeConsumerTests.cs` — covers PAUSE-04 idempotency (d) + consumer ACK semantics
- [ ] `tests/BaseApi.Tests/Keeper/KeeperPausePublishTests.cs` — covers PAUSE-01 Keeper publish sites (a)
- [ ] `tests/BaseApi.Tests/Messaging/PauseResumeContractTests.cs` — covers PAUSE-01 contract shape
- [ ] (optional) extend `KeeperRecoveryE2ETests` / a new orchestrator E2E for the live bus round-trip
- [ ] Framework install: none — xUnit + NSubstitute + real Quartz RAMJobStore already in `tests/BaseApi.Tests`

## Security Domain

`security_enforcement` not found as `false` in config — including per protocol. This phase adds two control-message contracts and consumers on the internal bus; no new external input surface, no auth/session/crypto.

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | Internal bus; no user auth surface added |
| V3 Session Management | no | — |
| V4 Access Control | no | Bus-internal control messages only |
| V5 Input Validation | partial | `WorkflowId` (Guid) + `H` (string) are bus-internal, producer-controlled (Keeper). `H` is logging-only (D-02). Follow the existing structured-logging discipline: log `H`/ids as STRUCTURED PARAMS, never string-interpolated (the Keeper fault consumers already enforce this `[VERIFIED: FaultEntryStepDispatchConsumer.cs:42-44 — ids/H are template holes]`) |
| V6 Cryptography | no | — |

### Known Threat Patterns
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Log injection via `H`/`WorkflowId` in new consumer logs | Tampering/Repudiation | Structured params under fixed template holes (existing repo idiom, T-35-04/05) |
| Lost Resume → workflow stranded paused (availability) | Denial of Service | D-07: NO drop-if-held stripe; un-acked → redelivered → reprocessed; `ConcurrentMessageLimit=1` serial. The deferred restart-loses-pause is the accepted inverse (fails OPEN to Running, not stuck Paused) |

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Trigger with auto-generated random identity | Deterministic `TriggerKey(jobId)` stamped via `.WithIdentity` | This phase | Enables `GetTriggerState`-based three-state model |
| Pause via `UnscheduleOnlyAsync`/`DeleteJob` (PAUSE-02 original) | Pause via Quartz `PauseJob` (D-08) | This phase | Quartz distinguishes Paused from Stopped; no orchestrator state field |
| Resume gated on sibling outcomes (PAUSE-05 original) | Resume on any success (D-09) | This phase | No reference-counting set needed |

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | An in-flight fire that reschedules during Pause lands a paused trigger (overwriting the deterministic key), so no orphan and cron stays halted | §2 | LOW — at worst one paused trigger exists; cron still halted. Verify in Validation (b). |
| A2 | `GetTriggerState(unknownKey)` returns `TriggerState.None` without throwing | §4 | LOW — docs list `None` as a return value; if it throws, resume's `None`-branch needs a try/catch. Verify in Validation (e). |
| A3 | A separate `"orchestrator-pauseresume"` fan-out endpoint is preferable to colocating on `"orchestrator"` | §5 | LOW — pure topology choice within Claude's-discretion; either works. Planner decides. |

## Open Questions

1. **Per-endpoint retry ownership if Pause/Resume colocate on `"orchestrator"`** — `UseMessageRetry` is per-endpoint; Start/Stop already own it. Resolved by §5 recommendation (b): give Pause/Resume their own shared endpoint. Planner should confirm the endpoint-naming choice and ensure only one definition registers retry per endpoint.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | build/test | ✓ | 8.0.421 | — |
| Quartz / Quartz.Extensions.Hosting | scheduler | ✓ | 3.18.1 | — |
| RabbitMQ + Redis + live consoles | real-stack close-gate E2E only | ✓ (docker compose, per repo convention) | — | hermetic unit tests cover all mechanics |

Hermetic unit validation has **no external dependency** (in-process RAMJobStore + NSubstitute). Only the optional live E2E needs the docker stack.

## Sources

### Primary (HIGH confidence)
- Repo source (VERIFIED): `WorkflowScheduler.cs`, `WorkflowFireJob.cs`, `WorkflowLifecycle.cs`, `WorkflowL1.cs`, `StartOrchestration.cs`, `IExecutionCorrelated.cs`, `EntryStepDispatch.cs`, `Start/StopOrchestrationConsumer(.Definition).cs`, `FaultEntryStepDispatchConsumer.cs`, `FaultExecutionResultConsumer.cs`, `L2ProbeRecovery.cs`, `Orchestrator/Program.cs`, `Keeper/Program.cs`, `OrchestrationService.cs`, `SchedulingTests.cs`, `StopConsumerLifecycleTests.cs`, `Directory.Packages.props`
- [Quartz IScheduler.cs source](https://github.com/quartznet/quartznet/blob/main/src/Quartz/IScheduler.cs) — PauseJob ("pauses all of its current ITriggers"), GetTriggerState returns None for non-existent, TriggerState enum values

### Secondary (MEDIUM confidence)
- [Quartz issue #193 — paused triggers reported as blocked](https://github.com/quartznet/quartznet/issues/193) — `Paused_Blocked` edge; informs the exact-`Paused` guard in §4
- [Quartz tutorial: Jobs and Triggers](https://www.quartz-scheduler.net/documentation/quartz-3.x/tutorial/jobs-and-triggers.html)

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new packages; versions VERIFIED in CPM
- Architecture / Quartz mechanics: HIGH — TriggerKey gap, PauseJob, resume-misfire, GetTriggerState all grounded in source + official docs
- Pitfalls: HIGH — the RescheduleAsync self-reschedule keying gap and per-endpoint retry ownership are the two real traps, both VERIFIED

**Research date:** 2026-06-06
**Valid until:** 2026-07-06 (stable — Quartz 3.18.1 pinned, repo patterns established)

## RESEARCH COMPLETE
