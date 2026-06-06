---
phase: 38-metrics-service-instance-labels
plan: 02
subsystem: observability
tags: [opentelemetry, prometheus, metrics, service_name, resource-attributes, otel-resource]

# Dependency graph
requires:
  - phase: 30-runtime-business-metrics
    provides: "metrics ConfigureResource block with service.name + service.instance.id on both base libs"
  - phase: 11-prometheus-elasticsearch
    provides: "resource_to_telemetry_conversion + the in-repo PromQL consumer literals (service_name=sk-api)"
provides:
  - "Combined metrics service_name={name}_{version} on the BaseConsole + BaseApi metrics resource (MLBL-01/D-01)"
  - "Bare logs service.name preserved on both base libs (MLBL-04) plus a hermetic guard test"
  - "All 4 in-repo PromQL consumer literals reconciled to service_name=\"sk-api_3.2.0\" (MLBL-05/D-08)"
affects: [38-03, 38-04, phase-39-keeper-metrics, prometheus-consumers]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Metrics-resource-only service.name combine: edit the ConfigureResource AddService serviceName arg only, never the logs SetResourceBuilder block"
    - "Hermetic OTel resource inspection: BaseProcessor<LogRecord>.ParentProvider.GetResource().Attributes to read service.name off a built logs provider with no compose stack"

key-files:
  created:
    - tests/BaseApi.Tests/Observability/LogsResourceBareNameFacts.cs
  modified:
    - src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs
    - src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs
    - tests/BaseApi.Tests/Observability/MetricsExportTests.cs
    - tests/BaseApi.Tests/Observability/SchemasMetricsE2ETests.cs
    - tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs

key-decisions:
  - "Combined name applied to the METRICS ConfigureResource only; LOGS SetResourceBuilder left byte-for-byte bare (MLBL-04 protects the Phase-35 ES service.name=\"keeper\" contract)"
  - "service.version kept as a standalone label (D-07) — AddService(serviceName: $\"{name}_{version}\", serviceVersion: version) sets both service.name and service.version"
  - "PromQL literals updated as EXACT literals (D-08), not regex — 4 query sites swapped to sk-api_3.2.0"

patterns-established:
  - "LogsResourceBareNameFacts: a hermetic RED-on-regression guard that builds a bare logs resource and asserts service.name has no _{version} suffix"

requirements-completed: [MLBL-01, MLBL-04, MLBL-05]

# Metrics
duration: 7min
completed: 2026-06-06
---

# Phase 38 Plan 02: Combined Metrics `service_name` + Bare Logs Guard + PromQL Reconcile Summary

**Metrics resource `service_name` is now the combined `{name}_{version}` (e.g. `sk-api_3.2.0`) on both base libs while the logs resource stays bare, guarded by a new hermetic test, with all 4 in-repo PromQL consumer literals reconciled to the combined value.**

## Performance

- **Duration:** ~7 min
- **Started:** 2026-06-06T07:03:58Z
- **Completed:** 2026-06-06T07:11:24Z
- **Tasks:** 3
- **Files modified:** 5 (4 modified + 1 created)

## Accomplishments
- Combined `service_name = {name}_{version}` on the metrics `ConfigureResource` block in both `BaseConsole.Core` and `BaseApi.Core`, keeping `service.version` standalone (D-07).
- Left both LOGS `SetResourceBuilder` blocks bare (MLBL-04) and added `LogsResourceBareNameFacts` — a hermetic test that goes RED if the `_{version}` combine ever leaks into the logs resource.
- Reconciled every in-repo PromQL consumer literal (3 in `MetricsExportTests`, 1 in `SchemasMetricsE2ETests`) to `service_name="sk-api_3.2.0"` plus doc-comment narratives; zero bare `service_name="sk-api"` query literals remain.

## Task Commits

Each task was committed atomically:

1. **Task 1: Combine service.name on the METRICS resource; leave LOGS bare** — `013bc0a` (feat)
2. **Task 2: Add hermetic logs-resource bare-name assertion (MLBL-04 guard)** — `39792b3` (test)
3. **Task 3: Reconcile in-repo PromQL consumer literals to sk-api_3.2.0** — `4d67977` (feat)

_Note: Task 2 is `tdd="true"`. It is a regression-guard whose asserted property (logs stay bare) was already established correct by Task 1, so it was authored GREEN-by-construction rather than RED-first; see Issues Encountered._

## Files Created/Modified
- `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs` — metrics `ConfigureResource` `serviceName: $"{serviceName}_{serviceVersion}"`; logs block unchanged.
- `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` — identical metrics-block combine; logs block unchanged.
- `tests/BaseApi.Tests/Observability/LogsResourceBareNameFacts.cs` — NEW hermetic guard (build bare logs provider, read `ParentProvider.GetResource()` `service.name`, assert `== "keeper"` and `DoesNotContain "_3.7.0"`).
- `tests/BaseApi.Tests/Observability/MetricsExportTests.cs` — 3 query literals + diagnostic strings → `sk-api_3.2.0`.
- `tests/BaseApi.Tests/Observability/SchemasMetricsE2ETests.cs` — 1 query literal + 2 doc comments + message string → `sk-api_3.2.0`.
- `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` — doc-comment narrative notes the metrics-only `{name}_{version}` scheme.

