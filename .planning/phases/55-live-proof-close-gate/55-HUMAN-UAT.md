---
status: pending
phase: 55-live-proof-close-gate
source: [55-01-PLAN.md]
started: 2026-06-12
updated: 2026-06-12
---

# Phase 55 — Live Proof & Close Gate — Operator Runbook (HUMAN-UAT)

## Current Test

[pending — the live N=3xGREEN close-gate run is operator-gated; not yet executed]

**Phase:** 55-live-proof-close-gate
**Milestone:** v5.0.0 (Recovery Re-architecture — messageId slot-array + 3-state keeper)
**Authored:** 2026-06-12
**Status:** PENDING — the close gate (`scripts/phase-55-close.ps1`) and the RealStack E2E proofs are authored
and hermetically green. The actual live N=3xGREEN close run is operator-gated (D-09). Until the operator
records a GREEN run in this file, **TEST-01 / TEST-02 stay `[ ]` in REQUIREMENTS.md.**

---

## Purpose

This is the operator runbook that gates the **TEST-01 / TEST-02** tick.

Phase 55 is the FINAL phase of v5.0.0 — a LIVE-PROOF + CLOSE-GATE phase, **not** a behavior phase. The
v5 RealStack E2E proofs (SC1 forward round-trip + slot-array index assertions, SC2 the three Keeper
recovery states + the organic recovery pass + the both-key DELETE, SC3 BIT-gate pause/resume across a
transient L2 outage) and the close machinery (`scripts/phase-55-close.ps1`) are **authored and hermetically
green**: they build 0-warning at Release + Debug and the non-RealStack suite passes.

The **actual live N=3xGREEN close run is operator-gated** (D-09 — every prior milestone close, Phase
49/39/35/36/33, deferred the live run to an operator gate). It requires the rebuilt v5 stack up. Until the
operator records a GREEN run in this file, **TEST-01 / TEST-02 stay `[ ]` in REQUIREMENTS.md.**

The v5 close gate (`phase-55-close.ps1`) is a verbatim clone of the proven Phase-49 triple-SHA net-zero
protocol carrying exactly three v5 deltas: the retired Model-B composite settle-GC block removed (D-06a),
an additive `skp:msg:*` count==0 assertion (D-06c), retitled to Phase 55 (seed version unchanged at
`3.5.0`). It proves net-zero of the processor-owned `messageId` slot-array index via the A19 active
two-key DELETE, **not** by waiting out the 300/600s SlotArrayOptions TTL (which cannot be waited out).

---

## Step 1 — Rebuild the v5 stack (breaking wire contract)

v5.0.0 is a **fresh breaking wire-contract change** (messageId slot-array + 3-state keeper + A19 active
reclaim). A mixed-version stack mis-deserializes the new contract — every contract-changed image MUST be
rebuilt before a live run is valid.

**First, a clean host build** so the host-built `Processor.Sample.dll` embedded `SourceHash` matches what
goes into the container image. A stale *incremental* host build is the documented Phase-49 failure mode (an
incremental build left a stale hash that mismatched the clean docker image, so the gate seeded a hash the
container could not resolve → the liveness gate false-passes or times out):

```
dotnet clean SK_P.sln
dotnet build SK_P.sln -c Release
```

> WHY the clean build: a `BaseProcessor.Core` change shifts the assembly-embedded `SourceHash`. An
> incremental build can leave a stale hash bit on the host `Processor.Sample.dll`; the close gate reflects
> that hash off the host binary and seeds the Processor row by it, but `PollForHealthyLivenessAsync` polls
> the REAL container's `skp:{procId:D}` heartbeat — which is keyed by the container image's hash. If host !=
> container, the gate either false-passes against a stale identity or times out waiting for liveness. A
> clean `dotnet clean + build` (host hash == container hash) is mandatory.

Rebuild the four contract-changed services (the v5 wire contract changed in all four — slot-array +
3-state keeper + A19):

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

