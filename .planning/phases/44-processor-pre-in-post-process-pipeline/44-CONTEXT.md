# Phase 44: Processor Pre/In/Post-Process Pipeline - Context

**Gathered:** 2026-06-08
**Status:** Ready for planning

<domain>
## Phase Boundary

`BaseProcessor` consumes an `EntryStepDispatch` and runs an explicit **Pre → In → Post** pipeline per dispatch, replacing the single `ProcessAsync` seam:
- **Pre-Process** — reads `L2[entryId]` (skipping on `Guid.Empty` source steps), validates against the input schema. Read failure (Redis exception **or** absent/empty key) after retry exhaustion → `infra(READ)` → Keeper `REINJECT` (input left intact, round trip ends). Input-schema validation failure → business `Failed`.
- **In-Process** — the author-overridden transform `(validatedData, payload) → List<ProcessItem>`; may throw a status-carrying exception; wrapped in try/catch.
- **Post-Process** — per `completed` item: output-validate → Keeper `UPDATE` → mint GUID `entryId` → write `L2[entryId]` (no TTL) → Keeper `CLEANUP`; route each item (not-infra → orchestrator result; infra → Keeper `INJECT`); N completed items → N per-item results (no manifest).
- **End-delete** — a `finally` over every read-succeeded path; deletes `L2[entryId]`, exhaustion → Keeper `DELETE`; skipped only on `infra(READ)`/`REINJECT` and `Guid.Empty` source steps.
- Every L2 op and every send is wrapped in a bounded **N-immediate-attempt** retry loop (shared `Retry:Limit`).

**Requirements (locked):** PIPE-01..08, RESIL-01.

**NOT in this phase:** the Keeper BIT health gate + global pause/resume (Phase 45), the Keeper recovery consumer that *processes* UPDATE/REINJECT/INJECT/DELETE/CLEANUP (Phase 46), and the `_DLQ1` consolidation + at-least-once semantics (Phase 47). Phase 44 only *emits* the Keeper messages and routes terminal give-ups to the existing bus error queue.

</domain>

<decisions>
## Implementation Decisions

### In-Process author seam (PIPE-04)
- **D-01:** The In-Process author method keeps the name **`ProcessAsync`** (with a new signature). It is the abstract seam a concrete `Processor.<Purpose>` overrides; the framework owns Pre, Post, end-delete, retry, and all message sends.
- **D-02:** `validatedData` and `payload` are passed to the author as **raw `string` (JSON)** — the author deserializes both. Consistent with the prior `ProcessAsync(string inputData, string config)` shape and the design doc's "Author deserializes payload". No framework-side generic typing / pre-parsing.
- **D-03:** The In-Process return-item type is a **new record** `ProcessItem(ProcessOutcome Result, string Data, Guid ExecutionId)` where `ProcessOutcome = { Completed, Failed }`. The author **constructs it directly** and **mints `ExecutionId` itself** (new GUID per item) — per the design's author-minted `executionId` (A8/A12). `ProcessAsync` returns `List<ProcessItem>` (an empty list = no continuation, sends nothing).

### Status-carrying exception design (PIPE-05)
- **D-04:** Author signals a batch-aborting status via an **abstract base `ProcessStatusException(status)` with three concrete subclasses** — `ProcessingException`, `FailedException`, `CancelledException`. The framework does `catch (ProcessStatusException e)` → map `e.Status` to the matching `Step*` record; a separate `catch (Exception)` ⇒ `failed` (unexpected/non-status exception). Any thrown status aborts the whole batch (no Post-Process), sends exactly one orchestrator result, then runs end-delete.
- **D-05:** All three status exceptions accept an **author-supplied message** that flows into the corresponding `Step*` record's message field where one exists (`StepFailed.ErrorMessage`, `StepCancelled.CancellationMessage`). If `StepProcessing` exposes no message field on the wire, the processing message is captured for logging only — but the author-facing API is uniform (all three take a message).

### Old-seam migration
- **D-06:** **Clean break.** Delete the old `ProcessAsync(string inputData, string config) → IReadOnlyList<ProcessResult>` signature and the output-only `ProcessResult` record (`src/BaseProcessor.Core/Processing/ProcessResult.cs`). No compatibility adapter — matches the breaking v4.0.0 milestone posture. The internal `ExecuteAsync` forwarder retypes to the new seam.
- **D-07:** **Migrate `Processor.Sample` in-phase** to the new `ProcessAsync → List<ProcessItem>` seam (compile-forced once the old seam is deleted). It doubles as the **worked example** of the new author contract + status exceptions.

