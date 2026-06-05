# Phase 37: Orchestrator Pause/Resume Coordination - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-06
**Phase:** 37-orchestrator-pause-resume-coordination
**Areas discussed:** Keeper publish points, pending-recovery state, concurrency model, orchestrator-restart behavior, operator-Stop vs Keeper-Resume, give-up semantics

---

## 1. Keeper publish points & give-up semantics

| Option | Description | Selected |
|--------|-------------|----------|
| Pause at intake (before probe loop), Resume on success, nothing on give-up | Halt cron as the outage spreads; resume after re-inject; give-up leaves it paused | ✓ |
| Pause after probe / other timings | — | |

**User's choice:** Confirmed as proposed. (Give-up semantics later refined — see §5/§6.)
**Notes:** Both fault consumers (`EntryStepDispatch` + `ExecutionResult`) publish.

---

## 2. Pending-recovery state — set vs flag vs none

| Option | Description | Selected |
|--------|-------------|----------|
| Multi-element set keyed by `H` on `WorkflowL1` | Reference-count in-flight recoveries; resume when empty | |
| Separate `ConcurrentDictionary` store | Parallel to `IWorkflowL1Store` | |
| No set — Quartz state only | Collapsed after the give-up rule changed (§6) | ✓ |

**User's choice:** No set. The user established that (a) all probes for one workflow share L2
health, so an early/duplicate resume among successes is harmless, and (b) give-up is out of scope
(§6). With give-ups no longer pinning a resumed workflow, reference-counting is unnecessary.
**Notes:** `H` retained for correlation only, not counting.

---

## 3. Concurrency model

| Option | Description | Selected |
|--------|-------------|----------|
| Reuse existing drop-if-held (`Wait(0)`) stripe | Drops a contended Resume → workflow stuck paused | |
| Dedicated queueing per-workflow lock | Serializes pause/resume without dropping | |
| Consumer `ConcurrentMessageLimit = 1` + idempotency + redelivery | Serial consume; crash → un-acked → redelivered | ✓ |

**User's choice:** Consume one at a time; rely on un-acked-redelivery for crash recovery and
H-idempotent handlers. No dedicated lock. The user pushed back on processing these concurrently at all.
**Notes:** Stop-vs-Resume race folded into the Quartz-state check (§4).

---

## 4. State source of truth — Quartz vs L1 marker

| Option | Description | Selected |
|--------|-------------|----------|
| Explicit `{Running,Stopped,Paused}` marker on `WorkflowL1` | One bit, delete-based pause | |
| Quartz `GetTriggerState` (Normal/Paused/None) | Pause via `PauseJob`; Quartz owns the 3-state | ✓ |

**User's choice:** Lock Quartz as the source of truth — it natively distinguishes `Normal`
(running) / `Paused` / `None` (deleted/stopped). Requires Pause to use `PauseJob` (not `DeleteJob`),
revising PAUSE-02. Resume checks `GetTriggerState`; legitimate only if `Paused`; reschedules by
deleting the stale trigger and recomputing a fresh one from L1 cron (avoids one-shot misfire).
**Notes:** Resolves the §3 Stop-vs-Resume residual — Quartz job-store concurrency serializes the race.

---

## 5. Operator Stop vs Keeper Resume

| Option | Description | Selected |
|--------|-------------|----------|
| Out of scope / operator re-issues Stop | Accept the race | |
| Three-state guard: resume only if Paused | Stop wins; resume ignored when Stopped/Running | ✓ |

**User's choice:** Three states — running / stopped / paused. Resume is legitimate **only if paused**.
Operator Stop wins over a later Keeper Resume. Implemented via the Quartz `GetTriggerState` check (§4).

---

## 6. Give-up semantics (PAUSE-05)

| Option | Description | Selected |
|--------|-------------|----------|
| Keep PAUSE-05: stay paused if ≥1 sibling gave up | Requires the reference-counting set | |
| Resume on any success; give-up → DLQ → out of scope | Keeper stateless; collapses set to none | ✓ |

**User's choice:** Keeper is stateless — it probes and resumes on its own success regardless of other
messages' states. If a probe attempt exhausts, the message goes to `keeper-dlq` and from there it is
**out of scope** (operator-handled). The orchestrator does not re-pin a resumed workflow because of a
sibling give-up. A workflow stays paused only if none of its recoveries succeed.
**Notes:** Revises PAUSE-05. The user reasoned: a successful probe proves L2 is healthy, so resuming
is safe; runs are independent, so a parked message is an isolated operator concern.

---

## Orchestrator-restart behavior (confirmed)

**User's choice:** On restart the orchestrator re-hydrates from L2 and reschedules the job as
documented — pause-state (in-memory / Quartz-only, never L2) is lost. Accepted as a known limitation.

## Claude's Discretion

- Deterministic per-workflow `TriggerKey` derivation for `GetTriggerState`.
- New contracts' file layout and the two consumers' endpoint placement (mirror Start/Stop).

## Deferred Ideas

- Quartz-native `ResumeJob` on the stale trigger (rejected for misfire reasons).
- Durable pause-state across orchestrator restart (contradicts "never L2").
- Auto-resume after give-up (operator action — FUTURE-KEEPER-SWEEP).
