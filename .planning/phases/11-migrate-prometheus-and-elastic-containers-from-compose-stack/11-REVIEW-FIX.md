---
phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
fixed_at: 2026-05-28T00:00:00Z
review_path: .planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-REVIEW.md
iteration: 1
findings_in_scope: 5
fixed: 5
skipped: 0
status: all_fixed
---

# Phase 11: Code Review Fix Report

**Fixed at:** 2026-05-28
**Source review:** `.planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-REVIEW.md`
**Iteration:** 1

**Summary:**
- Findings in scope: 5 (Critical + Warning; Info findings deferred per default `critical_warning` scope)
- Fixed: 5
- Skipped: 0

All 5 Warning-level findings were addressed. The 8 Info-level findings remain open for a follow-up hardening pass (out of scope for this iteration).

After every fix the test project (`tests/BaseApi.Tests/BaseApi.Tests.csproj`) was rebuilt with `dotnet build` — all 5 commits compiled clean with 0 warnings, 0 errors.

## Fixed Issues

### WR-01: HTTP polling helpers ignore CancellationToken — tests cannot be canceled cleanly

**Files modified:** `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs`, `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs`, `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs`, `tests/BaseApi.Tests/Observability/LogExportTests.cs`, `tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs`, `tests/BaseApi.Tests/Observability/MetricsExportTests.cs`, `tests/BaseApi.Tests/Observability/SchemasLogsE2ETests.cs`, `tests/BaseApi.Tests/Observability/SchemasMetricsE2ETests.cs`
**Commit:** `025af0b`
**Applied fix:** Added `CancellationToken ct = default` parameter to `ElasticsearchTestClient.PollEsForLog`, `PrometheusTestClient.PollPrometheusUntilSumAtLeast`, and `PrometheusTestClient.QueryPrometheus`. Threaded the token through every inner `await` (`SendAsync`, `ReadAsStringAsync`, `GetAsync`, `Task.Delay`) and added `ct.ThrowIfCancellationRequested()` at each loop iteration entry. Updated all 6 call-site files to pass `TestContext.Current.CancellationToken` through.

### WR-02: `HealthDeadPostgresFixture` / `HealthLiveLocalhostFixture` mutate process-wide env vars without re-entrancy safety

**Files modified:** `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs`
**Commit:** `e801920`
**Applied fix:** Wrapped both fixture constructors' `Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", ...)` calls in `try/catch` blocks that restore the captured `_priorEnvValue` on failure before rethrowing. This prevents the env var from staying pinned to the dead/localhost string if construction throws between `SetEnvironmentVariable` and `DisposeAsync`.

### WR-03: `Phase11WebAppFactory` env-var mutation leaks across collections

**Files modified:** `tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs`
**Commit:** `a290cb7`
**Applied fix:** Added `_priorOtlpEndpoint` field, captured the prior value of `OTEL_EXPORTER_OTLP_ENDPOINT` before mutating it, gated the `SetEnvironmentVariable` call on `_priorOtlpEndpoint is null` (do not clobber an explicit operator setting), and added a `DisposeAsync` override that best-effort restores the prior value and calls `base.DisposeAsync()`.

### WR-04: `HealthDeadPostgresFixture` still spins up a real Postgres container it never uses

**Files modified:** `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs`, `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs`
**Commit:** `ee9800b`
**Applied fix:** Added a new `protected Phase8WebAppFactory(bool skipPostgresFixture, string connectionStringOverride)` ctor and an early-return guard in `InitializeAsync` that bypasses the testcontainer construction when `_skipPostgresFixture` is set. Updated `HealthDeadPostgresFixture` to invoke the new ctor with `skipPostgresFixture: true, connectionStringOverride: DeadConnectionString`. Saves ~10 s × 4 facts = ~40 s of dev-loop time per run.

### WR-05: `MetricsExportTests.Test_HealthPath_Absent_From_HttpServerMetrics` has a TOCTOU window

**Files modified:** `tests/BaseApi.Tests/Observability/MetricsExportTests.cs`
**Commit:** `5bf0b53`
**Applied fix:** Two changes to the test body — (1) doubled the wait from 15 s to 30 s (`2 × scrape_interval`) so a missed scrape still has time to deliver samples in the second cycle; (2) added a positive-control loop that issues 5 hits to `/test-obs/ok` and a corresponding `positiveControl` query (`http_route!~".*health.*"`) asserted `NotEmpty` BEFORE the negative `Assert.Empty(healthSamples)` assertion. The positive control proves the Prom pipeline IS alive, distinguishing "filter dropped /health/*" from "collector silently broken."

---

_Fixed: 2026-05-28_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
