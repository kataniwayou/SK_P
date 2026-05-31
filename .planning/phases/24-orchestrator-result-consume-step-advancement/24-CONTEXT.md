# Phase 24: Orchestrator Result-Consume & Step Advancement - Context

**Gathered:** 2026-05-31
**Status:** Ready for planning

<domain>
## Phase Boundary

Consume processor `ExecutionResult` messages on a load-balanced shared queue, advance each workflow's DAG by matching the result outcome against next-step entry conditions (L1-only reads), and dispatch the job-trigger-shaped continuation to the matching next steps. Includes a lifecycle gating redesign: WebApi first-win duplicate-suppression, conditionless orchestrator Start/Stop consumers, gate-closed never-drop/redeliver, and stop-keeps-L1 (drain).
</domain>

<spec_lock>
## Requirements (locked via SPEC.md)

**9 requirements are locked.** See `24-SPEC.md` for full requirements, boundaries, and acceptance criteria. Downstream agents MUST read `24-SPEC.md` before planning or implementing — requirements are not duplicated here.

**In scope (from SPEC.md):** `StepOutcome` + `ExecutionResult` contracts; shared load-balanced result consumer; L1-only edge traversal + match; continuation dispatch reusing `EntryStepDispatch`; result ack semantics; gate-closed never-drop/redeliver (Start/Stop/result); conditionless Start (hydrate+reschedule) and Stop (delete jobs, keep L1); WebApi first-win idempotent L2-root create/delete.

**Out of scope (from SPEC.md):** processor implementation; `FUT-REQRESP-01` self-id; multiple-results-per-step; duplicate-delivery dedup; **L1 eviction of stopped workflows** (deferred — needs per-workflowId "no in-flight bus messages" detection); L2 access on the result path; processor-output payload forwarding; cross-replica dedup; cron/fire changes.

**Supersedes:** Phase 23 `ORCH-CONSUME-01` (start) and `ORCH-STOP-01` (stop) behavior, and D-12 gate-drop.
</spec_lock>

<decisions>
## Implementation Decisions

### Dispatch reuse
- **D-01:** Extract a shared `IStepDispatcher` (single method: build `EntryStepDispatch` → `GetSendEndpoint(new Uri($"queue:{processorId:D}"))` → `Send`). Both `WorkflowFireJob` (entry-step fire, Phase 23) and the new result consumer use it — one place owns the dispatch shape (DRY). Refactor `WorkflowFireJob` lines ~66–73 to call it.

