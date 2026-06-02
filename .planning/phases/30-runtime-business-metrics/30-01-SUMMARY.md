---
phase: 30-runtime-business-metrics
plan: 01
subsystem: infra
tags: [opentelemetry, prometheus, metrics, service-instance-id, observability, otel-resource]

# Dependency graph
requires:
  - phase: 11-migrate-prometheus-elasticsearch
    provides: collector prometheus exporter (resource_to_telemetry_conversion + add_metric_suffixes), PrometheusTestClient skeleton, ElasticsearchTestClient poll-with-backoff analog
  - phase: 18-baseconsole-core
    provides: BaseConsoleObservabilityExtensions (console metrics-only OTel resource)
provides:
  - service.instance.id resource attribute on BOTH logs and metrics resources in BaseApi.Core and BaseConsole.Core (resolved once per process; POD_NAME -> HOSTNAME -> MachineName -> GUID)
  - PrometheusTestClient.PollPromForQuery(promQL, predicate, timeoutMs) + VectorNonEmpty/HasNumericValue static predicate helpers (consumed by the Wave 2 MetricsRoundTripE2ETests)
  - ResolveInstanceIdFacts hermetic env-precedence test
affects: [30-02-orchestrator-metrics, 30-03-processor-metrics, 30-04-metrics-e2e]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Resolve service.instance.id ONCE per process into a single local, apply via AddAttributes() to BOTH the logs SetResourceBuilder and metrics ConfigureResource (D-10 correctness)"
    - "Duplicate the ~6-line ResolveInstanceId() helper per base lib (D-09) rather than a shared lib (BaseConsole.Core cannot reference BaseApi.Core)"
    - "Additive predicate-poll variant on the existing PrometheusTestClient, preserving the Phase-11 sum-threshold API"

key-files:
  created:
    - tests/BaseApi.Tests/Observability/ResolveInstanceIdFacts.cs
  modified:
    - src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs
    - src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs
    - tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs

key-decisions:
  - "service.instance.id resolved once per process and applied to logs + metrics resources via a single captured local (prevents the Guid fallback diverging between the two resources)"
  - "ResolveInstanceId() duplicated independently in each base lib (D-09); no shared lib"
  - "Extended the pre-existing Phase-11 PrometheusTestClient with PollPromForQuery + predicate helpers (additive) instead of replacing it — its PollPrometheusUntilSumAtLeast/QueryPrometheus/SumSampleValues API is still used by SchemasMetricsE2ETests and MetricsExportTests"
  - "Collector metrics pipeline left untouched (METRIC-07) — resource_to_telemetry_conversion already promotes service.instance.id -> service_instance_id label"

patterns-established:
  - "Resolve-once instance id + shared OTel resource across logs and metrics (D-09/D-10)"
  - "Predicate-driven Prometheus poll-with-backoff (200ms -> 3.2s) mirroring ElasticsearchTestClient"

requirements-completed: [METRIC-01, METRIC-02, METRIC-03, METRIC-07]

# Metrics
duration: 8min
completed: 2026-06-02
---

# Phase 30 Plan 01: service.instance.id + Prometheus Test Scaffolding Summary

**A code-defined `service.instance.id` resource attribute on BOTH the logs and metrics OTel resources in `BaseApi.Core` and `BaseConsole.Core` (resolved once per process: POD_NAME → HOSTNAME → MachineName → GUID), plus the Wave 0 `PrometheusTestClient.PollPromForQuery` + `ResolveInstanceId` precedence test the Wave 2 E2E consumes — collector metrics pipeline untouched.**

## Performance

- **Duration:** ~8 min
- **Started:** 2026-06-02T19:54:49Z
- **Completed:** 2026-06-02T20:02:29Z
- **Tasks:** 2
- **Files modified:** 4 (1 created, 3 modified)

## Accomplishments
- Every metric (runtime, HTTP, custom) and every log emitted by both base libs now carries a non-empty `service_instance_id` Prometheus label / log attribute — a free consequence of the existing runtime + HTTP instrumentation, with NO collector metrics-pipeline change (METRIC-01/02/03/07).
- Added `PollPromForQuery(promQL, predicate, timeoutMs)` (200ms→3.2s backoff, `Uri.EscapeDataString` on the query param) + the `VectorNonEmpty` / `HasNumericValue` static predicate helpers to the existing `PrometheusTestClient`, additive (the Phase-11 sum-threshold API is preserved for its current callers).
- Added the hermetic `ResolveInstanceIdFacts` proving the `POD_NAME > HOSTNAME > MachineName` env precedence with env restored in a `finally`.

## Task Commits

Each task was committed atomically:

1. **Task 1: PrometheusTestClient PollPromForQuery + predicate helpers + ResolveInstanceId precedence facts** - `8c84b9d` (test)
2. **Task 2: Unify the shared OTel resource + set service.instance.id in both base libs** - `0d6df04` (feat)

