---
phase: 05-observability-health-probes
reviewed: 2026-05-27T00:00:00Z
depth: standard
files_reviewed: 19
files_reviewed_list:
  - .gitignore
  - Directory.Packages.props
  - compose.yaml
  - compose/otel-collector-config.yaml
  - src/BaseApi.Core/BaseApi.Core.csproj
  - src/BaseApi.Core/Health/IStartupGate.cs
  - src/BaseApi.Core/Health/StartupCompletionService.cs
  - src/BaseApi.Core/Health/StartupHealthCheck.cs
  - src/BaseApi.Service/Program.cs
  - tests/.otel-out/.gitkeep
  - tests/BaseApi.Tests/Observability/CollectionDefinitions.cs
  - tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs
  - tests/BaseApi.Tests/Observability/LogExportTests.cs
  - tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs
  - tests/BaseApi.Tests/Observability/MetricsExportTests.cs
  - tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs
  - tests/BaseApi.Tests/Observability/OtelEndOfSuiteCleanup.cs
  - tests/BaseApi.Tests/Observability/TestObservabilityController.cs
  - tests/BaseApi.Tests/Observability/TraceExportTests.cs
findings:
  critical: 0
  warning: 3
  info: 6
  total: 9
status: issues_found
---

# Phase 5: Code Review Report

**Reviewed:** 2026-05-27
**Depth:** standard
**Files Reviewed:** 19
**Status:** issues_found

## Summary

Phase 5 wires the production observability surface (MEL→OTel logs bridge, metrics+traces with AspNetCore/Http/Runtime/Npgsql instrumentation, three K8s-style health probes backed by an atomic startup latch) and the verification battery (xUnit v3 fixtures + 5 fact-test classes reading the otel-collector file exporter's JSON-lines output).

Threat-model focus areas (T-05-PII, T-05-LOG-INJECT, T-05-READY-DB-EXPOSE, T-05-OTLP-EXFIL) are all addressed with positive regression tests; the production `Program.cs` shape (bare `.AddNpgsql()`, `UIResponseWriter.WriteHealthCheckUIResponse`, tag-predicate health endpoint mapping) is correct and minimal.

The `IStartupGate` / `StartupGate` / `StartupCompletionService` / `StartupHealthCheck` Core types are clean: thread-safe latch via `Volatile.Read` / `Interlocked.Exchange`, idempotent `MarkReady`, surface ready for the Phase 8 `MigrationRunner` swap. Documented deviations from CONTEXT (public-sealed vs internal-sealed, bare `.AddNpgsql()`, Collector-side metrics filter) are sound and well-justified in inline comments.

Findings below are mostly fixture-lifecycle robustness and test-determinism concerns; no security or correctness defects in production code paths.

## Warnings

### WR-01: Env-var restoration leaks if base WAF constructor throws mid-construction

**File:** `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs:218-226` (and `HealthEndpointsTests.cs:244-249`)

**Issue:** `HealthDeadPostgresFixture` and `HealthLiveLocalhostFixture` ctors set the process-wide `ConnectionStrings__Postgres` env var BEFORE the `WebApplicationFactory<Program>` base ctor runs:

```csharp
public HealthDeadPostgresFixture()
{
    _priorEnvValue = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
    Environment.SetEnvironmentVariable("ConnectionStrings__Postgres",
        "Host=localhost;Port=1;Database=postgres;Username=postgres;Password=postgres;Timeout=2");
}
```

Restoration happens in `DisposeAsync`, but `DisposeAsync` only runs if construction completes. If `WebApplicationFactory<Program>`'s lazy host build later throws (it's deferred to `CreateClient()`/`Server`, not the ctor itself in WAF 8.x, so this is currently low-likelihood) — or if a future refactor adds eager work to the base ctor — the env var stays mutated for the remainder of the test session, contaminating subsequent fixture instances and any other tests that read `ConnectionStrings__Postgres`.

The bigger concrete risk is if a test method throws between `new HealthDeadPostgresFixture()` and the `await using` binding — but `await using var factory = new HealthDeadPostgresFixture();` binds at declaration, so the disposer runs even if `InitializeAsync` throws. The main residual risk is ctor-internal throw before line 224's `SetEnvironmentVariable` returning, or process abort.

**Fix:** Wrap the mutation in a try/catch that captures the prior value first, then restores on any pre-base-init failure:

```csharp
public HealthDeadPostgresFixture()
{
    _priorEnvValue = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
    try
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres",
            "Host=localhost;Port=1;Database=postgres;Username=postgres;Password=postgres;Timeout=2");
    }
    catch
    {
        // SetEnvironmentVariable can throw ArgumentException/SecurityException — restore + rethrow
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", _priorEnvValue);
        throw;
    }
}
```

