---
phase: 39-keeper-observability-real-stack-e2e-close-gate
plan: 03
subsystem: testing
tags: [prometheus, scrape-assertion, realstack, e2e, keeper, observability, histogram-suffix, cardinality]

# Dependency graph
requires:
  - phase: 39-keeper-observability-real-stack-e2e-close-gate (Plan 02)
    provides: "the eight KeeperMetrics instruments threaded into both fault consumers + the L2 probe loop — emits keeper_* series with {fault_type,outcome,reason,ProcessorId} tags and recovery_duration in seconds (RunAsync(entryId,h,procId,ct))"
  - phase: 39-keeper-observability-real-stack-e2e-close-gate (Plan 01)
    provides: "histogram unit \"s\" decision → Prom name keeper_recovery_duration_seconds_{count,sum,bucket}"
  - phase: 36-l2-health-probe-recovery-loop-dlqs
    provides: "the two EXISTING RealStack facts KeeperRecovery_RecoversBothPaths + KeeperRecovery_GivesUp_ParksToDlq (recover/give-up live recipes) that this plan extends in place"
  - phase: 30 (MetricsRoundTripE2ETests)
    provides: "the scrape-assertion pattern + PrometheusTestClient.PollPromForQuery/VectorNonEmpty + the FirstMetricObject/TryGetNonEmpty/no-workflowId label-shape helpers reused here"
provides:
  - "KeeperRecovery_RecoversBothPaths now asserts the recover-path keeper_* series appear in Prometheus for THIS procId: keeper_fault_consumed_total{fault_type=dispatch}, keeper_recovered_total{fault_type=dispatch}, keeper_workflow_paused_total, keeper_workflow_resumed_total, keeper_recovery_duration_seconds_count{outcome=recovered} — each with non-empty service_instance_id and NO workflowId"
  - "KeeperRecovery_GivesUp_ParksToDlq now asserts the give-up-path keeper_* series: keeper_dlq_pushed_total{reason=probe_exhausted,fault_type=result}, keeper_recovery_duration_seconds_count{outcome=gave_up}, keeper_l2_probe_failed_total — each with non-empty service_instance_id and NO workflowId"
  - "AssertKeeperLabels + FirstMetricObject + TryGetNonEmpty private helpers on KeeperRecoveryE2ETests (cardinality-ban label-shape check)"
  - "PromPollTimeoutMs=120_000 const (SDK 60s export + 15s scrape budget)"
affects: [phase-39-close-gate (Plan 04), keeper-observability]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Extend an EXISTING RealStack fact with a keeper_* Prometheus scrape-assertion block appended after the recover/give-up + net-zero teardown, reusing the captured procId — no separate test class (D-06)"
    - "All queries go through PrometheusTestClient.PollPromForQuery (Uri.EscapeDataString built in); only the validated {procId:D} GUID is interpolated — no raw query-URL concatenation (T-39-05 mitigation)"
    - "Histogram Prom suffix written to the unit-\"s\" _seconds form per Plan 01; live-confirmed on the first GREEN gate run in Plan 04 (no keeper_* series were live at authoring time)"

key-files:
  created: []
  modified:
    - tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs

key-decisions:
  - "Wave-0 histogram-suffix gate: the live stack carried ZERO keeper_* series at authoring (curl {__name__=~\"keeper_.*\"} → empty vector — the deployed keeper container predates the new Keeper meter), so the suffix could NOT be live-confirmed now. Per the plan's explicit fallback, the queries are written to the unit-\"s\" _seconds form (keeper_recovery_duration_seconds_count) per Plan 01's locked unit decision, to be CONFIRMED on the first GREEN gate run in Plan 04 (which rebuilds the keeper container with the new meter)."
  - "Reused MetricsRoundTripE2ETests's label-shape helpers (re-declared as private statics on this class: AssertKeeperLabels wrapping FirstMetricObject + TryGetNonEmpty) rather than promoting them to a shared base — keeps the change file-local and mirrors the existing per-class helper convention."
  - "keeper_in_flight is NOT asserted: it is a transient gauge (UpDownCounter → 0 after the loop) whose correctness is covered by the hermetic KeeperMetricsFacts (Plan 02), not this live race (RESEARCH OQ-1)."

patterns-established:
  - "keeper_* live-scrape contract: recover fact → consumed/recovered/paused/resumed/recovery_duration{recovered}; give-up fact → dlq_pushed{probe_exhausted}/recovery_duration{gave_up}/l2_probe_failed — all ProcessorId-filtered, non-empty service_instance_id, no workflowId"

requirements-completed: [TEST-01, TEST-02]

