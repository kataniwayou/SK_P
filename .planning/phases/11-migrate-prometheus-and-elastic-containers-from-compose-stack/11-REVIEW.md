---
phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
reviewed: 2026-05-28T00:00:00Z
depth: standard
files_reviewed: 17
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
findings:
  critical: 0
  warning: 5
  info: 8
  total: 13
status: issues_found
---

# Phase 11: Code Review Report

**Reviewed:** 2026-05-28
**Depth:** standard
**Files Reviewed:** 17 (one path in config — `tests/BaseApi.Tests/Validation/ValidationWebAppFactory.cs` — was reviewed but counted with the 17 in scope; total above includes only source/config files actually present and reviewed)
**Status:** issues_found

## Summary

Phase 11 migrates the Phase 5 file-exporter telemetry pipeline to a real backend stack: OTel Collector → Elasticsearch (logs) and OTel Collector → Prometheus (metrics). The new compose services, collector config, and test helpers are well-documented (extensive XML doc cross-references to RESEARCH pitfalls and decision records) and the migration is internally consistent with sk2_1's lock-in posture.

No **Critical** security or correctness defects were found. The 5 **Warning**-level items are mostly robustness / cancellation gaps in the new HTTP polling helpers and a couple of test-isolation rough edges in `HealthEndpointsTests`. The 8 **Info** items cover dead-effort code, minor compose inconsistencies, and documentation gaps. None of these block the phase — they are clean-up opportunities for a follow-up hardening pass.

The overall code quality is **high** for new test code: per-test correlation IDs are used consistently for ES isolation (Pitfall 5), Prom polling honors the mandatory 15s pre-sleep (Pitfall 7), and version-coupled assertions are explicitly avoided (Checker WARNING #7).

## Warnings

### WR-01: HTTP polling helpers ignore CancellationToken — tests cannot be canceled cleanly

**File:** `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs:62-107`
**File:** `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs:58-110`
**Issue:** Neither `PollEsForLog`, `PollPrometheusUntilSumAtLeast`, nor `QueryPrometheus` accepts a `CancellationToken`. All inner `await` calls — `_es.SendAsync(req)`, `resp.Content.ReadAsStringAsync()`, `_prom.GetAsync(url)`, and `Task.Delay(...)` — pass no CT. xUnit's `TestContext.Current.CancellationToken` is captured by callers but never threaded through. If a test is canceled (test timeout, `Ctrl+C`, the suite's own timeout enforcement), the polling loop continues for the full 30 s / 60 s budget. Worst case: a hung `SendAsync` against an unreachable backend can keep the test process alive past cancellation. The Prom client's `InitialSleepMs = 15_000` blocking delay is also uncancellable, which compounds the issue.

**Fix:**
```csharp
public async Task<JsonElement?> PollEsForLog(
    string queryBody, int timeoutMs, string? indexPath = null, CancellationToken ct = default)
{
    indexPath ??= EsIndexNames.LogsDataStream;
    var sw    = Stopwatch.StartNew();
    var delay = InitialDelayMs;
    while (sw.ElapsedMilliseconds < timeoutMs)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{indexPath}/_search")
            {
                Content = new StringContent(queryBody, Encoding.UTF8, "application/json"),
            };
            using var resp = await _es.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                // ... unchanged
            }
        }
        catch (HttpRequestException) { /* retry */ }
        var remaining = (int)(timeoutMs - sw.ElapsedMilliseconds);
        if (remaining <= 0) break;
        await Task.Delay(Math.Min(delay, remaining), ct);
        delay = Math.Min(delay * 2, MaxDelayMs);
    }
    return null;
}
```
Apply the same `CancellationToken ct = default` parameter to all three Prom helpers and thread it through every `await`. Call sites already have `TestContext.Current.CancellationToken` available.

---

### WR-02: `HealthDeadPostgresFixture` / `HealthLiveLocalhostFixture` mutate process-wide env vars without re-entrancy safety

**File:** `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs:226-283`
**Issue:** Both fixtures set `ConnectionStrings__Postgres` in the constructor and restore the captured prior value in `DisposeAsync`. The `Observability` collection serializes facts, but the fixtures themselves are short-lived (constructed per-fact via `new`). If a fact throws between `new` and `DisposeAsync` (e.g., `factory.InitializeAsync()` throws because Postgres is genuinely down), the restore never runs — the env var stays pinned to the dead string for the rest of the process, which would cascade-poison any subsequent test in any collection that reads `ConnectionStrings__Postgres` from environment.

