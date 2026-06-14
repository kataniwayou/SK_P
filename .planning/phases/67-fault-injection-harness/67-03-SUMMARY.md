---
phase: 67-fault-injection-harness
plan: 03
subsystem: testing
tags: [fault-injection, harness, docker, prometheus, elasticsearch, analyzer, observability, live-proof]

# Dependency graph
requires:
  - phase: 67-fault-injection-harness (plan 01)
    provides: "D-16 env-var seam (SCENARIO_ID / WINDOW_START_UTC / WINDOW_END_UTC) in AnalyzerE2ETests"
  - phase: 67-fault-injection-harness (plan 02)
    provides: "scripts/phase-67-harness.ps1 — the -ScenarioId fault-injection orchestrator (clean→seed→activate→observe→crash→recover→analyze→teardown)"
  - phase: 66-analyzer
    provides: "PassFailEngine + AnalyzerReport + AnalyzerE2ETests — the scored PASS/FAIL verdict the harness invokes"
  - phase: 65-fan-out-workflow-seeder-clean-state-stack
    provides: "phase-65-up.ps1 bring-up + phase-65-reset.ps1 clean-state + FanOutSeeder fixture"
provides:
  - "Live end-to-end proof of the harness on the two reference runs (TEST-01 baseline, TEST-02 processor whole-tier crash + in-window recovery), each producing a correctly-named analyzer-reports/{id}.json with a Pass verdict"
  - "OBS-04 verdict-logic correction: ES-started-runs as the binding denominator; Prometheus demoted to non-fatal corroboration (absorbs the mid-window Prom counter-reset signature)"
  - "STEP A0 rebuild + --force-recreate in the harness (SourceHash currency — fixes the stale-image 422 gate)"
  - "Time-pinned analyzer Prom reads bounded to the harness window (full ~10-fire cohort, not a ~60s tail)"
affects: [68-live-resilience-proof]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "ES-binding verdict arbiter: the started-runs denominator comes from Elasticsearch (distinct correlationIds with ≥1 Step_* log); Prometheus is non-fatal corroboration (±1-run boundary tolerance), so a mid-window Prom counter reset on a tier restart cannot fail the verdict"
    - "Instant-query-at-time Prom reads (/api/v1/query?time=) pinned to the harness window so the dispatch delta spans the full cohort regardless of when the analyzer fixture launches"
    - "Image-currency guard: docker compose build (A0) + --force-recreate before the proof so the running container's assembly-embedded SourceHash matches the host-built Processor.Sample the seeder binds (avoids a self-inflicted liveness 422)"

key-files:
  created:
    - .planning/phases/67-fault-injection-harness/67-03-SUMMARY.md
  modified:
    - scripts/phase-67-harness.ps1
    - tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs
    - tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs
    - tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs
    - tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs
    - tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs

key-decisions:
  - "OBS-04 verdict logic re-founded on an ES-binding arbiter (started runs = distinct correlationIds with ≥1 Step_* log; missing = started − complete; duplicates fail-closed). This CORRECTS a Phase-66 engine defect — per-step dispatch_sent used as a per-run denominator — surfaced under Phase 67's first real fan-out load. Approved by the spec owner as a verdict-semantics change."
  - "Prometheus demoted from binding denominator to non-fatal corroboration. A tier crash resets the processor's process-local counters mid-window; that counter-reset signature is now expected and absorbed (impliedRuns = round(DispatchSentDelta/9) vs started, ±1 tolerance), never a verdict failure."
  - "Harness STEP A0 rebuild + --force-recreate added so the live proof runs against a container whose SourceHash matches the seeded workflow's processor binding (the stale image self-registered a different processor id → correct-but-fatal liveness 422)."
  - "Analyzer Prom reads time-pinned to WINDOW_START_UTC..WINDOW_END_UTC (instant-query-at-time) so DispatchSentDelta spans the full ~10-fire cohort, not the ~60s tail between a late fixture launch and the AFTER read. Standalone Phase-66 path (env-absent) preserved byte-for-byte."

patterns-established:
  - "Live-proof deviations are committed atomically and the verdict logic is allowed to be corrected under real load — the harness's correctness is producing a defensible verdict, not its initial value (D-11)."

