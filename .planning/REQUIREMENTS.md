# Steps API — v9.0.0 Requirements

> **Milestone:** v9.0.0 — Canonical Recovery: Orchestrator Alignment
> **Source of truth:** This session's design conversation (2026-06-16), confirmed point-by-point.
> **Posture:** Extend the Phase-69 processor recovery-spec alignment to the orchestrator's result-consume path, and finish a small processor INJECT cleanup that makes the keeper delete-invariant uniform across both consoles. Canonical pattern: `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` + `docs/design/processor-keeper-recovery-spec.md` §3–§8. Phases continue at **70**.

## Goal

Give the orchestrator's result-consume path the same `messageId`-indexed forward/recovery/keeper pipeline the processor has, so an orchestrator crash/redelivery re-emits stable entryIds idempotently (at-least-once delivery + stable re-emission; **NOT** message-level dedup), and make the keeper delete-invariant uniform: keys are deleted ONLY in the cleanup tail, completed out-of-band by the DELETE keeper state on exhaust — INJECT and REINJECT never delete.

**Idempotency model:** the inbound result's broker `messageId` is the idempotency/branch key (`L2[messageId]` index, slot array). A redelivery re-enters RECOVERY and re-sends the **persisted** entryIds (stable, retired slots skipped); the atomic two-key delete closes the index-gone/input-alive reprocess window. This mirrors the processor exactly and reverses Phase 24.1's L1-only `TypedResultConsumer` (re-introduces L2 to the result path).

## Requirements

### Processor INJECT Cleanup (KINJ) — Phase 70

- [x] **KINJ-01
**: The keeper INJECT path is non-destructive — it writes the data key and sends the result, and deletes NO key (the `delete L2[DeleteEntryId]` step is removed from `InjectConsumer`).
- [x] **KINJ-02
**: The vestigial `KeeperInject.DeleteEntryId` field is removed from the contract; `ProcessorPipeline.BuildInject` no longer supplies it; `InjectConsumerFacts` and the Phase-50 golden tests are updated to the new shape; the solution builds 0-warning.
- [x] **KINJ-03
**: DELETE is the ONLY keeper state that deletes keys — REINJECT and INJECT are non-destructive — enforced by a negative-guard fact (no `KeyDelete*` in the INJECT/REINJECT consumers).

### Orchestrator Recovery Pipeline (ORCV) — Phase 71

- [ ] **ORCV-01**: The orchestrator result-consume path gates on `exist L2[messageId]` (messageId = the inbound result's broker message id): absent → FORWARD, present → RECOVERY. A gate L2-op exhaustion routes to REINJECT and ends the round trip (no cleanup).
- [ ] **ORCV-02**: FORWARD, per next step, performs ONE atomic op (index-slot `HSET` + whole-hash `PEXPIRE` + copy `L2[origin entryId] → L2[new entryId]` with the data TTL). An atomic-write exhaustion routes to a single `OrchestratorInject` (no silent drop).
- [ ] **ORCV-03**: Each index slot value carries the full dispatch tuple `(nextStepId, nextProcessorId, payload, newEntryId)` so RECOVERY can reconstruct the heterogeneous per-slot dispatch (orchestrator slots are not homogeneous like the processor's).
- [ ] **ORCV-04**: FORWARD sends `EntryStepDispatch` to `queue:{nextProcessorId}` then retires the slot to `guid.empty`; the cleanup tail (atomic two-key `DEL` of `L2[messageId]` + `L2[origin entryId]`) runs ONLY if no item escalated to the keeper this pass; a delete exhaustion routes to DELETE.
- [ ] **ORCV-05**: RECOVERY re-emits idempotently — per slot a 3-way classification (data exists → re-send; clean not-exist → drop, no retire; L2 fault → leave slot intact); the tail REINJECTs if any slot faulted, else runs the atomic two-key delete. A redelivery re-sends the stable persisted entryIds and skips retired slots.
- [x] **ORCV-06
**: Keeper contracts are split by origin (route-by-type, no discriminator switch): `KeeperInject`/`KeeperReinject` are renamed `ProcessorInject`/`ProcessorReinject`, and `OrchestratorInject`/`OrchestratorReinject` are added; `KeeperDelete` stays shared. The two new consumers bind on the same `keeper-recovery` endpoint (same partitioner, health gate, and exhaustion posture, no new queue). `OrchestratorReinject` rebuilds the result (carrying the outcome to pick the `IStepResult` subtype) and re-injects to `queue:orchestrator-result`; `OrchestratorInject` completes the index+data copy and sends `EntryStepDispatch` downstream.
- [ ] **ORCV-07**: The delete invariant holds orchestrator-side — keys are deleted ONLY in the cleanup tail (a forward exit where no item escalated, or the end of a recovery pass), completed out-of-band by DELETE on exhaust; `OrchestratorInject` and `OrchestratorReinject` never delete a key.

## Future Requirements (deferred)

- **Live close-gate proof of orchestrator recovery** — a real-stack fault-injection run proving zero-missing + effect-once when the orchestrator crashes mid-fan-out (folds into the v8.0.0 7-scenario harness; this milestone proves it at the hermetic/unit level).
- **Orchestrator slot-array metrics** — a keeper-style counter for orchestrator INJECT/REINJECT/DROP, mirroring the `processor_*` meters.

## Out of Scope

- **Message-level dedup revival** — the system stays at-least-once + stable entryId re-emission; the retired v3.6.0 `H`/`flag[H]` effect-first CAS dedup is NOT reintroduced.
- **Multi-replica orchestrator** — the orchestrator stays single-replica; this milestone does not add competing-consumer orchestrators.
- **Changing the keeper health-gate / partitioner / exhaustion posture** — reused verbatim; the new consumers bind on the existing endpoint with the existing posture.
- **New keeper queue/endpoint** — orchestrator recovery rides the existing `keeper-recovery` endpoint, not a separate queue.

## Traceability

REQ-IDs are filled into phases by the roadmapper (Step 10). Every requirement maps to exactly one phase; the roadmapper validates 100% coverage. Phases continue at **70**.

| Requirement | Phase | Status |
|-------------|-------|--------|
| KINJ-01 | Phase 70 | Complete |
| KINJ-02 | Phase 70 | Complete |
| KINJ-03 | Phase 70 | Complete |
| ORCV-01 | Phase 71 | Pending |
| ORCV-02 | Phase 71 | Pending |
| ORCV-03 | Phase 71 | Pending |
| ORCV-04 | Phase 71 | Pending |
| ORCV-05 | Phase 71 | Pending |
| ORCV-06 | Phase 71 | Pending |
| ORCV-07 | Phase 71 | Pending |

**Coverage:** 10 requirements across 2 categories (KINJ, ORCV). Filled into phases by the roadmapper.
