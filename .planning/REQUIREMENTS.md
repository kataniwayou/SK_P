# Steps API — v5.0.0 Requirements

> **Milestone:** v5.0.0 — Recovery Re-architecture (messageId slot-array + 3-state keeper)
> **Source of truth:** `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` → "Recovery Re-architecture (A18)" (LOCKED 2026-06-11)
> **Posture:** Breaking successor to v4.0.0's recovery core. **Supersedes Model B** (keeper-owned composite backup key + `UPDATE`/`CLEANUP` + the 5-state consumer) with a **processor-owned `messageId` slot-array** model + a **3-state keeper**. **Retained unchanged from v4.0.0:** BIT health gate + global pause/resume (A14), four typed `Step*` result records (A15), at-least-once / no-dedup (A16), single consolidated `skp-dlq-1` (A4). Phases continue at **50**.

## Requirements

### Processor Slot-Array Recovery (SLOT)
- [ ] **SLOT-01**: Post-Process generates a GUID `entryId`, then writes the allocation index `L2[messageId][slot]=entryId` (TTL random) **before** writing the data key (allocation-before-data, so a crash never leaves unreferenced data).
- [ ] **SLOT-02**: Post-Process writes `L2[entryId]=data` after the allocation index write.
- [ ] **SLOT-03**: A slot is retired to `guid.empty` **only after** that item's `completed` result is confirmed-sent to the orchestrator (send-before-retire), so a recovery replay never re-sends a completed entry while leaving infra entries re-checkable.

### Infra Taxonomy (INFRA)
- [ ] **INFRA-01**: An allocation-index write exhausted after its retry loop sets `error_message="infra_messageId"`; the item is **dropped** (no send anywhere).
- [ ] **INFRA-02**: A data-key write exhausted after its retry loop sets `error_message="infra_entryId"`; the item is sent to keeper **`INJECT`** carrying `(data, deleteEntryId)`.

### Forward Pass (FWD)
- [ ] **FWD-01**: On `NOT exist L2[messageId]` the processor runs the forward pass (Pre → In → Post); an existence-check / source-read L2 exhaustion routes to keeper **`REINJECT`** and ends the round trip with input intact.
- [ ] **FWD-02**: Forward dispatch routes per item — non-`infra_*` → orchestrator result; `infra_entryId` → keeper `INJECT`; `infra_messageId` → drop.
- [ ] **FWD-03**: The forward happy-path tail deletes the source `entryId`; delete exhaustion → keeper **`DELETE`**.

### Recovery Pass (RECOV)
- [ ] **RECOV-01**: On `exist L2[messageId]` the processor runs the recovery pass — reads `entryIds[]` and builds a temp list per slot (`exists`→completed · `not-exist`→failed · L2-fail→failed+`infra_entryId`); a `read L2[messageId]` / existence-check exhaustion routes to keeper `REINJECT`.
- [ ] **RECOV-02**: Recovery dispatch — `completed` → re-send orchestrator(`completed`) then retire the slot to `guid.empty`; `failed` not-exist → drop; `failed` `infra_entryId` → leave the slot intact (preserved for retry).
- [ ] **RECOV-03**: If any recovery item is `infra_entryId` → send keeper `REINJECT` and **do NOT delete the source** (`REINJECT` and source-delete are mutually exclusive); otherwise delete the source `entryId` (exhaustion → keeper `DELETE`).

### 3-State Keeper (KEEP)
- [ ] **KEEP-01**: `REINJECT` reads the source `entryId` (drops if absent), then re-injects a reconstructed `EntryStepDispatch` carrying the original `Payload` to the processor input (simulate orchestrator send).
- [ ] **KEEP-02**: `INJECT` (forward-only — data is in-hand) writes `L2[entryId]=data`, sends a reconstructed `StepCompleted` to the orchestrator, then deletes `deleteEntryId`.
- [ ] **KEEP-03**: `DELETE` deletes the L2 key; drops if the key is absent.
- [ ] **KEEP-04**: The keeper performs an L2 op only when the BIT gate is open; **gate-closed → non-destructive consume** (no dequeue-and-drop — pause consumption / requeue without ack so messages accumulate and drain when the gate opens).
- [ ] **KEEP-05**: Keeper exhaustion policy is **configurable** — DLQ1 mode (exhausted op/send dead-letters to `skp-dlq-1`) vs sustained-outage mode (hold/requeue and wait for L2 recovery, no dead-letter).

### Model-B Teardown (RETIRE)
- [ ] **RETIRE-01**: The composite backup key `L2[corr:wf:ProcessorId:executionId]` + its `BackupOptions` TTL are removed.
- [x] **RETIRE-02
**: The `UPDATE` and `CLEANUP` keeper-state contracts + consumers are removed.
- [ ] **RETIRE-03**: The 5-state recovery consumer collapses to the 3 surviving states (`REINJECT`/`INJECT`/`DELETE`); no Model-B remnants survive a source/reflection sweep.

### Live Proof & Close Gate (TEST)
- [ ] **TEST-01**: A RealStack E2E proves the forward pass + the recovery pass + each keeper state (`REINJECT` present/absent, `INJECT`, `DELETE`) under the new model.
- [ ] **TEST-02**: The close gate runs N-consecutive-GREEN + triple-SHA (psql `\l` / redis `--scan` / rabbitmq `list_queues`) BEFORE==AFTER net-zero — including the slot-array index keys + data keys (no leak), at Release + Debug 0-warning.

## Future Requirements (deferred)

- Auto-resume sweep into a recovered L2 after a sustained-outage hold (carried from the v3.7.0 `FUTURE-KEEPER-SWEEP` deferral) — out of scope for v5.0.0.

## Out of Scope

- **Re-introducing a dedup / idempotency key** — v5.0.0 stays at-least-once / no-dedup (A16); duplicate effects remain tolerated. The slot-array `if exist L2[messageId]` branch is a *replay-dedup-of-sends* optimization, not a downstream-effect dedup.
- **Changing the BIT gate, pause/resume, typed `Step*` records, or the single `skp-dlq-1`** — retained unchanged from v4.0.0 (A14/A15/A4).

## Traceability

| REQ-ID | Phase | Status |
|--------|-------|--------|
| SLOT-01 | Phase 51 | Pending |
| SLOT-02 | Phase 51 | Pending |
| SLOT-03 | Phase 51 | Pending |
| INFRA-01 | Phase 51 | Pending |
| INFRA-02 | Phase 51 | Pending |
| FWD-01 | Phase 51 | Pending |
| FWD-02 | Phase 51 | Pending |
| FWD-03 | Phase 51 | Pending |
| RECOV-01 | Phase 51 | Pending |
| RECOV-02 | Phase 51 | Pending |
| RECOV-03 | Phase 51 | Pending |
| KEEP-01 | Phase 52 | Pending |
| KEEP-02 | Phase 52 | Pending |
| KEEP-03 | Phase 52 | Pending |
| KEEP-04 | Phase 52 | Pending |
| KEEP-05 | Phase 52 | Pending |
| RETIRE-01 | Phase 50 | Pending |
| RETIRE-02 | Phase 50 | Pending |
| RETIRE-03 | Phase 53 | Pending |
| TEST-01 | Phase 54 | Pending |
| TEST-02 | Phase 54 | Pending |

*21 requirements across 7 categories (SLOT 3 · INFRA 2 · FWD 3 · RECOV 3 · KEEP 5 · RETIRE 3 · TEST 2). Phase assignments filled by the roadmap.*
