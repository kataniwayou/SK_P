---
phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
reviewed: 2026-05-28T00:00:00Z
depth: standard
files_reviewed: 19
files_reviewed_list:
  - .gitignore
  - compose.yaml
  - compose/otel-collector-config.yaml
  - prometheus.yml
  - src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs
  - tests/BaseApi.Tests/Observability/CollectionDefinitions.cs
  - tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs
  - tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs
  - tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs
  - tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs
  - tests/BaseApi.Tests/Observability/LogExportTests.cs
  - tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs
  - tests/BaseApi.Tests/Observability/MetricsExportTests.cs
  - tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs
  - tests/BaseApi.Tests/Observability/SchemasLogsE2ETests.cs
  - tests/BaseApi.Tests/Observability/SchemasMetricsE2ETests.cs
  - tests/BaseApi.Tests/Observability/TestObservabilityController.cs
  - tests/BaseApi.Tests/Validation/ValidationWebAppFactory.cs
  - tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs
findings:
  critical: 0
  warning: 1
  info: 11
  total: 12
status: issues_found
---

# Phase 11: Code Review Report (Re-Review)

**Reviewed:** 2026-05-28
**Depth:** standard
**Files Reviewed:** 19
**Status:** issues_found

## Summary

This re-review confirms the five Warning-level findings from the prior review (WR-01..WR-05) are addressed in the current source. Cancellation tokens are now threaded through every `await` in both `ElasticsearchTestClient` and `PrometheusTestClient` (WR-01); both env-var-mutating fixtures wrap `SetEnvironmentVariable` in `try`/`catch` restore blocks (WR-02); `Phase11WebAppFactory` now captures the prior `OTEL_EXPORTER_OTLP_ENDPOINT` value and restores it in `DisposeAsync` while declining to clobber an explicit operator setting (WR-03); `Phase8WebAppFactory` exposes a `skipPostgresFixture` ctor overload consumed by `HealthDeadPostgresFixture` to avoid spinning up an unused testcontainer (WR-04); and `MetricsExportTests.Test_HealthPath_Absent_From_HttpServerMetrics` now waits 30 s (2× scrape interval) and runs a positive-control + negative-assertion query pair against the same Prom snapshot (WR-05).

**No NEW Critical or Warning defects are introduced by the fixes.** One Warning (WR-A) is carried forward because the WR-02 try/catch only narrows — not eliminates — the env-var leak failure window; the larger nested-fixture / cross-collection bleed-through scenarios remain unresolved at the same severity as the original WR-02 noted. Several small INFO-level rough edges are introduced by the fix shapes (a redundant `AddInMemoryCollection` in `HealthDeadPostgresFixture.ConfigureWebHost`, an unguarded invariant on `Phase8WebAppFactory.skipPostgresFixture: true ⇒ override required`, an unconditional restore in `Phase11WebAppFactory.DisposeAsync`). The eight INFO items from the prior review remain unaddressed (out of scope for the Warning-only fix pass) — they are retained for traceability.

Overall the fix pass is **clean** with no regressions, but a small follow-up hardening pass on env-var lifecycle and `Phase8WebAppFactory` invariant enforcement would close the WR-A residual.

## Fix Verification (Previous Warnings)

