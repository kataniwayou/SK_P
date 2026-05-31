# Phase 24: Orchestrator Result-Consume & Step Advancement — Specification

**Created:** 2026-05-31
**Ambiguity score:** 0.12 (gate: ≤ 0.20)
**Requirements:** 5 locked

## Goal

The orchestrator advances each workflow's DAG from real processor results: it consumes an `ExecutionResult` on a load-balanced shared queue, looks up the completed step in the in-memory L1 dictionary, and `Send`s a job-trigger-shaped continuation to every next step whose entry condition the result's outcome satisfies — performing L1 reads only and no L2 mutation.

## Background

Phase 23 built the orchestrator lifecycle: startup hydrates each workflow's root + per-step state into an in-memory L1 `ConcurrentDictionary`; a per-workflow Quartz job fires entry steps on cron, `Send`ing an `EntryStepDispatch` to `queue:{processorId}`; Stop tears the job + L1 down. The orchestrator never mutates L2.

Today the orchestrator **only fires entry steps on a cron and never advances past them** — there is no consumer for processor results, so the DAG cannot progress. The reader `StepProjection` (`Messaging.Contracts.Projections`) already carries `EntryCondition` (int), `ProcessorId`, `Payload`, and `NextStepIds` (`List<Guid>`), and these are hydrated into L1 — but `NextStepIds` is unused at runtime. The writer-side `StepEntryCondition` enum is `PreviousProcessing=0, PreviousCompleted=1, PreviousFailed=2, PreviousCancelled=3, Always=4, Never=5`.

No result contract exists: there is no `ExecutionResult` message and no result-outcome enum. `IExecutionCorrelated` (CorrelationId, ExecutionId, WorkflowId, StepId, ProcessorId, EntryId) exists but has only the orchestrator→processor implementer (`EntryStepDispatch`, with `ExecutionId`/`EntryId` = `Guid.Empty` on initial fire). The orchestrator's only consume endpoints are the instance-unique fan-out Start/Stop control queues — there is no load-balanced shared results queue for processors to send to. This phase pulls the processor→orchestrator round-trip (`FUT-SEND-02`, result half of `FUT-CONTRACTS-01`) forward into v3.4.0.

## Requirements

1. **ExecutionResult + StepOutcome contracts**: The result vocabulary exists in `Messaging.Contracts`.
   - Current: No result message or outcome enum exists; `IExecutionCorrelated` has no return-path implementer.
   - Target: A `StepOutcome` enum (`Processing=0, Completed=1, Failed=2, Cancelled=3`) and an `ExecutionResult` record implementing `IExecutionCorrelated` (CorrelationId, ExecutionId, WorkflowId, StepId, ProcessorId, EntryId) carrying `StepOutcome Outcome`, nullable `ErrorMessage` (set on Failed), and nullable `CancellationMessage` (set on Cancelled). NO processor-output payload field.
   - Acceptance: `StepOutcome` int values equal the `StepEntryCondition.Previous*` subset (0–3); `ExecutionResult` implements `IExecutionCorrelated`; a serialize→deserialize round-trip test preserves all fields; no output/payload field is present on the record.

2. **Load-balanced result consume**: The orchestrator receives results on a shared competing-consumer endpoint.
   - Current: The orchestrator binds only instance-unique fan-out endpoints (Start/Stop control); processors have nowhere to send results.
   - Target: An `IConsumer<ExecutionResult>` bound to a single **shared named queue** (competing-consumer semantics across replicas; 1 active today), distinct from the instance-unique fan-out control endpoints.
   - Acceptance: an `ExecutionResult` sent to the shared queue is consumed exactly once across the consumer set (competing-consumer, NOT fan-out broadcast); the endpoint is a stable shared queue name, not an instance-unique queue.

