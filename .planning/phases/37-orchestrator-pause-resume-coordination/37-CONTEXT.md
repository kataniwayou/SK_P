# Phase 37: Orchestrator Pause/Resume Coordination - Context

**Gathered:** 2026-06-06
**Status:** Ready for planning

<domain>
## Phase Boundary

Add the `PauseWorkflow` / `ResumeWorkflow` message contracts and the orchestrator-side
consumers that **halt and reschedule a workflow's cron** in response to Keeper's L2-outage
recovery. Keeper publishes these signals around its existing Phase-36 recovery loop; the
orchestrator (single-replica) acts on them. **Re-injection itself already lives in Keeper**
(Phase 36, INTAKE-04) — this phase is *only* the cron-scheduling half. The only phase in this
milestone that touches the `Orchestrator` project.

Clarified HOW (mechanism), not WHAT — PAUSE-01..05 were already locked in REQUIREMENTS.md.
**Two of those requirements were revised during this discussion** (see Decisions D-08, D-09);
the planner must apply the revisions, not the original wording.

</domain>

<decisions>
## Implementation Decisions

### Contracts (PAUSE-01)
- **D-01:** New `PauseWorkflow` and `ResumeWorkflow` sealed records in `Messaging.Contracts`,
  shaped exactly like `StartOrchestration` (implement `ICorrelated`, body-carried `CorrelationId`).
  Each carries `WorkflowId` + `H`. The inner fault messages already expose both via
  `IExecutionCorrelated` (`WorkflowId`, `H`), so Keeper reads them straight off `context.Message.Message`.
- **D-02:** `H` is carried for **correlation / observability only** — NOT for counting or
  reference-tracking (see D-07). PAUSE-04's "do not double-count" holds trivially because there is
  no count to keep.

### Keeper publish points (PAUSE-01)
- **D-03:** In *both* Keeper consumers (`FaultEntryStepDispatchConsumer`,
  `FaultExecutionResultConsumer`): publish `PauseWorkflow(workflowId)` **at intake, before** the
  `L2ProbeRecovery.RunAsync` loop starts (the whole point is to stop the outage spreading while we probe).
- **D-04:** On `ProbeOutcome.Recovered` — after the existing re-inject `Send` — publish
  `ResumeWorkflow(workflowId)`. On `ProbeOutcome.GaveUp` — park the original `Fault<T>` to
  `keeper-dlq` (existing Phase-36 behavior) and publish **nothing** (see D-09).

### Orchestrator state model — Quartz is the source of truth (PAUSE-02, PAUSE-03)
- **D-05:** A workflow's scheduling state is **derived from Quartz**, via
  `IScheduler.GetTriggerState(triggerKey)` — **no separate L1 state field, no in-memory marker**:
  - `Normal`  → **Running**  (live scheduled trigger)
  - `Paused`  → **Paused**   (paused via `PauseJob`)
  - `None`    → **Stopped**  (job/trigger absent via `DeleteJob`)
- **D-06:** Mechanism per state transition:
  - **Stop** = `DeleteJob` → `None`  *(unchanged — Phase 37 does NOT modify the Stop/Start consumers)*
  - **Pause** = `PauseJob` → `Paused`  *(L1 preserved; this is the **revision** of PAUSE-02's named
    mechanism — see D-08)*
  - **Resume** = read `GetTriggerState`; **only if `Paused`**, delete the stale paused trigger and
    `ScheduleAsync` a **fresh** trigger recomputed from L1's `cron` (recompute-from-now). If `None`
    (operator-Stopped) or `Normal` (already Running) → **ignore**. Recompute-from-now sidesteps
    Quartz misfire policy on the stale one-shot trigger.
  - **Start** = schedule → `Normal`  *(unchanged)*

### Concurrency & crash-safety (PAUSE-04)
- **D-07:** **No dedicated lock and no reference-counting set.** The pause/resume consumer runs at
  **`ConcurrentMessageLimit = 1`** (serial), every handler is **idempotent** (re-applying a Quartz
  state transition is a no-op), and a crash before ack leaves the message **un-acked → redelivered →
  reprocessed** to the same state. This deliberately does **NOT** reuse the existing
  `IWorkflowL1Store` drop-if-held (`Wait(0)`) stripe — that stripe *drops* a contended caller, which
  would silently lose a Resume and strand a workflow paused forever.
