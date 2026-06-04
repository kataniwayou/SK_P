# Phase 32: Cancelled Circuit-Breaker — Context

**Gathered:** 2026-06-04
**Status:** Ready for planning
**Supersedes:** the prior 32-CONTEXT stub. The signalling mechanism changed during discussion — the processor no longer sends a point-to-point `Cancelled` message to the orchestrator; budget exhaustion now fans out via MassTransit `Fault<EntryStepDispatch>`. The Phase-31 deferred Failure-policy design record (`31-CONTEXT.md` §"Failure-policy hook") and ROADMAP Phase-32 success criteria are partially overridden by the decisions below (see D-03/D-04/D-06).

<domain>
## Phase Boundary

On retry-budget exhaustion, a workflow is cleanly and completely stopped — the **current in-flight fire halted** and **future cron fires unscheduled** — instead of silently dead-lettering to `_error`. Stop is two-level: a shared L2 `cancelled[workflowId]` marker (in-flight) plus a fanned-out fault that unschedules the Quartz job (future fires). The design is **multi-replica-correct** even though only one orchestrator replica runs today.

This phase clarifies HOW to implement that stop. New capabilities belong in other phases.
</domain>

<spec_lock>
## Requirements (locked via SPEC.md)

**8 requirements are locked.** See `32-SPEC.md` for full requirements, boundaries, and acceptance criteria.

Downstream agents MUST read `32-SPEC.md` before planning or implementing. Requirements are not duplicated here — the `<decisions>` section below captures only the HOW (D-01…D-13).

**Note on ordering:** discuss-phase ran *before* spec-phase on this phase, so this CONTEXT.md was the source 32-SPEC.md was derived from. The two are consistent: each SPEC requirement traces to a D-* decision (req-1→D-01, req-2→D-02/D-05/D-07, req-3→D-05, req-4→D-03/D-06, req-5→D-04/D-03, req-6→D-12, req-7→D-10/D-11, req-8→D-08/D-13).

**In scope (from SPEC.md):**
- Breaker trigger on `GetRetryAttempt() == RetryOptions.Limit` (infra-fault exhaustion only)
- `L2ProjectionKeys.Cancelled(workflowId)` marker builder + effect-first marker-set on the final attempt
- Check-and-drop gate at both `EntryStepDispatchConsumer` and `ResultConsumer`
- `IConsumer<Fault<EntryStepDispatch>>` per-replica fan-out endpoint that unschedules the Quartz job via existing Stop/Teardown machinery
- Removal of `StepEntryCondition.PreviousCancelled (3)` (numeric gap; keep `StepOutcome.Cancelled (3)`)
- `processor_dispatch_deduped_total` + `orchestrator_result_deduped_total` + `workflow_cancelled_total` counters and a structured trip log
- Manual resume via existing `POST /orchestration/start` (clear marker + re-start)

**Out of scope (from SPEC.md):**
- Persistent business-failure circuit-breaking (B2) — would rework the D-15 catch-and-`Failed` contract; deferred
- A dedicated `POST /orchestration/resume` endpoint or auto-cooldown (TTL) resume — rejected in favour of manual clear + re-start (D-08)
- Renumbering `StepEntryCondition` 0/1/2/4/5 — would reinterpret existing step data
- Routing a `Cancelled` `ExecutionResult` over `orchestrator-result` for the halt — wrong channel (D-04)
- Putting the Fault path through the `flag[H]` CAS dedup — naturally idempotent (D-13)
- Changing the existing token-cancellation `Cancelled` business outcome (EXEC-08)
- Multi-replica deployment itself — design is multi-replica-correct, but one replica runs today

</spec_lock>

<decisions>
## Implementation Decisions

### Trigger — what trips the breaker
- **D-01 (B1):** The breaker trips on **infra-fault retry-budget exhaustion only** — `GetRetryAttempt() == configured Limit`, where `Limit` is the Phase-31 `RetryOptions.Limit` (single source of truth, already bound from the `"Retry"` config section and fed to `UseMessageRetry`). Business failures (a `ProcessAsync` throw) remain **immediate-`Failed`-and-acked** per the established D-15 contract — they do NOT retry and do NOT trip the breaker. Rationale: matches ROADMAP criterion #1 as written (`GetRetryAttempt()` only counts infra retries today); minimal, surgical change. Trip on **first exhaustion**; blast radius = **whole workflow** (carried forward, locked).

