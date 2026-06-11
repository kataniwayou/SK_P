# Phase 52: Three-State Keeper - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-11
**Phase:** 52-three-state-keeper
**Areas discussed:** Exhaustion policy (KEEP-05), Gate-closed consume (KEEP-04), REINJECT data-gone (KEEP-01), Phase-53 boundary

---

## Exhaustion policy (KEEP-05)

| Option | Description | Selected |
|--------|-------------|----------|
| Single global enum | Keeper-wide `Recovery:ExhaustionPolicy` read at startup | ✓ |
| Per-state config | Separate policy per REINJECT/INJECT/DELETE | |

| Option | Description | Selected |
|--------|-------------|----------|
| DLQ1 | Default to dead-letter on exhaustion (current v4 behavior) | ✓ |
| Sustained-outage | Default to hold/requeue, never dead-letter | |

| Option | Description | Selected |
|--------|-------------|----------|
| Pure hold/requeue, accept spin | Literal A18; poison op spins, no DLQ1 backstop | ✓ |
| Bounded-then-DLQ1 hybrid | Hold N cycles then dead-letter as poison backstop | |

**User's choice:** Single global enum; default DLQ1; sustained-outage = pure hold/requeue.

---

## Gate-closed consume (KEEP-04)

| Option | Description | Selected |
|--------|-------------|----------|
| Pause/resume the receive endpoint | BIT loop stops/starts keeper-recovery endpoint; messages accumulate on broker; kills WR-02 | ✓ |
| Bounded-wait, requeue-not-deadletter | Keep await-in-Consume, requeue on timeout; keeps WR-02, hot-spins | |
| Immediate NAK-requeue when closed | No wait; bounce immediately; hardest spin | |

| Option | Description | Selected |
|--------|-------------|----------|
| Yes — KEEP-04 unconditional | Gate-closed non-destructive in both modes; policy governs only gate-open failures | ✓ |
| No — gate-closed honors the mode | DLQ1 mode could dead-letter a long gate-closed wait | |

**User's choice:** Pause/resume the receive endpoint; KEEP-04 is unconditional.

---

## REINJECT data-gone (KEEP-01)

| Option | Description | Selected |
|--------|-------------|----------|
| Silent drop / ack | A18 literal "if NOT exist → drop"; retire RecoveryDataGoneException for REINJECT | ✓ |
| Keep terminal dead-letter | Preserve v4 throw → skp-dlq-1 on absent data | |

| Option | Description | Selected |
|--------|-------------|----------|
| Log + metric | Info/Warning log + counter on each by-design drop | ✓ |
| Fully silent ack | No log/metric | |

**User's choice:** Silent drop/ack with a log + counter.

---

## Phase-53 boundary

| Option | Description | Selected |
|--------|-------------|----------|
| Keeper-endpoint-local only | Touch only the keeper-recovery endpoint; processor latch + global UseMessageRetry=none + RETIRE-03 stay Phase 53 | ✓ |
| Also start the global teardown now | Begin removing the processor latch / global rule in Phase 52 | |

| Option | Description | Selected |
|--------|-------------|----------|
| Remove in-phase | Delete RecoveryGateTimeoutException + GateWaitSeconds (obsoleted by pause/resume) | ✓ |
| Leave dark for Phase 53 | Keep compiling-but-unused for the RETIRE-03 sweep | |

**User's choice:** Keeper-endpoint-local scope; remove the obsoleted gate-wait machinery in-phase.

## Claude's Discretion

- MassTransit mechanism for endpoint pause/resume; startup-time conditional endpoint config for the two exhaustion modes; how much of RecoveryConsumerBase survives; reinject-drop metric naming; hermetic-fact decomposition.

## Deferred Ideas

- Global `UseMessageRetry=none` rule + processor-side latch removal → Phase 53.
- Model-B remnant sweep (RETIRE-03) → Phase 53.
