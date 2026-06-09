# Processor Pre/In/Post-Process + Keeper Recovery — Redesign Spec

**Status:** LOCKED 2026-06-08 — source of truth. No additions or discrepancies beyond what is stated here.
**Amended 2026-06-08 (A15):** the processor→orchestrator result is split into **four typed records** (`StepCompleted`/`StepFailed`/`StepCancelled`/`StepProcessing`), replacing the single `ExecutionResult(Outcome)` — see the **Result contract** section and locked-decision **A15**. Every "send orchestrator result" / `ExecutionResult(<status>, …)` phrasing below is read through that section.
**Amended 2026-06-09 (A16):** the **at-least-once / no-dedup delivery model is now a named guarantee** — the v4 execution path is at-least-once, carries **no dedup / idempotency key** (the v3.x `H` + dedup gate were removed in Phase 43), and **tolerates duplicate effects downstream by construction** (a redelivered message reproduces its effect; nothing collapses it). This elevates the **Delivery model** line (line 5) and locked decisions **No dedup / idempotency key** (line 105) + **A4** single `_DLQ1` (line 112) to a single test-cited guarantee. It is proven **hermetically** by the Phase-47 traceability ledger `.planning/phases/47-dlq-consolidation-at-least-once-semantics/47-DLQ-AUDIT.md` (every RESIL-02 / RESIL-03 + SC-1/2/3 row → a green test: single-DLQ consolidation + duplicate-delivery no-collapse + data-gone terminal); the **live / real-stack** proof (broker `skp-dlq-1` + `x-message-ttl`) is **Phase 49** (TEST-01..03). **Bundled (Phase-46 deferred note):** `KeeperReinject` carries an additive **`Payload : string`** field — verified live by `KeeperContractTests` and stamped/set by `ProcessorPipeline.BuildReinject` + `RecoveryDeadLetterFacts`; without it a recovered run silently loses its author config. *(Additive amendment; no source change.)*
**Delivery model:** at-least-once; **no dedup / idempotency key** (the v3.x `H` + dedup gate are removed); duplicate effects are tolerated downstream.

---

## Identities & L2

Every message carries: `correlationId, workFlowId, stepId, ProcessorId, executionId, entryId`.

- `entryId` is a **GUID**. `Guid.Empty` = source step → **skip L2 read and skip end-delete**.
- L2 (Redis projection), two key schemes:
  - **Data key** `L2[entryId]` — per-item payload. **No TTL.**
  - **Composite backup key** `L2[correlationId:workFlowId:ProcessorId:executionId]` — written by `UPDATE`, **deleted the moment it is redundant** (processor `CLEANUP` after a successful output write; keeper after a successful `INJECT`). **TTL = 2 days (configurable in days) is a crash-backstop only** — not the cleanup.