### Signal mechanism — Fault fanout, not a Cancelled send
- **D-02:** On the final attempt the processor sets `cancelled[workflowId] = true` in L2, then lets the infra fault propagate. **Effect-first: marker-set precedes fault publication.**
- **D-03 (A1, mod 2):** The future-fire signal is **MassTransit's automatic `Fault<EntryStepDispatch>`**, published (fanout) when `UseMessageRetry` exhausts. No custom event, no explicit publish. The SAME exhaustion also dead-letters the message to `_error` — so the **bus-down `_error` backstop falls out of the same mechanism** (retained, no separate path).
- **D-04 (mod 4):** **No `Cancelled` ExecutionResult is sent to `orchestrator-result` for the breaker path.** That queue is a shared competing-consumer queue (one replica consumes), which is the wrong channel for a broadcast halt. The prior design's "processor sends consumed message back as `Cancelled` → orchestrator unschedules on `Cancelled`" is **removed**.

### Two-level stop
- **D-05:** **In-flight stop** = the shared L2 `cancelled[workflowId]` marker. Every receiver (processor `EntryStepDispatchConsumer`, orchestrator `ResultConsumer`) checks it before processing and **ack-and-discards** any in-flight message for a cancelled workflow → the fire drains to a halt (stops advancing, no rollback, no dead-lettering of dropped messages). Multi-replica-safe by construction (shared L2, `workflowId`-keyed so concurrent fires stop too).
- **D-06 (mod 3):** **Future-fire stop** = every orchestrator replica runs `IConsumer<Fault<EntryStepDispatch>>` on a **per-replica temporary fan-out endpoint** (mirrors the existing Start/Stop `InstanceId` + `Temporary` wiring in `Orchestrator/Program.cs`, NOT the shared `orchestrator-result` endpoint). Each replica extracts `WorkflowId` from `Fault.Message`, resolves `jobId` from **L1** (`store.TryGet(workflowId) → wf.JobId`; in-memory, no L2 read; absent-from-L1 ⇒ no-op), and unschedules the Quartz job — **reusing the existing Stop/Teardown machinery** (idempotent). Only the schedule-owning replica acts; others no-op.

### Marker lifecycle & resume
- **D-07:** The `cancelled[workflowId]` marker has **no TTL** — it persists until resume explicitly clears it. A self-expiring breaker defeats the purpose. Stop/Teardown does NOT clear it.
- **D-08:** **Resume is manual** — operator clears the marker + re-`POST /orchestration/start` (reuses the existing endpoint; no new API surface). Circuit-breaker semantics want a human to confirm the underlying fault is fixed before re-arming.

### Idempotency & ordering
- **D-09:** Effect-first and idempotent on re-exhaustion: a redelivery (from `_error` reprocessing or a re-fire) re-exhausts → re-sets the marker (idempotent) → re-publishes `Fault` (idempotent halt — unschedule is idempotent, marker already set).
- **D-13:** The `Fault<EntryStepDispatch>` path is **outside** the Phase-31 `flag[H]` "Pending"/"Ack" effect-first dedup. The fault carries no `H`, and the processor seeds **no** `flag[resultH]=Pending` for it (unlike the Completed-result pre-write at `EntryStepDispatchConsumer.cs:199-203`). Duplicate fault deliveries are absorbed purely by the **natural idempotency of the halt** — Quartz unschedule is idempotent (delete / absent-from-L1 no-op) and the marker SET-and-check is idempotent. The `flag[H]` CAS machinery exists to make step **advancement/fan-out** exactly-once; the halt is neither, so it gets no CAS gate. **Downstream agents MUST NOT pattern-match the Completed-result `flag[H]=Pending` pre-write onto the fault path.**