| ID    | Status   | Notes |
|-------|----------|-------|
| WR-01 | RESOLVED | `PollEsForLog`, `PollPrometheusUntilSumAtLeast`, `QueryPrometheus` accept `CancellationToken ct = default` and thread it through every `await` (`SendAsync`, `GetAsync`, `ReadAsStringAsync`, `Task.Delay`) + call `ct.ThrowIfCancellationRequested()` at loop heads. |
| WR-02 | PARTIAL  | The `try`/`catch` around `SetEnvironmentVariable` in `HealthDeadPostgresFixture` (`HealthEndpointsTests.cs:247-255`) and `HealthLiveLocalhostFixture` (`HealthEndpointsTests.cs:296-305`) covers throws *inside* `SetEnvironmentVariable` itself (extremely rare). Throws *between* ctor return and `DisposeAsync` (e.g., `factory.InitializeAsync()` failure) still leak. See WR-A below. |
| WR-03 | RESOLVED | `Phase11WebAppFactory` ctor now captures `_priorOtlpEndpoint` and only sets the env var when it is `null` (doesn't clobber operator); `DisposeAsync` writes `_priorOtlpEndpoint` back. Minor IN-09 below. |
| WR-04 | RESOLVED | `Phase8WebAppFactory` added `protected Phase8WebAppFactory(bool skipPostgresFixture, string connectionStringOverride)` (`Phase8WebAppFactory.cs:59-63`); `InitializeAsync` early-returns when `_skipPostgresFixture` is true (`:75-78`); `HealthDeadPostgresFixture` consumes it (`HealthEndpointsTests.cs:236-238`). |
| WR-05 | RESOLVED | `MetricsExportTests.Test_HealthPath_Absent_From_HttpServerMetrics` now waits 30 s, issues 5 `/test-obs/ok` warm hits, and runs two single-shot queries (positive control + negative assertion) against the same snapshot (`MetricsExportTests.cs:62-107`). |

## Warnings

### WR-A: WR-02 fix narrows but does not eliminate env-var leak — `factory.InitializeAsync()` throw still pins the dead-port value

**File:** `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs:236-256` (`HealthDeadPostgresFixture`)
**File:** `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs:286-306` (`HealthLiveLocalhostFixture`)
**Issue:** The applied fix wraps only the `SetEnvironmentVariable` call in `try`/`catch`. That call itself rarely throws (only on argument-validation issues — name contains `=`, or process is exiting). The actual leak scenario the original WR-02 described is a throw *downstream* of the ctor:

```csharp
await using var factory = new HealthDeadPostgresFixture();   // env var set here, ctor returns OK
await factory.InitializeAsync();                              // throws here — DisposeAsync never runs cleanly
using var client = factory.CreateClient();
```

`await using` does call `DisposeAsync` on exception, so the restore DOES run in the C# 8+ `await using` path. BUT — if a future test author writes `var factory = new HealthDeadPostgresFixture();` (no `await using`) and `InitializeAsync` throws, the restore is silently skipped. The XML doc on lines 243-245 advertises full WR-02 coverage, which understates the residual surface.

Additionally, the nested-usage problem from the original WR-02 is unaddressed: if a future test creates one fixture while another is alive, the inner captures the outer's already-overridden value as `_priorEnvValue`. Disposing inner-then-outer restores `null` last (the outer's captured baseline), but disposing outer-first then inner makes the inner write the OUTER's mutation back — silently undoing the inner's restore. The `Observability` collection serialization currently prevents this, but the comment doesn't note the dependency.

**Fix:** Either (a) tighten the doc to explicitly scope the guarantee — "restore runs only when caller uses `await using` AND fixture is the only env-var mutator on the call stack" — or (b) move to a finalizer / SafeHandle pattern that guarantees restore regardless of caller discipline. Minimal doc fix:

```csharp
// WR-02 review fix: try/catch protects against SetEnvironmentVariable itself throwing.
// CALLERS MUST USE `await using` — disposing through that path is the only way the
// restore runs when InitializeAsync throws. Fixture is NOT safe to nest inside another
// env-var-mutating fixture (inner captures outer's mutation as the "prior" baseline).
```

For a stronger guarantee, factor out an `EnvVarScope` helper:

```csharp
public sealed class EnvVarScope : IDisposable
{
    private readonly string _name;
    private readonly string? _prior;
    public EnvVarScope(string name, string? value)
    {
        _name = name;
        _prior = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }
    public void Dispose() => Environment.SetEnvironmentVariable(_name, _prior);
}
```

Then fixtures hold an `EnvVarScope` field and dispose it deterministically. The current shape is acceptable for the present collection-serialization invariant; the Warning persists because the doc overstates coverage and the nested-fixture trap remains a footgun.

## Info

### IN-09: `Phase11WebAppFactory.DisposeAsync` unconditionally restores even when ctor skipped the set

**File:** `tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs:75-83`
**Issue:** The ctor gates the `SetEnvironmentVariable` call on `if (_priorOtlpEndpoint is null)` (line 69) — correctly skipping when an explicit operator setting exists. But `DisposeAsync` unconditionally writes `_priorOtlpEndpoint` back (line 81). When the ctor SKIPPED the set, `DisposeAsync` writes the prior value (same value already in the env) — a no-op, but if any code path between ctor and dispose mutates `OTEL_EXPORTER_OTLP_ENDPOINT` to a third value, that mutation is silently overwritten.

