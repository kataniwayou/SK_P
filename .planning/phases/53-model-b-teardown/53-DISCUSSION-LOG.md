# Phase 53: Model-B Teardown - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-11
**Phase:** 53-model-b-teardown
**Areas discussed:** Retry/_error end-state policy, Retry-rule reach (blast radius), RETIRE-03 verification depth

---

## Area Selection

| Option | Description | Selected |
|--------|-------------|----------|
| Retry/_error end-state policy | Enforce A18 literally vs ratify the as-built Immediate(N)→skp-dlq-1 latch | ✓ |
| Retry-rule reach (blast radius) | Processor-only vs processor + orchestrator execution path | ✓ |
| RETIRE-03 verification depth | Extend existing guard vs new dedicated sweep; lock the new end-state | ✓ |

**User's choice:** All three.

---

## Retry / `_error` End-State Policy + Reach

Initial AskUserQuestion (Ratify as-built / Enforce A18 literally / Defer) was redirected by the
user to clarify the intended end-state directly rather than pick from the framed options.

**User's clarified intent (verbatim):**
- Processor + Orchestrator → `UseMessageRetry = none` + `_error` disabled (send-exhaust → throw → broker redelivery).
- Keeper → config-driven: gate-open Dlq1 (send exhausts → `skp-dlq-1`) **or** message park, no DLQ (SustainedOutage); gate-closed non-destructive for both modes.
- `skp-dlq-1` only ever receives keeper traffic.

**Resolution:** Enforce A18 literally for the execution/forward path (processor + ALL orchestrator
consumers); keeper unchanged (Phase-52 settled). Aligns code to the LOCKED A18 §Global-rules — no
doc amendment. (→ D-01, D-02, D-05)

**Confirmed consequences:**
- `skp-dlq-1` scoped to the keeper recovery endpoint only — `ConsolidatedErrorTransportFilter` +
  `GenerateFaultFilter` move from global to keeper-local. (→ D-03)
- Unbounded requeue spin on a poison send is accepted (same tradeoff as keeper SustainedOutage). (→ D-04)

**Notes:** The exact MT 8.5.5 throw→redelivery mechanism (plain nack-requeue vs never-exhausting
large-finite `Immediate`) and the filter-scoping seam are HOW details handed to the researcher/planner.

---

## RETIRE-03 Verification Depth

| Option | Description | Selected |
|--------|-------------|----------|
| Extend existing guard + lock end-state | Extend ModelBContractsRetiredFacts (5→3 collapse + source/string sweep) + new guard for the no-UseMessageRetry/_error end-state | ✓ |
| Extend existing guard only | Full remnant verification but no standing guard for the new end-state | |
| New dedicated sweep test | Fresh Phase-53 RetireRemnantSweepFacts covering all three | |

**User's choice:** Extend existing guard + lock end-state.

**Notes:** Model-B contracts/consumers already deleted (Phase 50/52) and partially guarded; RETIRE-03
is the broader sweep + the 5→3 assertion + a standing guard for the newly-introduced
no-retry/no-`_error` invariant. (→ D-06)

## Claude's Discretion

- Exact MT 8.5.5 redelivery mechanism without the delayed-exchange plugin (researcher/planner).
- How to scope `ConsolidatedErrorTransportFilter` + `GenerateFaultFilter` to one endpoint (planner).
- Teardown commit ordering/atomicity; hermetic-fact decomposition.

## Deferred Ideas

- Live proof + N×GREEN triple-SHA close gate → Phase 54 (close-gate `skp-dlq-1` baseline = keeper-only traffic).