_Note: Task 1 is the TDD scaffolding (the precedence facts went GREEN immediately against a local mirror of the duplicated helper); Task 2 implements the resource-attribute wiring the facts mirror._

## Files Created/Modified
- `tests/BaseApi.Tests/Observability/ResolveInstanceIdFacts.cs` (NEW) - hermetic `[Collection("Observability")]` proof of the POD_NAME → HOSTNAME → MachineName precedence (D-10).
- `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` - added `PollPromForQuery` + `VectorNonEmpty`/`HasNumericValue` statics (Wave 2 E2E consumes these); the existing Phase-11 sum-threshold methods are unchanged.
- `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` - resolve instance id once; `AddAttributes([service.instance.id])` on logs + metrics resources; duplicated `ResolveInstanceId()` helper.
- `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs` - same edit shape; MassTransit meter preserved; duplicated `ResolveInstanceId()` helper.

## Decisions Made
- **Resolve once, capture in one local.** The same `instanceAttrs` array is applied to both the logs `SetResourceBuilder` and metrics `ConfigureResource`, so the GUID fallback can never differ between the two resources (D-10 correctness).
- **Duplicate the helper (D-09).** `BaseConsole.Core` cannot reference `BaseApi.Core` and the helper is ~6 lines, so it is copied independently into each lib rather than extracted to a shared lib.
- **Extend, don't replace, the existing PrometheusTestClient.** The plan's `<read_first>` framed `PrometheusTestClient` as NEW, but a Phase-11 `PrometheusTestClient` already exists (with a `PollPrometheusUntilSumAtLeast`/`QueryPrometheus`/`SumSampleValues` API used by `SchemasMetricsE2ETests` and `MetricsExportTests`). Adding `PollPromForQuery` + the two predicate helpers additively satisfies every Task 1 acceptance grep (`PollPromForQuery`, `Uri.EscapeDataString`, `localhost:9090`; no `8889`) without breaking those callers.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] PrometheusTestClient already existed — extended additively rather than created**
- **Found during:** Task 1 (PrometheusTestClient creation)
- **Issue:** The plan's action says "Create `tests/.../PrometheusTestClient.cs` by copying the `ElasticsearchTestClient` skeleton", but a Phase-11 `PrometheusTestClient` already exists at that path with a different (sum-threshold) API that `SchemasMetricsE2ETests` and `MetricsExportTests` depend on. Overwriting it would break those tests; creating a duplicate type would be a compile error.
- **Fix:** Added the required `PollPromForQuery(promQL, predicate, timeoutMs)` method + the `VectorNonEmpty`/`HasNumericValue` static predicate helpers (with the 200ms→3.2s backoff + `Uri.EscapeDataString` discipline the plan specifies) to the existing class, leaving the Phase-11 methods intact.
- **Files modified:** tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs
- **Verification:** Build 0/0; `dotnet test --filter-class "*ResolveInstanceIdFacts"` 3/3; full hermetic suite 408/408 (the Phase-11 metrics tests still compile and pass).
- **Committed in:** 8c84b9d (Task 1 commit)

**2. [Rule 2 - Tampering mitigation present] T-30-01 `Uri.EscapeDataString` on the PromQL query param**
- **Found during:** Task 1
- **Issue:** The threat register assigns `mitigate` to PromQL query construction (T-30-01).
- **Fix:** `PollPromForQuery` escapes the interpolated PromQL via `Uri.EscapeDataString` (matching the existing `QueryPrometheus`), so the only interpolated value (a validated GUID procId in Wave 2) is encoded.
- **Files modified:** tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs
- **Verification:** grep confirms `Uri.EscapeDataString(promQL)` in the new method.
- **Committed in:** 8c84b9d (Task 1 commit)

---

**Total deviations:** 2 (1 blocking-resolved, 1 threat-mitigation confirmed)
**Impact on plan:** Both adjustments preserve correctness and existing callers; no scope creep. The plan's interfaces and acceptance criteria are all satisfied.

## Issues Encountered
None — both tasks built clean on the first attempt; the hermetic suite went from 405 → 408 (the +3 ResolveInstanceIdFacts) with zero regression.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Plans 30-02 (Orchestrator counters) and 30-03 (Processor counters in BaseProcessor.Core) can now increment business counters that automatically inherit the `service_instance_id` resource label.
- Plan 30-04 (`MetricsRoundTripE2ETests`) can consume `PrometheusTestClient.PollPromForQuery` + `VectorNonEmpty`/`HasNumericValue` for series-existence and bottleneck assertions against `localhost:9090`.
- No blockers.

## Self-Check: PASSED

- Files: ResolveInstanceIdFacts.cs, PrometheusTestClient.cs, ObservabilityServiceCollectionExtensions.cs, BaseConsoleObservabilityExtensions.cs, 30-01-SUMMARY.md — all FOUND.
- Commits: 8c84b9d (test), 0d6df04 (feat) — both FOUND in git log.

---
*Phase: 30-runtime-business-metrics*
*Completed: 2026-06-02*
