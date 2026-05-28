---
phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
fixed_at: 2026-05-28T00:00:00Z
review_path: .planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-REVIEW.md
iteration: 1
findings_in_scope: 12
fixed: 12
skipped: 0
status: all_fixed
---

# Phase 11: Code Review Fix Report (Iteration 1 — Re-Review Pass)

**Fixed at:** 2026-05-28
**Source review:** `.planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-REVIEW.md`
**Iteration:** 1 (fresh fix pass against the re-review; orthogonal to the prior WR-01..WR-05 pass)

**Summary:**
- Findings in scope: 12 (1 Warning + 11 Info — `--all` scope)
- Fixed: 12
- Skipped: 0

All findings from the re-review applied. Build is clean (0 warnings, 0 errors) after each fix. Per-finding atomic commits; no batched edits.

## Fixed Issues

### WR-A: WR-02 fix narrows but does not eliminate env-var leak

**Files modified:** `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs`
**Commit:** `3a6f98f`
**Applied fix:** Took option (a) from the review — tightened XML doc on `HealthDeadPostgresFixture` and `HealthLiveLocalhostFixture` to explicitly scope the WR-02 guarantee. Notes that (1) try/catch only protects against synchronous `SetEnvironmentVariable` throws; (2) `InitializeAsync` throw restoration depends on caller using `await using` discipline; (3) fixture is NOT nesting-safe across multiple env-var-mutating fixtures on the same key. Did not factor out the `EnvVarScope` helper from the review snippet — current `Observability` collection serialization invariant makes the inline pattern acceptable, and the larger refactor would touch more surface than warranted for the residual.

### IN-09: `Phase11WebAppFactory.DisposeAsync` unconditionally restores even when ctor skipped the set

**Files modified:** `tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs`
**Commit:** `c9482f7`
**Applied fix:** Symmetrized the dispose-side gate with the ctor-side gate per the review snippet. When `_priorOtlpEndpoint is null` (ctor performed the set), dispose clears the env var to `null`. When `_priorOtlpEndpoint` was non-null (ctor skipped the set), dispose leaves the env var untouched — preserving any third-value mutation made by code between ctor and dispose.

### IN-10: `Phase8WebAppFactory` `skipPostgresFixture: true` invariant not enforced at ctor

**Files modified:** `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs`
**Commit:** `2ebd9a6`
**Applied fix:** Added `ArgumentException` ctor-time validation matching the review snippet — `skipPostgresFixture=true` with empty/null `connectionStringOverride` now fails fast at construction with a clear message naming the offending parameter, rather than deferring to a confusing `InvalidOperationException` at first `ConnectionString` property read.

### IN-11: `HealthDeadPostgresFixture.ConfigureWebHost` writes DeadConnectionString twice (dead-code redundancy)

**Files modified:** `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs`
**Commit:** `364607a`
**Applied fix:** Dropped the `ConfigureWebHost` override block entirely. Replaced with a single comment block documenting why the override is no longer needed: the pre-WR-04 world required overriding base because base would write its own (throwaway-DB) connection string; after WR-04, base writes `_connectionStringOverride` = `DeadConnectionString`, so the override was writing the same key=value pair a second time. The redundant `AddInMemoryCollection` call and stale comment are now removed.

### IN-01: `HealthEndpointsTests.Test_HealthEndpoints_Absent_From_OTLP_Logs` — `probeBatchId` generated but never queried

**Files modified:** `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs`
**Commit:** `7cabe1a`
**Applied fix:** Took the "remove unused header" branch from the review. The `probeBatchId` GUID, `Guid.NewGuid():N` call, and `X-Probe-Batch-Id` header assignment are all removed. Replaced the stale "positive-control sentinel" comment with a note that future hardening can add a positive control via a parallel `test-obs ok ran` body-text probe.

### IN-02: `TestObservabilityController` — redundant field assignment on primary constructor

**Files modified:** `tests/BaseApi.Tests/Observability/TestObservabilityController.cs`
**Commit:** `3dc1587`
**Applied fix:** Removed the explicit `private readonly ILogger<...> _log = log;` field. Replaced `_log.LogInformation(...)` with `log.LogInformation(...)` so the primary constructor parameter is used directly. Behavior identical; no semantic change.

