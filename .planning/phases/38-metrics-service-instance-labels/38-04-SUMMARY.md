---
phase: 38-metrics-service-instance-labels
plan: 04
subsystem: testing
tags: [observability, prometheus, opentelemetry, metrics, realstack, e2e, service_name, service_instance_id]

# Dependency graph
requires:
  - phase: 38-02
    provides: combined metrics service_name={name}_{version} on both base libs (sk-api_3.2.0 / orchestrator_3.4.0 / keeper_3.7.0)
  - phase: 38-03
    provides: processor MeterProviderHolder swap placeholder->DB {Name}_{Version} on identity-resolve
provides:
  - RealStack scrape gate proving combined service_name + non-empty service_instance_id across runtime/HTTP/business families
  - live proof the processor steady-state series carries the DB-sourced {Name}_{Version} (NOT the boot placeholder)
  - SeedProcessorAsync now surfaces the seeded (Id, Name, Version) so the DB-sourced label is asserted dynamically
affects: [phase-39-keeper-instruments, observability, metrics-validation]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "RealStack family scrape: poll combined service_name selector per instrument family with the 120s Prom pull-latency budget (Pitfall 7)"
    - "Dynamic DB-sourced label assertion: capture the seeded {Name}_{Version} from the read DTO rather than hardcoding (GET-or-create row wins)"

key-files:
  created:
    - .planning/phases/38-metrics-service-instance-labels/38-04-SUMMARY.md
  modified:
    - tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs

key-decisions:
  - "Assert the processor service_name dynamically from the seeded row's {Name}_{Version}, never the boot placeholder processor-sample_3.5.0"
  - "Rebuild + recreate baseapi/orchestrator/processor containers before the live scrape (22h-stale images predated the 38-02 combine + 38-03 swap)"

patterns-established:
  - "Per-family RealStack assertion: VectorNonEmpty poll on a combined service_name selector + FirstMetricObject/TryGetNonEmpty for service_instance_id"

requirements-completed: [MLBL-01, MLBL-02, MLBL-03, MLBL-05]

# Metrics
duration: ~40min
completed: 2026-06-06
---

# Phase 38 Plan 04: RealStack Metrics Round-Trip Gate Summary

**Live Prometheus scrape proves combined `service_name={name}_{version}` + non-empty `service_instance_id` across runtime/HTTP/business families and the processor's DB-sourced `{Name}_{Version}` steady-state series — verified end-to-end through the rebuilt compose stack (1/1 RealStack GREEN; 479/479 hermetic GREEN).**

## Performance

- **Duration:** ~40 min
- **Started:** 2026-06-06 (Wave 3)
- **Completed:** 2026-06-06
- **Tasks:** 2
- **Files modified:** 1 (test) + planning docs

## Accomplishments
- Extended `MetricsRoundTripE2ETests` with four new RealStack assertions: DB-sourced processor business series + combined `service_name` & non-empty `service_instance_id` on HTTP (sk-api_3.2.0), business (orchestrator_3.4.0), and runtime families.
- `SeedProcessorAsync` now returns the seeded `(Id, Name, Version)` so the processor's expected combined label is built dynamically (`{procName}_{procVersion}`) — robust against the GET-or-create pre-existing-row case; no hardcoded `processor-sample_3.5.0`.
- Rebuilt + recreated the three app containers (they were ~22h stale, predating the 38-02 combine + 38-03 swap), then ran the live scrape gate GREEN.
- Verified MLBL-03 (iii): `src/Processor.Sample/appsettings.json` RETAINS `Service:Name=processor-sample` / `Service:Version=3.5.0` (boot-window placeholder source — GA-3).
- Recorded the MLBL-05 PromQL inventory and grep-gate result.

## Task Commits

1. **Task 1: Extend MetricsRoundTripE2ETests with combined-service_name + instance-id assertions across families + DB-sourced processor series** — `30a23d7` (test)
2. **Task 2: Verify appsettings retention + record MLBL-05 inventory + run the hermetic & RealStack gates** — no code change required (appsettings already retained, grep gate clean from Plan 02); verification-only task, results recorded here. Folded into the metadata commit.

**Plan metadata:** (final docs commit — this SUMMARY + STATE + ROADMAP)

## Files Created/Modified
- `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs` — added four RealStack family assertions (DB-sourced processor + HTTP/business/runtime combined `service_name` + `service_instance_id`); `SeedProcessorAsync` now returns `(Id, Name, Version)` and the test builds `procServiceName` dynamically. NET-ZERO-31 `orchestration/stop` teardown left intact.

## Live RealStack Result (scrape evidence)

**Container rebuild:** `docker compose -f compose.yaml build baseapi-service orchestrator processor-sample` (BUILD_EXIT=0, all three images Built) → `up -d` recreated them (all three reached `(healthy)`). This was mandatory: the running images were ~22h old and emitted bare `service_name` (the pre-38-02/03 code).

**RealStack test:** `MetricsRoundTripE2ETests` — **1/1 PASSED** (exit 0, 2m47s) against the rebuilt + up stack.

**Direct Prometheus `:9090` scrape evidence captured post-run:**