requirements-completed: [FAULT-01, FAULT-02, FAULT-03]

# Metrics
duration: ~3h (two ~50-min live runs + three atomic deviation fixes + re-runs)
completed: 2026-06-14
---

# Phase 67 Plan 03: Live Proof of the Fault-Injection Harness Summary

**The fault-injection harness was proven end-to-end on both reference runs — TEST-01 (no-fault baseline) and TEST-02 (whole-tier processor crash + in-window recovery) each scored Pass (10/10 started runs complete, zero missing, zero duplicates) fully automated — and along the way the OBS-04 verdict logic was re-founded on an ES-binding arbiter, correcting a Phase-66 per-run-denominator defect surfaced under the first real fan-out load.**

## Performance

- **Duration:** ~3h wall (two live runs at ~50 min each — 5-min window + bring-up/rebuild/reset/drain — plus three atomic deviation fixes and re-runs)
- **Completed:** 2026-06-14
- **Tasks:** 2 of 2 checkpoints approved (both reference runs)
- **Files modified:** 6 (1 harness script, 5 analyzer/test files); no product `src/**` changed

## Accomplishments

- **TEST-01 baseline — Pass.** The harness ran clean→seed→activate(204)→observe→analyze→teardown with no human step and wrote a correctly-named `analyzer-reports/TEST-01.json`: `Verdict=Pass`, `StartedRuns=10`, `CompleteRuns=10`, `Missing=0`, `Duplicates=[]`, `Reconciliation=Reconciled`. All 10 traces show the full fan-out (A→B→C→{D1→E1→F1, D2→E2→F2}). This proved the harness wiring and the D-16 window handoff bounded the full ~10-fire cohort.
- **TEST-02 processor whole-tier crash — Pass via genuine tier-crash recovery.** The harness stopped the whole `processor-sample` tier mid-window, dwelt past a 30s cron interval, restarted it, waited both replicas healthy before window close (no exit-60 abort), drained, and wrote `analyzer-reports/TEST-02.json`: `Verdict=Pass`, `StartedRuns=10`, `CompleteRuns=10`, `Missing=0`, `Duplicates=[]`, `Reconciliation=Reconciled`. Every started run reached both sinks despite the mid-window tier crash — a real recovery proof.
- **Prom counter-reset signature correctly absorbed.** TEST-02's report shows the restart's process-local counter reset (`DispatchConsumedDelta=63`, `ResultSentCompletedDelta=63` vs `DispatchSentDelta=81`) — the processor counters reset on restart while the orchestrator's did not. The ES-binding arbiter scored the verdict from ES started/complete runs and treated the Prom delta as corroboration only (`PromImpliedRuns=9`, within ±1-run tolerance), so the expected counter reset did NOT fail the run.
- **Both runs fully automated** (FAULT-03) — no Read-Host, no manual intervention after launch.
- The 67-01 seam and the 67-02 harness were proven by these runs; the substantive find was the analyzer verdict-logic correction (below).

## Task Commits

This plan's two tasks are checkpoint approvals (each ran the harness; the harness writes its report to the test bin dir, not the repo). The work committed during the plan was the cross-plan deviations required to make the live proof defensible — each committed atomically:

1. **`d2445fb`** (fix) — STEP A0 rebuild + `--force-recreate` in the harness (SourceHash currency).
2. **`a9e42e0`** (fix) — time-pin analyzer Prom reads to the harness window (OBS-04 denominator).
3. **`574739a`** (fix) — re-found OBS-04 verdict on the ES-binding arbiter (Prom corroborating).

**Plan metadata:** (this SUMMARY + tracking updates — final docs commit)

## Files Created/Modified

- `scripts/phase-67-harness.ps1` — added STEP A0 `docker compose build` before bring-up and `--force-recreate` in STEP A (`d2445fb`).
- `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` — window-pinned Prom instant-queries when the D-16 seam is present; ES-binding wiring (`a9e42e0`, `574739a`).
- `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` — optional `evalTime` param for instant-query-at-time (`a9e42e0`).
- `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` — ES-started-runs binding denominator; Prom demoted to non-fatal corroboration (`574739a`).
- `tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs` — added `StartedRuns`, `PromImpliedRuns`, `CorroborationDetail`; updated `HumanSummary` (`574739a`).
- `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs` — updated to ES-binding semantics (7 facts green) (`574739a`).

