---
status: partial
phase: 49-live-proof-close-gate
source: [49-VERIFICATION.md]
started: 2026-06-09
updated: 2026-06-09
---

# Phase 49 — Live Proof & Close Gate — Operator Runbook (HUMAN-UAT)

## Current Test

[awaiting operator — live N×GREEN close-gate run against the rebuilt v4 stack]

**Phase:** 49-live-proof-close-gate
**Milestone:** v4.0.0 (Processor Pre/In/Post-Process + Keeper Recovery Redesign)
**Authored:** 2026-06-09
**Status:** AUTHORED + hermetically green — the live N×GREEN close run is OPERATOR-GATED (D-03).

---

## Purpose

This is the operator runbook that gates the **TEST-01 / TEST-02 / TEST-03** tick.

Phase 49 is the FINAL phase of v4.0.0 — a LIVE-PROOF + CLOSE-GATE phase, **not** a behavior phase. The
v4 RealStack E2E proofs (SC1 round-trip, SC2 recovery paths, SC3 BIT-gate pause/resume across a transient
L2 outage) and the close machinery (`scripts/phase-49-close.ps1`) are **authored and hermetically green**:
they build 0-warning at Release + Debug and the non-RealStack suite passes.

The **actual live N×GREEN close run is operator-gated** (D-03 — every prior milestone close, Phase
39/35/36/33, deferred the live run to an operator gate). It requires the rebuilt v4 stack up. Until the
operator records a GREEN run in this file, **TEST-01 / TEST-02 / TEST-03 stay `[ ]` in REQUIREMENTS.md.**

---

## Step 1 — Rebuild the v4 stack (breaking wire contract)

v4.0.0 is a **fresh breaking wire-contract change** (Pre/In/Post pipeline + Keeper 5-state recovery
redesign). A mixed-version stack mis-deserializes the new contract — every contract-changed image MUST be
rebuilt before a live run is valid.

Rebuild the four contract-changed services:

```
docker compose up -d --build baseapi-service orchestrator processor-sample keeper
```

Then bring up the full stack (the infra services + the four rebuilt above):

```
docker compose up -d postgres redis rabbitmq otel-collector elasticsearch prometheus orchestrator processor-sample baseapi-service keeper
```

Wait until every service reports healthy:

```
docker compose ps
```

> WHY rebuild: the v4 embedded SourceHash on `Processor.Sample` must match the host build, and the new
> wire contract is NOT backward-compatible. A stale image false-passes/times out the liveness gate or
> mis-deserializes a message. `keeper` runs `deploy.replicas: 2` — both replicas must be healthy.

---

## Step 2 — Invoke the close gate

```
pwsh -File scripts/phase-49-close.ps1
```

The gate:
1. Seeds the idempotent steady-state `Processor.Sample` row (genuine embedded SourceHash, seed version
   `3.5.0`), waits for `processor-sample` healthy.
2. Runs the compose-stack health pre-flight (v4 canonical service list).
3. Captures the BEFORE triple-SHA snapshot (psql `\l`, redis `--scan`, rabbitmq `list_queues name`).
4. Runs the BOTH-config 0-warning build gate (`-c Release` + `-c Debug`).
5. Runs the **full suite live across the 3-consecutive-GREEN cadence** — including the three RealStack E2E
   facts SC1 / SC2 / SC3. The SC3 outage test (`docker stop`/`docker start sk-redis`) runs **serialized in
   its own non-parallel collection** so it cannot destabilize sibling RealStack tests. An identical
   `Passed` fact count across all 3 runs is the Smell-A guard.
6. Settle-drains the short-TTL `skp:data:*` round-trip keys (NO composite-TTL settle-wait — the 2-day
   composite `corr:wf:proc:exec` backup is proven net-zero by active E2E teardown / Keeper INJECT+CLEANUP,
   so a leak surfaces as a redis SHA mismatch, not a silent TTL pass).
7. Captures the AFTER triple-SHA snapshot and asserts BEFORE == AFTER for all three.
8. Asserts the sole DLQ `skp-dlq-1` depth == 0 (separate `list_queues name messages` read).

**Exit codes:**
- `0` — both build configs 0-warning, all three runs GREEN, all three SHA-256 invariants held,
  `skp-dlq-1` depth == 0.  **PASS.**
- `1` — invariant violation OR any build/test run RED OR unparseable fact count (Smell A).
- `2` — environment misconfigured (compose stack not healthy).

---

## Step 3 — Record the GREEN run

On a PASS (exit 0), copy the gate's PASS-summary values into this table. This recorded GREEN run is the
artifact that gates the TEST-01/02/03 tick.

| Field                                          | Value |
|------------------------------------------------|-------|
| psql `\l` SHA-256 (BEFORE == AFTER)            |       |
| redis `--scan` SHA-256 (BEFORE == AFTER)       |       |
| rabbitmq `list_queues` SHA-256 (BEFORE == AFTER) |     |
| `Passed` fact count (identical across all 3 runs) |    |
| `skp-dlq-1` depth (== 0)                        |       |
| Run date                                       |       |
| Operator                                       |       |

> The three SHA values + the `Passed` count + the `skp-dlq-1` depth mirror the gate's operator-append line
> printed on PASS.

---

## Step 4 — DoD gate (tick TEST-01/02/03)

The following requirements are ticked **ONLY** after a GREEN run is recorded in Step 3 above. Until then
they stay `[ ]` in `.planning/REQUIREMENTS.md`.