### Advancement / match logic
- **D-02:** Put the outcome→entry-condition match + `NextStepIds` traversal in a small, unit-testable `StepAdvancement` helper in `Orchestrator` (pure function: given the incoming `StepOutcome` + the completed step's projection + the L1 step map, return the next steps to dispatch). Match is **int equality** on `StepProjection.EntryCondition`: select iff `EntryCondition == (int)outcome || EntryCondition == Always`. Define `Always = 4` / `Never = 5` as named int constants on the orchestrator side (the orchestrator does NOT reference `BaseApi.Service.StepEntryCondition`). `StepOutcome` int values (0–3) are the source of truth for the `Previous*` subset.

### Result queue + endpoint
- **D-03:** Shared result queue named `queue:orchestrator-result` (mirrors the `queue:{processorId}` convention); bound as a shared competing-consumer `ReceiveEndpoint` (NOT instance-unique fan-out). Define the name as a single named constant alongside the contracts/keys so processor and orchestrator agree on one source of truth.

### Gating model (redesign)
- **D-04 (WebApi = single dedup point):** Duplicate-suppression lives in the WebApi as first-win on the L2 root — Start creates the root-parent `workflowId` only if absent (else skip), Stop deletes only if present (else skip). The orchestrator therefore only sees genuine deduped transitions and needs NO idempotency logic.
- **D-05 (conditionless consumers):** Gate-open Orchestrator Start = unconditionally hydrate L2→L1 + (re)schedule (no existence skip, no stripe gating on existence). Gate-open Stop = unconditionally delete the Quartz job(s). Drop the Phase 23 teardown+skip and stripe-held-skip logic.
- **D-06 (gate-closed never-drop):** While `!gate.IsReady`, Start/Stop/result consumers throw/nack to trigger **delayed redelivery** (NOT clean-ack-drop), so messages survive hydration and reprocess after `MarkReady`. Requires a delayed/second-level redelivery policy on these endpoints sized to outlast hydration (the existing fast `Immediate(3)` alone would exhaust before `MarkReady`). A true Redis-down outage eventually routes to `_error`.
- **D-07 (stop keeps L1 — drain):** Stop deletes jobs but KEEPS the L1 entry, so late `ExecutionResult` messages for a stopped workflow still resolve in L1 and dispatch their next steps (graceful drain of in-flight executions). `stop` halts new cron fires, not in-flight drainage.
- **D-08 (no stripe on result path):** The result consumer only READS L1 (lock-free `ConcurrentDictionary` via `IWorkflowL1Store.TryGet`) and never mutates it, so it takes no per-workflow stripe. A `TryGet` miss (gate open) = graceful business-ack.

### Claude's Discretion
- Exact delayed-redelivery numbers (attempt count / spacing) — planner/researcher chooses values that comfortably outlast typical hydration; surface the chosen policy.
- Whether `IStepDispatcher` lives in `Orchestrator/Scheduling` or a new `Orchestrator/Dispatch` folder — planner's call.
- Test-harness shape — reuse Phase 23's in-memory-bus + Redis-mux-stub pattern and `CapturingDispatchConsumer` to assert result→continuation dispatch, the match table, gate-closed redelivery, and the ack cases.
</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Locked requirements
- `.planning/phases/24-orchestrator-result-consume-step-advancement/24-SPEC.md` — 9 locked requirements, boundaries, acceptance criteria. MUST read before planning.

### Contracts (reuse / extend)
- `src/Messaging.Contracts/Projections/StepProjection.cs` — `EntryCondition(int)`, `ProcessorId`, `Payload`, `NextStepIds` — the L1 step value the match/traversal reads.
- `src/Messaging.Contracts/EntryStepDispatch.cs` — the job-trigger message the continuation reuses (copy `correlationId/entryId/executionId/workflowId`).
- `src/Messaging.Contracts/IExecutionCorrelated.cs` — the interface `ExecutionResult` implements.
- `src/BaseApi.Service/Features/Step/StepEntryCondition.cs` — the canonical enum values (`PreviousProcessing=0 … Always=4, Never=5`) that `StepOutcome` must mirror (orchestrator does NOT reference this file — mirror the ints).

### Runtime to extend (Phase 23)
- `src/Orchestrator/Scheduling/WorkflowFireJob.cs` — dispatch logic to extract into `IStepDispatcher` (lines ~66–73).
- `src/Orchestrator/L1/IWorkflowL1Store.cs` + `WorkflowL1Store.cs` — `TryGet` + `WorkflowL1.Steps`/`EntryStepIds`: the read surface (no new store API needed).
- `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs` + `StopOrchestrationConsumer.cs` — to be revised to conditionless + gate-closed-redeliver.
- `src/Orchestrator/Hydration/WorkflowLifecycle.cs` — hydrate/teardown reused by the conditionless start; review the `IsBusiness`/`IsInfra` split for the gate-closed throw.
- `src/Orchestrator/Program.cs` — endpoint wiring (add the shared result `ReceiveEndpoint`; redelivery policy).
- WebApi Start/Stop publish + L2-root write path (Phase 22) — to make first-win idempotent (verify current behavior during research).

### Requirements traceability
- `.planning/REQUIREMENTS.md` — Phase 24 section (9 reqs) + the superseded Phase 23 `ORCH-CONSUME-01`/`ORCH-STOP-01`.
</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `WorkflowFireJob` dispatch block (`new EntryStepDispatch(...) → GetSendEndpoint(queue:{processorId:D}) → Send`) — extract to `IStepDispatcher`, reuse for continuation.
- `IWorkflowL1Store.TryGet(workflowId, out WorkflowL1)` + `WorkflowL1.Steps` (keyed by stepId → `StepProjection`) + `EntryStepIds` — full L1 read surface; no new store method required.
- `IStartupGate` (`IsReady`/`MarkReady`) — gate the consumers.
- Phase 23 consumer pattern (primary-ctor `IConsumer<T>`, business-ack/infra-throw split, bounded retry via consumer definition) — the result consumer mirrors it, plus gate-closed-redeliver.
- Test harness: in-memory MassTransit bus + Redis `IConnectionMultiplexer` mux stub + `CapturingDispatchConsumer` (Phase 23) — reuse for Phase 24 tests.

### Established Patterns
- Orchestrator references ONLY `Messaging.Contracts` — so `StepOutcome` lives there and matching is int-based (cannot use `BaseApi.Service.StepEntryCondition`).
- `Send` (not `Publish`) to `queue:{processorId:D}` is the dispatch convention.
- No L2 writes anywhere in orchestrator lifecycle/consumers (zero `StringSetAsync`/`SetAddAsync`/`KeyDeleteAsync`); the result path is L1-only (no L2 read either).

### Integration Points
- New shared `ReceiveEndpoint` (`queue:orchestrator-result`) in `Program.cs`; delayed-redelivery policy on Start/Stop/result endpoints.
- WebApi Start/Stop handler(s) gain first-win create/delete-if-(not-)exists on the L2 root-parent.
</code_context>

<specifics>
## Specific Ideas

- Outcome↔entry-condition is a strict positional match (`Processing↔PreviousProcessing(0)`, `Completed↔1`, `Failed↔2`, `Cancelled↔3`, `Always(4)` matches any, `Never(5)` never). Match rule: `EntryCondition == (int)outcome || EntryCondition == 4`.
- One `ExecutionResult` per step execution (no progress-then-terminal sequences this phase).
- Continuation copies `correlationId, entryId, executionId, workflowId` from the result; `stepId, processorId, payload` come from the next-step L1 projection.
</specifics>

<deferred>
## Deferred Ideas

- **Per-workflowId safe L1 eviction (`FUTURE-STOP-EVICTION`)** — verify no processor messages for a `workflowId` remain in flight on the bus, then clear its L1 entry. Until built, stopped workflows linger in L1 (accepted; bounded population). **NOT this phase.**
  - **Seed idea (user):** precompute `numOfTerminationSteps` = count of leaf steps (`NextStepIds == null`); store `correlationId`, `numOfTerminationSteps`, `numOfTerminatedSteps` on the L1 root; each fire updates the stored `correlationId`; increment `numOfTerminatedSteps` when a branch reaches a leaf or dies on an unmatched entry condition; after stop, clear L1 when `numOfTerminatedSteps == numOfTerminationSteps`.
  - **Analysis (do NOT ship the static-count form):** a static leaf count ≠ the dynamic number of branch-terminations in a run, so the equality is unsafe — (1) **fan-in/diamonds** (`NextStepIds` is a junction → a leaf reached via 2 paths under-counts → premature clear → dropped result), (2) **pruning** (dead branches make the count land short → leak, or per-edge counting over-counts → premature clear), (3) **concurrent executions** (one per-workflow counter + "last correlationId" conflates overlapping fires).
  - **Robust refinement (recommended future mechanism):** a **per-`correlationId` in-flight (pending-dispatch) reference counter** — on fire set `pending = #entry dispatches`; on each result `pending -= 1` then `pending += #onward dispatches` (pruned/leaf add 0); `pending == 0` ⇒ that execution drained. Track the set of live correlationIds; clear L1 only when the job is stopped AND no live correlationId has `pending > 0`. Correct under fan-out/fan-in/diamonds/pruning and never conflates concurrent runs. (Analyzed 2026-05-31.)
- Duplicate-delivery dedup / idempotency tracking — accepted duplicates this phase (consistent with `ORCH-SCALE-01`).
- Cross-replica duplicate-dispatch coordination — deferred (`ORCH-SCALE-01`).
- `FUT-REQRESP-01` processor self-id request/response — separate, still deferred.
</deferred>

---

*Phase: 24-orchestrator-result-consume-step-advancement*
*Context gathered: 2026-05-31*
