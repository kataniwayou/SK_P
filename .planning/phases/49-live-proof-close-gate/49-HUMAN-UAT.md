# Phase 49 — Live Proof & Close Gate — Operator Runbook (HUMAN-UAT)

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