## Decisions Made

- **OBS-04 verdict re-founded on an ES-binding arbiter.** The binding denominator is now ES *started runs* (distinct correlationIds with ≥1 `Step_*` log); missing = started − complete; duplicates remain a fail-closed condition. Three Prometheus conflations were retired from the binding gate — (#1) per-step `dispatch_sent` used as a per-run denominator, (#2) `result_sent ≥ complete × 9`, (#3) `|dispatch − trigger|` — and now survive only as corroboration math. This is a **correction of a Phase-66 engine defect** (per-step `dispatch_sent` used as a per-run denominator) that only manifested under Phase 67's first real fan-out load. Approved by the spec owner as a verdict-semantics change.
- **Prometheus is non-fatal corroboration, not the arbiter.** `PromImpliedRuns = round(DispatchSentDelta / 9)` is compared to ES started runs with a ±1-run boundary tolerance; dead-run and terminal-outcome mismatches surface as warnings, never verdict failures. This is precisely what lets a mid-window tier-restart counter reset (TEST-02's 63-vs-81 deltas) be absorbed.
- **Harness image currency.** `phase-65-up.ps1` never rebuilds; a stale image self-registered L2 liveness under a different processor id than the seeded `v8-fanout-proof` workflow binds, so the `ProcessorLivenessValidator` correctly (but fatally) 422'd `POST /start` with 0 matching replicas. STEP A0 `build` + STEP A `--force-recreate` make the running container's SourceHash match the host build.
- **Window-pinned Prom reads.** With the D-16 seam present, both Prom counter sets are instant-queried at `windowStart`/`windowEnd` so the dispatch delta spans the full cohort. The standalone Phase-66 path (env-absent) is preserved byte-for-byte (live before/after reads, `evalTime=null`).

## Deviations from Plan

This plan's job was to *run* the already-authored harness; the plan declared no file modifications. Three cross-plan deviations were required to make the live proof defensible, each committed atomically (D-11: a non-trivial finding under load is real work, not a harness defect).

### Auto-fixed Issues

**1. [Rule 3 - Blocking] STEP A0 rebuild + --force-recreate (stale-image 422)**
- **Found during:** Task 1 (TEST-01 first launch) — `POST /start` 422'd because the running container's processor id (stale image) did not match the seeded workflow's processor binding.
- **Issue:** `phase-65-up.ps1` never rebuilds; the running container's assembly-embedded SourceHash diverged from the host-built `Processor.Sample` the seeder reflects, so it self-registered L2 liveness under a different processor id and the liveness gate correctly returned 0 matching replicas. Observed live: container `536d0868` (proc `2f6f59b0`) vs host build `a67a3ed8` (proc `3cf7023b`).
- **Fix:** Added STEP A0 `docker compose build` before bring-up and `--force-recreate` in STEP A so the freshly-built image is the one running.
- **Files modified:** `scripts/phase-67-harness.ps1`
- **Verification:** Re-run reached the 204 gate and produced TEST-01.json.
- **Committed in:** `d2445fb`

**2. [Rule 1 - Correctness] Time-pin analyzer Prom reads to the harness window (OBS-04 denominator)**
- **Found during:** Task 1 (TEST-01) — `TriggerCount` reflected only a ~60s tail between the late analyzer-fixture launch and the AFTER read, not the full ~10-fire cohort.
- **Issue:** The analyzer's live before/after counter reads were anchored to fixture lifecycle, not the harness window, so `DispatchSentDelta` undercounted the cohort.
- **Fix:** When both `WINDOW_*_UTC` parse, instant-query both Prom counter sets pinned to `windowStart`/`windowEnd` (`/api/v1/query?time=`). Standalone Phase-66 path kept byte-for-byte (`evalTime=null`).
- **Files modified:** `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs`, `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs`
- **Verification:** TEST-01.json `DispatchSentDelta=81` spans the full cohort; verdict semantics unchanged.
- **Committed in:** `a9e42e0`

**3. [Rule 1 - Correctness] Re-found OBS-04 verdict on an ES-binding arbiter (corrects a Phase-66 defect)**
- **Found during:** Task 2 (TEST-02) — the mid-window tier-restart counter reset surfaced a Reconciliation discrepancy under the original Prom-denominator engine; investigation (per D-11) traced it to a Phase-66 verdict-logic defect: per-step `dispatch_sent` was being used as a per-run denominator, which only breaks under real fan-out (9 steps/run) at scale.
- **Issue:** Using Prometheus per-step counters as the per-run denominator both miscounts (×9) and is fragile to a legitimate mid-window counter reset on a tier restart.
- **Fix:** Bind the verdict to ES started runs (distinct correlationIds with ≥1 `Step_*` log); missing = started − complete; duplicates fail-closed. Demote Prom to non-fatal corroboration (`PromImpliedRuns` vs started, ±1-run tolerance). Added `StartedRuns`/`PromImpliedRuns`/`CorroborationDetail` to the report; updated `PassFailEngineFacts` (7 facts green); build `-warnaserror` 0/0.
- **Files modified:** `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs`, `.../AnalyzerReport.cs`, `.../PassFailEngineFacts.cs`, `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs`
- **Verification:** Both TEST-01 and TEST-02 re-ran to `Verdict=Pass` with the counter-reset signature (TEST-02: 63-vs-81 deltas) correctly absorbed (`Reconciliation=Reconciled`, `PromImpliedRuns=9`). Spec owner approved the verdict-semantics change.
- **Committed in:** `574739a`

---

**Total deviations:** 3 auto-fixed (1 blocking, 2 correctness — one of which corrects a prior-phase engine defect).
**Impact on plan:** All three were necessary to produce a defensible live verdict. No scope creep — no new product code; the analyzer correction is a verdict-semantics fix the spec owner explicitly approved. The harness/seam (67-01, 67-02) were proven; the analyzer verdict-logic correction was the substantive find.

## Authentication Gates

None — all operations are local (docker, loopback HTTP, container-side psql, Prometheus/Elasticsearch on localhost).

## Known Stubs

None. Both reference runs ran the full pipeline end-to-end and produced real verdicts from live Prometheus + Elasticsearch data.

## Issues Encountered

- The original (Phase-66) verdict engine used a Prometheus per-step counter as a per-run denominator; this was latent until Phase 67's first real fan-out load (9 steps/run) and a mid-window counter reset exposed it. Resolved by re-founding the verdict on the ES-binding arbiter (deviation #3) — investigated as a finding per D-11, not papered over.
- The stale-image liveness 422 (deviation #1) was a correct gate firing on a real divergence, not an analyzer bug; fixed at the source (rebuild + force-recreate).

## Next Phase Readiness

- **Ready for Phase 68:** the harness is proven end-to-end on both reference runs, the verdict engine is now ES-binding (robust to mid-window counter resets), and the `[ordered]` scenario table is the data-only seam for rows TEST-03..TEST-07. Each remaining scenario is now a table-row addition + a live run.
- **FAULT-01/02/03 genuinely met** by this live end-to-end proof, finalizing the re-opening from 67-01's `2b5cec9`.

## Self-Check: PASSED

- `.planning/phases/67-fault-injection-harness/67-03-SUMMARY.md` — FOUND.
- `analyzer-reports/TEST-01.json` (test bin dir) — FOUND: `Verdict=Pass`, `StartedRuns=10`, `CompleteRuns=10`, `Missing=0`.
- `analyzer-reports/TEST-02.json` (test bin dir) — FOUND: `Verdict=Pass`, `StartedRuns=10`, `CompleteRuns=10`, `Missing=0`, counter-reset deltas (63 vs 81) Reconciled.
- Commit `d2445fb` (deviation 1) — FOUND in git log.
- Commit `a9e42e0` (deviation 2) — FOUND in git log.
- Commit `574739a` (deviation 3) — FOUND in git log.

All claims verified against the live analyzer reports and git history.

---
*Phase: 67-fault-injection-harness*
*Completed: 2026-06-14*
