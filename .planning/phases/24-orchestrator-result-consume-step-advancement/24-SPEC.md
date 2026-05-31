# Phase 24: Orchestrator Result-Consume & Step Advancement — Specification

**Created:** 2026-05-31
**Updated:** 2026-05-31 (gating redesign — WebApi first-win suppression + conditionless orchestrator consumers + drain-keep L1)
**Ambiguity score:** 0.12 (gate: ≤ 0.20)
**Requirements:** 9 locked

## Goal

The orchestrator advances each workflow's DAG from real processor results: it consumes an `ExecutionResult` on a load-balanced shared queue, looks up the completed step in the in-memory L1 dictionary, and `Send`s a job-trigger-shaped continuation to every next step whose entry condition the result's outcome satisfies — performing L1 reads only and no L2 mutation. The lifecycle's gating is redesigned so duplicate start/stop are suppressed first-win at the **WebApi** (L2 root create/delete), the orchestrator's start/stop consumers become **conditionless**, the orchestrator **never drops** messages received before startup hydration completes, and **stop keeps L1** so late processor results can still drain.

## Background

Phase 23 built the orchestrator lifecycle: startup hydrates each workflow's root + per-step state into an in-memory L1 `ConcurrentDictionary`; a per-workflow Quartz job fires entry steps on cron, `Send`ing an `EntryStepDispatch` to `queue:{processorId}`; Stop deletes the job and **cleared L1**; the Start/Stop consumers **gate-dropped** (clean-acked) messages while the startup gate was closed and held a per-workflow stripe.

Today the orchestrator **only fires entry steps on a cron and never advances past them** — there is no consumer for processor results. The reader `StepProjection` (`Messaging.Contracts.Projections`) already carries `EntryCondition` (int), `ProcessorId`, `Payload`, and `NextStepIds` (`List<Guid>`), hydrated into L1 via `IWorkflowL1Store.TryGet` → `WorkflowL1.Steps`; `NextStepIds` is unused at runtime. `StepEntryCondition` (in `BaseApi.Service`, NOT referenced by the orchestrator) is `PreviousProcessing=0, PreviousCompleted=1, PreviousFailed=2, PreviousCancelled=3, Always=4, Never=5`. No `ExecutionResult` message or result-outcome enum exists; `IExecutionCorrelated` has only the orchestrator→processor implementer (`EntryStepDispatch`, with `ExecutionId`/`EntryId` = `Guid.Empty` on initial fire). The orchestrator's only consume endpoints are instance-unique fan-out Start/Stop control queues — there is no load-balanced shared results queue.

This phase pulls the processor→orchestrator round-trip (`FUT-SEND-02`, result half of `FUT-CONTRACTS-01`) forward into v3.4.0 AND redesigns the lifecycle gating discovered during discussion. Because a processor result is a one-time event (dropping it silently stalls the DAG) and processors are unaware of the orchestrator's startup timing, the previous "gate-drop" semantics are unsafe for results; the redesign moves duplicate-suppression to the WebApi and makes the orchestrator consumers conditionless and loss-free.

## Requirements

1. **ExecutionResult + StepOutcome contracts**: The result vocabulary exists in `Messaging.Contracts`.
   - Current: No result message or outcome enum exists; `IExecutionCorrelated` has no return-path implementer.
   - Target: A `StepOutcome` enum (`Processing=0, Completed=1, Failed=2, Cancelled=3`) and an `ExecutionResult` record implementing `IExecutionCorrelated` (CorrelationId, ExecutionId, WorkflowId, StepId, ProcessorId, EntryId) carrying `StepOutcome Outcome`, nullable `ErrorMessage` (Failed), and nullable `CancellationMessage` (Cancelled). NO processor-output payload field.
   - Acceptance: `StepOutcome` int values equal the `StepEntryCondition.Previous*` subset (0–3); `ExecutionResult` implements `IExecutionCorrelated`; a serialize→deserialize round-trip preserves all fields; no output/payload field present.

2. **Load-balanced result consume**: The orchestrator receives results on a shared competing-consumer endpoint.
   - Current: The orchestrator binds only instance-unique fan-out endpoints; processors have nowhere to send results.
   - Target: An `IConsumer<ExecutionResult>` bound to a single shared named queue (competing-consumer across replicas; 1 active today), distinct from the instance-unique fan-out control endpoints.
   - Acceptance: an `ExecutionResult` sent to the shared queue is consumed exactly once across the consumer set (competing-consumer, NOT fan-out broadcast); the endpoint is a stable shared queue name.