Additionally, the capture/restore pair is not idempotent under nested usage: if a future test ever creates one of these fixtures while another is still alive, the inner fixture captures the outer's already-overridden value as the "prior" — disposing the inner first restores nothing, then disposing the outer restores `null` (the original baseline), leaving the inner's mutation silently undone. The pattern works today only because the collection serializes everything.

**Fix:** Wrap the constructor body in a try/catch that restores on failure, or use a finalizer / disposable-scope helper. Minimal fix:
```csharp
public HealthDeadPostgresFixture()
{
    _priorEnvValue = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
    try
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", DeadConnectionString);
    }
    catch
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", _priorEnvValue);
        throw;
    }
}
```
For long-term safety, consider a `using var scope = EnvVarScope.Set("ConnectionStrings__Postgres", value);` helper that restores on `Dispose()` deterministically.

---

### WR-03: `Phase11WebAppFactory` env-var mutation leaks across collections

**File:** `tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs:62-66`
**Issue:** The constructor unconditionally sets `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317` and the XML doc explicitly says this is "NOT cleared on DisposeAsync — same posture as the retired Phase 5 fixture; the var stays pinned for the test process lifetime." With xUnit running multiple test collections in parallel processes (each AppDomain) this is generally fine, but within a single test process this env var bleeds into Validation, Composition, and Phase 4 collections that may NOT want OTLP exporters firing at localhost:4317 (which is unreachable in CI without Docker, producing noisy gRPC connection-refused error logs every export interval).

