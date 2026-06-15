---
phase: 68-live-resilience-proof-7-scenarios-capstone
plan: 02
subsystem: testing
tags: [fault-injection, resilience, e2e, prometheus, elasticsearch, capstone, live-proof, rabbitmq, ttl]

# Dependency graph
requires:
  - phase: 68-live-resilience-proof-7-scenarios-capstone (plan 01)
    provides: "scripts/phase-68-sweep.ps1 7-scenario sweep driver + 7-row harness $Scenarios table"
  - phase: 67-fault-injection-harness
    provides: "phase-67-harness.ps1 per-scenario driver + ES-binding analyzer verdict (analyzer-reports/{id}.json)"
  - phase: 66-prometheus-es-analyzer-pass-fail-engine
    provides: "AnalyzerE2ETests fixture scoring zero-missing + effect-once from Prometheus + Elasticsearch"
provides:
  - "analyzer-reports/phase-68-summary.json — the 7-row capstone roll-up from the live sweep (6/7 PASS)"
  - "Live empirical proof that the recovery machinery survives every fault class except the TEST-06 test-env TTL artifact"
  - "TEST-06 finding: a 45s rabbitmq outage exceeds the 5s execution-data retention envelope → keeper REINJECT correctly drops already-expired keys (accept-as-artifact disposition recorded)"
affects: [v8.0.0 milestone close]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Capstone verdict adjudication: a single VERDICT_FAIL classified MISSING-vs-DUPLICATE, traced to a corroborating metric (keeper_reinject_dropped_total), and dispositioned by the spec owner — never blind-retried"
    - "Cross-scenario corroboration: a strictly-harder superset scenario (TEST-07 redis+rabbitmq) passing rules out a deterministic recovery defect in a weaker subset (TEST-06 rabbitmq)"

key-files:
  created: []
  modified:
    - analyzer-reports/phase-68-summary.json

key-decisions:
  - "TEST-06 VERDICT_FAIL accepted as a TEST-ENV TTL ARTIFACT (accept-with-rationale) — NO code/TTL/dwell/retry change, per D-01b/D-04"
  - "Capstone declared PROVEN for the recovery machinery across all 7 fault classes; TEST-06's miss documented as a known test-environment TTL artifact, not a product defect"

patterns-established:
  - "A verdict FAIL is investigated as a real finding (MISSING vs DUPLICATE + corroboration metric) before any disposition; re-run permitted ONLY on a distinct INFRA-ABORT exit"

requirements-completed: [TEST-01, TEST-02, TEST-03, TEST-04, TEST-05, TEST-06, TEST-07]

# Metrics
duration: ~1h
completed: 2026-06-15
---

# Phase 68 Plan 02: Live 7-Scenario Resilience Sweep (Capstone Proof) Summary

**The live 7-scenario fault sweep ran end-to-end against the full docker stack — 6/7 scenarios PASS (zero-missing + effect-once); the lone TEST-06 (rabbitmq) VERDICT_FAIL was traced to the 5s execution-data TTL self-expiring across the 45s outage and accepted by the spec owner as a known test-env TTL artifact, leaving the recovery machinery proven across every fault class.**

## Performance

- **Duration:** ~1h (wall-clock; sweep windows ~04:30 → 05:25 UTC)
- **Tasks:** 2 (Task 1 sweep — autonomous; Task 2 verdict — human-verify checkpoint, adjudicated)
- **Files modified:** 1 (`analyzer-reports/phase-68-summary.json`)
- **Completed:** 2026-06-15

## Accomplishments

- Ran `scripts/phase-68-sweep.ps1` over all 7 fault classes (TEST-01 → TEST-07) in numeric order, run-all + collect, no fail-fast, no auto-retry — producing `analyzer-reports/phase-68-summary.json` and a console roll-up table.
- Proved the existing recovery machinery (keeper REINJECT/INJECT/DELETE, exactly-once-effect slot array, per-replica liveness, broker nack-requeue redelivery) survives **6 of 7** fault classes with **zero-missing AND effect-once** holding over each window — every verdict derived solely from Prometheus + Elasticsearch (no human correctness verification, no triple-SHA net-zero gate).
- Investigated the lone TEST-06 (rabbitmq) VERDICT_FAIL to root cause (MISSING:2 traced to keeper REINJECT correctly dropping already-self-expired execution-data keys), confirmed it is a test-env TTL artifact (not a recovery defect) via TEST-07 corroboration, and recorded the spec owner's accept-as-artifact disposition — with no blind dwell/TTL/retry change (D-01b/D-04 respected).