3. **Edge traversal & entry-condition match (L1-only)**: A result selects the next steps to dispatch from L1.
   - Current: No runtime code reads `NextStepIds` to advance; the hydrated `NextStepIds` is unused.
   - Target: On each result the orchestrator reads the completed step's L1 projection by `(workflowId, stepId)`, enumerates its `NextStepIds`, reads each next step's L1 projection, and selects a next step iff `nextStep.EntryCondition == (int)outcome` OR `nextStep.EntryCondition == Always(4)` (`Never(5)` never selected). NO L2/Redis read on the result path.
   - Acceptance: a `Completed` result selects only `PreviousCompleted`(+`Always`) successors and a `Processing` result selects only `PreviousProcessing`(+`Always`); a `Never` successor is never selected; no Redis/L2 read occurs on the result path.

4. **Continuation dispatch**: Each selected next step gets a job-trigger-shaped dispatch that continues the same execution.
   - Current: Continuation dispatch does not exist; `EntryStepDispatch` is emitted only by the Quartz fire job for entry steps.
   - Target: For each selected next step the orchestrator `Send`s an `EntryStepDispatch` (same structure) to `queue:{nextStep.ProcessorId}`, copying `CorrelationId`, `EntryId`, `ExecutionId`, and `WorkflowId` from the result and taking `StepId`, `ProcessorId`, `Payload` from the next step's L1 projection.
   - Acceptance: a captured dispatch on `queue:{nextStep.ProcessorId}` has `CorrelationId`/`EntryId`/`ExecutionId`/`WorkflowId` equal to the result's and `StepId`/`ProcessorId`/`Payload` equal to the next step's L1 projection values; exactly one dispatch per selected next step.

5. **Result-consume ack semantics**: Business outcomes ack cleanly; only infra faults retry.
   - Current: `ORCH-ACK-01` governs Start/Stop + hydration; no ack policy exists for a result consumer.
   - Target: With the gate open, a result whose `(workflowId, stepId)` is not in L1, a step with no matching next step, or a corrupt L1 projection is a clean ack (`return`, no throw). Only genuine infra faults propagate to bounded retry → `_error`. A duplicate/redelivered result MAY produce duplicate downstream dispatch (accepted; dedup deferred).
   - Acceptance: tests assert no throw and no `_error` for the unknown-`(workflow,step)`, no-matching-next-step, and corrupt-projection cases; an injected infra fault propagates; a redelivered result is permitted to re-dispatch (no dedup assertion).

6. **Gate-closed never-drop (redeliver)**: Messages received before hydration completes are not lost. *(Supersedes Phase 23 D-12 ack-drop.)*
   - Current: Phase 23 gate-drops (clean-acks) Start/Stop while `!gate.IsReady`; there is no result consumer.
   - Target: While `!gate.IsReady`, Start, Stop, AND `ExecutionResult` messages are NOT acked-dropped — the consumer throws/nacks to trigger **delayed redelivery**, and the message is reprocessed once the gate opens (`MarkReady`). Processors are unaware of orchestrator startup timing, so a result arriving during hydration must survive it.
   - Acceptance: a message delivered while the gate is closed is redelivered (not acked away) and is processed after `MarkReady`; the gate-closed branch performs no ack.

7. **Conditionless start (reload)**: The orchestrator start consumer carries no idempotency logic. *(Supersedes Phase 23 ORCH-CONSUME-01 behavior.)*
   - Current: Phase 23 start does gated teardown + hydrate + reschedule with a stripe and a stripe-held skip.
   - Target: With the gate open, a Start unconditionally hydrates L2→L1 and (re)schedules the workflow's Quartz job — no L1-existence check, no skip (the WebApi suppresses duplicates per `WEBAPI-SUPPRESS-01`). A Start for a workflow whose entry lingers in L1 (e.g. after a prior stop) re-hydrates and reschedules, reviving it.
   - Acceptance: a Start for a workflow already present in L1 still re-hydrates and reschedules (no existence-based skip path); after a stop→start sequence the workflow has a live job again.