The pre-Phase-11 fixture had the same problem and it was explicitly deferred ("flagged for Phase 6+" per `.planning/phases/05-observability-health-probes/05-02-PLAN.md` INFO #3). Phase 11 carries it forward unchanged. The leak is now reaching a wider blast radius (every test class that builds a host with the default OTel SDK wiring will attempt OTLP export).

**Fix:** Capture-and-restore in DisposeAsync, even though InfraN test ordering makes this best-effort. Better: gate on whether the var is already set so we don't clobber an integration-test runner that legitimately wants a different endpoint:
```csharp
internal Phase11WebAppFactory(string? logLevelDefaultOverride)
{
    _logLevelDefaultOverride = logLevelDefaultOverride;
    _priorOtlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
    if (_priorOtlpEndpoint is null)  // don't clobber an explicit operator setting
    {
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317");
    }
}

public override async ValueTask DisposeAsync()
{
    Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", _priorOtlpEndpoint);
    await base.DisposeAsync();
}
```

---

### WR-04: `HealthDeadPostgresFixture` still spins up a real Postgres container it never uses

**File:** `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs:226-260`
**Issue:** The fixture inherits `Phase8WebAppFactory`, whose `InitializeAsync` unconditionally constructs a `PostgresFixture` (a real testcontainer) and waits for it to become ready (`Phase8WebAppFactory.cs:47-58`). The dead-port override only affects the conn string read by `AddNpgSql`. The testcontainer overhead — Docker `docker.elastic.co/...:postgres pull`, ~5-10 s container boot, then teardown — is pure waste for the four tests that use `HealthDeadPostgresFixture` (since they explicitly want Postgres to be UNREACHABLE).

This is a *test-only* performance smell, not a bug. But the dev-loop cost is non-trivial: ~10 s × 4 facts = 40 s wasted per run on a fixture that should be near-instant.

**Fix:** Add a `Phase8WebAppFactory` ctor overload that skips testcontainer creation:
```csharp
// Phase8WebAppFactory.cs
protected Phase8WebAppFactory(bool skipPostgresFixture, string connectionStringOverride)
{
    _skipFixture = skipPostgresFixture;
    _connectionStringOverride = connectionStringOverride;
}

public async ValueTask InitializeAsync()
{
    if (_skipFixture) return;          // dead-port tests never need Postgres
    if (_connectionStringOverride is null) { /* unchanged */ }
}
```
Then `HealthDeadPostgresFixture` calls the new ctor with `skipPostgresFixture: true, DeadConnectionString`.

---

### WR-05: `MetricsExportTests.Test_HealthPath_Absent_From_HttpServerMetrics` has a TOCTOU window

**File:** `tests/BaseApi.Tests/Observability/MetricsExportTests.cs:62-88`
**Issue:** The test issues 10 `/health/live` probes, sleeps 15 s, then runs a SINGLE Prom query. If a sample arrives in the scrape cycle AFTER `Task.Delay` returns but BEFORE `QueryPrometheus` runs (the test process could be paged out between those two calls on a loaded CI runner), the query catches it and the test fails. More importantly, the test does NOT issue a positive-control "this poll batch was here" check — if the collector silently drops the metric for an UNRELATED reason (collector restart, filterprocessor misconfig), the test passes spuriously even when the filter is broken.

The 15 s wait is also the bare minimum scrape interval. A single missed scrape (timeout, network blip) defers sample arrival into the second scrape cycle (30 s in), making the "wait 15 s then query once" pattern fragile.

**Fix:** Two improvements:
1. Wait 2 × `scrape_interval` (30 s) so a missed scrape still gets a chance.
2. Add a positive control: also query for the `/test-obs/ok` route count and assert it's > 0 (already done for the first fact — could be combined). This proves "the Prom pipeline IS receiving samples, the filter IS the reason `/health/*` is empty."

```csharp
await Task.Delay(30_000, ct);   // 2 × scrape_interval — accommodate a missed scrape

const string positiveControl =
    """http_server_request_duration_seconds_count{service_name="sk-api",http_route!~".*health.*"}""";
const string negativeQuery =
    """http_server_request_duration_seconds_count{service_name="sk-api",http_route=~".*health.*"}""";

using var prom = new PrometheusTestClient();
var positiveSamples = await prom.QueryPrometheus(positiveControl);
var healthSamples   = await prom.QueryPrometheus(negativeQuery);

Assert.NotEmpty(positiveSamples);  // pipeline is alive
Assert.Empty(healthSamples);       // filter dropped /health/*
```

## Info

### IN-01: `HealthEndpointsTests.Test_HealthEndpoints_Absent_From_OTLP_Logs` — `probeBatchId` is generated but never queried

**File:** `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs:164-192`
**Issue:** A unique `probeBatchId` GUID is generated and attached to every probe request as the `X-Probe-Batch-Id` header (lines 164-165). The XML comment justifies this as a "positive-control sentinel" to distinguish "filter works" from "transport silently dropped everything." But the only ES query performed (lines 184-191) searches for the substring `/health/` — the `probeBatchId` is never used in any assertion or query. The defensive value advertised in the comment is not actually realized.

**Fix:** Either remove the unused header (keep the test smaller) or add the promised positive-control assertion:
```csharp
// After the negative assertion, prove the probe batch DID reach OTLP at all
// (otherwise our "no /health/* hits" is meaningless — could mean transport dropped everything).
// We expect ZERO hits for the batch id too because /health/* is filtered, but if a NON-health
// log entry exists carrying our header (e.g., from middleware logging the request headers),
// we'd see it. Skipping for now — see IN-01.
```
Or document that the header is reserved for future use.

### IN-02: `TestObservabilityController` — redundant field assignment on primary constructor

**File:** `tests/BaseApi.Tests/Observability/TestObservabilityController.cs:27-37`
**Issue:** The primary constructor `TestObservabilityController(ILogger<TestObservabilityController> log)` already captures `log` as an implicit private field. Line 29 explicitly creates `private readonly ILogger<TestObservabilityController> _log = log;` then uses `_log.LogInformation(...)` at line 37. The explicit `_log` field is redundant — `log.LogInformation(...)` works identically and matches the project's primary-constructor idiom elsewhere.

**Fix:** Remove the explicit field, use the primary-ctor parameter directly:
```csharp
public sealed class TestObservabilityController(ILogger<TestObservabilityController> log) : ControllerBase
{
    [HttpGet("ok")]
    public IActionResult Ok2xx()
    {
        log.LogInformation("test-obs ok ran");
        return Ok(new { ok = true });
    }
    // ...
}
```

### IN-03: `compose.yaml` elasticsearch service missing `restart` policy

**File:** `compose.yaml:28-48`
**Issue:** `postgres`, `otel-collector`, and `baseapi-service` all declare `restart: unless-stopped`. The new `elasticsearch` service does not. The `prometheus` service is also missing `restart:`. Inconsistency could surprise operators expecting "compose up; leave it; survives single restarts."

**Fix:** Add `restart: unless-stopped` to both `elasticsearch` and `prometheus` for parity:
```yaml
elasticsearch:
  image: docker.elastic.co/elasticsearch/elasticsearch:8.15.5
  container_name: sk-elasticsearch
  restart: unless-stopped
  # ...
```

### IN-04: `PrometheusTestClient.PollPrometheusUntilSumAtLeast` — early-exit logic short-circuits when threshold is 0

**File:** `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs:58-78`
**Issue:** The loop guard is `if (lastSamples.Count > 0 && SumSampleValues(lastSamples) >= threshold)`. If `threshold == 0` is ever passed (unusual but legal), the first arm short-circuits to `false` whenever the result vector is empty, and the loop keeps polling even though the threshold is already met. Conversely, if the caller passes `threshold == 0` intending "any samples at all," the current code requires at least one sample AND `Sum >= 0` (trivially true) — accidentally correct, but reads wrong.

No current caller passes 0, so this is latent. Worth tightening:

**Fix:**
```csharp
while (elapsed < PollTimeoutMs)
{
    if (SumSampleValues(lastSamples) >= threshold)
    {
        return lastSamples;
    }
    // ...
}
```
Drop the `lastSamples.Count > 0 &&` gate — `SumSampleValues` returns 0 for empty input, which compares correctly against any threshold.

### IN-05: `EsIndexNames.CorrelationIdFieldPath` — `term` query assumes dynamic mapping picked `keyword`, not `text`

**File:** `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs:46-49`
**File:** `tests/BaseApi.Tests/Observability/LogExportTests.cs:53-62`
**File:** `tests/BaseApi.Tests/Observability/SchemasLogsE2ETests.cs:83-88`
**Issue:** Queries use ES `term` filter against `attributes.CorrelationId`. ES dynamic mapping for string fields creates BOTH `attributes.CorrelationId` (text, analyzed — for `match` queries) AND `attributes.CorrelationId.keyword` (keyword, not analyzed — for `term` queries). A `term` query against the un-suffixed `text` field can fail to match because the analyzer lowercases or tokenizes the value at index time, while `term` queries do not run the analyzer at query time.

A 32-hex GUID is all-lowercase + no tokenization happens at word boundaries, so the default `standard` analyzer accidentally preserves it as one token — the current tests likely pass. But if a future correlation id format adds dashes (`f47ac10b-58cc-...`), the analyzer splits on `-` and `term` queries silently fail to match. The robust query is `{"term": {"attributes.CorrelationId.keyword": "..."}}`.

**Fix:** Use the keyword sub-field, OR add an explicit index template that maps `attributes.CorrelationId` as `keyword`. Minimum-disruption change:
```csharp
public const string CorrelationIdFieldPath = "attributes.CorrelationId.keyword";
```
Then re-run Wave 0 to confirm the keyword sub-field exists (it should under default ES dynamic mapping).

### IN-06: `MetricsExportTests.Test_RuntimeMetric_ProcessRuntimeDotnet_Exported` — `_ = await ...` discards exception path

**File:** `tests/BaseApi.Tests/Observability/MetricsExportTests.cs:97`
**Issue:** `_ = await client.GetAsync("/test-obs/ok", ct);` discards the response and any non-200 status. If the test endpoint genuinely returns 500 (e.g., logger DI broken), the test still polls Prom for runtime metrics, finds some (runtime metrics are emitted on a timer independent of the request), and passes. The "warm" request becomes meaningless yet the test passes for the wrong reason.

This pattern repeats in `LogExportTests.cs` (lines 87-90 — `_ = await client.SendAsync`) and `HealthEndpointsTests.cs:170-173`. In Health tests it's documented and intentional (status codes are intentionally ignored — "the path-string negation is what is being verified"). In `Test_RuntimeMetric` it is not documented.

**Fix:** Either add `Assert.Equal(HttpStatusCode.OK, warmResp.StatusCode)` after the warm request, or document the intent inline:
```csharp
// Warm a request so the runtime instrumentation has fired at least once.
// Status code ignored — runtime metrics are emitted on the SDK's periodic timer
// independent of request success; the request only exists to bootstrap the host.
_ = await client.GetAsync("/test-obs/ok", ct);
```

### IN-07: `compose.yaml` healthcheck strings — multiple sentinels duplicated across services

**File:** `compose.yaml:18, 44, 105, 123`
**Issue:** Each service writes its own healthcheck `test:` array with a different probe tool (`pg_isready`, `curl`, `wget --spider`, `wget --spider -q`). Postgres uses `pg_isready`; ES uses `curl -fs`; Prometheus uses `wget --spider`; baseapi-service uses `wget --spider -q`. Code-quality nit: four different idioms make the file harder to scan and harder to update if one tool changes. Standardize on `curl -fs ... || exit 1` for HTTP services (already the ES pattern).

**Fix:** Standardize HTTP healthchecks on `curl -fs <url> || exit 1` for the three HTTP services. Postgres legitimately needs `pg_isready`. Not load-bearing — only run this on a wider compose cleanup.

### IN-08: `prometheus.yml` `scrape_timeout` redundant with default

**File:** `prometheus.yml:11`
**Issue:** `scrape_timeout: 10s` is documented as "Must be < scrape_interval per Prometheus validation" but it is also the Prometheus default — explicit-default is fine for documentation but adds a maintenance surface (someone reading the file may think "I should tune this for my scrape latency budget" when it is in fact load-bearing-default).

No fix required — flagging for awareness. If kept, expand the inline comment to read `# Prometheus default; explicit for clarity. Must be < scrape_interval.`

---

_Reviewed: 2026-05-28_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