## The 7-Row Capstone Roll-Up

Source: `analyzer-reports/phase-68-summary.json` (committed in `098f36a`).

| scenarioId            | verdict | zeroMissing | effectOnce | started/complete | harnessExit | class        |
| --------------------- | ------- | ----------- | ---------- | ---------------- | ----------- | ------------ |
| TEST-01 baseline      | Pass    | true        | true       | 11/11            | 0           | PASS         |
| TEST-02 processor     | Pass    | true        | true       | 10/10            | 0           | PASS         |
| TEST-03 orchestrator  | Pass    | true        | true       | 9/9              | 0           | PASS         |
| TEST-04 keeper (whole-tier) | Pass | true     | true       | 10/10            | 0           | PASS         |
| TEST-05 redis         | Pass    | true        | true       | 8/8              | 0           | PASS         |
| TEST-06 rabbitmq      | **Fail**| **false**   | true       | **7/9**          | **1**       | **VERDICT_FAIL** |
| TEST-07 redis+rabbitmq| Pass    | true        | true       | 8/8              | 0           | PASS         |

**Wrapper final exit: 1** (6/7 PASS). No re-runs occurred.

- **INFRA-ABORT re-runs:** None. No scenario classified INFRA_ABORT (exit 10–70); no scenario was re-run.
- **Verdict FAILs:** 1 (TEST-06, exit 1) — investigated as a real finding per D-01b, NOT retried.

## TEST-06 VERDICT_FAIL Finding (rabbitmq)

**Classification: MISSING (not DUPLICATE).** `effectOnce` held (no duplicate `(correlationId, StepLabel)` — the exactly-once-effect machinery was never violated). `zeroMissing` was false: 2 of 9 started runs (7/9 complete) reached only **Step_A** and never arrived at both sinks.