> WHY rebuild all four: the v5 embedded `SourceHash` on `Processor.Sample` must match the host build, and
> the new wire contract (slot-array `skp:msg:*` index + 3-state keeper + A19 two-key DELETE) is NOT
> backward-compatible. A stale image false-passes/times out the liveness gate or mis-deserializes a
> message. `keeper` runs `deploy.replicas: 2` — both replicas must be healthy.

---

## Step 2 — Invoke the close gate

```
pwsh -File scripts/phase-55-close.ps1
```

The gate runs the **FULL test suite live (RealStack included), x3, with NO filter** — the `Phase=55` retag
does NOT change which tests the gate runs; it always runs everything. The gate:

1. Seeds the idempotent steady-state `Processor.Sample` row (genuine embedded `SourceHash`, seed version
   `3.5.0`), waits for `processor-sample` healthy.
2. Runs the compose-stack health pre-flight (v5 canonical service list, keeper `replicas: 2`).
3. Captures the BEFORE triple-SHA snapshot (psql `\l`, redis `--scan`, rabbitmq `list_queues name`).
4. Runs the BOTH-config 0-warning build gate (`-c Release` + `-c Debug`).
5. Runs the **full suite live across the 3-consecutive-GREEN cadence** — including the RealStack E2E facts
   SC1 / SC2 / SC3. The SC3 outage test (`docker stop`/`docker start sk-redis`) runs **serialized in its
   own non-parallel collection** so it cannot destabilize sibling RealStack tests. An identical `Passed`
   fact count across all 3 runs is the Smell-A guard (D-10).
6. Captures the AFTER triple-SHA snapshot and asserts BEFORE == AFTER for all three. There is **no
   composite-TTL settle-wait** — Model-B is retired (Phases 50/53), and net-zero of the `skp:msg:*`
   slot-array index is proven by the A19 active two-key DELETE (`ProcessorPipeline.DeleteTerminalAsync` +
   `DeleteConsumer`), so a leak surfaces as a redis SHA mismatch, not a silent TTL pass.
7. Asserts the sole DLQ `skp-dlq-1` depth == 0 (separate `list_queues name messages` read).
8. Asserts the NEW `skp:msg:*` slot-array index count == 0 (additive A19 active-reclaim assertion, D-06c).

**Exit codes:**
- `0` — both build configs 0-warning, all three runs GREEN, all three SHA-256 invariants held,
  `skp-dlq-1` depth == 0, and `skp:msg:*` count == 0.  **PASS.**
- `1` — invariant violation OR any build/test run RED OR unparseable fact count (Smell A).
- `2` — environment misconfigured (compose stack not healthy).

---

## Step 3 — Record the GREEN run

On a PASS (exit 0), copy the gate's PASS-summary values into a record block below (one per run). The N=3
GREEN guard requires the **same `Passed` fact count across all three runs** (Smell-A guard, D-10). This
recorded GREEN run is the artifact that gates the TEST-01/02 tick.

Each record block captures:

| Field                                            | Value |
|--------------------------------------------------|-------|
| psql `\l` SHA-256 (BEFORE == AFTER)              | `<fill>` |
| redis `--scan` SHA-256 (BEFORE == AFTER)         | `<fill>` (net-zero keyspace, liveness key excluded) |
| rabbitmq `list_queues` SHA-256 (BEFORE == AFTER) | `<fill>` (transient `_bus_` queues excluded) |
| `Passed` fact count (identical across all 3 runs)| `<fill>` |
| `skp-dlq-1` depth (== 0)                         | `<fill>` |
| `skp:msg:*` slot-array index count (== 0)        | `<fill>` |
| Run date                                         | `<fill>` |
| Operator                                         | `<fill>` |

> The three SHA values + the `Passed` count + the `skp-dlq-1` depth + the `skp:msg:*` count mirror the
> gate's operator-append line printed on PASS.

---

## Step 4 — DoD gate (tick TEST-01/02)

The following requirements are ticked **ONLY** after the N=3 consecutive GREEN run with triple-SHA
BEFORE == AFTER, `skp-dlq-1` depth == 0, AND `skp:msg:*` count == 0 is recorded in Step 3 above — and
**ONLY** by the operator. Until then they stay `[ ]` in `.planning/REQUIREMENTS.md`.

