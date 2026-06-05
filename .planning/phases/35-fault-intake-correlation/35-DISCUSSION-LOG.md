# Phase 35: Fault Intake & Correlation - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-05
**Phase:** 35-fault-intake-correlation
**Areas discussed:** DLQ-1 scope boundary, Fault-consumer endpoint topology, Consumer body + log-scope mechanism, End-to-end correlation proof, Log content/level
**Format:** Prose confirm-loop (numbered forks + recommendations, plain "confirm") — per user preference, no AskUserQuestion menus.

---

## GA-1 — DLQ-1 scope boundary (the crux)

| Option | Description | Selected |
|--------|-------------|----------|
| Build DLQ-1 topology now | Phase 35 builds the TTL'd shared error-transport / consolidated DLQ-1 | |
| Confirm-only; build in Phase 36 | Phase 35 builds no DLQ-1/TTL; confirms Keeper recovers off `Fault<T>`, never reads `_error`/DLQ-1, no double-process. Consolidation = Phase 36 (DLQ-04) | ✓ |

**User's choice:** Confirmed recommendation (confirm-only).
**Notes:** Requirement map puts all `DLQ-0x` in Phase 36; SC1's "TTL'd DLQ-1" wording does not pull topology into 35. Consistent with Phase 33 D-10. Verified live: no centralized DLQ-1 / TTL exists today (per-consumer `{queue}_error` only).

---

## GA-2 — Fault-consumer endpoint topology

| Option | Description | Selected |
|--------|-------------|----------|
| One shared queue | Both `Fault<EntryStepDispatch>` + `Fault<ExecutionResult>` colocate on `keeper-fault-recovery` (one consolidated worklist) | ✓ |
| Two separate endpoints | One queue per fault type | |

**User's choice:** Confirmed recommendation (one shared queue).
**Notes:** Evolves Phase 34's single-queue design; keep `KeeperQueues.FaultRecovery` name; replace placeholder wholesale. MassTransit colocation mechanism (same `EndpointName` vs explicit `ReceiveEndpoint`) left to planner discretion.

---

## Consumer body + log-scope mechanism (GA-3 — KMET-04)

| Option | Description | Selected |
|--------|-------------|----------|
| Manual scope + shared helper | Unwrap → extract → manual `BeginScope` → log → ack; refactor scope-dict builder into shared `ExecutionLogScope.BuildState(...)` | ✓ |
| Manual scope + inline duplicate | Same body, but duplicate the empty-skip logic inline in Keeper | (fallback) |

**User's choice:** Confirmed recommendation (manual scope + shared helper).
**Notes:** `Fault<T>` is NOT `IExecutionCorrelated`, so the auto `InboundExecutionScopeConsumeFilter` does not fire — Keeper opens the scope manually from `context.Message.Message`. "Observe-and-ack" is the deliberate Phase-35 shape (recovery loop is Phase 36). Refactor touches a base library shared by all consoles — existing scope tests must stay green; inline is the documented fallback.

---

## End-to-end correlation proof (GA-4 — SC3)

| Option | Description | Selected |
|--------|-------------|----------|
| Extend Phase-33 spike test | Extend `FaultRecoverySpikeE2ETests` (or sibling) to assert the running Keeper container emits the correlated ES log | ✓ |
| New Keeper-specific test | Fresh RealStack E2E | (discretion) |

**User's choice:** Confirmed recommendation (extend spike / sibling).
**Notes:** Assert against the real Keeper container, not an in-test bus. Reuse WRONGTYPE live-trip + `PollEsForLog`. Extend-vs-new + settle windows + query shape = discretion.

---

## Log content/level (GA-5 — KMET-04 detail)

| Option | Description | Selected |
|--------|-------------|----------|
| Information structured log | "keeper fault intake" at Information, carrying correlationId + 5 execution ids + fault type + originating `Fault<T>.Exceptions[0]` message | ✓ |

**User's choice:** Confirmed recommendation.
**Notes:** Match other consoles' conventions. Exact wording, exception-field surfacing, and level nuance (Information vs Warning) = discretion.

---

## Claude's Discretion

- MassTransit endpoint colocation mechanism (same `EndpointName` vs explicit `ReceiveEndpoint`).
- Shared-helper vs inline for the scope-dict builder (shared recommended).
- Log event wording + which `Fault<T>.Exceptions` fields to surface + level nuance.
- Test vehicle: extend Phase-33 spike vs new test; settle windows; `PollEsForLog` query shape.

## Deferred Ideas

- L2 probe loop + re-inject (INTAKE-04) + `keeper-dlq`/DLQ-2 + two-DLQ split + shared error-transport/TTL'd DLQ-1 (DLQ-01..04) — Phase 36.
- Pause/resume coordination — Phase 37.
- Keeper meter + counters + close gate — Phase 38.