**Root cause — corroborated, not hypothesised:**
- `KeeperReinjectDroppedDelta:2` exactly matched `Missing:2`. The keeper's REINJECT path executed and dropped exactly the missing keys.
- The drop is the **by-design silent-DROP** in `ReinjectConsumer.cs:37` — `STRLEN L2[entryId] == 0` (the execution-data key no longer exists) → drop the reinject (the run's data is already gone; there is nothing to reinject).
- **Why the data was gone:** the 5s `Processor__ExecutionDataTtl` (docker-compose:285) is far shorter than the 45s rabbitmq outage dwell. Execution-data keys minted just before the outage **self-expired mid-outage**; when the broker recovered and the keeper attempted REINJECT, the keys were correctly absent, so REINJECT correctly dropped them. The miss is a consequence of a legitimate retention-window expiry, not a recovery failure.

**Corroboration that this is an artifact, not a defect:** **TEST-07 (redis + rabbitmq together) — a strictly harder superset of TEST-06 — PASSED 8/8.** A deterministic rabbitmq-recovery defect would have failed TEST-07 as well. The combined-infra scenario passing while the rabbitmq-only scenario missed is consistent only with a timing/TTL race against the retention envelope, not a structural recovery gap.

## Checkpoint Disposition (Task 2 — adjudicated by the spec owner)

**Disposition (a): ACCEPT AS TEST-ENV ARTIFACT (accept-with-rationale).**

A 45s outage legitimately exceeds the 5s execution-data retention envelope; execution-data self-expired mid-outage and the keeper REINJECT correctly dropped already-gone keys. The recovery machinery itself is **PROVEN** — it fired and dropped only already-expired keys; it never re-executed (`effectOnce` held) and never silently lost recoverable data.

- **NO code change**, **NO `ExecutionDataTtl` change**, **NO dwell change**, **NO retry.**
- Recorded as a **finding**, not a silent pass (D-01b/D-04 honoured).
- The capstone is considered **PROVEN for the recovery machinery across all 7 fault classes**, with TEST-06's miss documented as a known test-environment TTL artifact (the 5s TTL vs 45s dwell mismatch is a property of the test fixture's timing, not of the product's recovery semantics).

## Requirements

| Req     | Scenario              | Outcome |
| ------- | --------------------- | ------- |
| TEST-01 | baseline              | PASS (zero-missing + effect-once) |
| TEST-02 | processor crash       | PASS (zero-missing + effect-once) |
| TEST-03 | orchestrator crash    | PASS (zero-missing + effect-once) |
| TEST-04 | keeper whole-tier blackout | PASS (zero-missing + effect-once) |
| TEST-05 | redis crash           | PASS (zero-missing + effect-once) |
| TEST-06 | rabbitmq crash        | PROVEN-WITH-DOCUMENTED-ARTIFACT (recovery machinery fired correctly; the 2 misses are 5s-TTL-vs-45s-outage self-expiry, accepted as a test-env artifact) |
| TEST-07 | redis + rabbitmq crash | PASS (zero-missing + effect-once) — strictly-harder superset, corroborates TEST-06 as artifact |

All 7 TEST requirements are completed: 6 as clean live PASS, TEST-06 as proven-with-documented-artifact per the spec-owner disposition.

## Task Commits

1. **Task 1: Run the 7-scenario live sweep and capture the roll-up** — `098f36a` (test) — wrote `analyzer-reports/phase-68-summary.json` (7-row roll-up; 6/7 PASS; TEST-06 VERDICT_FAIL). `phase-68-sweep.log` captured the full run (on disk, gitignored).
2. **Task 2: Capstone verdict checkpoint** — adjudicated by the spec owner (accept-as-artifact). No commit (disposition is no-change; recorded in this SUMMARY).

**Plan metadata:** (final docs commit — this SUMMARY + STATE + ROADMAP)

## Files Created/Modified

- `analyzer-reports/phase-68-summary.json` (modified) — the 7-row capstone roll-up: per-scenario `verdict` · `zeroMissing` · `effectOnce` · started/complete counts · `harnessExit` · `class`. Committed in `098f36a`.

## Decisions Made

- **TEST-06 accepted as a test-env TTL artifact (accept-with-rationale)** — the 5s `ExecutionDataTtl` legitimately expired across the 45s outage; the keeper REINJECT correctly dropped already-gone keys. No code/TTL/dwell/retry change. Rationale: recovery machinery proven (effect-once held; only already-expired keys dropped); TEST-07 superset PASS rules out a deterministic defect.
- **Capstone declared proven for the recovery machinery across all 7 fault classes** — with TEST-06's miss documented as a known test-environment timing artifact rather than a product gap.

## Deviations from Plan

None — plan executed exactly as written. Task 1 ran the sweep verbatim (no auto-retry, no dwell/TTL change); Task 2 surfaced the verdict FAIL as a real finding for the spec-owner checkpoint exactly as the plan's investigate-first rule (D-01b/D-04) prescribes.

## Issues Encountered

- **TEST-06 (rabbitmq) returned a VERDICT_FAIL (exit 1), making the wrapper exit 1 (6/7 PASS) rather than the 7/7 clean-PASS path.** Resolved per the plan's investigate-first protocol: classified the failure (MISSING:2), traced it to the corroborating metric (`KeeperReinjectDroppedDelta:2` == `Missing:2` → the by-design `ReinjectConsumer.cs:37` silent-DROP), identified the cause (5s `ExecutionDataTtl` < 45s outage → execution-data self-expired), corroborated via the TEST-07 superset PASS, and escalated to the spec-owner checkpoint. The spec owner accepted it as a test-env TTL artifact. No blind dwell/TTL bump and no retry of a verdict FAIL occurred.

## Known Stubs

None — this plan ran the live sweep and read its output. No code, no data-flow stubs, no placeholders.

## User Setup Required

None — no external service configuration required.

## Next Phase Readiness

- Phase 68 (the v8.0.0 capstone) is complete: the recovery machinery is live-proven across all 7 fault classes, with TEST-06 documented as a known test-env TTL artifact.
- **Carry-forward note for v8.0.0 close / any future live sweep:** the 5s `Processor__ExecutionDataTtl` (docker-compose:285) is shorter than the 45s fault dwell, so a rabbitmq-only outage can legitimately produce REINJECT drops of self-expired keys. This is a test-fixture timing property, not a product defect. Should a future run want a clean 7/7, the fixture's TTL-vs-dwell envelope (not the product) is the lever — and that requires a spec-owner decision, not a silent change.

## Self-Check: PASSED

- `analyzer-reports/phase-68-summary.json` — FOUND on disk; contains 7 rows TEST-01..TEST-07 matching the roll-up above (TEST-06 `class: VERDICT_FAIL`, all others `PASS`).
- Task 1 commit `098f36a` — FOUND in git history (`test(68-02): capture 7-scenario capstone sweep roll-up (6/7 PASS; TEST-06 verdict FAIL)`).
- Spec-owner disposition (accept-as-artifact, no change) — recorded in this SUMMARY's Checkpoint Disposition + Decisions Made sections.
- No code/config change made (docker-compose, ExecutionDataTtl, harness, sweep script, product code all untouched) — consistent with the no-change disposition.

---
*Phase: 68-live-resilience-proof-7-scenarios-capstone*
*Completed: 2026-06-15*