# Metrics
duration: 6min
completed: 2026-06-06
---

# Phase 39 Plan 03: Keeper Prometheus Scrape Assertions Summary

**Extended the two EXISTING Phase-36 RealStack facts (`KeeperRecovery_RecoversBothPaths`, `KeeperRecovery_GivesUp_ParksToDlq`) with a `keeper_*` Prometheus scrape-assertion block each — reusing `PrometheusTestClient.PollPromForQuery`/`VectorNonEmpty` and a no-`workflowId` label-shape helper — so the live recover/give-up flows assert their recover and give-up `keeper_*` series appear in Prometheus with the right `fault_type`/`outcome`/`reason`/`ProcessorId` labels and a non-empty `service_instance_id`.**

## Performance
- **Duration:** ~6 min
- **Started:** 2026-06-06T10:53Z
- **Completed:** 2026-06-06T10:59Z
- **Tasks:** 1 (auto)
- **Files modified:** 1 (test MODIFY)

## Wave-0 Gate — histogram Prometheus suffix
- **Live scrape attempted:** `curl -G http://localhost:9090/api/v1/query --data-urlencode 'query={__name__=~"keeper_.*"}'` → `{"status":"success","data":{"resultType":"vector","result":[]}}` — Prometheus is reachable but carries **ZERO** `keeper_*` series (the deployed keeper container predates Plan 01/02's new `Keeper` meter; it has not been rebuilt yet).
- **Resolution (per the plan's explicit fallback):** the suffix cannot be live-confirmed now, so the histogram query is written to the **unit-`"s"` `_seconds` form** locked by Plan 01: `keeper_recovery_duration_seconds_count{outcome="...",ProcessorId="..."}`. This is to be **confirmed on the first GREEN gate run in Plan 04**, which rebuilds the keeper container (DELTA 1) so the `Keeper` meter is in the live image.
- **Confirmed query strings (locked for the Plan 04 gate):**
  - Recover fact: `keeper_fault_consumed_total{fault_type="dispatch",ProcessorId="{procId:D}"}`, `keeper_recovered_total{fault_type="dispatch",ProcessorId="{procId:D}"}`, `keeper_workflow_paused_total{ProcessorId="{procId:D}"}`, `keeper_workflow_resumed_total{ProcessorId="{procId:D}"}`, `keeper_recovery_duration_seconds_count{outcome="recovered",ProcessorId="{procId:D}"}`.
  - Give-up fact: `keeper_dlq_pushed_total{reason="probe_exhausted",fault_type="result",ProcessorId="{procId:D}"}`, `keeper_recovery_duration_seconds_count{outcome="gave_up",ProcessorId="{procId:D}"}`, `keeper_l2_probe_failed_total{ProcessorId="{procId:D}"}`.

## Accomplishments
- **Recover fact (`KeeperRecovery_RecoversBothPaths`) extended** with a 5-series scrape block appended after the existing recover/result-reinject assertions + net-zero teardown, reusing the `procId` the fact captures at line 129. Asserts `keeper_fault_consumed_total{fault_type="dispatch"}`, `keeper_recovered_total{fault_type="dispatch"}`, `keeper_workflow_paused_total`, `keeper_workflow_resumed_total`, `keeper_recovery_duration_seconds_count{outcome="recovered"}` — all `ProcessorId`-filtered, each followed by `AssertKeeperLabels` (non-empty `service_instance_id` + no `workflowId`).
- **Give-up fact (`KeeperRecovery_GivesUp_ParksToDlq`) extended** with a 3-series scrape block. The synthetic carrier is a `Fault<ExecutionResult>` so the series carry `fault_type="result"`: `keeper_dlq_pushed_total{reason="probe_exhausted",fault_type="result"}`, `keeper_recovery_duration_seconds_count{outcome="gave_up"}`, `keeper_l2_probe_failed_total` — all `ProcessorId`-filtered + `AssertKeeperLabels`.
- **Label-shape helpers added** (`AssertKeeperLabels`, `FirstMetricObject`, `TryGetNonEmpty`) mirroring `MetricsRoundTripE2ETests.cs:243-277`: assert a non-empty `service_instance_id` and run the `foreach (var prop in metric.EnumerateObject()) Assert.False(...workflowId...)` cardinality-ban loop on every keeper_* series.
- **`PromPollTimeoutMs = 120_000`** const added (mirror `MetricsRoundTripE2ETests.cs:61` — SDK 60s export + 15s scrape budget).
- **`keeper_in_flight` deliberately NOT asserted** (transient gauge → 0 after the loop; covered hermetically in Plan 02 — RESEARCH OQ-1).

## Task Commits
1. **Task 1 (auto): extend both Keeper RealStack facts with keeper_* Prometheus scrape blocks** - `9e938eb` (test)

**Plan metadata:** _(this SUMMARY + STATE/ROADMAP commit)_

## Files Created/Modified
- `tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs` (MODIFY) - `PromPollTimeoutMs` const + a `keeper_*` scrape-assertion block at the end of each of the two facts + 3 private label-shape helpers (`AssertKeeperLabels`/`FirstMetricObject`/`TryGetNonEmpty`). +122 lines, 0 deletions. The existing outage recipe (`ArmWrongTypePoisonAsync`), `L2KeysToCleanup` teardown, and `keeper-dlq` ACK-drain are UNCHANGED (D-07/D-10).

## Decisions Made
- **Suffix written to `_seconds`, confirmed later (Wave-0 gate).** No live keeper_* series exist yet, so the histogram suffix is taken from Plan 01's unit-`"s"` decision and verified on the Plan 04 gate run (which rebuilds the keeper container). This is the plan's documented fallback path, not a guess.
- **No separate test class (D-06).** The two existing facts already induce the exact TEST-01/02 outages (recover-both-paths + give-up-park), so the scrape blocks are appended in place rather than authoring a third class.
- **Helpers re-declared file-local** (not promoted to a shared base) — matches the existing per-class helper convention in both `KeeperRecoveryE2ETests` and `MetricsRoundTripE2ETests`.

## Deviations from Plan
None — plan executed exactly as written. The Wave-0 suffix gate resolving to "write `_seconds`, confirm on Plan 04" is the plan's explicit conditional, not a deviation.

## Authentication Gates
None.

## Verification
- **Build:** `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release` → **0 Warning / 0 Error** (TreatWarningsAsErrors).
- **Grep gates:** the file contains `PrometheusTestClient`, `PollPromForQuery`, `PromPollTimeoutMs`; the recover fact references `keeper_fault_consumed_total`/`keeper_recovered_total`/`keeper_workflow_paused_total`/`keeper_workflow_resumed_total`/`keeper_recovery_duration_seconds_count`; the give-up fact references `keeper_dlq_pushed_total`/`keeper_recovery_duration_seconds_count`/`keeper_l2_probe_failed_total`; both facts run `AssertKeeperLabels` (non-empty `service_instance_id` + no-`workflowId` loop). No raw query-URL concatenation (all through `PollPromForQuery`).
- **No file deletions** (`git diff --diff-filter=D HEAD~1 HEAD` empty); pre-existing untracked items (`.claude/`, `27-PATTERNS.md`, `psql-*.txt`, `launchSettings.json`) left untouched.
- **Live GREEN execution** of the two extended facts is gated by the **Plan 04 close gate** (3×GREEN, RealStack live, against the rebuilt keeper container) — that run also confirms the `_seconds` histogram suffix.

## Threat Surface
- **T-39-05 (Tampering — PromQL interpolation):** mitigated as planned — every query flows through `PrometheusTestClient.PollPromForQuery` (built-in `Uri.EscapeDataString`); only the validated `{procId:D}` GUID is interpolated. No raw query-URL concatenation.
- **T-39-06 (Info Disclosure):** accept — the test reads telemetry series + label shapes only; the no-`workflowId` assertion actively guards the cardinality/info-shape contract.
- No new threat surface introduced.

## Known Stubs
None — the scrape assertions query the live deployed series; no placeholder/empty-data paths introduced. (The series are not yet live because the keeper container predates the new meter; that is a Plan-04 rebuild concern, explicitly tracked in the Wave-0 gate above, not a stub.)

## Next Phase Readiness
- Plan 04 (close gate) must rebuild the **keeper** container (DELTA 1) so the `Keeper` meter emits, then run the two extended facts as part of the 3×GREEN cadence. On the first GREEN run, **confirm the histogram suffix is `keeper_recovery_duration_seconds_count`** (unit `"s"` set) — if the live scrape shows the bare `keeper_recovery_duration_count` instead, update the two `recovery_duration` query strings in `KeeperRecoveryE2ETests.cs` accordingly (the only suffix-sensitive lines).
- The locked query strings (above) match what the gate run will scrape.

## Self-Check: PASSED
- FOUND: tests/BaseApi.Tests/Keeper/KeeperRecoveryE2ETests.cs
- FOUND: .planning/phases/39-keeper-observability-real-stack-e2e-close-gate/39-03-SUMMARY.md
- FOUND commit: 9e938eb (test)

---
*Phase: 39-keeper-observability-real-stack-e2e-close-gate*
*Completed: 2026-06-06*