| Family | Selector | series | service_name | service_instance_id |
|--------|----------|--------|--------------|---------------------|
| HTTP (sk-api) | `http_server_request_duration_seconds_count{service_name="sk-api_3.2.0"}` | 15 | `sk-api_3.2.0` | `USERPC` (non-empty) |
| business (orchestrator) | `orchestrator_dispatch_sent_total{service_name="orchestrator_3.4.0"}` | 1 | `orchestrator_3.4.0` | `f2735eda5f87` (non-empty) |
| runtime (orchestrator) | `{__name__=~"process_runtime_dotnet_.*",service_name="orchestrator_3.4.0"}` | 27 | `orchestrator_3.4.0` | `f2735eda5f87` (non-empty) |
| processor (DB-sourced) | `processor_dispatch_consumed_total` (exercised ProcessorId `f3f8682e…`) | — | `sample-proc-8ca4608c07744158b90d586533752433_1.0.0` | `fed831ef1cac` (non-empty) |

**MLBL-03 (ii) proof — the key result:** the exercised processor's business series carries `service_name="sample-proc-8ca4608c07744158b90d586533752433_1.0.0"` — exactly the DB row's `{Name}_{Version}`, NOT the boot placeholder. A `count(processor_dispatch_consumed_total{service_name="processor-sample_3.5.0"})` query returned **0** — the placeholder never carried a business counter (the MeterProvider swap fired before any dispatch increment), confirming the Plan 03 swap is observable live. (A second, stale `service_name=processor-sample` series on a different/old ProcessorId is a pre-rebuild artifact aging out via Prom retention — expected per T-38-09 accept / D-02.)

## MLBL-03 (iii) — appsettings retention (verified)

`src/Processor.Sample/appsettings.json` lines 9-12 RETAIN:
```json
"Service": { "Name": "processor-sample", "Version": "3.5.0" }
```
No change required — they feed `cfg.Require("Service:Name")` in the unchanged shared bootstrap as the boot-window placeholder (GA-3). Confirmed present, not removed.

## MLBL-05 — PromQL consumer inventory

The in-repo PromQL `service_name` query sites reconciled/added across Phase 38:

| File | Combined-literal occurrences | Role |
|------|------------------------------|------|
| `tests/BaseApi.Tests/Observability/MetricsExportTests.cs` | 5 | HTTP family (sk-api_3.2.0) — Plan 02 |
| `tests/BaseApi.Tests/Observability/SchemasMetricsE2ETests.cs` | 4 | schema metrics (sk-api_3.2.0) — Plan 02 |
| `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` | 1 | doc-comment narrative — Plan 02 |
| `tests/BaseApi.Tests/Observability/LogsResourceBareNameFacts.cs` | 1 | logs-bare guard reference — Plan 02 |
| `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs` | 9 | RealStack family assertions — **this plan** (4 quoted query literals: sk-api_3.2.0, orchestrator_3.4.0 ×2, {procServiceName}) |

- **grep gate (bare literals):** `service_name="(sk-api|orchestrator|keeper|processor-sample)"` over `tests/**/*.cs` → **0 hits**. Bare `service_name=` query literals for the four services are fully eliminated.
- **No recording/alert rules:** `prometheus.yml` defines none.
- **No committed Grafana dashboards.**
- **Conclusion:** test assertions are the sole in-repo PromQL consumers; all reconciled to the combined `{name}_{version}` scheme and passing.

## Hermetic build/test evidence

- `dotnet build SK_P.sln -c Release --nologo` → **Build succeeded, 0 Warning(s), 0 Error(s)** (the RealStack test compiles; `[Trait("Category","RealStack")]` keeps it out of the hermetic gate).
- Hermetic suite (`BaseApi.Tests.exe --filter-not-trait Category=RealStack`, the MTP trait opt-out) → **479 passed / 0 failed / 0 skipped, exit 0** (2m59s). The `Connection Failed: rabbitmq://…` lines are background MassTransit reconnect noise from a broker-down posture test, not failures (succeeded=479, failed=0).

## Decisions Made
- **Dynamic DB-sourced assertion over hardcoded literal:** `SeedProcessorAsync` is GET-or-create by SourceHash, so the row may pre-exist with a guid-suffixed name. The test captures the actual `(Name, Version)` and asserts `{Name}_{Version}` — never the placeholder. (RESEARCH line 239 / D-05.)
- **Rebuild before scrape:** mandatory per MEMORY (embedded SourceHash + the 38-02/03 code must be in the running images); the 22h-stale containers would have failed the new assertions.

## Deviations from Plan
None — plan executed exactly as written. Task 2 required no code change (appsettings already retained from inception; the grep gate was already clean from Plan 02's reconcile), so it resolved to a verification-only task whose results are recorded above. No architectural changes, no auth gates, no stubs, no new threat surface (T-38-08 / T-38-09 both accept — bounded test-controlled seed; stale placeholder series age out by-design).

## Issues Encountered
- The MTP runner prints `--help` when passed the filter behind a `--` separator or when the value is quoted-as-one-arg in this shell. Resolved by invoking `BaseApi.Tests.exe --filter-not-trait Category=RealStack` (no `--` separator, unquoted pair) and `--filter-class "BaseApi.Tests.Orchestrator.MetricsRoundTripE2ETests"` for the RealStack run. This matches the MEMORY note that the MTP filter is trait-based.

## Next Phase Readiness
- Phase 38 is hermetically + live-scrape complete across all 4 plans (38-01 identity plumbing, 38-02 combine, 38-03 swap, 38-04 RealStack gate). No live operator step (SPEC constraint satisfied via the scrape assertion).
- Phase 39's new Keeper instruments inherit this labeling automatically at the resource/meter-provider level and are verified there.

## Self-Check: PASSED

- FOUND: `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs`
- FOUND: `.planning/phases/38-metrics-service-instance-labels/38-04-SUMMARY.md`
- FOUND: commit `30a23d7` (Task 1)

---
*Phase: 38-metrics-service-instance-labels*
*Completed: 2026-06-06*
