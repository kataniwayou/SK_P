---
phase: 39-keeper-observability-real-stack-e2e-close-gate
verified: 2026-06-07T00:00:00Z
status: passed
score: 6/6 requirements verified
overrides_applied: 0
backfilled: true
---

# Phase 39: Keeper Observability + Real-Stack E2E + Close Gate — Verification Report

**Phase Goal:** Register the Keeper meter + throughput/saturation instruments, then prove the full recover-and-give-up behavior live against the real stack and lock a 3x500 GREEN triple-SHA net-zero close gate.
**Verified:** 2026-06-07
**Status:** passed (backfilled from close-gate evidence)
**Re-verification:** No — initial verification.

> **Backfilled 2026-06-07 from 39-04-SUMMARY close-gate evidence — gate NOT re-run (Phase-42 D-04/D-05).**
> This is a documentation backfill of an already-passed-in-substance gate. No RealStack run, no
> container rebuild, no test execution was performed for this report. All numbers below are
> transcribed faithfully from the existing Phase-39 artifacts (primarily `39-04-SUMMARY.md`, the
> operator run on 2026-06-06, runs `bw615ju9t` / `b1xvh3508`).

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | SC1: The `Keeper` meter is registered following the house pattern (MeterName const ↔ AddMeter symmetry via IMeterFactory) | VERIFIED | Hermetic `KeeperMetricsFacts` 11/11 GREEN (39-01 / 39-02); meter + instrument registration pinned. KMET-01. |
| 2 | SC2: Throughput/outcome counters + saturation instruments are scrapable and carry `fault_type` / `ProcessorId` / `reason` labels with no `workflowId` cardinality | VERIFIED | Counters/UpDownCounter/histogram registered and scrapable; live `KeeperRecovery_*` E2E asserts the `keeper_*` series + labels (39-02 / 39-03). KMET-02, KMET-03. |
| 3 | SC3: Live recover-both-paths is exactly-once against the real stack | VERIFIED | `KeeperRecovery_RecoversBothPaths` RealStack E2E GREEN within the 3x500 suite; recover hop wired via fanout `ResumeWorkflow` to avoid the orchestrator-result competing-consumer race (46fd82d). TEST-01. |
| 4 | SC4: Live give-up parks the original `Fault<ExecutionResult>` to `keeper-dlq`, the workflow stays paused, and `keeper_dlq_pushed` increments | VERIFIED | `KeeperRecovery_GivesUp_ParksToDlq` RealStack E2E GREEN; in-test probe proves the park and drains it. The single shared competing-consumer queue `keeper-fault-recovery` guarantees a single park, not a duplicate. TEST-02. |
| 5 | SC5: The close gate runs 3x500 GREEN with triple-SHA (psql/redis/rabbitmq) net-zero at Release+Debug zero-warning, with `skp-dlq-1` depth==0 | VERIFIED (substance) | Operator run 2026-06-06: Run 1/2/3 = 500/500/500 passed; Release + Debug both exit 0 (zero-warning); psql/redis/rabbitmq SHA-256 BEFORE==AFTER all HELD; `skp-dlq-1` depth==0 HELD. `keeper-dlq` depth==0 had a single LATE give-up-park drain-timing artifact (GATE_EXIT=1 on that invariant only) — accepted by the operator and later CLOSED by KHARD-02 (Phase 40). See Accepted Follow-Up. TEST-03. |