3. **Edge traversal & entry-condition match (L1-only)**: A result selects the next steps to dispatch from L1.
   - Current: No runtime code reads `NextStepIds` to advance; the hydrated `NextStepIds` is unused.
   - Target: On each result the orchestrator reads the completed step's L1 projection by `(workflowId, stepId)`, enumerates its `NextStepIds`, reads each next step's L1 projection, and selects a next step iff `nextStep.EntryCondition == (int)outcome` OR `nextStep.EntryCondition == Always` (`Never` is never selected). The result path performs NO L2/Redis read.
   - Acceptance: for a workflow with known next-step entry conditions, a `Completed` result selects only `PreviousCompleted`(+`Always`) successors and a `Processing` result selects only `PreviousProcessing`(+`Always`); a `Never` successor is never selected; no Redis/L2 read occurs on the result path (verified by mux `DidNotReceive` / structural absence of a Redis dependency on the path).

4. **Continuation dispatch**: Each selected next step gets a job-trigger-shaped dispatch that continues the same execution.
   - Current: Continuation dispatch does not exist; `EntryStepDispatch` is emitted only by the Quartz fire job for entry steps.
   - Target: For each selected next step the orchestrator `Send`s an `EntryStepDispatch` (same message structure) to `queue:{nextStep.ProcessorId}`, copying `CorrelationId`, `EntryId`, `ExecutionId`, and `WorkflowId` from the result, and taking `StepId`, `ProcessorId`, `Payload` from the next step's L1 projection.
   - Acceptance: a captured dispatch on `queue:{nextStep.ProcessorId}` has `CorrelationId`/`EntryId`/`ExecutionId`/`WorkflowId` equal to the result's and `StepId`/`ProcessorId`/`Payload` equal to the next step's L1 projection values; exactly one dispatch is sent per selected next step.

5. **Result-consume ack semantics**: Business outcomes ack cleanly; only infra faults retry.
   - Current: `ORCH-ACK-01` governs Start/Stop + hydration; no ack policy exists for a result consumer.
   - Target: A result for an unknown workflow/step, a terminal step with no matching next step, or a corrupt/absent L1 projection is a clean ack (`return`, no throw); only genuine infra faults propagate to the bounded retry → `_error`. A duplicate/redelivered result MAY produce duplicate downstream dispatch (accepted; dedup deferred).
   - Acceptance: tests assert no throw and no `_error` for the unknown-workflow, no-matching-next-step, and corrupt-projection cases; an injected infra fault on the result path propagates; a redelivered result is permitted to re-dispatch (no dedup assertion).

## Boundaries

**In scope:**
- `StepOutcome` enum + `ExecutionResult` record in `Messaging.Contracts`.
- A single shared load-balanced result-consume endpoint (`IConsumer<ExecutionResult>`).
- L1-only edge traversal (`NextStepIds` lookup) and entry-condition matching.
- Continuation dispatch reusing the `EntryStepDispatch` structure, sent to `queue:{nextStep.ProcessorId}`.
- Ack semantics mirroring `ORCH-ACK-01` (business-ack / infra-throw).
- Tests proving the match table, id-copy correctness, and the ack cases.

**Out of scope:**
- The processor implementation that emits results — separate concern (Processor milestone); this phase only consumes.
- `IRequestClient`/responder self-id-by-SourceHash (`FUT-REQRESP-01`) — unrelated round-trip, still deferred.
- Multiple results per step / streaming progress sequences — locked to one-result-per-step this phase (decision A).
- Duplicate-delivery dedup / idempotency tracking — deferred (decision C), consistent with `ORCH-SCALE-01`.
- Any L2/Redis read or write on the result path — L1-only; the orchestrator remains non-mutating against L2.
- Processor-output payload forwarding — continuation payload comes from the next-step L1 projection, so the result carries no output field (decision B).
- Cross-replica duplicate-dispatch coordination — deferred (`ORCH-SCALE-01`).
- Cron/scheduling/fire behavior — unchanged from Phase 23.

## Constraints