**Fix:** Symmetrize the gate:
```csharp
public override async ValueTask DisposeAsync()
{
    // Only restore if ctor performed the set — otherwise leave the env var as caller left it.
    if (_priorOtlpEndpoint is null)
    {
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);
    }
    await base.DisposeAsync();
}
```

### IN-10: `Phase8WebAppFactory` `skipPostgresFixture: true` invariant not enforced at ctor

**File:** `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs:59-63`
**Issue:** The new `(bool skipPostgresFixture, string connectionStringOverride)` ctor's XML doc (lines 52-58) states: "Callers MUST supply `connectionStringOverride`." The parameter is declared `string` (non-nullable), but C# nullable-reference-type checking is advisory — a caller passing `null!` (forced suppression) gets a runtime `InvalidOperationException` at first `ConnectionString` property read instead of at construction. Plus, when `_skipPostgresFixture: true` AND `_connectionStringOverride` were both wrong, no immediate failure surfaces.

**Fix:** Add ctor-time validation:
```csharp
protected Phase8WebAppFactory(bool skipPostgresFixture, string connectionStringOverride)
{
    if (skipPostgresFixture && string.IsNullOrEmpty(connectionStringOverride))
    {
        throw new ArgumentException(
            "skipPostgresFixture=true requires a non-empty connectionStringOverride.",
            nameof(connectionStringOverride));
    }
    _skipPostgresFixture = skipPostgresFixture;
    _connectionStringOverride = connectionStringOverride;
}
```

### IN-11: `HealthDeadPostgresFixture.ConfigureWebHost` writes DeadConnectionString twice (dead-code redundancy)

**File:** `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs:257-271`
**Issue:** The WR-04 fix routed the dead-port string through `Phase8WebAppFactory.ConnectionString` (via `_connectionStringOverride`), and `Phase8WebAppFactory.ConfigureWebHost` (lines 104-112) adds `["ConnectionStrings:Postgres"] = ConnectionString` to in-memory config. So when `HealthDeadPostgresFixture.ConfigureWebHost` calls `base.ConfigureWebHost(builder)`, the dead-port string is ALREADY in the in-memory configuration. The subsequent `AddInMemoryCollection` block at lines 264-270 adds the same dead-port string a SECOND time. The override comment (lines 259-262) still describes the pre-WR-04 world ("Phase8WebAppFactory.ConfigureWebHost adds its throwaway-DB conn string ... which would OVERRIDE the env-var dead-port value") — but the WR-04 fix made that no longer true.

This is dead code, not a bug. The second `AddInMemoryCollection` is harmless (writes the same key=value pair the base already wrote), but the comment is stale and the redundancy invites future confusion.

**Fix:** Drop the override block now that base handles the value correctly:
```csharp
private sealed class HealthDeadPostgresFixture : Phase8WebAppFactory
{
    private const string DeadConnectionString =
        "Host=localhost;Port=1;Database=postgres;Username=postgres;Password=postgres;Timeout=2";
    private readonly string? _priorEnvValue;

    public HealthDeadPostgresFixture()
        : base(skipPostgresFixture: true, connectionStringOverride: DeadConnectionString)
    {
        _priorEnvValue = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        try { Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", DeadConnectionString); }
        catch { Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", _priorEnvValue); throw; }
    }

    // ConfigureWebHost no longer needed — base correctly routes DeadConnectionString through.

    public override async ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", _priorEnvValue);
        await base.DisposeAsync();
    }
}
```

### IN-01: `HealthEndpointsTests.Test_HealthEndpoints_Absent_From_OTLP_Logs` — `probeBatchId` is generated but never queried (carried forward)

**File:** `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs:164-192`
**Issue:** A unique `probeBatchId` GUID is generated and attached to every probe request as the `X-Probe-Batch-Id` header (lines 164-165). The XML comment justifies this as a "positive-control sentinel" to distinguish "filter works" from "transport silently dropped everything." But the only ES query performed (lines 184-191) searches for the substring `/health/` — `probeBatchId` is never used in any assertion or query. The defensive value advertised in the comment is not actually realized.