### Observability
- **D-10 (mod 1):** **Redelivery/dedup business counters, both sides.** Increment a dedicated counter at each consumer's effect-first dedup gate (`flag[H] == "Ack" → return`): `processor_dispatch_deduped_total` (`EntryStepDispatchConsumer.cs:76`) and `orchestrator_result_deduped_total` (`ResultConsumer.cs:65`), tagged `ProcessorId`, on the existing Phase-30 `ProcessorMetrics`/`OrchestratorMetrics` meters. Surfaces redelivery-collapse frequency (how often a duplicate is dropped). NOTE: this instruments the Phase-31 dedup gates — adjacent scope, deliberately folded here as part of the failure/redelivery observability story.
- **D-11:** **Breaker-trip counter** `workflow_cancelled_total` incremented when the breaker trips, plus a structured WARN/ERROR log carrying `workflowId` / `stepId` / `processorId` / `H`. Reuses Phase-29 logging + Phase-30 metrics. Both D-10 and D-11 are kept — they answer different questions (redelivery rate vs. trip count).

### Enum change
- **D-12:** **Remove `StepEntryCondition.PreviousCancelled (3)`** — leave `3` as a numeric gap (do NOT renumber 0/1/2/4/5, or existing step data reinterprets). `StepDtoValidator`'s `.IsInEnum()` auto-rejects `3`. Verify no live step has `EntryCondition == 3` (dual-pipeline steps use `Always`/4 — likely clean). **Keep `StepOutcome.Cancelled (3)`** — the processor still reports it for the existing token-cancellation business outcome (`EntryStepDispatchConsumer.cs:123-128`, EXEC-08), which is unchanged by this phase and advances no successor via its empty manifest. Update the "ints mirror `Previous*`" note: `Cancelled` is special-cased, not matched by `SelectNext`.

