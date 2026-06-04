# Phase 32: Cancelled Circuit-Breaker — Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-04
**Phase:** 32-cancelled-circuit-breaker
**Areas discussed:** Resume mechanism, Marker lifecycle, In-flight drop disposition, Observability, Signal mechanism (Fault fanout), Breaker trigger

---

## Starting position

The existing 32-CONTEXT.md was a stub; the real design record lives in 31-CONTEXT.md §"Failure-policy hook" + ROADMAP Phase-32 success criteria. Claude presented four open gray areas with recommendations (Resume / Marker TTL / In-flight drop / Observability). Before settling those, the user introduced four modifications that re-shaped the signalling architecture.

## User modifications

| # | Modification | Resolution |
|---|--------------|------------|
| 1 | Business metrics (orchestrator + processor) for redelivery: counter of how many times the consumer checked the dedup flag and it was `Ack`. | Accepted → D-10 |
| 2 | On attempts-budget exhaustion, use `Fault<T>` to fan out to all orchestrators (not a point-to-point send). | Accepted → D-03 |
| 3 | All orchestrators consume `Fault` and halt the job (single replica today, design supports multi-replica). | Accepted → D-06 |
| 4 | Consequently no `Cancelled` message is sent to the orchestrator — fix the design record. | Accepted (breaker-path only; token-cancellation `ExecutionResult` retained) → D-04, D-12 |

**Grounding:** `orchestrator-result` is a shared competing-consumer queue (`ResultConsumerDefinition.cs:29`) → a `Send` reaches only one replica, but the Quartz job lives on whichever replica owns the schedule. `Program.cs:31-46` already implements the per-replica `InstanceId`+`Temporary` fan-out endpoint (Start/Stop) the fault consumer mirrors.

---

## Signal mechanism (Fault<T>)

| Option | Description | Selected |
|--------|-------------|----------|
| A1 — MassTransit automatic `Fault<EntryStepDispatch>` | Published (fanout) on retry exhaustion; `_error` dead-letter backstop falls out of the same mechanism | ✓ |
| A2 — Explicit custom `Publish(WorkflowCancelled)` | Full payload/ordering control, but re-implements the `_error` backstop and fanout | |

**User's choice:** A1. **Notes:** Marker-set on the final attempt precedes the fault → effect-first preserved.

## Breaker trigger

| Option | Description | Selected |
|--------|-------------|----------|
| B1 — Infra-fault exhaustion only | `GetRetryAttempt() == Limit`; business failures stay immediate-`Failed`. Matches ROADMAP criterion #1; minimal change | ✓ |
| B2 — Persistent business-failure exhaustion too | Rework D-15 catch-and-`Failed` so `ProcessAsync` exceptions retry and trip the breaker | |

**User's choice:** B1. **Notes:** B2 captured as a deferred idea.

## Resume mechanism

| Option | Description | Selected |
|--------|-------------|----------|
| 1a — Manual (clear marker + re-`POST /start`) | Reuses existing surface; human confirms fault fixed before re-arming | ✓ |
| 1b — Dedicated `/resume` endpoint | Atomic clear+restart, new surface | |
| 1c — Auto-cooldown (TTL) | Risks flapping back into exhaustion | |

**User's choice:** 1a.

## Marker lifecycle

| Option | Description | Selected |
|--------|-------------|----------|
| 2a — No TTL, persists until resume clears it | Self-expiring breaker defeats the purpose; Stop does not clear it | ✓ |
| 2b — TTL = cooldown | Only if 1c chosen | |

**User's choice:** 2a.

## In-flight drop disposition

| Option | Description | Selected |
|--------|-------------|----------|
| 3a — Ack-and-discard | Drains to a halt; messages leave the bus | ✓ |
| 3b — Dead-letter dropped in-flight messages | Noisy, fights the clean-stop goal | |

**User's choice:** 3a.

## Observability

| Option | Description | Selected |
|--------|-------------|----------|
| Dedup-hit counters (mod 1) | `processor_dispatch_deduped_total` + `orchestrator_result_deduped_total` at the `Ack` gates | ✓ |
| Breaker-trip counter + structured log | `workflow_cancelled_total` + WARN/ERROR log on trip | ✓ |

**User's choice:** Keep both — they answer different questions (redelivery rate vs. trip count).

## Clarification — Fault path is outside flag[H] dedup

User confirmed the `Fault<T>` publish does NOT go through the `flag[H]` "Pending"/"Ack" mechanism: the job halt is idempotent (unschedule + marker SET/check), so no CAS dedup gate is needed on the fault path. → D-13.

## Claude's Discretion

- Marker key shape (`L2ProjectionKeys.Cancelled(workflowId)`), exact metric names/tag literals, fault-consumer endpoint registration details, accepting the extra per-message Redis `GET`.

## Deferred Ideas

- B2 (business-failure circuit-breaking); dedicated `/resume` endpoint / auto-cooldown resume.

---

## Addendum — 2026-06-04 (spec reconciliation, no new Q&A)

After this discussion, `/gsd-spec-phase 32` ran and produced `32-SPEC.md` (8 falsifiable requirements, ambiguity 0.11) derived from the decisions above. A follow-up `/gsd-discuss-phase 32` then reconciled the two: a `<spec_lock>` section + a `32-SPEC.md` canonical ref were added to CONTEXT.md so the planner reads the locked requirements first. **No gray areas were re-opened — D-01…D-13 are unchanged.** Each SPEC requirement traces to a decision: req-1→D-01, req-2→D-02/D-05/D-07, req-3→D-05, req-4→D-03/D-06, req-5→D-04/D-03, req-6→D-12, req-7→D-10/D-11, req-8→D-08/D-13.