### IN-03: `compose.yaml` elasticsearch + prometheus services missing `restart:` policy

**Files modified:** `compose.yaml`
**Commit:** `9ca77f3`
**Applied fix:** Added `restart: unless-stopped` to both `elasticsearch` and `prometheus` services. Matches the existing pattern on `postgres`, `otel-collector`, and `baseapi-service`.

### IN-04: `PrometheusTestClient.PollPrometheusUntilSumAtLeast` — early-exit short-circuits when threshold is 0

**Files modified:** `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs`
**Commit:** `ae22f4d`
**Applied fix:** Dropped the `lastSamples.Count > 0 &&` gate from the early-exit condition. `SumSampleValues` on an empty list returns 0 — that compares correctly with arithmetic alone (`0 >= 0` true; `0 >= positive` false). Added a comment explaining the latent edge case.

### IN-05: `EsIndexNames.CorrelationIdFieldPath` — `term` query assumes dynamic mapping picked `keyword`

**Files modified:** `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs`
**Commit:** `9370e89`
**Applied fix:** Changed the constant value from `"attributes.CorrelationId"` to `"attributes.CorrelationId.keyword"`. ES 8.x default dynamic mapping creates a `text` field with a `fields.keyword` sub-field for string attributes, so the `.keyword` route is robust to any correlation-id format (current 32-hex GUIDs only worked because they accidentally survived the `standard` analyzer). All four query sites (`LogExportTests`, `LogLevelFilterTests`, `SchemasLogsE2ETests`, `HealthEndpointsTests`) consume this constant transitively, so a single edit propagates the fix. Note: requires human verification — the dynamic-mapping assumption is correct for default ES 8.x behavior but has not been validated against the live `logs-generic.otel-default` data stream in this fix pass.

### IN-06: `MetricsExportTests.Test_RuntimeMetric_ProcessRuntimeDotnet_Exported` — discarded warm-request status

**Files modified:** `tests/BaseApi.Tests/Observability/MetricsExportTests.cs`
**Commit:** `2f0bb78`
**Applied fix:** Replaced `_ = await client.GetAsync(...)` with `var warmResp = ...` + `Assert.Equal(HttpStatusCode.OK, warmResp.StatusCode)`. Runtime metric timer fires regardless of warm-request status, so a 500 response would silently let the test pass for the wrong reason; the assertion closes that escape hatch.

### IN-07: `compose.yaml` healthcheck idiom inconsistency

**Files modified:** `compose.yaml`
**Commit:** `0a64dfb`
**Applied fix:** Aligned the two `wget` invocations (prometheus, baseapi-service) to the same flag form: `wget --spider -q`. Did NOT switch to `curl -fs` per the review snippet because `prom/prometheus` and the ASP.NET Core runtime image do not reliably ship `curl`; switching to curl risked breaking the healthchecks. The `pg_isready` and elasticsearch `curl -fs` lines stay as-is — postgres uses its native readiness tool and the elasticsearch image ships curl. Added a comment documenting the curl-omission rationale.

### IN-08: `prometheus.yml` `scrape_timeout` redundant with default

**Files modified:** `prometheus.yml`
**Commit:** `e01da73`
**Applied fix:** Appended `"Prometheus default; explicit for clarity."` to the comment on the `scrape_timeout: 10s` line. Documents intent for future readers — the explicit-default declaration is now self-documenting as a clarity choice rather than an opaque override.

## Skipped Issues

None — all 12 in-scope findings produced applied fixes.

## Verification

- `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj` ran clean (0 warnings, 0 errors) after each substantive code change.
- All 12 fixes committed atomically (one finding per commit).
- No `REVIEW-FIX.md` self-commit — orchestrator will commit this artifact separately.

**Per-finding commit hashes (chronological):**
- WR-A: `3a6f98f`
- IN-09: `c9482f7`
- IN-10: `2ebd9a6`
- IN-11: `364607a`
- IN-01: `7cabe1a`
- IN-02: `3dc1587`
- IN-03: `9ca77f3`
- IN-04: `ae22f4d`
- IN-05: `9370e89`
- IN-06: `2f0bb78`
- IN-07: `0a64dfb`
- IN-08: `e01da73`

---

_Fixed: 2026-05-28_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
