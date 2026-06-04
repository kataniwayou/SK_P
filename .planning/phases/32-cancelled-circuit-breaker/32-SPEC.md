# Phase 32: Cancelled Circuit-Breaker — Specification

**Created:** 2026-06-04
**Ambiguity score:** 0.11 (gate: ≤ 0.20)
**Requirements:** 8 locked

## Goal

On infra-fault retry-budget exhaustion (`GetRetryAttempt() == RetryOptions.Limit`), a workflow is cleanly and completely stopped — the current in-flight fire drains to a halt and future cron fires are unscheduled — via a two-level stop (an L2 `cancelled[workflowId]` marker plus a fanned-out `Fault<EntryStepDispatch>` that unschedules the Quartz job), instead of silently dead-lettering to `_error`.

## Background

The processor (`src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs`) and orchestrator (`src/Orchestrator/Consumers/ResultConsumer.cs`) each carry a Phase-31 effect-first `flag[H]` dedup gate. `RetryOptions.Limit` (Phase-31, bound from the `"Retry"` config section) feeds `UseMessageRetry(Immediate(Limit))` at four sites. `src/Orchestrator/Program.cs:31-46` already wires per-replica `InstanceId`+`Temporary` fan-out endpoints for Start/Stop, and `StopOrchestrationConsumer` + `WorkflowLifecycle` + `WorkflowScheduler` provide idempotent Quartz unschedule machinery.

Today, when `UseMessageRetry` exhausts, the message silently dead-letters to `_error` — there is **no clean stop**: the current fire keeps advancing and future cron fires keep firing. `StepEntryCondition.PreviousCancelled = 3` (`src/BaseApi.Service/Features/Step/StepEntryCondition.cs:19`) is a dead enum member never set by live data. `StepOutcome.Cancelled = 3` (`src/Messaging.Contracts/StepOutcome.cs:20`) is live — the processor still reports it for the existing token-cancellation business outcome (`EntryStepDispatchConsumer.cs:126,289`), which advances no successor and is unchanged by this phase.

This phase was discussed via `/gsd-discuss-phase`; the locked design is in `32-CONTEXT.md` (decisions D-01…D-13). The signalling mechanism changed during that discussion: the stop fans out via MassTransit's automatic `Fault<EntryStepDispatch>` (D-03), **not** a point-to-point `Cancelled` send — so ROADMAP Phase-32 success criteria #1/#3 wording is superseded by D-03/D-04/D-06 and this SPEC reflects the Fault-fanout design.

What does NOT exist yet: the `cancelled[workflowId]` marker + `L2ProjectionKeys.Cancelled` builder; the check-and-drop gate at both consumers; the `IConsumer<Fault<EntryStepDispatch>>` fan-out endpoint; the redelivery-dedup + breaker-trip counters; and the removal of `PreviousCancelled (3)`.

## Requirements

1. **Breaker trigger (infra-exhaustion only)**: The breaker trips only on infra-fault retry-budget exhaustion.
   - Current: On `UseMessageRetry(Immediate(Limit))` exhaustion the message silently dead-letters to `_error`; no breaker exists. Business `ProcessAsync` throws are already caught and reported immediate-`Failed` (D-15) and do not retry.
   - Target: The breaker trips when `GetRetryAttempt() == RetryOptions.Limit` (the single source already feeding `UseMessageRetry`); business `ProcessAsync` failures remain immediate-`Failed`-and-acked and do NOT trip the breaker. Trip on first exhaustion; blast radius = whole workflow.
   - Acceptance: A dispatch whose infra op fails through `Limit` attempts trips the breaker (marker set + fault fanout); a `ProcessAsync` throw produces a `Failed` `ExecutionResult` with no marker set and no fault-driven unschedule.