- The Stop-vs-Resume race is resolved by the Quartz check itself (D-06): Resume acts only on
  `GetTriggerState == Paused`, and Quartz's own job-store concurrency serializes a racing
  `DeleteJob` (Stop) against the Resume.

### Requirement revisions (apply these, not the original REQUIREMENTS.md wording)
- **D-08 (revises PAUSE-02):** Pause halts cron fires via **`PauseJob`** (Quartz-native), **not**
  `UnscheduleOnlyAsync`/`DeleteJob`. Rationale: with both Stop and Pause deleting the job, Quartz
  could not distinguish Stopped from Paused; `PauseJob` makes Quartz the single source of truth for
  the three-state model (D-05) and removes the need for any orchestrator-side state field. L1 is
  still preserved (PauseJob keeps the job + trigger).
- **D-09 (revises PAUSE-05):** The orchestrator **resumes a workflow on any successful recovery**,
  regardless of sibling messages' outcomes. A given-up message is parked to `keeper-dlq` and handled
  by an operator **out-of-band — it does NOT re-pin a workflow back to paused.** A workflow remains
  paused only when **none** of its recoveries ever succeed (no Resume is ever published).
  Rationale: a successful probe proves L2 is healthy, so resuming is safe; runs are independent, so
  one parked message is an isolated DLQ/operator concern, not grounds to freeze the schedule. This is
  why the reference-counting set (D-07) is unnecessary.

### Claude's Discretion
- The deterministic per-workflow `TriggerKey` derivation needed for `GetTriggerState`
  (the scheduler currently addresses jobs by `JobKey(jobId)` — see research flag below).
- Exact placement of the two new orchestrator consumers (endpoint topology mirroring the existing
  Start/Stop fan-out) and the new contracts' file layout.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Requirements (with revisions)
- `.planning/REQUIREMENTS.md` §"Workflow Pause/Resume Coordination (PAUSE)" — PAUSE-01..05.
  **Apply D-08 (PAUSE-02 → `PauseJob`) and D-09 (PAUSE-05 → resume-on-success) over the original text.**
- `.planning/ROADMAP.md` §"Phase 37" — goal + success criteria (criteria 2/5 reflect the
  pre-revision wording; reconcile against D-08/D-09).

### Orchestrator scheduling (where the work lands)
- `src/Orchestrator/Scheduling/WorkflowScheduler.cs` — `ScheduleAsync` / `RescheduleAsync` /
  `UnscheduleAsync` (`DeleteJob`); add `PauseJob` + `GetTriggerState` here, plus the `TriggerKey` scheme.
- `src/Orchestrator/Scheduling/WorkflowFireJob.cs` — the one-shot **self-rescheduling** fire job
  (`RescheduleAsync` on fire); the misfire/self-reschedule interaction to verify (research flag).
- `src/Orchestrator/Hydration/WorkflowLifecycle.cs` — `UnscheduleOnlyAsync` (current Stop/Pause seam,
  being superseded for pause by `PauseJob`), `HydrateAndScheduleAsync` (resume rebuilds from L1).