## Decisions Made
- None beyond the plan-locked decisions (D-01/D-07/D-08 + MLBL-04). Plan executed as specified.

## Deviations from Plan

None - plan executed exactly as written.

The only minor expansion was updating the unquoted diagnostic/assertion-message strings (`service_name=sk-api, http_route=...`) alongside the quoted query literals for accuracy. These were not query literals and did not trip the MLBL-05 grep gate, but were reconciled to keep the failure messages truthful. No behavior change.

## Issues Encountered
- **MTP test invocation:** the plan's verify command used VSTest-style `--filter-not-trait`/`--filter`. This repo uses Microsoft.Testing.Platform (xunit.v3), so the flags must be passed after `--` (e.g. `dotnet test ... -- --filter-not-trait "Category=RealStack" --filter-class "..."`). Translated and ran successfully.
- **TDD RED-first not applicable for Task 2:** the guard asserts the logs resource stays bare, a property Task 1 deliberately preserved (logs block untouched). There was no implementation gap to make the test fail first, so per the fail-fast rule the test was authored as a standing GREEN regression guard. Its protective value is real: flipping the logs block to the `_{version}` combine would make `service.name` read `keeper_3.7.0`, failing both assertions.
- **OTel resource-inspection seam:** no prior test reads a provider's resource (`grep GetResource/CreateMeterProviderBuilder` → 0 hits). Confirmed via SDK reflection that `BaseProcessor<LogRecord>.ParentProvider` (a `BaseProvider`) + `ProviderExtensions.GetResource()` → `Resource.Attributes` (`IEnumerable<KeyValuePair<string,object>>`) is the available seam (OpenTelemetry 1.15.1, net8.0).

## Verification Evidence
- `dotnet build SK_P.sln -c Release` — **Build succeeded, 0 Warning(s), 0 Error(s)** (after Task 1 and again after Task 3).
- `dotnet test tests/BaseApi.Tests -c Release -- --filter-class "BaseApi.Tests.Observability.LogsResourceBareNameFacts"` — **Passed! Failed: 0, Passed: 1, Total: 1.**
- Full hermetic Observability namespace (`--filter-not-trait "Category=RealStack" --filter-namespace "BaseApi.Tests.Observability"`) — **Passed! Failed: 0, Passed: 26, Total: 26.** (RealStack live-Prometheus E2E tests excluded — those are the operator/Phase-39 live gate.)
- grep `service_name="sk-api"` (bare, quoted) over `tests/` → **0 hits.** grep `service_name="(sk-api|orchestrator|keeper|processor-sample)"` → **0 hits.** grep `service_name="sk-api_3.2.0"` → **7 occurrences** (4 query literals + 3 doc/comment mentions).
- grep `_{serviceVersion}` over `src/` → exactly **1 hit per base-lib file**, both inside the metrics `ConfigureResource` block (never in a `SetResourceBuilder` logs block).

## MLBL-05 PromQL Consumer Inventory (updated sites)
| File | Site | Before | After |
|------|------|--------|-------|
| `MetricsExportTests.cs` | line ~50 query | `service_name="sk-api"` | `service_name="sk-api_3.2.0"` |
| `MetricsExportTests.cs` | positiveControl query | `service_name="sk-api"` | `service_name="sk-api_3.2.0"` |
| `MetricsExportTests.cs` | negativeQuery | `service_name="sk-api"` | `service_name="sk-api_3.2.0"` |
| `SchemasMetricsE2ETests.cs` | line ~102 query | `service_name="sk-api"` | `service_name="sk-api_3.2.0"` |
| `PrometheusTestClient.cs` | doc-comment narrative | bare-name mention | `{name}_{version}` / `sk-api_3.2.0` |

No recording rules, alert rules, or committed Grafana dashboards reference `service_name` (confirmed in SPEC background); the only consumers are the test assertions above.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Wave 1 of Phase 38 (this plan + 38-01 processor identity plumbing) is complete and independent; no shared files touched.
- The combined metrics `service_name` + bare logs guard are in place for the downstream processor MeterProvider-swap plan (38-03) and the live verification plan (38-04).
- The live scrape-assertion E2E (RealStack-trait `SchemasMetricsE2ETests`/`MetricsExportTests` now expecting `sk-api_3.2.0`) is an operator/live gate against the compose stack — not run hermetically here.

## Self-Check: PASSED

- Created files verified present: `LogsResourceBareNameFacts.cs`, `38-02-SUMMARY.md`.
- Modified base-lib files present.
- Task commits verified in git history: `013bc0a`, `39792b3`, `4d67977`.

---
*Phase: 38-metrics-service-instance-labels*
*Completed: 2026-06-06*