2. **In-flight marker (effect-first, no TTL)**: On the final attempt the processor sets `cancelled[workflowId] = true` in L2 before the infra fault propagates.
   - Current: No `cancelled[workflowId]` marker and no `L2ProjectionKeys.Cancelled` builder exist.
   - Target: A new `L2ProjectionKeys.Cancelled(Guid workflowId)` builder (flat `skp:` convention) keys a marker the processor sets on the final attempt (marker-set precedes fault publication). The marker has no TTL; Stop/Teardown does not clear it.
   - Acceptance: After exhaustion, `GET skp:cancelled:{workflowId:D}` (or the builder's key) returns the set marker; the key has no expiry (`TTL` = -1); the marker write happens before the fault is observed.

3. **Check-and-drop gate (drains to a halt)**: Every receiver checks the marker before processing and ack-and-discards in-flight messages for a cancelled workflow.
   - Current: Neither `EntryStepDispatchConsumer` nor `ResultConsumer` reads a cancelled marker; in-flight messages always process.
   - Target: Both consumers check `cancelled[workflowId]` at consume time and, when set, ack-and-discard the message (no further advancement, no rollback, no dead-lettering of the dropped message). `workflowId`-keyed so concurrent fires of the same workflow also stop.
   - Acceptance: With the marker set, an in-flight `EntryStepDispatch` and an in-flight `ExecutionResult` for that workflow are each acked and produce no downstream dispatch/advancement; messages for other (un-cancelled) workflows are unaffected.

4. **Fault fanout → Quartz unschedule**: Every orchestrator replica consumes `Fault<EntryStepDispatch>` on a per-replica fan-out endpoint and unschedules the job.
   - Current: No `IConsumer<Fault<EntryStepDispatch>>` exists; nothing unschedules the Quartz job on exhaustion.
   - Target: A `Fault<EntryStepDispatch>` consumer on a per-replica `InstanceId`+`Temporary` endpoint (mirroring the Start/Stop pattern in `Program.cs:31-46`, NOT the shared `orchestrator-result` endpoint) extracts `WorkflowId` from `Fault.Message`, resolves `jobId` from L1 (`store.TryGet(workflowId) → wf.JobId`; absent-from-L1 ⇒ no-op), and unschedules via the existing idempotent Stop/Teardown machinery. Only the schedule-owning replica acts; others no-op.
   - Acceptance: On a published `Fault<EntryStepDispatch>`, the owning replica's Quartz job for that `WorkflowId` is unscheduled; a workflow absent from L1 yields a no-op (no throw); a duplicate fault delivery yields an idempotent no-op (job already gone).

5. **No `Cancelled` ExecutionResult on the breaker path**: The shared result queue is not used to broadcast the halt; the `_error` backstop is retained.
   - Current: The older design routed a `Cancelled` `ExecutionResult` to `orchestrator-result` to drive the unschedule.
   - Target: The breaker path publishes NO `Cancelled` `ExecutionResult` to `orchestrator-result` (a shared competing-consumer queue reaches only one replica — wrong channel for a broadcast halt). The same `UseMessageRetry` exhaustion that publishes the `Fault` also dead-letters to `_error`, so the bus-down `_error` backstop falls out of one mechanism. The existing token-cancellation `Cancelled` `ExecutionResult` (EXEC-08) is unchanged.
   - Acceptance: On breaker trip, no `Cancelled` `ExecutionResult` is sent to `orchestrator-result`; `_error` still receives the dead-lettered message; the pre-existing token-cancellation `Cancelled` outcome path still sends its `ExecutionResult`.

6. **Enum cleanup (remove `PreviousCancelled (3)`)**: The dead entry-condition member is removed as a numeric gap.
   - Current: `StepEntryCondition.PreviousCancelled = 3` exists but is never set by live data; `StepOutcome.Cancelled = 3` is live for token-cancellation.
   - Target: `PreviousCancelled = 3` is removed and `3` left as a numeric gap (0/1/2/4/5 NOT renumbered, or existing step data reinterprets). `StepDtoValidator.IsInEnum()` then auto-rejects `EntryCondition == 3`. `StepOutcome.Cancelled (3)` is kept; its mirror comment is updated (`Cancelled` is special-cased by the consumer, not matched by `SelectNext`).
   - Acceptance: `StepEntryCondition` has no `PreviousCancelled` member and no member numbered `3`; a `StepDto` with `EntryCondition == 3` fails validation; no live step row has `EntryCondition == 3`; `StepOutcome.Cancelled = 3` remains.

7. **Redelivery + breaker-trip observability**: Dedicated counters and a structured trip log are emitted.
   - Current: The effect-first dedup gates drop duplicates silently; no breaker-trip metric or log exists.
   - Target: A counter increments at each consumer's `flag[H] == "Ack" → return` gate — `processor_dispatch_deduped_total` (`EntryStepDispatchConsumer.cs:76`) and `orchestrator_result_deduped_total` (`ResultConsumer.cs:65`), tagged `ProcessorId`, on the existing Phase-30 meters. A `workflow_cancelled_total` counter increments on breaker trip, plus a structured WARN/ERROR log carrying `workflowId`/`stepId`/`processorId`/`H`. Metric/label literals follow the Phase-30 pinned-lowercase convention; no `workflowId` label on counters (cardinality).
   - Acceptance: A redelivered (already-`Ack`) message increments the matching `*_deduped_total` once at the drop point; a breaker trip increments `workflow_cancelled_total` once and emits a WARN/ERROR log carrying the four fields; no counter carries a `workflowId` label.

8. **Manual resume; Fault path outside `flag[H]` dedup**: Resume is operator-driven and the halt is idempotent without a CAS gate.
   - Current: No resume path; the `flag[H]` Pending/Ack CAS gates exist for step advancement.
   - Target: Resume = operator clears the `cancelled[workflowId]` marker and re-`POST /orchestration/start` (existing endpoint; no new API surface). The `Fault<EntryStepDispatch>` path carries no `H`, seeds no `flag[resultH]=Pending`, and passes through no CAS dedup gate — duplicate fault deliveries are absorbed purely by the natural idempotency of the halt (Quartz unschedule idempotent; marker SET/check idempotent).
   - Acceptance: After clearing the marker and re-POSTing `/orchestration/start`, the workflow fires again (in-flight messages no longer dropped); the fault consumer reads/writes no `flag[H]` key; two identical fault deliveries leave the same end state as one (job unscheduled, marker set).

## Boundaries

**In scope:**
- Breaker trigger on `GetRetryAttempt() == RetryOptions.Limit` (infra-fault exhaustion only)
- `L2ProjectionKeys.Cancelled(workflowId)` marker builder + effect-first marker-set on the final attempt
- Check-and-drop gate at both `EntryStepDispatchConsumer` and `ResultConsumer`
- `IConsumer<Fault<EntryStepDispatch>>` per-replica fan-out endpoint that unschedules the Quartz job via existing Stop/Teardown machinery
- Removal of `StepEntryCondition.PreviousCancelled (3)` (numeric gap; keep `StepOutcome.Cancelled (3)`)
- `processor_dispatch_deduped_total` + `orchestrator_result_deduped_total` + `workflow_cancelled_total` counters and a structured trip log
- Manual resume via existing `POST /orchestration/start` (clear marker + re-start)

**Out of scope:**
- Persistent business-failure circuit-breaking (B2 — making `ProcessAsync` exceptions retry-to-exhaustion and trip the breaker) — would rework the D-15 catch-and-`Failed` contract; deferred
- A dedicated `POST /orchestration/resume` endpoint or auto-cooldown (TTL) resume — rejected in favour of manual clear + re-start (D-08); risks flapping
- Renumbering `StepEntryCondition` 0/1/2/4/5 — would reinterpret existing step data
- Routing a `Cancelled` `ExecutionResult` over `orchestrator-result` for the halt — wrong channel (shared competing-consumer queue, D-04)
- Putting the Fault path through the `flag[H]` CAS dedup — the halt is naturally idempotent, no CAS gate (D-13)
- Changing the existing token-cancellation `Cancelled` business outcome (EXEC-08) — unchanged by this phase
- Multi-replica deployment itself — design is multi-replica-correct, but only one orchestrator replica runs today

## Constraints

- **Multi-replica-correct by construction**: the in-flight stop uses a shared L2 `workflowId`-keyed marker; the future-fire stop fans out to every replica via `Fault<EntryStepDispatch>` on per-replica temporary endpoints (only the schedule-owning replica acts). Must remain correct even though one replica runs today.
- **Effect-first ordering**: marker-set precedes fault publication; the marker write happens before the infra fault propagates.
- **Marker has no TTL**: a self-expiring breaker defeats the purpose; Stop/Teardown must not clear it.
- **Reuse, do not re-implement**: the fault halt reuses the existing idempotent Stop/Teardown unschedule machinery; the breaker reads the same `RetryOptions.Limit` that feeds `UseMessageRetry` (no desync).
- **Fault path is outside the `flag[H]` dedup** (D-13): the fault carries no `H`, seeds no `flag=Pending`, and gets no CAS gate; downstream agents MUST NOT pattern-match the Completed-result `flag[H]=Pending` pre-write onto the fault path.
- **Metric cardinality**: counters carry `ProcessorId` (+ ambient `service_instance_id`), never `workflowId`; label literals are pinned lowercase, decoupled from C# enum member names (Phase-30 convention).
- **Per-message cost**: the check-and-drop gate adds one Redis `GET` per received message — accepted (the same Redis is already hit for the `flag[H]` dedup).
- **Open implementation question (for plan/research, not a spec decision)**: the cleanest MassTransit-idiomatic placement of the final-attempt marker-set (consume filter vs. in-consumer guard around the infra op) — infra faults currently propagate uncaught.

## Acceptance Criteria

- [ ] Breaker trips on `GetRetryAttempt() == RetryOptions.Limit`; a `ProcessAsync` throw stays immediate-`Failed` and trips nothing
- [ ] `cancelled[workflowId]` marker is set (via the new `L2ProjectionKeys.Cancelled` builder) before the fault propagates, with no TTL, and Stop/Teardown does not clear it
- [ ] Both consumers ack-and-discard in-flight messages for a cancelled workflow (no advancement, no rollback); other workflows unaffected
- [ ] A `Fault<EntryStepDispatch>` consumer on a per-replica fan-out endpoint resolves `jobId` from L1 and unschedules the Quartz job; absent-from-L1 and duplicate deliveries are idempotent no-ops
- [ ] No `Cancelled` `ExecutionResult` is sent to `orchestrator-result` on the breaker path; `_error` still receives the dead-lettered message; the token-cancellation `Cancelled` outcome is unchanged
- [ ] `StepEntryCondition.PreviousCancelled` is removed (no member numbered `3`); `StepDto` with `EntryCondition == 3` fails `IsInEnum` validation; no live step has `EntryCondition == 3`; `StepOutcome.Cancelled = 3` kept
- [ ] `processor_dispatch_deduped_total`, `orchestrator_result_deduped_total`, and `workflow_cancelled_total` increment at their gates/trip; the trip emits a WARN/ERROR log with `workflowId`/`stepId`/`processorId`/`H`; no counter carries a `workflowId` label
- [ ] Resume works via clear-marker + re-`POST /orchestration/start` (no new API surface); the Fault path reads/writes no `flag[H]` key

## Ambiguity Report

| Dimension          | Score | Min  | Status | Notes                                                        |
|--------------------|-------|------|--------|--------------------------------------------------------------|
| Goal Clarity       | 0.92  | 0.75 | ✓      | Two-level stop on infra-exhaustion; precisely scoped         |
| Boundary Clarity   | 0.93  | 0.70 | ✓      | Explicit in/out; B2 + `/resume` deferred; enum-gap scope     |
| Constraint Clarity | 0.85  | 0.65 | ✓      | Multi-replica-correct, no-TTL marker, Fault outside flag[H]  |
| Acceptance Criteria| 0.85  | 0.70 | ✓      | 8 pass/fail criteria; D-12 grep checks                       |
| **Ambiguity**      | 0.11  | ≤0.20| ✓      | Gate passes on first assessment (discuss-phase pre-resolved) |

Status: ✓ = met minimum, ⚠ = below minimum (planner treats as assumption)

## Interview Log

This phase reached the ambiguity gate on the first assessment — `/gsd-discuss-phase` had already run and produced `32-CONTEXT.md` with 13 locked decisions (D-01…D-13). The interview rounds below were resolved in that prior discussion (see `32-DISCUSSION-LOG.md`); no further Socratic rounds were needed.

| Round | Perspective     | Question summary                          | Decision locked                                              |
|-------|-----------------|-------------------------------------------|-------------------------------------------------------------|
| 1     | Researcher      | What exists today; what trips the breaker?| Infra-fault exhaustion only (`==Limit`); business stays Failed (D-01) |
| 2     | Simplifier      | Simplest signalling mechanism?            | MassTransit automatic `Fault<EntryStepDispatch>` fanout; `_error` backstop falls out of it (D-03) |
| 3     | Boundary Keeper | What's NOT this phase?                     | B2 business-breaking + `/resume` endpoint deferred; no `Cancelled` send on shared queue (D-04) |
| 3     | Boundary Keeper | What does the two-level stop deliver?     | L2 marker drains in-flight; Fault unschedules future fires (D-05/D-06) |
| 4     | Failure Analyst | What breaks on duplicate/edge?            | Halt is naturally idempotent → Fault path outside `flag[H]` CAS (D-13); marker-write-fault → `_error` bucket |
| 5     | Seed Closer     | Resume + marker lifecycle?                | Manual clear-marker + re-Start; marker has no TTL (D-07/D-08) |

---

*Phase: 32-cancelled-circuit-breaker*
*Spec created: 2026-06-04*
*Next step: /gsd-discuss-phase 32 — implementation decisions (CONTEXT.md already exists; discuss-phase will reconcile SPEC against it)*