- `src/Orchestrator/L1/WorkflowL1.cs` — the L1 entry carrying `JobId` + `Cron` (resume's source).
- `src/Orchestrator/L1/WorkflowL1Store.cs` + `IWorkflowL1Store.cs` — the drop-if-held (`Wait(0)`)
  stripe that pause/resume must **NOT** reuse (D-07).

### Orchestrator consumers (patterns to mirror)
- `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs`,
  `src/Orchestrator/Consumers/StopOrchestrationConsumer.cs` — control-message consumer shape;
  these stay **unchanged** (D-06).

### Keeper (publish sites)
- `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs`,
  `src/Keeper/Consumers/FaultExecutionResultConsumer.cs` — add `PauseWorkflow` at intake +
  `ResumeWorkflow` on `Recovered`.
- `src/Keeper/Recovery/L2ProbeRecovery.cs` — `ProbeOutcome { Recovered, GaveUp }`, the publish trigger.

### Contracts
- `src/Messaging.Contracts/StartOrchestration.cs` — the record shape to mirror for the two new contracts.
- `src/Messaging.Contracts/IExecutionCorrelated.cs`, `src/Messaging.Contracts/EntryStepDispatch.cs` —
  source of `WorkflowId` + `H` on the inner fault messages.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `WorkflowScheduler.ScheduleAsync(workflowId, jobId, cron, ct)` — resume's reschedule primitive (reuse).
- `WorkflowScheduler.UnscheduleAsync` (`DeleteJob`) — Stop's existing primitive; `PauseJob` is the new sibling.
- `WorkflowL1` record (`EntryStepIds`, `Cron`, `JobId`, `Steps`, `Liveness`) — already carries
  everything resume needs (`JobId` + `Cron`); **no new field required** thanks to D-05.
- `StartOrchestration` record — exact template for the two new `ICorrelated` contracts.
- Keeper's two fault consumers + `L2ProbeRecovery` — the publish sites already have `workflowId`/`H`
  in scope (`context.Message.Message`).

### Established Patterns
- One-shot **self-rescheduling** Quartz jobs (each fire computes the next occurrence off Cronos and
  re-adds a fresh trigger). This is *why* resume recomputes-from-now rather than resuming a stale trigger.
- Orchestrator is **single-replica** by design — the whole pause-state model relies on it; in-memory
  is acceptable, but here we lean on Quartz state instead of in-memory anyway (D-05).
- Business-ack / infra-throw split + `ConcurrentMessageLimit` control on consumers.

### Integration Points
- New orchestrator consumers join the orchestrator's existing control-message fan-out (mirror Start/Stop).
- Keeper publishes onto the bus; orchestrator consumes — no new infrastructure tier.

### Known limitation (accepted)
- **Orchestrator restart loses pause-state.** On restart, `HydrationBackgroundService` re-hydrates
  from L2 and reschedules **every** workflow → a paused workflow resumes firing. Accepted &
  documented (consistent with PAUSE-02's "never in L2" and the single-replica assumption that the
  orchestrator stays up during the L2 outage). No durable pause-state.

</code_context>

<specifics>
## Specific Ideas

- **Three-state model, Quartz-owned:** Running (`Normal`) / Paused (`Paused`) / Stopped (`None`),
  read via `GetTriggerState`. "Resume is legitimate **only if Paused**" — a Stopped workflow ignores
  Keeper resumes until an operator `Start`.
- **Keeper is stateless** w.r.t. coordination: it probes and resumes on its own success regardless of
  sibling recoveries; give-up → `keeper-dlq` → out of scope.

### Research flag for the planner
The orchestrator's one-shot self-rescheduling trigger model needs the exact `PauseJob` interaction
pinned down: (a) a deterministic per-workflow `TriggerKey` for `GetTriggerState` (jobs are currently
`JobKey(jobId)`-addressed), and (b) confirmation that pausing prevents the next self-reschedule and
that resume's delete-stale-trigger + fresh `ScheduleAsync` avoids any misfire. The *decision*
(Quartz is the source of truth) is locked; this is the implementation detail to verify.

</specifics>

<deferred>
## Deferred Ideas

- **Quartz-native `ResumeJob` on the stale trigger** — rejected in favor of delete-stale +
  recompute-from-now `ScheduleAsync`, to avoid one-shot misfire semantics. (Noted in case a future
  recurring-trigger redesign revisits it.)
- **Durable pause-state across orchestrator restart** — out of scope; contradicts PAUSE-02
  ("never L2") and the single-replica assumption. (Related: FUTURE-KEEPER-SWEEP in REQUIREMENTS.md
  already owns operator-resume-after-give-up.)
- **Auto-resume after give-up** — explicitly an operator action (FUTURE-KEEPER-SWEEP).

None of these are in Phase 37 scope.

</deferred>

---

*Phase: 37-orchestrator-pause-resume-coordination*
*Context gathered: 2026-06-06*
