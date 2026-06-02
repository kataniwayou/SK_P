---
phase: 28-sourcehash-identity-processor-sample-e2e-closeout
plan: 04
subsystem: testing
tags: [close-gate, triple-sha, 3-green, processor-sample, steady-state, closeout, regression-fix]

# Dependency graph
requires:
  - phase: 28-sourcehash-identity-processor-sample-e2e-closeout
    plan: 01
    provides: "SourceHash.targets + Processor.Sample concrete — the genuine embedded 64-hex identity the gate's steady-state seed registers"
  - phase: 28-sourcehash-identity-processor-sample-e2e-closeout
    plan: 02
    provides: "processor-sample Dockerfile + compose tier (the container the gate adds to the $services pre-flight) + PROVEN cross-OS SourceHash reproducibility"
  - phase: 28-sourcehash-identity-processor-sample-e2e-closeout
    plan: 03
    provides: "SampleRoundTripE2ETests — the live round-trip proof that runs (live) in each of the gate's 3 full-suite GREEN runs; establishes the steady-state stable Processor row + skp:{procId:D} + queue:{procId:D}"
provides:
  - "scripts/phase-28-close.ps1 — the Phase 28 triple-SHA / 3-consecutive-GREEN close gate (mirrors phase-22-close.ps1 with processor-sample added to the $services pre-flight + the steady-state stable-processor-row resolution)"
  - "Phase 28 close-gate evidence: 395 facts GREEN x3 (full suite, RealStack live) + triple-SHA BEFORE==AFTER held + zero-warning Release+Debug — TEST-02 satisfied, milestone v3.5.0 close-gate discipline retained"
affects: [milestone-v3.5.0-closeout]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Phase-close gate (unchanged discipline since Phase 3 D-15/D-18): 3-consecutive-GREEN full-suite + triple-SHA (psql -lqt / redis-cli --scan / rabbitmqctl list_queues) BEFORE==AFTER + zero-warning Release+Debug; exit 0 = all green + SHAs held, 1 = invariant/RED/unparseable, 2 = stack unhealthy"
    - "Steady-state stable Processor row (Open Q1/Q2 resolution): GET-or-create on the unique source-hash makes the live processor-sample's skp:{procId:D} liveness key + {procId:D} dispatch queue STEADY-STATE — present in BOTH the BEFORE and AFTER snapshots, so the redis --scan + rabbitmq list_queues SHAs hold across the 3-run gate"
    - "processor-sample is REQUIRED healthy at pre-flight (NOT added to the otel-collector health-exception clause) — a health exception would let the gate pass with a dead processor and defeat its purpose"

key-files:
  created:
    - scripts/phase-28-close.ps1
  modified:
    - tests/BaseApi.Tests/Orchestrator/CorrelationPropagationE2ETests.cs
    - tests/BaseApi.Tests/.../ConcurrencyTokenTests + the /test/concurrency harness endpoint

key-decisions:
  - "Open Q1/Q2 RESOLVED in-script: the gate requires a stable Processor row (GET-or-create on the unique source-hash) and REQUIRES processor-sample healthy at pre-flight (NOT a health exception). This keeps the new skp:{procId:D} + {procId:D} steady-state across all three runs so the redis/rabbitmq SHAs hold; the row is never torn down (psql -lqt is db-list level, so it does not move the postgres SHA)."
  - "The steady-state procId reused across all 3 runs is 4315324c-8c3f-4a24-984d-f78d63ce499e — its skp:{procId} liveness key + {procId} dispatch queue appear in both the BEFORE and AFTER snapshots; the E2E's idempotent GET-or-create resolves THIS same row each run so the id never churns."
  - "The close gate was the first full live suite run since feat(24.1); it surfaced two prior-phase test defects (a stale seam assertion + a flaky racing-writes assertion) that had to be fixed to reach 3x GREEN. Both fixes are committed (13400f7, 327b1bb) and recorded here as gate findings — exactly the regression-surfacing function the close gate exists to perform."

requirements-completed: [TEST-02]

# Metrics
duration: ~operator-run (gate authored + run across multiple full-suite live passes)
completed: 2026-06-02
---

# Phase 28 Plan 04: Phase-28 Close Gate (3-GREEN + Triple-SHA) Summary