8. **Conditionless stop (drain-keep L1)**: Stop deletes jobs but retains L1 for draining. *(Supersedes Phase 23 ORCH-STOP-01 behavior.)*
   - Current: Phase 23 stop resolves `jobId`, `DeleteJob`, and **clears** the L1 entry.
   - Target: With the gate open, a Stop unconditionally deletes the workflow's Quartz job(s) but **keeps** the L1 entry, so late `ExecutionResult` messages for the stopped workflow still resolve in L1 and continue advancing the DAG (graceful drain). No L1 clear this phase.
   - Acceptance: after a Stop, the Quartz job is gone but the workflow's L1 entry remains; a late result for the stopped workflow still resolves in L1 and dispatches its matching next steps.

9. **WebApi first-win suppression**: Duplicate start/stop are deduped at the L2 root by the WebApi.
   - Current: The WebApi publishes start/stop and writes/deletes the L2 root + parent index (Phase 22); duplicate-suppression semantics to be confirmed against current code.
   - Target: Start creates the L2 root-parent `workflowId` only if absent (else skip — no overwrite/republish); Stop deletes it only if present (else skip). First write wins; duplicate start/stop become no-ops, so the orchestrator only ever sees genuine deduped transitions.
   - Acceptance: a second Start for an existing `workflowId` does not overwrite the root or re-emit to the orchestrator; a second Stop for an absent `workflowId` is a no-op.

## Boundaries

**In scope:**
- `StepOutcome` enum + `ExecutionResult` record in `Messaging.Contracts`.
- A single shared load-balanced result-consume endpoint (`IConsumer<ExecutionResult>`).
- L1-only edge traversal (`NextStepIds` lookup) and entry-condition matching.
- Continuation dispatch reusing the `EntryStepDispatch` structure, sent to `queue:{nextStep.ProcessorId}`.
- Result ack semantics (gate-open business-ack / infra-throw).
- Gate-closed never-drop / delayed-redelivery for Start, Stop, and result consumers.
- Conditionless orchestrator Start (hydrate+reschedule) and Stop (delete jobs, keep L1).
- WebApi first-win idempotent create/delete of the L2 root-parent on Start/Stop.

**Out of scope:**
- The processor implementation that emits results — separate concern (Processor milestone); this phase only consumes.
- `IRequestClient`/responder self-id-by-SourceHash (`FUT-REQRESP-01`) — unrelated round-trip, still deferred.
- Multiple results per step / streaming progress sequences — locked to one-result-per-step this phase.
- Duplicate-delivery dedup / idempotency tracking — deferred, consistent with `ORCH-SCALE-01`.
- **L1 eviction of stopped workflows** — deferred (`FUTURE-STOP-EVICTION`). The target solution: a per-`workflowId` mechanism that verifies no processor messages for that workflow remain in flight on the bus, after which it is safe to clear that workflow's L1 entry. Until then, stopped workflows linger in L1 (accepted; bounded population).
- Any L2/Redis read or write on the **result** path — L1-only; the orchestrator remains non-mutating against L2.
- Processor-output payload forwarding — continuation payload comes from the next-step L1 projection; no output field on the result.
- Cross-replica duplicate-dispatch coordination — deferred (`ORCH-SCALE-01`).
- Cron/scheduling/fire behavior — unchanged from Phase 23.

## Constraints

- **L1-only on the result path** — no Redis/L2 access; the orchestrator performs no L2 mutation.
- **Reuse `EntryStepDispatch`** for the continuation message (no new dispatch contract).
- `StepOutcome` int values MUST equal the `StepEntryCondition.Previous*` subset (0–3) so matching is a direct equality (the orchestrator does not reference the `BaseApi.Service` enum; `Always=4`/`Never=5` are named int constants on the orchestrator side).
- **The WebApi is the single duplicate-suppression point**; orchestrator Start/Stop consumers are conditionless.
- **Gate-closed never drops** — requires a delayed-redelivery policy on the affected endpoints, sized to outlast hydration (a true Redis-down outage eventually routes to `_error`).
- **Stopped workflows linger in L1 this phase** (eviction deferred); memory is bounded by the workflow population.
- Single active replica assumed at runtime; competing-consumer topology supports N but cross-replica dedup is deferred.
- Assume exactly one `ExecutionResult` per step execution (one-result-per-step).
- Follow the existing MSG-ACK / ORCH-ACK business-ack vs infra-throw split (extended by the gate-closed redeliver rule).

## Acceptance Criteria