**Fix:** Either remove the unused header (keep the test smaller) or add the promised positive-control assertion. See prior REVIEW.md IN-01.

### IN-02: `TestObservabilityController` — redundant field assignment on primary constructor (carried forward)

**File:** `tests/BaseApi.Tests/Observability/TestObservabilityController.cs:27-37`
**Issue:** Primary constructor captures `log` implicitly; the explicit `private readonly ILogger<...> _log = log;` field at line 29 is redundant. `log.LogInformation(...)` works identically.

**Fix:** Use the primary-ctor parameter directly. See prior REVIEW.md IN-02.

### IN-03: `compose.yaml` elasticsearch + prometheus services missing `restart:` policy (carried forward)

**File:** `compose.yaml:28-48, 85-109`
**Issue:** `postgres`, `otel-collector`, and `baseapi-service` declare `restart: unless-stopped`. `elasticsearch` and `prometheus` do not. Inconsistency could surprise operators.

**Fix:** Add `restart: unless-stopped` to both services. See prior REVIEW.md IN-03.

### IN-04: `PrometheusTestClient.PollPrometheusUntilSumAtLeast` — early-exit logic short-circuits when threshold is 0 (carried forward)

**File:** `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs:68-78`
**Issue:** The loop guard `if (lastSamples.Count > 0 && SumSampleValues(lastSamples) >= threshold)` short-circuits to false on empty result vectors even when `threshold == 0` (which would be trivially met). No current caller passes 0; latent edge case.

**Fix:** Drop the `lastSamples.Count > 0 &&` gate. See prior REVIEW.md IN-04.

### IN-05: `EsIndexNames.CorrelationIdFieldPath` — `term` query assumes dynamic mapping picked `keyword` (carried forward)

**File:** `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs:46-49`
**File:** `tests/BaseApi.Tests/Observability/LogExportTests.cs:50-62, 102-114`
**File:** `tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs:50-55, 79-84`
**File:** `tests/BaseApi.Tests/Observability/SchemasLogsE2ETests.cs:83-88`
**Issue:** Queries use ES `term` filter against `attributes.CorrelationId` (analyzed text field). 32-hex GUIDs accidentally survive the `standard` analyzer; if a future correlation-id format adds dashes, `term` queries silently fail to match. Robust query: use `.keyword` sub-field.

**Fix:** Either change the constant to `"attributes.CorrelationId.keyword"` or add an explicit index template. See prior REVIEW.md IN-05.

### IN-06: `MetricsExportTests.Test_RuntimeMetric_ProcessRuntimeDotnet_Exported` — discarded warm-request status (carried forward)

**File:** `tests/BaseApi.Tests/Observability/MetricsExportTests.cs:116`
**Issue:** `_ = await client.GetAsync("/test-obs/ok", ct);` discards the response. If the warm request returns 500 (logger DI broken), runtime metrics still fire on a timer and the test passes for the wrong reason. Repeats in `LogExportTests` (line 89) and `HealthEndpointsTests` (lines 171-173) — in Health it's documented and intentional; in `Test_RuntimeMetric` it is not.

**Fix:** Either `Assert.Equal(HttpStatusCode.OK, warmResp.StatusCode)` or add an inline comment noting the intent. See prior REVIEW.md IN-06.

### IN-07: `compose.yaml` healthcheck idiom inconsistency (carried forward)

**File:** `compose.yaml:18, 44, 105, 123`
**Issue:** Four different probe-tool idioms across services (`pg_isready`, `curl -fs`, `wget --spider`, `wget --spider -q`). Code-quality nit; standardize HTTP services on `curl -fs ... || exit 1`. See prior REVIEW.md IN-07.

### IN-08: `prometheus.yml` `scrape_timeout` redundant with default (carried forward)

**File:** `prometheus.yml:11`
**Issue:** `scrape_timeout: 10s` is the Prometheus default; explicit-default is fine for clarity but expands maintenance surface. Comment should note "Prometheus default; explicit for clarity." See prior REVIEW.md IN-08.

---

_Reviewed: 2026-05-28_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
