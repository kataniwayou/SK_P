# Steps API — v4.0.0 Requirements

> **Milestone:** v4.0.0 — Processor Pre/In/Post-Process + Keeper Recovery Redesign
> **Source of truth:** `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` (LOCKED 2026-06-08)
> **Posture:** Breaking successor to the v3.x execution model. At-least-once delivery; **no dedup key**; duplicate effects tolerated. Recovery assumes a **transient** L2 outage (backup lives in L2). Phases continue at **43**.

## Requirements

### Processor Pipeline (PIPE)
- [ ] **PIPE-01**: `BaseProcessor` runs an explicit Pre-Process → In-Process → Post-Process pipeline per consumed dispatch, replacing the single `ProcessAsync` seam.
- [ ] **PIPE-02**: Pre-Process reads `L2[entryId]` with a bounded retry loop; read failure (Redis exception **or** absent/empty key) after exhaustion → `infra(READ)`. `entryId == Guid.Empty` skips the read with empty validated data.
- [ ] **PIPE-03**: Pre-Process validates the read data against the input schema; validation failure → business `Failed` (not infra).
- [ ] **PIPE-04**: In-Process is an author-overridden abstract method receiving `(validatedData, payload)` and returning a list of items, each `{ result: completed|failed, data, executionId }` with an author-minted `executionId`.
- [ ] **PIPE-05**: In-Process is wrapped in try/catch; the author may throw a status-carrying exception (`processing`/`failed`/`cancelled`); any exception sends one orchestrator result (an unexpected exception ⇒ `failed`) and aborts the batch.
- [ ] **PIPE-06**: Post-Process per `completed` item validates output against the output schema, generates a GUID `entryId`, and writes `L2[entryId]` (no TTL) with a bounded retry loop (exhaustion → `failed (infra)`).
- [ ] **PIPE-07**: Post-Process routes each item — not-infra (`completed` ∪ business-`failed`) → orchestrator result (a `completed` result carries `entryId` + `executionId`); infra → Keeper `INJECT`. N completed items → N per-item orchestrator results (no manifest).
- [ ] **PIPE-08**: End-delete (a `finally` over every read-succeeded path) deletes `L2[entryId]` with a bounded retry loop; exhaustion → Keeper `DELETE`.

### Message Contracts (MSG)
- [ ] **MSG-01**: `EntryStepDispatch` and `ExecutionResult` carry the six ids (`correlationId, workFlowId, stepId, ProcessorId, executionId, entryId`) and no longer carry `H`.
- [ ] **MSG-02**: `entryId` is a GUID; `Guid.Empty` is the explicit source-step sentinel (no read, no delete).
- [ ] **MSG-03**: Four Keeper message contracts exist — `UPDATE` (+ validated data), `REINJECT`, `INJECT`, `DELETE` — each carrying its specified id set.

### Orchestrator (ORCH)
- [ ] **ORCH-01**: Orchestrator consumes per-item `ExecutionResult` messages (no manifest fan-out) and advances workflow steps accordingly; a Keeper-`INJECT`'d completion is indistinguishable from a direct one.
- [ ] **ORCH-02**: Orchestrator's pause-all / resume-all is idempotent per job via Quartz `TriggerState` (pause only if running, resume only if paused).

### Keeper Recovery (KEEP)
- [ ] **KEEP-01**: Keeper runs a background BIT loop at a configurable delay (seconds) that probes L2 health; results are suppressed (never crash the loop).
- [ ] **KEEP-02**: The BIT result drives a global pause-all (unhealthy) / resume-all (healthy) broadcast to all orchestrators.
- [ ] **KEEP-03**: The Keeper recovery consumer performs each L2 operation only while the BIT gate is open (waits while closed, bounded under the broker consumer timeout).
- [ ] **KEEP-04**: `UPDATE` writes validated data to `L2[corr:wf:ProcessorId:executionId]` with a configurable TTL (default 2 days).
- [ ] **KEEP-05**: `REINJECT` reads `L2[entryId]` and re-injects the dispatch to `queue:{ProcessorId}`; if the data is gone, the round terminates at `_DLQ1`.
- [ ] **KEEP-06**: `INJECT` reads `L2[corr:wf:ProcessorId:executionId]`, generates `entryId`, writes `L2[entryId]`, and injects `ExecutionResult(Completed)` to the orchestrator.
- [ ] **KEEP-07**: `DELETE` deletes `L2[entryId]`.

### Resilience & Semantics (RESIL)
- [ ] **RESIL-01**: Every L2 op and every message send is wrapped in a bounded retry loop (N immediate attempts, shared `Retry:Limit`).
- [ ] **RESIL-02**: Processor and Keeper terminal give-ups (send exception exhausted; Keeper L2 op exhausted) route to a single consolidated `_DLQ1`.
- [ ] **RESIL-03**: The execution path is at-least-once with no dedup/idempotency key; duplicate effects are tolerated downstream.

### v3.x Teardown (RETIRE)
- [ ] **RETIRE-01**: Remove the `H` identity, `flag[H]` dedup gate, and CAS `Pending→Ack` flips from processor and orchestrator.
- [ ] **RETIRE-02**: Remove content-addressed L2 data, the result manifest, and N×M manifest fan-out.
- [ ] **RETIRE-03**: Remove the reactive `Fault<EntryStepDispatch>`/`Fault<ExecutionResult>` Keeper recovery path and the `keeper-dlq` queue.

### Live Proof & Close Gate (TEST)
- [ ] **TEST-01**: Real-stack E2E proves the full Pre/In/Post round trip plus each recovery path (`REINJECT` data-present, `REINJECT` data-gone → `_DLQ1`, `INJECT`, `DELETE`).
- [ ] **TEST-02**: Real-stack proof of the BIT-gate global pause-all/resume-all across a transient L2 outage (outage → pause → recover → resume).
- [ ] **TEST-03**: Close gate — N consecutive GREEN runs + triple-SHA (psql / redis / rabbitmq) net-zero, matching prior milestone close-gate discipline.

## Future Requirements (deferred)

- **Durable (non-L2) recovery backup** — surviving a full L2 data-loss (not just a transient outage). The current model deliberately stores the recovery backup in L2 itself.

## Out of Scope

- **Exactly-once-effect** — deliberately traded away. v3.6.0's `H` + `flag[H]` exactly-once-effect is replaced by at-least-once + Keeper-replay; duplicates are tolerated. *Why: explicit user direction in the locked spec.*
- **Any dedup / idempotency key** — the `H` identity and dedup gate are removed, not relocated. *Why: the redesign is at-least-once by construction.*
- **Non-transient L2 recovery** — if L2 returns with data lost (TTL-expired backup, or flush), `REINJECT`/`INJECT` cannot reconstruct and the round terminates at `_DLQ1` for operator triage. *Why: recovery backup lives in L2; transient-outage model only.*
- **Authentication / authorization** — unchanged from v1; service is open. *Why: carried project-wide exclusion.*

## Traceability

(empty — filled by roadmap)