- [ ] `StepOutcome` enum exists with `Processing=0, Completed=1, Failed=2, Cancelled=3` (values match `StepEntryCondition.Previous*`).
- [ ] `ExecutionResult` implements `IExecutionCorrelated`, carries `Outcome` + nullable `ErrorMessage` + nullable `CancellationMessage`, and has NO output-payload field.
- [ ] The orchestrator consumes `ExecutionResult` on a shared load-balanced queue (consumed once across the consumer set, not fan-out).
- [ ] Result-path code reads next-step data from L1 only — no Redis/L2 read on the result path.
- [ ] Next-step selection matches: outcome → `Previous{outcome}` or `Always`; `Never` is never selected.
- [ ] Continuation dispatch to `queue:{nextStep.ProcessorId}` copies `correlationId`/`entryId`/`executionId`/`workflowId` from the result and `stepId`/`processorId`/`payload` from the next-step L1 projection.
- [ ] Gate-open: unknown-`(workflow,step)`, no-matching-next-step, and corrupt-projection results are clean acks (no throw, no `_error`); an infra fault propagates.
- [ ] Gate-closed: Start, Stop, and result messages are redelivered (not acked away) and processed after `MarkReady` — no message loss during hydration.
- [ ] Orchestrator Start is conditionless: a Start for a workflow already in L1 re-hydrates + reschedules (no existence skip).
- [ ] Orchestrator Stop is conditionless: deletes the Quartz job but keeps the L1 entry; a late result for the stopped workflow still dispatches.
- [ ] WebApi: a second Start for an existing `workflowId` does not overwrite/republish; a second Stop for an absent `workflowId` is a no-op.
- [ ] The orchestrator performs no L2 mutation on the result path.

## Ambiguity Report

| Dimension          | Score | Min  | Status | Notes                                                                 |
|--------------------|-------|------|--------|-----------------------------------------------------------------------|
| Goal Clarity       | 0.92  | 0.75 | ✓      | Consume → L1 lookup → match → dispatch + redesigned gating; precise    |
| Boundary Clarity   | 0.90  | 0.70 | ✓      | Explicit out-of-scope incl. L1 eviction mechanism + its target shape   |
| Constraint Clarity | 0.88  | 0.65 | ✓      | L1-only, conditionless consumers, WebApi-suppression, keep-L1-on-stop  |
| Acceptance Criteria| 0.85  | 0.70 | ✓      | 12 pass/fail criteria                                                  |
| **Ambiguity**      | 0.11  | ≤0.20| ✓      |                                                                       |

Status: ✓ = met minimum, ⚠ = below minimum (planner treats as assumption)

## Interview Log

| Round | Perspective     | Question summary                                              | Decision locked                                                                 |
|-------|-----------------|--------------------------------------------------------------|---------------------------------------------------------------------------------|
| 0     | Researcher      | Read L1 or L2 on the result path?                            | L1-only (no L2 read on the result path)                                          |
| 0     | Researcher      | New continuation message or reuse the job trigger?           | Reuse `EntryStepDispatch` structure                                              |
| 0     | Researcher      | New outcome enum or reuse `StepEntryCondition`?              | New `StepOutcome` enum, int values matching `Previous*` (0–3)                     |
| 0     | Researcher      | Which ids carry forward into the continuation?               | Copy `correlationId, entryId, executionId, workflowId`; `stepId/processorId/payload` from next-step projection |
| 1     | Boundary Keeper | How does outcome match entry condition?                      | Position-for-position: `outcome == Previous{outcome}` or `Always`; `Never` never |
| 1     | Failure Analyst | Can a step emit multiple results?                            | No — one result per step execution                                               |
| 1     | Boundary Keeper | `ExecutionResult` field shape + output payload?              | Two nullable messages (Error/Cancellation); no output payload                    |
| 2     | Failure Analyst | Gate-closed handling for a one-time result (loss risk)?      | Redesign: never drop — redeliver until gate opens                                |
| 2     | Boundary Keeper | Where does duplicate start/stop suppression live?            | WebApi first-win (create/delete L2 root); orchestrator consumers conditionless   |
| 2     | Failure Analyst | Does stop clear L1 (loses drain) or keep it?                 | Keep L1 — late results drain; safe-clear (no in-flight bus messages) deferred     |

---

*Phase: 24-orchestrator-result-consume-step-advancement*
*Spec created: 2026-05-31 · Updated 2026-05-31 (gating redesign)*
*Next step: /gsd-plan-phase 24 — CONTEXT.md captures the implementation decisions*