### Retry loop mechanism (RESIL-01 / A3)
- **D-08:** Implement a **shared reusable retry helper** (e.g. `RetryLoop.ExecuteAsync(op, limit, ct)`) wrapping each L2 op and each send. It runs **N immediate attempts (no backoff)** and **surfaces exhaustion** so the pipeline routes to the correct terminal: `infra(READ)` → `REINJECT`, output-write exhaustion → `failed (infra)` → `INJECT`, end-delete exhaustion → `DELETE`, send exhaustion → bus error queue. One place for the A3 N-immediate semantics — no per-site duplication.
- **D-09:** Introduce a **`Retry:Limit`** config key (shared across all ops, A3) replacing the hardcoded `Immediate(3)`. The bus-level `UseMessageRetry` is reconciled with the in-code loop so retries are not doubly applied to L2/send ops.
- **D-10:** On in-code retry **exhaustion of a processor send** (the design's "exhausted → `_DLQ1`" terminal), Phase 44 lets the exception **propagate so MassTransit dead-letters to the existing bus error queue** (`_error`). The `_DLQ1` rename/consolidation is **Phase 47** scope — do NOT build it here.

### Claude's Discretion
- **Pipeline code decomposition** — whether Pre/In/Post live as private methods on the rewritten `EntryStepDispatchConsumer` or in a dedicated pipeline-runner class. Left to research/planning; favor testability.
- **Schema-validation reuse** — reuse the existing `ProcessorJsonSchemaValidator` for both the Pre input-schema check and the Post output-schema check.
- **`RetryLoop` helper placement/signature** — exact namespace, sync/async overloads, and how exhaustion is surfaced (return flag vs sentinel vs typed result) are Claude's to design within D-08.
- **Keeper message construction** — the exact id-set wiring for `REINJECT`/`UPDATE`/`INJECT`/`DELETE`/`CLEANUP` follows the design doc §Processor round trip verbatim.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Locked design (source of truth)
- `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` — LOCKED 2026-06-08. **§Processor round trip** (Pre/In/Post/End-delete, lines 42–76) is the authoritative flow; **§Locked decisions** (lines 101–115) carries A2/D1/A3/A8/A12/A15 and the no-dedup/at-least-once posture. The Keeper §82–97 is context only (Phases 45–47 consume it; Phase 44 only emits the messages).

### Requirements
- `.planning/REQUIREMENTS.md` — PIPE-01..08 (lines 10–17), RESIL-01 (line 43). Every PIPE id must be accounted for in the plan.
- `.planning/ROADMAP.md` §"Phase 44: Processor Pre/In/Post-Process Pipeline" — the 5 success criteria are the verification target.

### Prior-phase contracts (consumed, not modified here)
- `.planning/phases/43-message-contracts-l2-key-reshape/43-CONTEXT.md` + `43-SUMMARY.md` — the reshaped `Step*` records (A15), `SourceStep.IsSource`, `IKeeperRecoverable` + five Keeper records, `L2ProjectionKeys` (data + composite backup), `BackupOptions`. These exist and are GREEN; Phase 44 builds on them.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `src/BaseProcessor.Core/Validation/ProcessorJsonSchemaValidator.cs` — `TryValidate(definition, data, out errors)`; reuse for both Pre (input) and Post (output) schema validation.
- `src/BaseProcessor.Core/Identity/IProcessorContext.cs` / `ProcessorContext.cs` — carries `Id`, `InputDefinition` (and presumably output definition); the pipeline reads these for schema selection and the `Guid.Empty` source-step branch.
- `Messaging.Contracts` — `StepCompleted`/`StepFailed`/`StepCancelled`/`StepProcessing` (`: IStepResult`), `SourceStep.IsSource`, `L2ProjectionKeys.ExecutionData`/`CompositeBackup`, `BackupOptions`, the five `Keeper*` records + `KeeperQueues` (all from Phase 43).
- `src/BaseProcessor.Core/Observability/ProcessorMetrics.cs` — `DispatchConsumed` meter already wired; keep the entry-point increment.

### Established Patterns
- **Business-ack vs infra-throw** (current `EntryStepDispatchConsumer` doc + `WorkflowLifecycle.IsBusiness`): business outcomes send a `Step*` result and ack; only infra faults (L2 read/write, broker send) escape to retry. Phase 44 generalizes this into the Pre/In/Post terminal routing — the split is the existing mental model.
- **L2 read-skip via `SourceStep.IsSource`** (never inline `== Guid.Empty`) — already in the straight-through consumer; the pipeline keeps this single predicate (T-43-06).
- Retry is currently `UseMessageRetry(Immediate(3))` at the runtime bus bind — D-09 replaces the literal `3` with `Retry:Limit` and adds the in-code `RetryLoop`.

### Integration Points (files this phase rewrites/adds)
- **Rewrite:** `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` (straight-through from 43-03 → full Pre/In/Post pipeline).
- **Rewrite:** `src/BaseProcessor.Core/Processing/BaseProcessor.cs` (new `ProcessAsync` seam shape).
- **Delete:** `src/BaseProcessor.Core/Processing/ProcessResult.cs`.
- **Add:** `ProcessItem` record + `ProcessOutcome` enum; the `ProcessStatusException` family; the `RetryLoop` helper; `Retry:Limit` config binding.
- **Migrate:** `Processor.Sample` to the new seam (the worked example).

</code_context>

<specifics>
## Specific Ideas

- The author API should read cleanly: override `ProcessAsync(string validatedData, string payload, CancellationToken ct)`, deserialize both, return `List<ProcessItem>` (each `new ProcessItem(ProcessOutcome.Completed, outJson, Guid.NewGuid())`), or `throw new FailedException("reason")` / `ProcessingException(...)` / `CancelledException(...)` to abort the batch with a status.
- `Processor.Sample` is the canonical demonstration of this contract — keep it small but realistic (deserialize, emit ≥1 completed item, show one status-exception path).

</specifics>

<deferred>
## Deferred Ideas

- **`_DLQ1` consolidation** (rename/consolidate the terminal give-up queue, remove `keeper-dlq`) — Phase 47 (A4). Phase 44 throws to the existing `_error` bus queue on send-exhaustion.
- **Keeper recovery consumer** that actually *processes* UPDATE/REINJECT/INJECT/DELETE/CLEANUP (partitioned by `corr:wf:ProcessorId:executionId`) — Phase 46. Phase 44 only emits these messages.
- **Keeper BIT health gate + global pause-all/resume-all** — Phase 45.

</deferred>

---

*Phase: 44-processor-pre-in-post-process-pipeline*
*Context gathered: 2026-06-08*
