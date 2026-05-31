# Phase 24: Orchestrator Result-Consume & Step Advancement - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-05-31
**Phase:** 24-orchestrator-result-consume-step-advancement
**Areas discussed:** Dispatch reuse, Match/traversal placement, Result queue/endpoint, Gating redesign (gate-closed, conditionless consumers, stop-keeps-L1, WebApi suppression)

---

## Dispatch reuse (D-01)

| Option | Description | Selected |
|--------|-------------|----------|
| Extract shared `IStepDispatcher` | One Send-to-`queue:{processorId}` unit reused by fire job + result consumer (DRY) | ✓ |
| Inline ~8 lines in the consumer | Duplicate the dispatch block; simpler, some duplication | |

**User's choice:** recommended — extract shared dispatcher.

---

## Match / traversal placement (D-02)

| Option | Description | Selected |
|--------|-------------|----------|
| `StepAdvancement` helper in Orchestrator | Pure, unit-testable; int-based match; `Always=4`/`Never=5` named consts | ✓ |
| Inline in the consumer | Match + walk inside Consume | |
| In Messaging.Contracts | Rejected — orchestrator behavior; can't reference `BaseApi.Service` enum | |

**User's choice:** recommended — `StepAdvancement` helper.

---

## Result queue / endpoint (D-03)

| Option | Description | Selected |
|--------|-------------|----------|
| `queue:orchestrator-result` shared ReceiveEndpoint | Competing-consumer, mirrors `queue:{processorId}` convention | ✓ |
| `execution-result` / appsettings-configurable | Alternative names | |

**User's choice:** "similar" — `queue:orchestrator-result`-style shared competing-consumer endpoint.

---

## Gating: result-consumer gate-closed handling (D-06)

| Option | Description | Selected |
|--------|-------------|----------|
| A — ack-drop when gate closed | Simple, matches Phase 23; but silently loses a one-time result on restart | |
| B — retry/redeliver when gate closed | Throw → delayed redelivery; result survives hydration | ✓ |
| C — no gate, rely on TryGet | Acts on partial L1; same loss as A | |

**User's choice:** redesign → never drop (redeliver). Extended to ALL consumers (Start/Stop/result): gate-closed throws/nacks for delayed redelivery (D-06). No stripe on read-only result path (D-08).

---

## Gating: duplicate start/stop suppression (D-04, D-05)

**User's redesign:** Move duplicate-suppression to the WebApi (first-win create/delete of L2 root). With duplicates suppressed upstream, the orchestrator Start/Stop consumers become **conditionless** — Start unconditionally hydrates L2→L1 + reschedules; Stop unconditionally deletes jobs. This also removes the Phase 23 teardown+skip / stripe-held-skip logic and resolves the "restart dead-end" (no skip-if-exists to get stuck on).

**Notes:** Supersedes Phase 23 ORCH-CONSUME-01 (start) and ORCH-STOP-01 (stop) behavior.

---

## Gating: does stop clear L1? (D-07)

| Option | Description | Selected |
|--------|-------------|----------|
| Clear L1 (symmetric, simplest) | Stop deletes jobs + L1; late results hit TryGet miss → ack, no drain; no eviction problem | |
| Keep L1 (drain) | Stop deletes jobs, keeps L1; late results still advance the DAG | ✓ |

**User's choice:** keep L1 (drain) for this phase. Late processor results for a stopped workflow still dispatch.
**Notes:** User explicitly wants to later find a per-workflowId mechanism that verifies no processor messages remain on the bus before it is safe to clear L1 → deferred (`FUTURE-STOP-EVICTION`).

---

## Claude's Discretion

- Delayed-redelivery attempt count / spacing (must outlast hydration).
- `IStepDispatcher` folder location.
- Test-harness specifics (reuse Phase 23 in-memory bus + Redis mux + `CapturingDispatchConsumer`).

## Deferred Ideas

- Per-workflowId safe L1 eviction: verify no in-flight processor messages on the bus for a workflowId, then clear L1 (`FUTURE-STOP-EVICTION`).
- Duplicate-delivery dedup; cross-replica dispatch coordination; `FUT-REQRESP-01` self-id.