- **infra failure** = an L2 op that fails after its retry loop is exhausted. For a **read**, failure = a Redis exception **or an absent/empty key** (the data isn't there). For **delete/write**, failure = a Redis exception. Anything else that fails = **business failure**.
- **retry loop** = N immediate attempts, no backoff, shared config `Retry:Limit` (default 3).
- **`_DLQ1`** = single consolidated dead-letter queue for every terminal send/L2 give-up (processor *and* keeper). No separate `keeper-dlq`.

---

## Result contract (four typed records) — Amendment A15 (2026-06-08)

> **Supersedes** every "send orchestrator result" / `ExecutionResult(<status>, …)` phrasing in this doc. Where the round-trip and Keeper sections write `ExecutionResult(Completed, …)` etc., read it as **the matching one of the four typed records below**.

The processor→orchestrator result is **four typed records**, not a single `ExecutionResult` carrying a `StepOutcome`:

- **`StepCompleted`** `{ correlationId, workFlowId, stepId, ProcessorId, executionId, entryId }` — carries the **real** per-item data-key `entryId`.
- **`StepFailed`** `{ …six ids, errorMessage }` — `entryId = Guid.Empty`.
- **`StepCancelled`** `{ …six ids, cancellationMessage }` — `entryId = Guid.Empty`.
- **`StepProcessing`** `{ …six ids }` — `entryId = Guid.Empty`.

All four implement `IStepResult : IExecutionCorrelated`, carry the six ids, and drop `H`. **`entryId` seeding is done by the contracts** (`StepCompleted` = the real key; the other three default `Guid.Empty`), so a consumer reads `entryId` uniformly with no branching.

`StepOutcome` (`Processing/Completed/Failed/Cancelled`, int-aligned to `StepEntryCondition.Previous*`) is **no longer a wire field** — it survives only as the orchestrator-internal advancement vocabulary and the per-type consumer knob.

**Why typed (routing rationale):** the orchestrator consumes each result with a typed `TypedResultConsumer<TMessage> where TMessage : IStepResult` whose only per-type knob is its `StepOutcome` — **no status `if`/`switch` anywhere**; routing is by message type, funneling into the same `StepEntryCondition` match. The processor send-side emits the matching record per item (In-Process `completed`→`StepCompleted`, business `failed`→`StepFailed`, author-thrown `processing`/`cancelled`→`StepProcessing`/`StepCancelled`). Keeper `INJECT` reconstructs a `StepCompleted`. All four land on the one orchestrator-result queue.

---

## Processor round trip

### 1 · Pre-Process
1. Consume `EntryStepDispatch` from orchestrator.
2. If `entryId == Guid.Empty` → skip read; `validatedData` = empty (no input validation).
3. Else read `L2[entryId]`; on Redis exception **or absent/empty key** → retry loop; exhausted → **infra(READ)**.
4. Validate read data vs input schema; on failure → **business `Failed`**.

**Terminal:**
- **infra(READ)** → send Keeper **`REINJECT`** `{corr, wf, step, proc, exec, entryId}`. End round trip. *(Input left intact for the keeper.)*
- **business `Failed`** → send orchestrator result `Failed`, then run **End-delete (§4)**.
- **success** (read + validate OK) → In-Process.
- Any processor send that throws → retry loop; exhausted → `_DLQ1`.

### 2 · In-Process (author override)
- Signature: `(validatedData, payload) → List<Item>`, where `Item = { result: "completed" | "failed", data, executionId }` and **`executionId` is author-minted, new per item**.
- Author deserializes `payload`; may throw a status-carrying exception: `"processing" | "failed" | "cancelled"`.
- Wrapped in try/catch. **Any exception** → send ONE orchestrator result with Outcome = thrown status (an unexpected / non-status exception ⇒ `"failed"`); whole batch aborted (no Post-Process); then run **End-delete (§4)**.
- Normal return → per-item list flows to Post-Process.

### 3 · Post-Process (per item, in order)
1. If `completed` → validate `data` vs output schema → set `completed` | `failed`.
2. If `completed` → send Keeper **`UPDATE`** `{corr, wf, step, proc, exec, validatedData}` (keeper writes the composite backup, TTL 2d).
3. If `completed` → generate `entryId` (GUID), write `L2[entryId]` (no TTL); on Redis exception → retry loop; exhausted → item = `failed (infra)`, else `completed` **and send Keeper `CLEANUP {corr, wf, step, proc, exec}`** to delete the now-redundant composite backup.
4. If item is **not-infra** (`completed` ∪ business `failed`) → send orchestrator result. A `completed` result carries `entryId` + `executionId`.
5. If item is **infra** → send Keeper **`INJECT`** `{corr, wf, step, proc, exec}`.

- N completed items → **N separate per-item orchestrator results** (no manifest).
- Any processor send that throws → retry loop; exhausted → `_DLQ1`.

### 4 · End-delete (`finally` — runs on every path where the read succeeded)
- **Applies to:** happy path, pre-process business-fail, and In-Process exception.
- **Skipped only on:** infra(READ)/`REINJECT` (input left intact) and `Guid.Empty` source steps.
- Delete `L2[entryId]`; on Redis exception → retry loop; exhausted → **infra(DELETE)** → send Keeper **`DELETE`** `{corr, wf, step, proc, exec, entryId}`.
- End of round trip. *(A delete failure never alters results already sent to the orchestrator; `DELETE` is L2 garbage-collection only.)*

---

## Keeper

### BIT health gate (background)
- `while` loop with a configurable delay in seconds (`Probe:DelaySeconds`). Each tick runs **BIT** against L2 (read + write-then-delete probe).
- BIT results are **suppressed** (exceptions never crash the loop). The result fans out a **global** broadcast to **all** orchestrators: **unhealthy → pause all jobs; healthy → resume all jobs.**
- Orchestrator pause/resume-all is **idempotent per job** (pause only if Running, resume only if Paused); job state is known via **Quartz `TriggerState`**.

### Recovery consumer (messages from processors)
- Consumes `UPDATE / REINJECT / INJECT / DELETE / CLEANUP`.
- **Partitioned by `corr:wf:ProcessorId:executionId`** (per-key ordering — e.g. MassTransit `UsePartitioner`): messages for the **same exec** are processed in arrival order, so `UPDATE` always precedes that exec's `CLEANUP`/`INJECT`; different execs still run in parallel.
- Performs the L2 op **only when L2 is healthy (BIT gate open)**; gate-closed → the consumer **waits for the gate** (bounded so as not to exceed the broker consumer timeout).
- **`UPDATE`** → write `validatedData` to `L2[corr:wf:ProcessorId:executionId]`, TTL 2 days (configurable in days) — the TTL is a crash-backstop; the copy is normally deleted by `CLEANUP`/`INJECT`.
- **`REINJECT`** → read `L2[entryId]`; if **present** (transient outage — data survived) → re-inject a reconstructed `EntryStepDispatch` to `queue:{ProcessorId}`; if **absent/empty** (data truly gone — redelivery after end-delete, or missing input) → read failure → retry loop → `_DLQ1`.
- **`INJECT`** → read `L2[corr:wf:ProcessorId:executionId]` → **generate `entryId`** → write `L2[entryId]` (no TTL) → inject a reconstructed `StepCompleted` (carrying `entryId` + `executionId`; per A15 — was `ExecutionResult(Completed, …)`) to the orchestrator result queue → **delete the composite copy** (now redundant).
- **`DELETE`** → delete `L2[entryId]`.
- **`CLEANUP`** → delete the composite copy `L2[corr:wf:ProcessorId:executionId]` (happy-path redundancy cleanup; ordered after this exec's `UPDATE` by the partitioner).
- Any keeper send that throws → retry loop; exhausted → `_DLQ1`.
- Any keeper L2 op that throws → retry loop; exhausted → `_DLQ1`.

---

## Locked decisions (traceability)

| Tag | Decision |
|-----|----------|
| — | No dedup / idempotency key (v3.x `H` + dedup gate removed); at-least-once; duplicates tolerated. |
| A2 | A read finding an absent/empty `L2[entryId]` = read failure → **infra(READ)** → `REINJECT`. In the redelivery-after-end-delete (or genuinely-missing-input) case the data is truly gone, so Keeper's `REINJECT` read also misses → `_DLQ1`. |
| D1 | End-delete uses `finally` semantics over **all** read-succeeded paths (Option A). |
| — | `entryId` is a GUID; `L2[entryId]` has **no TTL**; composite backup key TTL = **2 days, configurable in days** — a crash-backstop only. |
| B / CLEANUP | Keeper owns the composite copy (Model B). The recovery consumer is **partitioned by `corr:wf:ProcessorId:executionId`** for per-key ordering. A 5th state **`CLEANUP`** deletes the redundant copy on the happy path (processor sends it after a successful output write); `INJECT` deletes it on the recovery path. Net-zero on every non-crash path; the 2-day TTL only covers a crash mid-recovery. Per-key ordering also guarantees `UPDATE` precedes `INJECT` (no data-gone-→`_DLQ1` race). |
| A8/A12 | Author mints `executionId` per item; N completed items → N per-item orchestrator results (no manifest). |
| A14 | Global pause-all / resume-all to all orchestrators (replaces per-workflow pause). |
| A4 | Single `_DLQ1` for all terminal give-ups; `keeper-dlq` removed. |
| A3 | Retry loops: N immediate attempts, no backoff, shared `Retry:Limit`. |
| A15 | Processor→orchestrator result is **four typed records** (`StepCompleted`/`StepFailed`/`StepCancelled`/`StepProcessing : IStepResult : IExecutionCorrelated`), replacing the single `ExecutionResult(Outcome)`. `entryId` seeding is contract-level (`StepCompleted` = real key, the other three `Guid.Empty`); `StepOutcome` is demoted off the wire to internal advancement/consumer vocabulary. Enables no-`if`/`else` typed-consumer routing. See the **Result contract** section. *(Amendment 2026-06-08.)* |

---

## Scope note

This **replaces** large parts of the shipped v3.x execution model — effect-first idempotency (`H`/content-addressing/CAS), the single-`ProcessAsync` seam, the result manifest, and the reactive `Fault<T>` recovery path. It is a **breaking change** and should be planned as the next **major** milestone (v4.0.0).