- **L1-only on the result path** — no Redis/L2 access; the orchestrator performs no L2 mutation.
- **Reuse `EntryStepDispatch`** for the continuation message (no new dispatch contract).
- `StepOutcome` int values MUST equal the `StepEntryCondition.Previous*` subset (0–3) so matching is a direct equality, not a translation table.
- Single active replica assumed at runtime; the competing-consumer topology supports N but cross-replica dedup is deferred.
- Assume exactly one `ExecutionResult` per step execution (one-result-per-step).
- Follow the existing MSG-ACK / ORCH-ACK business-ack vs infra-throw split.

## Acceptance Criteria

- [ ] `StepOutcome` enum exists with `Processing=0, Completed=1, Failed=2, Cancelled=3` (values match `StepEntryCondition.Previous*`).
- [ ] `ExecutionResult` implements `IExecutionCorrelated`, carries `Outcome` + nullable `ErrorMessage` + nullable `CancellationMessage`, and has NO output-payload field.
- [ ] The orchestrator consumes `ExecutionResult` on a shared load-balanced queue (consumed once across the consumer set, not fan-out).
- [ ] Result-path code reads next-step data from L1 only — no Redis/L2 read on the result path.
- [ ] Next-step selection matches: outcome → `Previous{outcome}` or `Always`; `Never` is never selected.
- [ ] Continuation dispatch to `queue:{nextStep.ProcessorId}` copies `correlationId`/`entryId`/`executionId`/`workflowId` from the result and `stepId`/`processorId`/`payload` from the next-step L1 projection.
- [ ] Unknown-workflow, no-matching-next-step, and corrupt-projection results are clean acks (no throw, no `_error`).
- [ ] An infra fault on the result path propagates to bounded retry.
- [ ] The orchestrator performs no L2 mutation on the result path.

## Ambiguity Report

| Dimension          | Score | Min  | Status | Notes                                                        |
|--------------------|-------|------|--------|--------------------------------------------------------------|
| Goal Clarity       | 0.92  | 0.75 | ✓      | Consume → L1 lookup → match → dispatch; precise & measurable  |
| Boundary Clarity   | 0.88  | 0.70 | ✓      | Explicit out-of-scope: dedup, multi-result, L2, output payload |
| Constraint Clarity | 0.85  | 0.65 | ✓      | L1-only, reuse EntryStepDispatch, StepOutcome 0–3, 1-result/step |
| Acceptance Criteria| 0.85  | 0.70 | ✓      | 9 pass/fail criteria                                          |
| **Ambiguity**      | 0.12  | ≤0.20| ✓      |                                                              |

Status: ✓ = met minimum, ⚠ = below minimum (planner treats as assumption)

## Interview Log

| Round | Perspective     | Question summary                                              | Decision locked                                                                 |
|-------|-----------------|--------------------------------------------------------------|---------------------------------------------------------------------------------|
| 0     | Researcher (prior phase-add discussion) | Read L1 or L2 on the result path?                | L1-only (no L2 read on the result path)                                          |
| 0     | Researcher      | New continuation message or reuse the job trigger?           | Reuse `EntryStepDispatch` structure                                              |
| 0     | Researcher      | New outcome enum or reuse `StepEntryCondition`?              | New `StepOutcome` enum, int values matching `Previous*` (0–3)                     |
| 0     | Researcher      | Which ids carry forward into the continuation?               | Copy `correlationId, entryId, executionId, workflowId` from result; `stepId/processorId/payload` from next-step projection |
| 1     | Boundary Keeper | How does outcome match entry condition?                      | Position-for-position: `outcome == Previous{outcome}` or `Always`; `Never` never |
| 1     | Failure Analyst | Can a step emit multiple results (progress then terminal)?   | No — exactly one result per step execution                                       |
| 1     | Boundary Keeper | `ExecutionResult` field shape + output payload?              | Two nullable messages (Error/Cancellation); no output payload                    |
| 1     | Failure Analyst | At-least-once redelivery → duplicate dispatch?               | Accept duplicates; dedup deferred (out of scope)                                 |

---

*Phase: 24-orchestrator-result-consume-step-advancement*
*Spec created: 2026-05-31*
*Next step: /gsd-discuss-phase 24 — implementation decisions (shared queue name, consumer/lifecycle wiring, test harness shape)*