Even simpler and stricter: switch from process-wide env var to `ConfigureAppConfiguration` IN-MEMORY override (the planner-checker's documented blocker — `.AddNpgSql` captures the connection string at registration time — is solvable by also re-registering the NpgSql check inside `ConfigureTestServices` after removing the prior registration by descriptor). That eliminates the process-wide mutation entirely and removes the restoration dependency.

### WR-02: `_startPosition` skip-past offset breaks under file rotation

**File:** `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs:114-122, 198-201`

**Issue:** `InitializeAsync` captures `_startPosition = new FileInfo(TelemetryFile).Length`. `ReadAllExportedRecords` skips past that offset via `fs.Seek(_startPosition, SeekOrigin.Begin)`. The Collector's file exporter is configured (`compose/otel-collector-config.yaml:48`) with `rotation: { max_megabytes: 10, max_days: 1 }`. If the file rotates between `InitializeAsync` and `ReadAllExportedRecords`, the underlying file the host sees becomes the freshly truncated `telemetry.jsonl` (the previous content moved to a sibling like `telemetry-2026-05-27-1.jsonl`). In that case:

- `_startPosition` (e.g., 5,000,000 from before rotation) is greater than `fs.Length` (small, post-rotation), the `if (_startPosition > 0 && _startPosition <= fs.Length)` guard fails, and the reader reads from offset 0 — silently INCLUDING records that may belong to other test runs OR completely missing the current test's records that were written to the now-rotated sibling file.

The 10 MB rotation threshold makes this rare in normal CI runs, but accumulated multi-day local-dev sessions can hit it.

**Fix:** Record the inode/identity (via `File.GetLastWriteTimeUtc(TelemetryFile)` snapshot OR a per-test UUID written-then-read-back as a sentinel record) at `InitializeAsync`, and either fail-fast on rotation OR read across all `telemetry*.jsonl` siblings ordered by mtime when the live file is shorter than `_startPosition`. Minimum acceptable mitigation: when `_startPosition > fs.Length`, log a warning so the test author knows the file rotated and assertions over `ReadAllExportedRecords` may be cross-contaminated:

```csharp
if (_startPosition > 0)
{
    if (_startPosition <= fs.Length)
        fs.Seek(_startPosition, SeekOrigin.Begin);
    else
        // File rotated since InitializeAsync — emit warning so test interprets results correctly
        Console.Error.WriteLine(
            $"[OtelCollectorFixture] WARN — telemetry.jsonl rotated since InitializeAsync " +
            $"(_startPosition={_startPosition} > fs.Length={fs.Length}); reading from offset 0.");
}
```

### WR-03: OTel resource attributes only set on metrics+traces branch; logs branch uses a separate ResourceBuilder

**File:** `src/BaseApi.Service/Program.cs:85-95, 104-107`

**Issue:** Logs go through `builder.Logging.AddOpenTelemetry(o => { ... o.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(...)); })` (lines 85-95). Metrics+traces go through `builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService(...))` (lines 104-107).

These are two SEPARATE resource configurations. They happen to set the same `serviceName`/`serviceVersion` from the same config, so today the resources match. But:

1. If a future phase adds resource detectors (e.g., `r.AddTelemetrySdk()`, `r.AddEnvironmentVariableDetector()`, `r.AddProcessRuntimeDetector()`) to the metrics+traces chain via `ConfigureResource`, the logs branch will NOT inherit them — log records will be missing the `telemetry.sdk.*` / `host.name` / `process.runtime.*` resource attributes that traces and metrics carry. Cross-signal correlation tools (Datadog, Grafana) that join on resource fingerprint will see two distinct services for the same process.

2. If `serviceName`/`serviceVersion` is changed but only updated in one branch by mistake, the divergence is silent — no compile error, no test catches it (current tests only assert the LOGS resource carries `sk-api`/`3.2.0`).

**Fix:** Extract a single shared `ResourceBuilder` and use it on both branches:

```csharp
var resourceBuilder = ResourceBuilder.CreateDefault()
    .AddService(serviceName: serviceName, serviceVersion: serviceVersion);

builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes           = true;
    o.ParseStateValues        = true;
    o.SetResourceBuilder(resourceBuilder);
    o.AddOtlpExporter();
});

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(serviceName: serviceName, serviceVersion: serviceVersion))
    // ... rest unchanged
```

Note: `ConfigureResource(Action<ResourceBuilder>)` and `SetResourceBuilder(ResourceBuilder)` don't share a type, so you can't literally pass the same instance — but you can extract the configuration delegate (`Action<ResourceBuilder> configure = r => r.AddService(...)`) and apply it on both sides; or build the `ResourceBuilder` once and use `SetResourceBuilder(rb)` for logs while invoking the same `configure` callback on the metrics/traces side.

## Info

### IN-01: Hardcoded dev-default Postgres credentials in test fixtures

**File:** `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs:225, 248`

**Issue:** Connection strings contain `Username=postgres;Password=postgres` literally. These are the well-known Postgres dev defaults already published in `src/BaseApi.Service/appsettings.Development.json:10` and `compose.yaml`, so no actual secret is exposed. Flagging only because static scanners (Trivy, gitleaks, GitHub secret-scanning) may flag these as findings and create CI noise.

**Fix:** Optional — extract a `TestConstants.DevPostgresCredentials` or read from `appsettings.Development.json` so there's a single source of truth. Not required since the value is already publicly the same across the repo.

### IN-02: Bare `catch` swallows all exceptions in InitializeAsync file-length capture

**File:** `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs:116-118`

**Issue:** `catch { _startPosition = 0; }` catches Exception (and ArgumentException, etc.) without specificity. A genuine bug in `FileInfo(...)` construction (path traversal, access denied) would be silently swallowed, leaving `_startPosition = 0` and causing the reader to include ALL pre-existing records — potentially masking a test-environment misconfiguration.

**Fix:** Narrow to expected exceptions (`IOException`, `UnauthorizedAccessException`) and log on swallow:

```csharp
try { _startPosition = new FileInfo(TelemetryFile).Length; }
catch (IOException ex)
{
    Console.Error.WriteLine($"[OtelCollectorFixture] WARN — could not snapshot telemetry.jsonl length: {ex.Message}");
    _startPosition = 0;
}
```

### IN-03: Null-forgiving operator on configuration reads can mask missing-config NREs

**File:** `src/BaseApi.Service/Program.cs:42-43, 147`

**Issue:** `cfg["Service:Name"]!`, `cfg["Service:Version"]!`, `cfg.GetConnectionString("Postgres")!` all use the null-forgiving operator. If `appsettings.json` is malformed in production (e.g., missing `Service:Name`), `!` masks the null and the failure surfaces as an NRE deep inside the OTel `.AddService(null, null)` call or NpgSql connection-string parser, with no actionable error message.

**Fix:** Replace with explicit null checks that produce a clear error early:

```csharp
var serviceName = cfg["Service:Name"]
    ?? throw new InvalidOperationException("Missing required configuration: Service:Name");
var serviceVersion = cfg["Service:Version"]
    ?? throw new InvalidOperationException("Missing required configuration: Service:Version");

var connStr = cfg.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Missing required ConnectionStrings:Postgres");
builder.Services.AddHealthChecks()
    // ...
    .AddNpgSql(connStr, tags: new[] { "ready" });
```

### IN-04: Hard-coded 1-second pre-init delay couples test to Collector batching cadence

**File:** `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs:162`

**Issue:** `await Task.Delay(TimeSpan.FromSeconds(1), ct);` before `factory.InitializeAsync()` to let prior tests' buffered records drain. This is documented inline as a race-condition guard, but:

- 1 second is a magic number — under high CI load or with a slower Collector (smaller container CPU quota), 1 second may not be enough.
- Total suite time grows by 1 second per fixture instance.

**Fix:** Replace with a deterministic flush against the SHARED fixture's batches. Since `OtelCollectorFixture` exposes `FlushAsync`, call it on the prior test's fixture before disposal (or invoke it from an `[OnFinishedAttribute]`-equivalent). Alternatively, take the position marker AFTER a `FlushAsync` of the broader process's OTel providers, rather than relying on wall-clock delay.

### IN-05: `OTEL_EXPORTER_OTLP_ENDPOINT` env-var override is process-wide and never restored

**File:** `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs:94`

**Issue:** Every `OtelCollectorFixture` constructor calls `Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317")`. This is documented as intentional (T-05-OTLP-EXFIL pin) and explicitly flagged for Phase 6+. Calling this out here because:

- Non-Observability test classes that run AFTER an Observability test class will inherit the env-var pin. If any future test asserts the prod-default behavior of "env var unset → SDK default endpoint", it will silently see the test override and pass under false pretenses.
- The fix is already TODO'd in the source comments — just noting that it should land before Phase 6 to avoid a deferred-debt bottleneck.

**Fix:** Capture + restore on `DisposeAsync` (same pattern as the connection-string env-var). Acceptable to defer per inline source comments, but should not leave Phase 5.

### IN-06: `processors` block in compose/otel-collector-config.yaml has no top-level docstring on the regex anchor choice

**File:** `compose/otel-collector-config.yaml:42`

**Issue:** The OTTL filter expression uses `IsMatch(attributes["http.route"], "^/health/.*")`. The anchored `^/health/.*` correctly matches `/health/live`, `/health/ready`, `/health/startup`. However:

- ASP.NET Core's `http.route` attribute carries the ROUTE TEMPLATE (e.g., `/health/live`), not the literal request path. For these three endpoints, template and path are identical, so the match holds.
- If a future endpoint registers `/health/{check}` (parameterized), the `http.route` attribute will be the literal `/health/{check}` — still matches `^/health/.*`. Good.
- If `attributes["http.route"]` is missing on a given data point (unusual but possible for short-circuited requests), OTTL's `IsMatch` raises an error — the processor has `error_mode: ignore` so this fails open (the data point passes through unfiltered). Acceptable since other processors and tests would catch a misconfiguration.

This is a robustness observation, not a defect — the regex is correct. Flag for awareness so future authors don't accidentally tighten the regex to `/health/live|/health/ready|/health/startup` and break parameterized routes.

**Fix:** None required. Optionally add a brief comment in the YAML noting the deliberate `.*` wildcard:

```yaml
# Wildcard '.*' suffix intentional: matches both fixed routes (/health/live, /health/ready,
# /health/startup) AND any future parameterized route under /health/.
- 'metric.name == "http.server.request.duration" and IsMatch(attributes["http.route"], "^/health/.*")'
```

---

_Reviewed: 2026-05-27_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