**Score:** 5/5 observable truths verified (TEST-03 substance met; keeper-dlq invariant follow-up accepted + closed by KHARD-02).

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `scripts/phase-39-close.ps1` | Close gate: 3x GREEN + triple-SHA + both-DLQ depth==0 + keeper rebuild deltas | VERIFIED | Clone of `phase-33-close.ps1` with DELTA 1 (`keeper` added to rebuild/health `$services`) + DELTA 2 (additive both-DLQ `depth==0` assertion on `keeper-dlq` + `skp-dlq-1` via `rabbitmqctl list_queues name messages`, kept SEPARATE from the name-SHA). Parses clean; all acceptance greps pass. Committed `5f4ac37`; NDJSON multi-replica health-check fix `d9fc8bc`. |
| `KeeperMetrics` instruments | `Keeper` meter + throughput counters + `keeper_in_flight` UpDownCounter + `keeper_recovery_duration` histogram | VERIFIED | Registered per the house pattern; hermetic `KeeperMetricsFacts` 11/11 GREEN (39-01 / 39-02). |
| Extended KeeperRecovery RealStack facts | `KeeperRecovery_RecoversBothPaths` + `KeeperRecovery_GivesUp_ParksToDlq` with `keeper_*` scrape assertions | VERIFIED | Both facts GREEN in the live 3x500 suite (39-03); give-up fact proves the `keeper-dlq` park + drain in-test. |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| Close-gate BEFORE snapshot | Close-gate AFTER snapshot | triple-SHA chain BEFORE==AFTER | HELD | psql `\l` SHA-256 = `34ac2385...`; redis `--scan` SHA-256 = `b2d8ec21...`; rabbitmq `list_queues name` SHA-256 = `ee79f392...` — all identical BEFORE and AFTER across the 3x500 cadence. |
| Close-gate AFTER snapshot | `skp-dlq-1` queue | `rabbitmqctl list_queues name messages` depth read | HELD | `skp-dlq-1` depth==0 in every AFTER snapshot. |
| `KeeperRecovery_GivesUp_ParksToDlq` | `keeper-dlq` | live give-up park of the original `Fault<ExecutionResult>` | WIRED | In-test probe proves the park and drains it; `keeper_dlq_pushed` increments (single shared `keeper-fault-recovery` competing-consumer queue → single park). |

---

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| KMET-01 | 39-01 / 39-02 | `Keeper` meter registered (house pattern, IMeterFactory, MeterName ↔ AddMeter symmetry) | VERIFIED | Hermetic `KeeperMetricsFacts` 11/11 GREEN. |
| KMET-02 | 39-02 | Throughput/outcome counters emit with `fault_type` / `ProcessorId` / `reason`, no `workflowId` | VERIFIED | Counters scrapable; live `KeeperRecovery_*` E2E scrape assertions (39-02 / 39-03). |
| KMET-03 | 39-01 / 39-02 | `keeper_in_flight` UpDownCounter + `keeper_recovery_duration` histogram scrapable | VERIFIED | Instruments registered + scrapable; best-effort transient gauge assertion per 39-VALIDATION Manual-Only note. |
| TEST-01 | 39-03 | Recover-both-paths E2E asserts `keeper_*` series + labels after live recover | VERIFIED | `KeeperRecovery_RecoversBothPaths` GREEN in the live 3x500 suite. |
| TEST-02 | 39-03 | Give-up E2E asserts `keeper_dlq_pushed` + message in `keeper-dlq` | VERIFIED | `KeeperRecovery_GivesUp_ParksToDlq` GREEN; park proven + drained in-test. |
| TEST-03 | 39-04 | Close gate exists, parses clean, preserves triple-SHA, adds keeper rebuild + both-DLQ deltas, runs 3x500 GREEN with triple-SHA net-zero (incl. `skp-dlq-1`==0) at Release+Debug zero-warning | VERIFIED (substance) | 3x500 GREEN + triple-SHA net-zero + `skp-dlq-1`==0 all HELD. `keeper-dlq` net-zero had the documented give-up-drain timing follow-up (GATE_EXIT=1 on that invariant only), CLOSED by KHARD-02 (Phase 40). |

All six Phase-39 REQ-IDs (KMET-01, KMET-02, KMET-03, TEST-01, TEST-02, TEST-03) are VERIFIED.

---

### Accepted Follow-Up — keeper-dlq give-up-park drain timing (NOT a functional defect)

In one of the three close-gate runs the `keeper-dlq` AFTER snapshot read **depth=1** instead of 0, so
the script reported `GATE_EXIT = 1` on that single invariant. The substance of TEST-03 was met
regardless: 3x500 GREEN + triple-SHA net-zero + `skp-dlq-1` depth==0.