### Claude's Discretion
- Marker key shape: a new `L2ProjectionKeys.Cancelled(Guid workflowId) => $"{Prefix}cancelled:{workflowId:D}"` builder added to the single-source-of-truth `L2ProjectionKeys` (follows the existing `skp:` flat convention).
- Exact metric names / tag-key literals (follow the Phase-30 pinned-lowercase-label convention, decoupled from C# enum member names).
- Fault-consumer endpoint registration/naming details (per-instance `Temporary` fan-out, mirroring `StartOrchestrationConsumerDefinition`/`StopOrchestrationConsumerDefinition`).
- The per-message marker read adds one Redis `GET` per received message — accepted (the same Redis is already hit for the dedup flag).
</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Locked requirements (SPEC)
- `.planning/phases/32-cancelled-circuit-breaker/32-SPEC.md` — Locked requirements — MUST read before planning. 8 falsifiable requirements + boundaries + pass/fail acceptance criteria (ambiguity 0.11). Each requirement traces to a D-* decision below.

### Design record (the source of this phase, partially overridden by D-03/D-04/D-06)
- `.planning/phases/31-idempotent-execution-exactly-once-effect/31-CONTEXT.md` §"Failure-policy hook (Cancelled = unconditional, two-level stop) — DEFERRED TO PHASE 32" — the original two-level-stop design; note the Cancelled-message → Fault-fanout change made in this discussion.
- `.planning/phases/31-idempotent-execution-exactly-once-effect/31-SPEC.md` — Phase-31 requirements this builds on: deterministic `H` (Cancelled/Fault correlation), configurable retry `Limit`.
- `.planning/ROADMAP.md` §"Phase 32: Cancelled Circuit-Breaker" (success criteria — criteria 1 & 3 reworded by D-03/D-04/D-06).

### Processor side
- `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` — the consumer to extend: dedup gate (line 76 → D-10 counter), infra-fault propagation path (lines 68-70, the breaker trigger D-01/D-02), existing token-`Cancelled` outcome (lines 123-128, kept per D-12).
- `src/BaseProcessor.Core/Observability/` (`ProcessorMetrics`) — Phase-30 meter to extend (D-10).

### Orchestrator side
- `src/Orchestrator/Consumers/ResultConsumer.cs` — dedup gate (line 65 → D-10 counter); the `Cancelled`-ExecutionResult path is NOT used by the breaker (D-04).
- `src/Orchestrator/Program.cs` (lines 31-46) — the per-replica `InstanceId`+`Temporary` fan-out endpoint pattern (Start/Stop) the new `Fault<EntryStepDispatch>` consumer mirrors (D-06).
- `src/Orchestrator/Consumers/ResultConsumerDefinition.cs` — `UseMessageRetry(Immediate(Limit))` wiring + the `RetryOptions.Limit` single source (D-01).
- `src/Orchestrator/Consumers/StopOrchestrationConsumer.cs` + `src/Orchestrator/Hydration/WorkflowLifecycle.cs` + `src/Orchestrator/Scheduling/WorkflowScheduler.cs` — the Stop/Teardown unschedule machinery reused by the fault halt (D-06).
- `src/Orchestrator/Observability/OrchestratorMetrics.cs` — Phase-30 meter to extend (D-10/D-11).

### Shared contracts
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — single source of truth for L2 keys; add the `Cancelled(workflowId)` marker builder here.
- `src/Messaging.Contracts/OrchestratorQueues.cs` — `orchestrator-result` is the shared competing-consumer queue (why D-04 holds).
- `src/Messaging.Contracts/Configuration/` (`RetryOptions`) — the `Limit` (D-01).
- `src/BaseApi.Service/Features/Orchestration/OrchestrationController.cs` — `POST /orchestration/start` reused for resume (D-08).
</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **Per-replica fan-out endpoint pattern** (`Program.cs:31-46`): Start/Stop already bind `.Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; })` → `orchestrator-{instanceId}` broadcast. The `Fault<EntryStepDispatch>` consumer drops straight into this pattern (D-06). The composition-root comment already anticipates "the per-replica fan-out endpoint."
- **Stop/Teardown machinery** is idempotent and already fanned out — the fault halt reuses it rather than re-implementing unschedule (D-06).
- **`RetryOptions.Limit`** is already the single source feeding `UseMessageRetry` — the breaker's `GetRetryAttempt() == Limit` check reads the same value, no desync (D-01).
- **Effect-first dedup gates** already exist at both consumers — D-10 only adds a counter at the existing `return` points.

### Established Patterns
- L2 keys flow through `L2ProjectionKeys` (flat `skp:` prefix, no type discriminator). New marker key follows suit.
- Metrics use pinned lowercase Prometheus label literals decoupled from enum names (`OutcomeLabel` pattern); `ProcessorId` tag only, NO `workflowId` (cardinality, T-30-04).
- `_error` is reached only via infra-fault exhaustion of `UseMessageRetry` — the same exhaustion that now also publishes `Fault<T>`.

### Integration Points / Open Implementation Question (for researcher/planner)
- **Final-attempt marker-set mechanism:** infra faults currently propagate uncaught (no `try/catch` at the Redis/Send sites). To set the marker "on the final attempt" before the fault publishes, the consumer must detect `GetRetryAttempt() == Limit` and set the marker before the throw (e.g. a catch-on-final-attempt around the infra op, or a consume filter). The researcher should determine the cleanest MassTransit-idiomatic placement (consume filter vs. in-consumer guard) — this is the main implementation unknown.
- **Marker-write-fault edge:** if the marker `SET` itself faults (Redis/bus down) the in-flight marker isn't set; this lands in the same bus/redis-down `_error` backstop bucket the design already accepts.
- **Fault consumer type binding:** `Fault<EntryStepDispatch>` — `Fault.Message` is the original `EntryStepDispatch`, which carries `WorkflowId` (enough to resolve `jobId` from L1 and halt). Confirm at plan time.
</code_context>

<specifics>
## Specific Ideas

- Use MassTransit's **built-in** `Fault<T>` rather than a hand-rolled cancel event — the user specifically wants the fanout + `_error` backstop to come from one mechanism.
- The dedup counter is explicitly "how many times the consumer checked the flag and it was `Ack`" — i.e. instrument the existing drop, do not add a new gate.
</specifics>

<deferred>
## Deferred Ideas

- **Persistent business-failure circuit-breaking (B2):** making `ProcessAsync` exceptions retry-to-exhaustion and trip the breaker (instead of immediate-`Failed`). Rejected for this phase — would rework the D-15 catch-and-`Failed` contract. Revisit if business-level circuit-breaking is wanted.
- **Dedicated `POST /orchestration/resume` endpoint / auto-cooldown resume:** rejected in favour of manual clear-marker + re-Start (D-08). Note for a future ops-ergonomics phase if manual resume proves painful.

</deferred>

---

*Phase: 32-cancelled-circuit-breaker*
*Context gathered: 2026-06-04*