- [ ] **TEST-01** — RealStack E2E: full Pre → In → Post round trip + each Keeper recovery path
      (REINJECT data-present, REINJECT data-gone → `skp-dlq-1`, INJECT, DELETE).
- [ ] **TEST-02** — RealStack E2E: BIT-gate global pause-all/resume-all across a transient L2 outage
      (outage → pause all → L2 recovers → resume all), idempotent per job.
- [ ] **TEST-03** — Close gate: N=3 consecutive GREEN + triple-SHA (psql `\l` / redis `--scan` /
      rabbitmq `list_queues`) BEFORE == AFTER net-zero (including the composite backup key + GUID data
      keys + `skp-dlq-1` depth == 0), at Release + Debug 0-warning.

When all three are recorded GREEN, the operator flips the corresponding `[ ]` → `[x]` in
`.planning/REQUIREMENTS.md` and references this GREEN-run record.

---

## Tests

### 1. Live N×GREEN close-gate run — gates TEST-01 / TEST-02 / TEST-03
expected: After rebuilding the v4 stack (Step 1), `pwsh -File scripts/phase-49-close.ps1` exits `0` — 3 consecutive GREEN runs with identical `Passed` fact count, triple-SHA (psql `\l` / redis `--scan` / rabbitmq `list_queues`) BEFORE == AFTER net-zero (including the composite `corr:wf:proc:exec` backup key + GUID data keys), `skp-dlq-1` depth == 0, at Release + Debug 0-warning. The SC1 round-trip, SC2 four recovery paths, and SC3 pause/resume-across-outage RealStack facts all pass live. Record the 3 SHA values + Passed count + DLQ depth in the Step 3 table, then tick TEST-01/02/03 in REQUIREMENTS.md.
result: [pending]

## Summary

total: 1
passed: 0
issues: 1
pending: 1
skipped: 0
blocked: 1

## Gaps

A first operator live run (2026-06-09) executed the gate against a freshly-rebuilt v4 stack. Pre-flight, 0-warning builds (Release+Debug), and the triple-SHA machinery all worked; the 3×GREEN cadence did not pass (Run 1: 509 passed / 5 failed — the 5 live round-trip facts). Two v4 defects were surfaced — **both are now closed**, so the gate is **unblocked** and ready for a re-run:

- **GAP-49-1 — RESOLVED (commit `5666fb7`).** `skp-dlq-1` 406 `x-message-ttl` poison-loop in `ConsolidatedErrorTransportFilter` (sent via `queue:` → re-declared the ttl'd queue without args → 4,133× redelivery storm). Fixed by sending via `exchange:skp-dlq-1`. Verified: 0× 406, DLQ depth 0.
- **GAP-49-2 — RESOLVED (commits `081895f`, `03e0129`, `ddea4df`; plan 49-05, decision D-08 Option A).** Pause-all/resume-all left the orchestrator Quartz scheduler stuck paused for newly-scheduled workflows (`PauseAll()` set `pausedTriggerGroups`; per-job resume never cleared it). Fix: `ResumeAllConsumer` now calls a scheduler-wide group-flag clear (`WorkflowScheduler.ResumeAllGroupsAsync` → `scheduler.ResumeAll()`) exactly once, **after** the per-job reschedule loop — so every trigger is already fresh-from-now `Normal` and no misfire herd fires (`PauseAllConsumer` unchanged). Covered by the `Normal_After_PauseAll_Resume_Cycle` regression (true PauseAll→ResumeAll over a RAM scheduler; post-cycle workflow born `Normal`) + the ordering/no-burst facts. Hermetic suite 508 passed / 0 failed; both build configs 0-warning. Re-verified in `49-VERIFICATION.md` (status `human_needed`, 5/5 must-haves).

The round-trip code itself was always sound (isolated SC1 passes on a clean scheduler).

### Live Run #2 (2026-06-09 — executed in-session, v4 stack rebuilt)

After the GAP-49-2 fix, the stack was rebuilt and the gate was re-run. **Result: FAILED — Run 1: 512 passed / 3 failed (of 515); exit 1, never reached 3×GREEN.** GAP-49-2 is **proven fixed** (SC1 + SampleRoundTrip now pass live — the scheduler no longer freezes). But **3 new live-proof gaps** surfaced, each reproducing in isolation (not load flakiness):

- **GAP-49-3 (high, root-caused) — SC2 consolidated DLQ.** Data-gone give-ups land in `skp-dlq-1_skipped` (depth 3), not `skp-dlq-1` (depth 0), because `skp-dlq-1` is a consuming MassTransit ReceiveEndpoint and the exchange-forwarded fault (GAP-49-1 fix) has no consumer → MassTransit skips it. Also makes the close-gate `skp-dlq-1 depth==0` check pass trivially while real give-ups pile up in `_skipped`. **Design decision needed** (park-queue vs assert `_skipped`).
- **GAP-49-4 (medium) — SC3 ES seam.** The `Global PauseAll` orchestrator seam wasn't found in Elasticsearch within 150s, though the orchestrator emits it and ES has otel logs. Not yet root-caused (BIT-gate timing vs ingestion lag vs query shape).
- **GAP-49-5 (medium) — MetricsRoundTrip Prometheus.** Business metric series empty in Prometheus within the poll window. Not yet root-caused (scrape lag vs missing series).

**The gate is BLOCKED again on GAP-49-3/4/5.** Close them via `/gsd-plan-phase 49 --gaps`, then re-run Step 1 → Step 2. TEST-01/02/03 stay `[ ]`.