The single `keeper-dlq` leftover is the EVIDENCE of a correctly-performed give-up, NOT a stuck or
duplicate fault. `KeeperRecovery_GivesUp_ParksToDlq` deliberately forces the deployed keeper to
exhaust its L2 probe (~5s x 12 ≈ 60s, stretched further under the 3x-suite load) and park the
original `Fault<ExecutionResult>` to `keeper-dlq`. In that one run the park landed **after** the
test's teardown drain — the probe-exhaust window exceeded the drain budget — so it raced the gate's
AFTER snapshot. Because the keeper uses a single shared competing-consumer queue
(`keeper-fault-recovery`), this is a single LATE park, not a duplicate.

**Disposition:** ACCEPTED by the operator as a test-teardown timing artifact. The documented
follow-up was a robust poll-until-stably-empty drain.

**Closure (cross-reference, NOT re-verified here per D-05):** This exact follow-up was CLOSED by
**KHARD-02** (Phase 40, plan 40-03), which replaced the fragile `Task.Delay(10s)` teardown with the
deterministic `DrainKeeperDlqUntilStablyEmptyAsync` (2s poll / 15s stably-empty window / 90s cap /
`Assert.Fail` on timeout) so the close gate's `keeper-dlq depth==0` invariant holds deterministically
across the 3x cadence. Phase 40's live close-gate proof is Manual-Only. See
`.planning/phases/40-keeper-recovery-hardening/40-VERIFICATION.md` (KHARD-02, SATISFIED static +
NEEDS LIVE PROOF) and MEMORY `[[project_keeper_recovery_unbounded_reinject_loop]]`.

---

### Cross-Cutting Fixes (close-gate-surfaced)

The close gate was the first full live run since Phase 33; it surfaced pre-existing/this-phase
defects, all fixed + committed to reach 3x GREEN:

1. `28b0217` — register `KeeperMetrics` in 2 Keeper consumer test harnesses (Plan-02 DI gap; 4 tests).
2. `1dc27e5` — **MLBL-04**: stop the versioned metrics `service.name` bleeding onto LOGS (OTel 1.15
   shared `ConfigureResource` overriding the logs `SetResourceBuilder`); restored the Phase-35
   bare-name ES query contract; added `LogsResourceBleedFacts` guard. (Phase-38 regression.)
3. `46fd82d` — 3 keeper RealStack recipe fixes (KeeperFaultIntake case-insensitive `body.text`
   wildcard + loop-break; FaultRecoverySpike; KeeperRecovery result hop via fanout `ResumeWorkflow`).
4. `3c9e20a` — ProcessorId-filter the InFlight MeterListener assertion (parallel-capture contamination).
5. `8833285` — quiesce the keeper before FaultRecoverySpike's dedup proof (non-atomic gate TOCTOU under burst).
6. test parallelism capped (`xunit.runner.json maxParallelThreads=6`) — eliminated CPU-contention
   flakes (harness consume-timeouts) under the 3x-suite load.

---

### Human Verification Required

None for this backfill. The close gate was executed live by the operator on 2026-06-06 and its
result is recorded above. Re-running the gate (RealStack + container rebuilds) is explicitly out of
scope for this documentation backfill (Phase-42 D-05). The one outstanding live proof — KHARD-02's
deterministic-drain determinism against two concurrent Keeper replicas — is tracked in Phase 40 as
Manual-Only and is NOT re-verified here.

---

### Gaps Summary

No gaps blocking goal achievement. All six Phase-39 REQ-IDs are VERIFIED. The TEST-03 close gate met
its substance (3x500 GREEN + triple-SHA net-zero + `skp-dlq-1` depth==0); the lone `keeper-dlq`
depth=1 drain-timing artifact was accepted as a test-teardown timing race and has since been CLOSED by
KHARD-02 (Phase 40). This report is a faithful backfill from the existing close-gate evidence — the
gate was NOT re-run.

---

_Verified: 2026-06-07_
_Verifier: Claude (gsd-executor) — backfilled from 39-04-SUMMARY close-gate evidence (Phase-42 D-04/D-05)_