- [ ] **TEST-01** — RealStack E2E: full forward round-trip (slot-array allocation-before-data) + each
      surviving Keeper recovery state (REINJECT data-present, REINJECT data-gone → silent drop, INJECT,
      DELETE two-key) + the organic recovery pass + the A19 two-key net-zero DELETE.
- [ ] **TEST-02** — Close gate: N=3 consecutive GREEN + triple-SHA (psql `\l` / redis `--scan` / rabbitmq
      `list_queues`) BEFORE == AFTER net-zero (including the GUID data keys + the `skp:msg:*` slot-array
      index proven leak-free by the A19 active two-key DELETE, not a TTL settle) + `skp-dlq-1` depth == 0 +
      `skp:msg:*` count == 0, at Release + Debug 0-warning.

When both are recorded GREEN, the operator flips the corresponding `[ ]` → `[x]` in
`.planning/REQUIREMENTS.md` and references this GREEN-run record.

---

## Threat mitigations (recorded — RESEARCH Security Domain)

Three threat surfaces are mitigated by this runbook + the cloned gate:

1. **Stale/mixed-version image mis-deserializes the v5 wire contract** (T-55-01, Tampering/DoS) — mitigated
   by the operator rebuild (Step 1: `docker compose up -d --build` of all four contract-changed services)
   plus the clean `dotnet clean + build`. The `SourceHash` == host-build liveness gate makes a false-pass
   impossible: the gate polls the REAL container heartbeat by that hash.
2. **Gate false-pass via a synthetic liveness seed** (T-55-02, Spoofing) — mitigated (already, upstream).
   The gate seeds the Processor row by the GENUINE embedded `SourceHash` and `PollForHealthyLivenessAsync`
   polls the REAL container `skp:{procId:D}` heartbeat — no synthetic seed exists in the cloned script.
3. **Net-zero TTL-race false-pass** (T-55-03, Repudiation / silent net-zero loss) — mitigated by the A19
   active two-key DELETE (`ProcessorPipeline.DeleteTerminalAsync` + `DeleteConsumer` both-key) plus the
   explicit `skp:msg:*` count == 0 assertion (D-06c). A lingering `skp:msg:*` index surfaces as BOTH a
   redis SHA mismatch AND count > 0 — never a silent TTL pass (the 300/600s TTL cannot be waited out).

---

## Tests

### 1. Live N×GREEN close-gate run — gates TEST-01 / TEST-02
expected: After rebuilding the v5 stack (Step 1, including the clean `dotnet clean + build` so host hash == container hash), `pwsh -File scripts/phase-55-close.ps1` exits `0` — 3 consecutive GREEN runs with identical `Passed` fact count, triple-SHA (psql `\l` / redis `--scan` / rabbitmq `list_queues`) BEFORE == AFTER net-zero (including the GUID data keys + the `skp:msg:*` slot-array index, proven leak-free by the A19 active two-key DELETE), `skp-dlq-1` depth == 0, `skp:msg:*` count == 0, at Release + Debug 0-warning. The SC1 forward round-trip + slot-array index assertions, SC2 three recovery states + organic recovery pass + both-key DELETE, and SC3 pause/resume-across-outage RealStack facts all pass live. Record the 3 SHA values + Passed count + DLQ depth + `skp:msg:*` count in a Step 3 record block, then tick TEST-01/02 in REQUIREMENTS.md.
result: [pending]

## Summary

total: 1
passed: 0
issues: 0
pending: 1
skipped: 0
blocked: 0

## Gaps

[none yet — the live run has not been executed. Any gaps surfaced during the operator's live run are
recorded in the per-run record blocks below and triaged via `/gsd-plan-phase 55 --gaps`.]

### Live Run #1 (pending)

[record block — fill from the gate's PASS-summary on exit 0]

### Live Run #2 (pending)

[record block — fill from the gate's PASS-summary on exit 0]

### Live Run #3 (pending)

[record block — fill from the gate's PASS-summary on exit 0]
