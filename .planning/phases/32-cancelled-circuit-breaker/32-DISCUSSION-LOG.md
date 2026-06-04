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

## Claude's Discretion

- Marker key shape (`L2ProjectionKeys.Cancelled(workflowId)`), exact metric names/tag literals, fault-consumer endpoint registration details, accepting the extra per-message Redis `GET`.

## Deferred Ideas

- B2 (business-failure circuit-breaking); dedicated `/resume` endpoint / auto-cooldown resume.