**`scripts/phase-28-close.ps1` is the Phase 28 close gate — the proven phase-22 triple-SHA / 3-consecutive-GREEN discipline extended for the new steady-state `processor-sample` container: it adds `processor-sample` to the `$services` pre-flight health list, resolves the chicken-and-egg by requiring a stable Processor row (GET-or-create on the unique source-hash, so the live container's `skp:{procId:D}` liveness key + `{procId:D}` dispatch queue are steady-state in BOTH snapshots), then captures triple-SHA BEFORE, runs zero-warning Release+Debug builds, runs the full suite 3 consecutive times (RealStack live each run), captures triple-SHA AFTER, and asserts BEFORE==AFTER. The operator ran it and it PASSED, exit 0: 395 facts GREEN x3 + triple-SHA held + zero-warning builds. Reaching 3x GREEN required two prior-phase regression fixes (a stale seam assertion + a flaky racing-writes assertion) the gate surfaced as the first full live suite run since feat(24.1).**

## Accomplishments

- **`scripts/phase-28-close.ps1`** (Task 1, commit `3f8d975`) — copied `scripts/phase-22-close.ps1` verbatim and changed only the label (`Phase 28` / `v3.5.0`), added `'processor-sample'` to the `$services` pre-flight list, and baked in the steady-state stable-Processor-row resolution (Open Q1/Q2). All three SHA captures (`psql -U postgres -lqt`, `redis-cli --scan`, `rabbitmqctl -q list_queues`) intact; no `FLUSHDB`; zero-warning Release+Debug build gate intact; 3-consecutive-GREEN cadence with the `Passed:` parse + distinct-count guard intact; exit codes 0/1/2 intact; BOM-free; parses clean.

## Gate PASS Evidence (Task 2 — operator-authorized, exit 0)

The operator ran `pwsh -File scripts/phase-28-close.ps1` against the full v3.5.0 stack (incl. the live `processor-sample` container). It ran to completion and **exited 0 — Phase 28 close gate PASSED**:

- **3-consecutive-GREEN cadence:** **395 facts GREEN across all 3 full-suite live runs** (Exit=0 each). Full suite, no Category filter — both real-stack E2Es (incl. `SampleRoundTripE2ETests`) ran live in every run.
- **Zero-warning builds:** Release exit 0, Debug exit 0 (0 Warning(s) / 0 Error(s) each).
- **Triple-SHA BEFORE == AFTER — all three HELD:**

  | Snapshot | SHA-256 | Result |
  |----------|---------|--------|
  | `psql \l` | `b48ce78302d9dd8ca93e6a7e694c153dc46705ec9ab4458b31c6933ea2e33fef` | HELD |
  | `redis-cli --scan` | `56e9e516d398a3d1e1e8e55eb5b39e76ddd4d3f4d99abe8f48471b51cdb607a6` | HELD |
  | `rabbitmqctl list_queues` | `67a92f451875a1196ccb20f287fa141b54a6d2357da7969b1825d95ea7058688` | HELD |

- **Steady-state procId (reused, idempotent):** `4315324c-8c3f-4a24-984d-f78d63ce499e` — its `skp:{procId}` liveness key + `{procId}` dispatch queue are steady-state in BOTH snapshots (no churn across the 3 runs), which is exactly why the redis `--scan` and rabbitmq `list_queues` SHAs held.

### Open Q1/Q2 Resolution (in-script)

- **Stable Processor row (Open Q1):** the gate resolves the Processor row via GET-or-create on the unique source-hash, so the same row (and thus the same procId, liveness key, and dispatch queue) is reused across all three runs — keeping the redis/rabbitmq SHAs stable.
- **Pre-flight health (Open Q2):** `processor-sample` is REQUIRED healthy at pre-flight (NOT added to the `otel-collector` health-exception clause). A health exception would let the gate pass with a dead processor and defeat its purpose; requiring health forces the stable row to exist before the gate snapshots.

## Gate Findings / Deviations

The close gate was the **first full live suite run since feat(24.1)**, and it surfaced two prior-phase test defects that had to be fixed to reach 3x GREEN. **Both fixes are already committed by the orchestrator — recorded here as the stale/flaky prior-phase tests the gate caught (this is precisely the regression-surfacing function the close gate exists to perform):**

**1. [Gate finding — stale seam assertion] `CorrelationPropagationE2ETests`**
- **Issue:** feat(24.1) renamed the orchestrator Start seam log from `"Scheduler job start (seam)"` to `"Start reload for WorkflowId={WorkflowId}"` AND the conditionless reload now emits a 2nd correlated orchestrator log (null-cron "skipping hydration"). The test's stale seam constant + un-pinned ES query no longer matched.
- **Fix:** updated the `SeamMessage` constant + pinned the ES query to the seam via a term on `attributes.{OriginalFormat}` so it selects the one seam log deterministically. Verified live 1/1.
- **Files modified:** `tests/BaseApi.Tests/Orchestrator/CorrelationPropagationE2ETests.cs`
- **Commit:** `13400f7`

**2. [Gate finding — flaky assertion] `ConcurrencyTokenTests.Test_RacingWrites_Produce_409`**
- **Issue:** flaky (~1-in-4) — the two concurrent POSTs could serialize by chance, producing no 409 and failing the assertion.
- **Fix:** added an opt-in load→save barrier (`POST /test/concurrency?delayMs=N`) to the test-harness endpoint so both callers reliably load the same `xmin` before either commits, making the 409 race deterministic. Verified 12/12 GREEN.
- **Files modified:** `ConcurrencyTokenTests` + the `/test/concurrency` test-harness endpoint.
- **Commit:** `327b1bb`

> Note: the close gate itself was NOT re-run as part of this finalization — it had already passed (exit 0) under the operator before this continuation. Task 1 was NOT redone; the two fix commits were NOT redone.

## Task Commits

1. **Task 1: author `scripts/phase-28-close.ps1` — triple-SHA gate with processor-sample** — `3f8d975` (feat).
2. **Task 2: run the close gate (operator-authorized, human-verify checkpoint)** — gate exit 0; no code commit (gate-run only). Two regression fixes required to reach 3x GREEN: `13400f7` (seam assertion), `327b1bb` (de-flake 409).

## Threat Surface

- **T-28-11** (stale/duplicate Processor row from a non-idempotent seed) — mitigated: the steady-state row is resolved by GET-or-create on the unique `uq_processor_source_hash`, so the id stayed stable (procId `4315324c-…` reused) across all 3 runs and the redis/rabbitmq SHAs held.
- **T-28-12** (leaked execution-data keys masking under the redis SHA) — mitigated: the redis `--scan` SHA HELD BEFORE==AFTER (`56e9e516…`); no leaked `skp:data:*` (short container ExecutionDataTtl + net-zero E2E teardown), fail-closed as exit 1 had any leaked.
- **T-28-13** (gate run without recorded evidence) — accepted/satisfied: all three SHAs + run counts + procId are recorded above (Phase 21/22 evidence precedent).
- No new threat surface beyond the plan's `<threat_model>`.

## Next Phase Readiness

- **TEST-02 is satisfied** — the phase-close gate retains the 3-consecutive-GREEN cadence + triple-SHA (`psql \l` / `redis-cli --scan` / `rabbitmqctl list_queues`) BEFORE==AFTER discipline with scan-clean teardown covering the new processor-liveness + execution-data keys.
- **Phase 28 = 4/4 plans complete.** All 6 Phase 28 requirements (IDENT-01/02, SAMPLE-01/02, TEST-01/02) are complete. Milestone v3.5.0 = 12/12 plans across phases 25-28; the milestone is ready for closeout.

---
*Phase: 28-sourcehash-identity-processor-sample-e2e-closeout*
*Completed: 2026-06-02*

## Self-Check: PASSED

- Created file `scripts/phase-28-close.ps1` exists on disk (17312 bytes); header line is `# Phase 28 close gate — v3.5.0 (triple-SHA)`; `processor-sample` present in the `$services` pre-flight list.
- Task 1 commit `3f8d975` present in git history; the two gate-finding fix commits `13400f7` + `327b1bb` present in git history (all three verified via `git log --oneline`).
- SUMMARY `28-04-SUMMARY.md` created. Gate PASS evidence recorded (three SHA-256 values + steady-state procId `4315324c-…` + 395 facts GREEN x3 + zero-warning Release+Debug). Open Q1/Q2 resolution + the two regression-fix gate findings documented. The gate was NOT re-run and Task 1 was NOT redone during finalization. TEST-02 satisfied.
