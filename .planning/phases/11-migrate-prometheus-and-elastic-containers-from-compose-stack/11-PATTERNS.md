# Phase 11: Migrate Prometheus and Elastic containers from compose stack sk2_1 to sk_p - Pattern Map

**Mapped:** 2026-05-28
**Files analyzed:** 17 (4 modify, 1 modify-or-replace, 2 new YAML/source-config, 6 test mutations, 4 new test files, 2 deletions, 1 doc edit, 1 gitignore edit)
**Analogs found:** 16 / 17 (only `prometheus.yml` at repo root has no in-repo analog of identical role — it borrows shape from `compose/otel-collector-config.yaml` as the closest "root-level YAML config consumed by a compose service via bind-mount")

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `compose.yaml` (modify) | config / compose-stack | service-orchestration | `compose.yaml` (self — extend existing `postgres` + `otel-collector` + `baseapi-service` blocks) | exact (self-extension) |
| `compose/otel-collector-config.yaml` (modify or replace) | config / collector-pipeline | telemetry transform + export | `compose/otel-collector-config.yaml` (self — re-wire existing receivers/processors/exporters/service blocks) | exact (self-evolution) |
| `prometheus.yml` (new, repo root) | config / scrape-target list | metrics pull (Prom-side) | `compose/otel-collector-config.yaml` (closest YAML-config-consumed-by-compose-service via bind-mount; no scrape-config analog exists in sk_p) | role-match only (YAML config bind-mounted from repo root/subdir; semantics differ) |
| `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` (modify) | service-extension / OTel SDK registration | startup DI registration | self (strip `.WithTracing(...)` from existing chain; keep MEL + Metrics blocks byte-identical) | exact (self-mutation, narrow surgical delete) |
| `src/BaseApi.Service/Program.cs` (likely unchanged) | composition root | startup wiring | self | N/A — D-03 wiring is centralized in extension method; Program.cs may not need touching |
| `tests/BaseApi.Tests/Observability/LogExportTests.cs` (modify) | test-fact / observability E2E | HTTP request → ES poll | self (replace `ReadExportedLogs() + telemetry.jsonl` body with `ElasticsearchTestClient.PollEsForLog`) | exact (self-evolution, fixture swap) |
| `tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` (modify) | test-fact / negative log filter | HTTP request → ES poll (assert empty) | self (same pattern as LogExportTests) | exact (self-evolution) |
| `tests/BaseApi.Tests/Observability/MetricsExportTests.cs` (modify) | test-fact / observability E2E | HTTP request → Prom poll | self (replace `ReadExportedMetrics()` with `PrometheusTestClient.PollPrometheusUntilSumAtLeast`) | exact (self-evolution, fixture swap) |
| `tests/BaseApi.Tests/Observability/TraceExportTests.cs` (DELETE) | — | — | N/A | N/A (file deletion only) |
| `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` (DELETE or restructure) | test-fixture / WebApplicationFactory subclass | in-process Kestrel host | `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs` (target replacement shape) | role-match (replacement preserves WAF + ApplicationPart pattern, drops file-exporter logic) |
| `tests/BaseApi.Tests/Observability/OtelEndOfSuiteCleanup.cs` (DELETE) | — | — | N/A | N/A (file deletion only) |
| `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs` (NEW) | test-helper / HTTP polling client | request-response + retry loop | `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` (closest existing "telemetry-read" helper; we replace JSON-lines file reads with HTTP poll loop) | role-match (same intent — read telemetry; different transport) |
| `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` (NEW) | test-helper / HTTP polling client | request-response + retry loop | same as ES client | role-match |
| `tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs` (NEW) | test-fixture / WebApplicationFactory subclass | in-process Kestrel host with OTel-export-interval override | `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs` + Phase 5 `OtelCollectorFixture` (combine env-var pin + AddApplicationPart + drop file-exporter logic + add `PeriodicExportingMetricReaderOptions` override) | role-match (composes patterns from two existing factories) |
| `tests/BaseApi.Tests/Observability/RoundTripE2ETests.cs` (NEW) | test-fact / end-to-end backend smoke | HTTP request → poll ES + Prom | `tests/BaseApi.Tests/Integration/SchemasIntegrationTests.cs` (closest existing integration-fact pattern using `Phase8WebAppFactory` + `[Trait("Phase","N")]`) | role-match (HTTP-driving fact pattern; observability assertion path is new) |
| `.gitignore` (modify) | config / VCS exclusion | — | self (remove lines 414-421 only) | exact (self-mutation) |
| `.planning/REQUIREMENTS.md` (modify) | doc / requirement spec | — | self | exact (self-mutation, Phase 10 doc-first commit precedent) |

## Pattern Assignments

### `compose.yaml` (modify — add elasticsearch + prometheus, mutate otel-collector + baseapi-service)

**Analog:** `compose.yaml` (self — extend the existing `postgres`, `otel-collector`, and `baseapi-service` service blocks)

**Existing `postgres` block as the verbatim healthcheck + `restart: unless-stopped` template** (lines 6-22 of current `compose.yaml`):
```yaml
postgres:
  image: postgres:17-alpine
  restart: unless-stopped
  environment:
    POSTGRES_DB: ${POSTGRES_DB}
    POSTGRES_USER: ${POSTGRES_USER}
    POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
  ports:
    - "5433:5432"
  volumes:
    - pgdata:/var/lib/postgresql/data
  healthcheck:
    test: ["CMD-SHELL", "pg_isready -U $$POSTGRES_USER -d $$POSTGRES_DB"]
    interval: 5s
    timeout: 5s
    retries: 10
    start_period: 5s
```
- **Pattern to copy for `elasticsearch` + `prometheus`:** image pin (exact-version), `restart: unless-stopped` (optional — sk2_1 omits restart for ES/Prom; pick one consistent with sk_p's existing posture which always uses `unless-stopped`), `healthcheck:` with CMD-SHELL form, `interval`/`timeout`/`retries`/`start_period` quartet — D-12 prescribes `start_period: 60s` for ES (Pitfall 6).

**Existing `otel-collector` block as the bind-mount + ports + distroless-no-healthcheck template** (lines 24-57 of current `compose.yaml`):
```yaml
otel-collector:
  image: otel/opentelemetry-collector-contrib:0.95.0     # <-- D-09: bump to 0.152.0
  container_name: sk-otel-collector
  restart: unless-stopped
  command: ["--config=/etc/otel-collector-config.yaml"]
  user: "0:0"                                            # <-- D-14: REMOVE (no file exporter anymore)
  volumes:
    - ./compose/otel-collector-config.yaml:/etc/otel-collector-config.yaml:ro
    - ./tests/.otel-out:/var/otel-out                    # <-- D-05 / D-14: REMOVE
  ports:
    - "4317:4317"
    - "4318:4318"
    - "13133:13133"
    # <-- D-14: ADD "8889:8889" (Prom scrape endpoint)
  # NOTE: distroless image — NO healthcheck declared (Pitfall 3)
```
- **Pattern to copy for new elasticsearch + prometheus blocks:** distroless caveat does NOT apply to ES/Prom (both have shell + wget/curl in their images); declare full healthcheck. The existing collector block's NO-HEALTHCHECK comment block (lines 47-57) is load-bearing context — keep it intact for the bumped image (0.152.0 is still distroless).

**Existing `baseapi-service.depends_on` block as the multi-condition template** (lines 76-80):
```yaml
depends_on:
  postgres:
    condition: service_healthy
  otel-collector:
    condition: service_started
```
- **Pattern to copy:** D-15 extends this block with `elasticsearch: { condition: service_healthy }` and `prometheus: { condition: service_healthy }`. Preserve the existing two entries byte-identical.

**Deviation notes for `compose.yaml`:**
- The existing `postgres` block uses `${POSTGRES_DB}` env-var interpolation — ES/Prom blocks per D-12/D-13 use INLINE env values (no secrets, dev posture).
- The new `elasticsearch` service uses `container_name: sk-elasticsearch` and `prometheus` uses `container_name: sk-prometheus` (Pitfall 4 — mutually exclusive with sk2_1 stack).
- D-15 says `baseapi-service.depends_on` chain extends to 4 entries; preserve existing 2-entry indentation style.
- Compose service ordering inside the file is Claude's Discretion (CONTEXT). Current ordering is `postgres → otel-collector → baseapi-service` (depends-on order). Recommend: `postgres → elasticsearch → otel-collector → prometheus → baseapi-service` (preserves the depends-on-order discipline + ES before collector + Prom after collector since Prom `depends_on: otel-collector` per D-13).

---

### `compose/otel-collector-config.yaml` (modify or replace)

**Analog:** `compose/otel-collector-config.yaml` (self — re-shape existing `receivers` + `processors` + `exporters` + `extensions` + `service` blocks)

**Existing `receivers` block (verbatim KEEP)** (lines 10-16):
```yaml
receivers:
  otlp:
    protocols:
      grpc:
        endpoint: 0.0.0.0:4317
      http:
        endpoint: 0.0.0.0:4318
```
- **Pattern:** byte-identical KEEP — sk_p SDK still emits over OTLP gRPC + HTTP.

**Existing `processors.filter/health_metrics` block (KEEP unchanged in body, re-point in pipeline)** (lines 18-42 — the Phase 5 fix-forward processor):
```yaml
filter/health_metrics:
  error_mode: ignore
  metrics:
    datapoint:
      - 'metric.name == "http.server.request.duration" and IsMatch(attributes["http.route"], "^/health/.*")'
```
- **Pattern to copy:** byte-identical KEEP including the OTTL expression. D-04 says re-point this at the Prom exporter pipeline (already in metrics pipeline — just needs to feed `[prometheus]` exporter instead of `[file, logging]`). The 24-line comment block (lines 19-36) explaining the fix-forward rationale is load-bearing context — preserve it OR move it into the SUMMARY as documentation. If editing in place, keep the comment.

**Existing `exporters` block (REPLACE)** (lines 44-51):
```yaml
exporters:
  file:                                # <-- D-05: DELETE
    path: /var/otel-out/telemetry.jsonl
    rotation:
      max_megabytes: 10
      max_days: 1
  logging:                             # <-- D-05: DELETE (or keep for stdout fan-out — Claude's Discretion)
    verbosity: detailed
```
- **Pattern to replace with:** elasticsearch + prometheus exporter blocks per D-06/D-07 (verbatim from sk2_1). RESEARCH.md lines 698-712 give the full replacement YAML.

**Existing `extensions.health_check` block (KEEP — Pitfall 3 + Open Q4 recommendation)** (lines 53-55):
```yaml
extensions:
  health_check:
    endpoint: 0.0.0.0:13133
```
- **Pattern:** byte-identical KEEP. RESEARCH Open Q4 recommends keeping for host-side `curl localhost:13133/` smoke probe.

**Existing `service` block (REPLACE pipelines)** (lines 57-69):
```yaml
service:
  extensions: [health_check]
  pipelines:
    logs:
      receivers: [otlp]
      exporters: [file, logging]                          # <-- D-01: [elasticsearch]
    metrics:
      receivers: [otlp]
      processors: [filter/health_metrics]
      exporters: [file, logging]                          # <-- D-02: [prometheus]
    traces:                                               # <-- D-03: DELETE entire traces pipeline
      receivers: [otlp]
      exporters: [file, logging]
```
- **Pattern to replace with:** RESEARCH.md lines 717-728 give the verbatim new shape.

**Deviation notes for `compose/otel-collector-config.yaml`:**
- Keep the existing top-of-file 8-line comment block (lines 1-8) lightly amended — describe the new shape (logs → ES, metrics → Prom, no traces). Recommend rewriting the comment to match the new shape rather than tacking on additions.
- Keep `health_check` extension per Open Q4.
- Keep `filter/health_metrics` processor and its OTTL expression verbatim per D-04.
- Whether to edit in place or wholesale-replace is Claude's Discretion (CONTEXT). Recommendation: edit-in-place to preserve the Phase 5 fix-forward narrative + minimize diff churn.

---

### `prometheus.yml` (NEW — repo root)

**Analog:** `compose/otel-collector-config.yaml` (closest sk_p file that is a YAML config bind-mounted from repo root/subdir into a compose service; no exact analog exists)

**Excerpt from analog showing the "bind-mount-target YAML config" shape** (compose service bind-mount that consumes a repo-relative YAML):
```yaml
# compose.yaml line 41 (otel-collector service):
volumes:
  - ./compose/otel-collector-config.yaml:/etc/otel-collector-config.yaml:ro
```
- **Pattern to copy:** the `prometheus` service block in the new `compose.yaml` (per D-13) will bind-mount `./prometheus.yml:/etc/prometheus/prometheus.yml:ro`. The `:ro` (read-only) flag is the established convention (V12 ASVS hygiene per RESEARCH).

**Deviation notes for `prometheus.yml`:**
- File location is **repo root**, NOT `compose/prometheus.yml`. D-08 + sk2_1 verbatim mirror lock this layout.
- File is sole-purpose Prom scrape-config; no in-sk_p analog of "scrape-config YAML" exists.
- Content is verbatim from `C:/Users/UserL/source/repos/sk2_1/prometheus.yml` per D-08; RESEARCH.md lines 663-675 give the full body.
- The `:ro` bind-mount mode is the load-bearing security posture; preserve it across phases.

---

### `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` (modify — strip `.WithTracing`)

**Analog:** `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` (self — surgical delete of the `.WithTracing(t => ...)` block)

**Existing imports block** (lines 1-10) — REMOVE 2 lines:
```csharp
using BaseApi.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;                                       // <-- REMOVE (TracerProviderBuilderExtensions.AddNpgsql is unused after .WithTracing delete)
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;                          // <-- REMOVE (AlwaysOnSampler unused after .WithTracing delete)
```

**Existing OTel registration block** (lines 62-82) — DELETE the `.WithTracing(...)` chain:
```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: serviceName,
        serviceVersion: serviceVersion))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddOtlpExporter())
    .WithTracing(t => t                                                          // <-- DELETE entire .WithTracing block
        .SetSampler(new AlwaysOnSampler())
        .AddAspNetCoreInstrumentation(opts =>
            opts.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
        .AddHttpClientInstrumentation()
        .AddNpgsql()
        .AddOtlpExporter());
```

**Existing MEL bridge + ConfigureResource + .WithMetrics chain** (lines 47-75) — KEEP byte-identical (D-03 prescription).

**Existing XML doc comment** (lines 14-20) — UPDATE to remove the traces sentence:
```csharp
/// <summary>
/// Phase 5 OTel wiring: logs via MEL bridge (Pitfall 8 / OBSERV-02 / OBSERV-06 / OBSERV-07) +
/// metrics with AspNetCore/HttpClient/Runtime instrumentation + traces with AspNetCore (filtered
/// to exclude <c>/health/*</c> per OBSERV-08 / HEALTH-05 / Pitfall 10) + HttpClient + Npgsql DB
/// spans (OBSERV-12 / T-05-PII — bare <c>.AddNpgsql()</c> per Phase 5 D-05 corrected — the
/// 8.0.4 package default already does NOT capture parameter values).
/// </summary>
```
- **Pattern to copy:** XML doc updates in this codebase typically include a phase-stamp narrative (Phase 5 / D-13 amendments inline). Phase 11 should add a `<para>Phase 11 D-03 — traces pipeline removed; OBSERV-12 superseded to Out of Scope.</para>` paragraph or rewrite the summary entirely.

**Deviation notes for `ObservabilityServiceCollectionExtensions.cs`:**
- `Npgsql.OpenTelemetry` NuGet package reference removal is Claude's Discretion per RESEARCH lines 749-752 (recommend KEEP for minimum-diff posture).
- The XML doc tone uses prior-phase decision markers (e.g., "Phase 5 D-09", "Phase 5 D-05 corrected") — Phase 11 should write `Phase 11 D-03 — ...` in the same style.
- The Phase 5 deviation comment about parameterless metrics-side AddAspNetCoreInstrumentation (lines 67-72) is load-bearing — preserve verbatim.

---

### `src/BaseApi.Service/Program.cs` (likely UNCHANGED)

**Analog:** `src/BaseApi.Service/Program.cs` (self)

**Existing 18-line file** (lines 1-18):
```csharp
using BaseApi.Core.DependencyInjection;
using BaseApi.Service;
using BaseApi.Service.Composition;

var builder = WebApplication.CreateBuilder(args);
builder.AddBaseApiObservability(builder.Configuration);   // <-- centralizes ALL OTel wiring
builder.Services.AddBaseApi<AppDbContext>(builder.Configuration);
builder.Services.AddAppFeatures();

var app = builder.Build();
app.UseBaseApi();
app.MapControllers();
app.Run();

public partial class Program { }
```
- **Pattern:** OTel registration is fully encapsulated in `AddBaseApiObservability(...)`; Program.cs needs no edit. RESEARCH confirms this in lines 206-207.

**Deviation notes:** None. File stays byte-identical unless the planner decides to add a comment marker for Phase 11.

---

### `tests/BaseApi.Tests/Observability/LogExportTests.cs` (modify — switch to ES polling)

**Analog:** `tests/BaseApi.Tests/Observability/LogExportTests.cs` (self — replace `_fixture.ReadExportedLogs()` body with `_esClient.PollEsForLog(...)`)

**Existing pattern to evolve** (lines 22-56) — current file-exporter assertion:
```csharp
[Fact]
public async Task Test_LogRecord_Has_CorrelationId_And_ServiceResource()
{
    var ct = TestContext.Current.CancellationToken;
    using var client = _fixture.CreateClient();

    var response = await client.GetAsync("/test-obs/ok", ct);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

    var corrId = response.Headers.GetValues("X-Correlation-Id").Single();
    Assert.Matches("^[a-f0-9]{32}$", corrId);

    await _fixture.FlushAsync();

    var logs = _fixture.ReadExportedLogs();                                       // <-- REPLACE with ES poll
    Assert.NotEmpty(logs);

    var hits = logs
        .SelectMany(GetLogRecords)
        .Where(rec => rec.TryGetProperty("body", out var body)
                   && body.TryGetProperty("stringValue", out var bs)
                   && bs.GetString() == "test-obs ok ran")
        .Where(rec =>
        {
            if (!rec.TryGetProperty("attributes", out var a)) return false;
            return a.EnumerateArray().Any(attr =>
                attr.GetProperty("key").GetString() == "CorrelationId"
                && attr.GetProperty("value").GetProperty("stringValue").GetString() == corrId);
        })
        .ToList();

    Assert.NotEmpty(hits);
    // ...
}
```

**Pattern to copy + adapt:**
- Replace `_fixture` of type `OtelCollectorFixture` with `_factory` of type `Phase11WebAppFactory` (or `Phase8WebAppFactory` per Claude's Discretion in Open Q2) + a new `_esClient` field of type `ElasticsearchTestClient`.
- Replace `await _fixture.FlushAsync(); var logs = _fixture.ReadExportedLogs();` with `var hit = await _esClient.PollEsForLog(query, timeoutMs: 30_000);` where `query` filters on `Attributes.CorrelationId` (per Open Q1's recommended `mode: none` field shape).
- KEEP the `[Collection("Observability")]` attribute (Pitfall 9).
- KEEP the `[Fact]` shape and the controller-driving prelude (`client.GetAsync("/test-obs/ok", ct)`) verbatim.
- The "find log record with body 'test-obs ok ran' AND attribute CorrelationId = corrId" lambda chain collapses to a single ES term query.

**Deviation notes for `LogExportTests.cs`:**
- The second `[Fact]` (Test_LogRecord_CorrelationId_Survives_Sanitization, lines 83-130) follows the same pattern — replace file-read with ES poll.
- The `GetLogRecords` helper (lines 132-138) is no longer needed (ES `_search` returns the hit directly; no need to flatten resourceLogs → scopeLogs → logRecords).
- The "service.name=sk-api / service.version=3.2.0" resource attribute assertions (lines 67-80) need re-targeting against the ES doc's `Resource.attributes` field shape (under `mode: none`); Open Q1 resolution work is a prerequisite.

---

### `tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` (modify — switch to ES polling)

**Analog:** `tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` (self) — same shape as LogExportTests.

**Existing key pattern** (lines 16-58) — currently uses inline `OtelCollectorFixture` construction with `logLevelDefaultOverride`:
```csharp
[Fact]
public async Task Test_Information_Log_Suppressed_When_Default_Warning()
{
    var ct = TestContext.Current.CancellationToken;

    // Spin up a fixture with the LogLevel override (uses the internal 2-arg overload)
    await using var factory = new OtelCollectorFixture(connectionString: null, logLevelDefaultOverride: "Warning");
    await factory.InitializeAsync();

    var thisRequestCorrId = $"{Guid.NewGuid():N}";

    using var client = factory.CreateClient();
    using var req = new HttpRequestMessage(HttpMethod.Get, "/test-obs/ok");
    req.Headers.Add("X-Correlation-Id", thisRequestCorrId);
    var response = await client.SendAsync(req, ct);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

    await factory.FlushAsync();

    var logs = factory.ReadExportedLogs();                                        // <-- REPLACE
    var hits = logs.SelectMany(GetLogRecords).Where(...).ToList();
    Assert.Empty(hits);                                                            // <-- KEEP (negative assertion)
}
```

**Pattern to copy + adapt:**
- The unique-per-test `corrId` (line 29) is the right isolation pattern per Pitfall 5 — KEEP verbatim.
- The `await using var factory = new OtelCollectorFixture(connectionString: null, logLevelDefaultOverride: "Warning")` inline construction needs to evolve to `Phase11WebAppFactory` with the same log-level override mechanism (in-memory configuration injection — same `builder.ConfigureAppConfiguration` pattern from `Phase7WebAppFactory.cs` lines 92-98 + existing `OtelCollectorFixture` lines 155-163).
- For the `Test_Information_Log_Suppressed_When_Default_Warning` fact, the assertion stays negative — poll ES with a SHORT timeout (e.g., 5_000ms) and assert zero hits (note: ES poll-for-empty-result is inherently slower; consider just asserting "no hit within budget").
- For `Test_Information_Log_Present_When_Default_Information`, the assertion is positive — poll until hit is found.

**Deviation notes for `LogLevelFilterTests.cs`:**
- The negative-assertion fact creates an asymmetric polling shape: positive cases get the full 30_000ms budget; negative cases need to either (a) accept a shorter budget as the "no hit" proof, OR (b) issue a positive control request first to seed the index, then poll for the suppressed message specifically. Option (b) is more robust.

---

### `tests/BaseApi.Tests/Observability/MetricsExportTests.cs` (modify — switch to Prom polling)

**Analog:** `tests/BaseApi.Tests/Observability/MetricsExportTests.cs` (self)

**Existing 3-fact structure** (full file 150 lines) — KEEP the 3 facts; rewire each backend:
```csharp
[Fact]
public async Task Test_HttpServerRequestDuration_Present_For_App_Endpoint()
{
    // ... issue 5 requests ...
    await _fixture.FlushAsync(TimeSpan.FromSeconds(1));
    var metricNames = _fixture.ReadExportedMetrics()                             // <-- REPLACE
        .SelectMany(EnumerateMetricNames)
        .ToHashSet();
    Assert.Contains("http.server.request.duration", metricNames);                // <-- ADAPT name to Prom form
}

[Fact]
public async Task Test_HealthPath_Absent_From_HttpServerMetrics()
{
    // ... 10 requests to /health/live ...
    // Then check ZERO data points have http.route="/health/*"
    Assert.Empty(healthRoutes);                                                   // <-- STRICT empty preserved
}

[Fact]
public async Task Test_RuntimeMetric_ProcessRuntimeDotnet_Exported()
{
    // Accept "process.runtime.dotnet.*" OR "dotnet.*" prefix
    var hasRuntimeMetric =
        metricNames.Any(n => n.StartsWith("process.runtime.dotnet.", ...)) ||
        metricNames.Any(n => n.StartsWith("dotnet.", ...));
    Assert.True(hasRuntimeMetric, ...);
}
```

**Pattern to copy + adapt:**
- Replace `_fixture.ReadExportedMetrics()` with `_promClient.QueryPrometheus(promql)` per RESEARCH Pattern 3.
- Metric name translation per Pitfall 1: `http.server.request.duration` → `http_server_request_duration_seconds_count` (the `_count` suffix of the histogram triplet is the right cardinality target for "X requests happened").
- `Test_HealthPath_Absent_From_HttpServerMetrics` is the D-04 invariant — replace with a Prom query that filters on `http_route=~".*/health/.*"` and asserts empty result vector.
- `Test_RuntimeMetric_ProcessRuntimeDotnet_Exported` adapts to Prom names like `process_runtime_dotnet_gc_collections_count` OR `dotnet_gc_collections_total` (the spec-form Prom name).
- The Prom poll loop MUST use the sleep-then-poll pattern per Pitfall 7 + RESEARCH Pattern 3 (initial 15s sleep for one scrape cycle, then 3s polls).

**Deviation notes for `MetricsExportTests.cs`:**
- The `EnumerateMetricNames`, `EnumerateMetricNodes`, `GetAllDataPointAttributes` helpers (lines 121-148) become irrelevant — replaced with a single `QueryPrometheus` helper from the `PrometheusTestClient`.
- The 3rd fact's "accept either old or new name" tolerance (lines 113-117) preserves itself in spirit: accept either Prom form (e.g., `process_runtime_dotnet_*` OR `dotnet_*`).

---

### `tests/BaseApi.Tests/Observability/TraceExportTests.cs` (DELETE)

**Analog:** None — file is removed entirely per D-16.

**Deviation notes:** Git rm only. No replacement, no migration — D-03 drops the traces pipeline so this fact has no backend target.

---

### `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` (DELETE or restructure)

**Analog:** `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs` (the target shape if planner picks "replace" per Open Q2; the existing OtelCollectorFixture if planner picks "evolve")

**Existing file (current shape, 258 lines) is heavily file-exporter-coupled:**
- Lines 49-95: 3 constructors handling `connectionString` + `logLevelDefaultOverride` parameters
- Lines 97-124: `InitializeAsync` with file position-marker (`_startPosition`)
- Lines 136-151: `DisposeAsync` (intentionally does NOT delete the file)
- Lines 153-186: `ConfigureWebHost` with `ExportProcessorType.Simple` override + endpoint pin + TestErrorDbContext registration
- Lines 188-229: 4 file-reader methods (`ReadAllExportedRecords`, `ReadExportedLogs`, `ReadExportedMetrics`, `ReadExportedTraces`)
- Lines 236-246: `FlushAsync` (force-flush MeterProvider/TracerProvider/LoggerProvider)
- Lines 248-257: `ResolveTelemetryFile` (walk up to SK_P.sln)

**Pattern to copy for replacement (`Phase11WebAppFactory`):**
Borrow the **construction + ConfigureWebHost surface** from current `OtelCollectorFixture` lines 153-186:
```csharp
protected override void ConfigureWebHost(IWebHostBuilder builder)
{
    if (_logLevelDefaultOverride is not null)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = _logLevelDefaultOverride,
            });
        });
    }
    builder.ConfigureTestServices(services =>
    {
        services.AddControllers()
            .AddApplicationPart(typeof(OtelCollectorFixture).Assembly);
        services.Configure<OtlpExporterOptions>(o =>
        {
            o.ExportProcessorType = ExportProcessorType.Simple;        // <-- DELETE; replaced by metric-reader override
            o.Endpoint            = new Uri("http://localhost:4317");
        });
        // ...
    });
}
```

And the **env-var defensive pin from current `OtelCollectorFixture` ctor** (lines 86-95):
```csharp
internal OtelCollectorFixture(string? connectionString, string? logLevelDefaultOverride)
{
    // ...
    Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317");
}
```
- **Pattern to copy:** preserve env-var pin (T-05-OTLP-EXFIL invariant per Pitfall 8).
- **Drop entirely:** file position-marker, file readers, FlushAsync, ResolveTelemetryFile, ExportProcessorType.Simple override (replaced by `PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 1000` per RESEARCH Pattern 4).

**Deviation notes for `OtelCollectorFixture.cs`:**
- RESEARCH Open Q2 recommends WHOLESALE REPLACE — create `Phase11WebAppFactory` and delete the old fixture. The current fixture's only reusable parts (env-var pin, AddApplicationPart, log-level override mechanism) compose into a ~30-line `Phase11WebAppFactory` per RESEARCH Open Q2 recommendation.
- If the planner picks "evolve" instead, the diff is ~150 lines of deletions + 5-10 lines of `PeriodicExportingMetricReaderOptions` addition. Less context-discontinuity at the cost of preserving a misleading filename.

---

### `tests/BaseApi.Tests/Observability/OtelEndOfSuiteCleanup.cs` (DELETE)

**Analog:** None — file is removed entirely per D-05 / D-16.

**Deviation notes:** Git rm only. The Phase 5 D-11 cleanup discipline (file handle release via `docker compose stop otel-collector`) becomes obsolete because there is no file to clean. The xUnit v3 `[assembly: AssemblyFixture]` pattern itself remains available for future use; this file is the only current consumer.

---

### `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs` (NEW)

**Analog:** `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` (the closest existing "telemetry-read helper" — same role: encapsulates a query+poll loop for telemetry); RESEARCH.md Pattern 2 gives the verbatim sk2_1 adaptation.

**Existing `OtelCollectorFixture.ReadAllExportedRecords()` pattern** (lines 190-220) — old shape:
```csharp
public IReadOnlyList<JsonElement> ReadAllExportedRecords()
{
    if (!File.Exists(TelemetryFile)) return Array.Empty<JsonElement>();
    using var fs = new FileStream(TelemetryFile, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);
    if (_startPosition > 0 && _startPosition <= fs.Length)
    {
        fs.Seek(_startPosition, SeekOrigin.Begin);
    }
    using var reader = new StreamReader(fs);
    var result = new List<JsonElement>();
    string? line;
    while ((line = reader.ReadLine()) is not null)
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        try
        {
            using var doc = JsonDocument.Parse(line);
            result.Add(doc.RootElement.Clone());
        }
        catch (JsonException) { /* partial line during write — ignore */ }
    }
    return result;
}
```

**New shape (excerpt from RESEARCH.md Pattern 2 lines 260-302) verbatim adaptation from sk2_1:**
```csharp
private const int InitialDelayMs = 200;
private const int MaxDelayMs     = 3_200;

private async Task<JsonElement?> PollEsForLog(string query, int timeoutMs)
{
    var sw    = Stopwatch.StartNew();
    var delay = InitialDelayMs;
    while (sw.ElapsedMilliseconds < timeoutMs)
    {
        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Post, "logs-generic-default/_search")
            {
                Content = new StringContent(query, Encoding.UTF8, "application/json")
            };
            using var resp = await _es.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("hits", out var outer)
                    && outer.TryGetProperty("hits", out var hits)
                    && hits.ValueKind == JsonValueKind.Array
                    && hits.GetArrayLength() > 0)
                {
                    using var inner = JsonDocument.Parse(hits[0].GetRawText());
                    return inner.RootElement.Clone();
                }
            }
        }
        catch (HttpRequestException) { /* ES briefly unreachable — retry. */ }
        var remaining = (int)(timeoutMs - sw.ElapsedMilliseconds);
        if (remaining <= 0) break;
        await Task.Delay(Math.Min(delay, remaining));
        delay = Math.Min(delay * 2, MaxDelayMs);
    }
    return null;
}
```

**Pattern to copy:**
- The exponential backoff (200ms → 3200ms cap) matches sk2_1 verbatim per Pattern 2.
- Tolerate HTTP 404 (index not yet created) AND empty hits (doc not yet indexed) per Pattern 2 + Pitfall 5.
- Use `JsonDocument.Parse(hits[0].GetRawText()).RootElement.Clone()` to detach the result from the parsing scope — matches the existing `OtelCollectorFixture.ReadAllExportedRecords` Clone() pattern (line 211) verbatim. This is a load-bearing memory-safety idiom in the codebase.
- The HttpClient `_es` field has `BaseAddress = new Uri("http://localhost:9200/")` per RESEARCH Pattern 5 (host-DNS, NOT compose-internal DNS).
- Index path is `logs-generic-default` per D-06 (Open Q1 verification required at Wave 0).

**Deviation notes for `ElasticsearchTestClient.cs`:**
- The class should expose a constructor taking optional `HttpClient` (for test DI) OR construct its own. The existing sk_p codebase typically uses the constructor-injection pattern.
- Recommended location: `tests/BaseApi.Tests/Observability/Helpers/` subdirectory (NEW). If the planner prefers a flatter layout, place it directly in `tests/BaseApi.Tests/Observability/` alongside other observability assets. Either works; nested `Helpers/` keeps the file count visible.
- Open Q1 (mode: none vs mode: otel) drives the exact field path the queries target. Wave 0 verification result lands here.

---

### `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` (NEW)

**Analog:** Same as `ElasticsearchTestClient.cs` (no exact sk_p analog; RESEARCH.md Pattern 3 gives the verbatim sk2_1 adaptation).

**New shape (excerpt from RESEARCH.md Pattern 3 lines 315-358) verbatim adaptation from sk2_1:**
```csharp
private const int InitialSleepMs = 15_000;     // one Prom scrape_interval
private const int PollTimeoutMs  = 60_000;
private const int PollIntervalMs = 3_000;

private async Task<List<JsonElement>> PollPrometheusUntilSumAtLeast(string promql, double threshold)
{
    await Task.Delay(InitialSleepMs);
    var lastSamples = await QueryPrometheus(promql);
    var elapsed     = InitialSleepMs;
    while (elapsed < PollTimeoutMs)
    {
        if (lastSamples.Count > 0 && SumSampleValues(lastSamples) >= threshold)
            return lastSamples;
        await Task.Delay(PollIntervalMs);
        elapsed += PollIntervalMs;
        lastSamples = await QueryPrometheus(promql);
    }
    return lastSamples;
}

private async Task<List<JsonElement>> QueryPrometheus(string promql)
{
    var url = $"api/v1/query?query={Uri.EscapeDataString(promql)}";
    using var resp = await _prom.GetAsync(url);
    resp.EnsureSuccessStatusCode();
    var json = await resp.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(json);
    if (!doc.RootElement.TryGetProperty("status", out var statusEl)
        || statusEl.GetString() != "success"
        || !doc.RootElement.TryGetProperty("data", out var dataEl)
        || !dataEl.TryGetProperty("result", out var results))
    {
        Assert.Fail($"Prometheus query failed. Query: {promql}. Response: {json}");
        return new List<JsonElement>();
    }
    var list = new List<JsonElement>();
    foreach (var r in results.EnumerateArray())
    {
        using var inner = JsonDocument.Parse(r.GetRawText());
        list.Add(inner.RootElement.Clone());
    }
    return list;
}
```

**Pattern to copy:**
- The sleep-then-poll pattern is load-bearing per Pitfall 7 + RESEARCH Pattern 3 (NEVER poll from t=0 — first scrape happens after 15s; SDK metric export interval is 60s default but overridden to 1s in fixture per Pattern 4).
- `Uri.EscapeDataString(promql)` mandatory because PromQL contains `{`, `}`, `"`, `=` per Don't Hand-Roll table.
- `SumSampleValues` helper aggregates across multi-label result vectors per Don't Hand-Roll table.
- The `.Clone()` idiom matches `ElasticsearchTestClient` and the existing `OtelCollectorFixture.ReadAllExportedRecords` (line 211).
- The HttpClient `_prom` field has `BaseAddress = new Uri("http://localhost:9090/")` per RESEARCH Pattern 5.

**Deviation notes for `PrometheusTestClient.cs`:**
- Same location convention discussion as `ElasticsearchTestClient.cs`.
- The `Assert.Fail` call in `QueryPrometheus` makes the helper test-framework-coupled (xUnit). The existing sk_p codebase tolerates this (`Assert.Fail` is used in `tests/BaseApi.Tests/Persistence/PostgresExceptionMapperTests.cs` style); alternatively, throw and let the caller fail-assert.

---

### `tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs` (NEW)

**Analog:** `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs` (closest existing `WebApplicationFactory<Program>` subclass with `IAsyncLifetime`); also borrow from `OtelCollectorFixture.cs` (env-var pin) + `WebAppFactory.cs` (AddApplicationPart).

**Existing `Phase8WebAppFactory` shape** (lines 26-92) — target template:
```csharp
public class Phase8WebAppFactory : WebAppFactory, IAsyncLifetime
{
    private PostgresFixture? _fixture;
    private readonly string? _connectionStringOverride;

    public Phase8WebAppFactory() { }

    public async ValueTask InitializeAsync()
    {
        if (_connectionStringOverride is null)
        {
            _fixture = new PostgresFixture();
            await _fixture.InitializeAsync();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_fixture is not null)
        {
            await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
            {
                NpgsqlConnection.ClearPool(conn);
            }
            await _fixture.DisposeAsync();
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = ConnectionString,
            });
        });
        base.ConfigureWebHost(builder);
    }
}
```

**Existing `OtelCollectorFixture` ctor env-var pin pattern** (lines 87-95):
```csharp
internal OtelCollectorFixture(string? connectionString, string? logLevelDefaultOverride)
{
    _connectionString = connectionString;
    _logLevelDefaultOverride = logLevelDefaultOverride;
    Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317");
}
```

**Pattern to copy + adapt (new Phase11WebAppFactory):**
- Inherit from `WebAppFactory` (base) — gives `AddApplicationPart(typeof(WebAppFactory).Assembly)` for free (matches existing `WebAppFactory.cs` lines 42-43).
- Implement `IAsyncLifetime` and own a `PostgresFixture` per the Phase 8 pattern (the round-trip E2E facts hit `/api/v1/schemas` which needs a real DB).
- Pin `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317` in the constructor (T-05-OTLP-EXFIL inheritance from Phase 5).
- In `ConfigureWebHost`, add `services.Configure<PeriodicExportingMetricReaderOptions>(opts => opts.ExportIntervalMilliseconds = 1_000);` per RESEARCH Pattern 4 + Pitfall 7. This is the ONLY new wiring vs Phase 8.
- Optionally accept a `logLevelDefaultOverride` parameter to support `LogLevelFilterTests` (matching `OtelCollectorFixture` lines 155-163).

**Recommended skeleton (NEW file, ~40 lines):**
```csharp
using BaseApi.Tests.Composition;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// Phase 11 WebApplicationFactory subclass for backend-round-trip E2E facts.
/// Composes Phase8WebAppFactory's Postgres-fixture pattern with:
/// (a) OTEL_EXPORTER_OTLP_ENDPOINT defensive env-var pin (T-05-OTLP-EXFIL inheritance from Phase 5).
/// (b) PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 1_000 (Pitfall 7).
/// (c) optional Logging:LogLevel:Default override (LogLevelFilterTests parity).
/// </summary>
public class Phase11WebAppFactory : Phase8WebAppFactory
{
    private readonly string? _logLevelDefaultOverride;
    public Phase11WebAppFactory() : this(null) { }
    public Phase11WebAppFactory(string? logLevelDefaultOverride)
    {
        _logLevelDefaultOverride = logLevelDefaultOverride;
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317");
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (_logLevelDefaultOverride is not null)
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    { ["Logging:LogLevel:Default"] = _logLevelDefaultOverride }));
        }
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.Configure<PeriodicExportingMetricReaderOptions>(opts =>
                opts.ExportIntervalMilliseconds = 1_000);
        });
    }
}
```

**Deviation notes for `Phase11WebAppFactory.cs`:**
- Inheriting from `Phase8WebAppFactory` (which itself inherits from `WebAppFactory`) reuses the throwaway-Postgres-DB pattern + AddApplicationPart — minimizes duplication.
- Open Q2 recommendation prefers this composition over evolving `OtelCollectorFixture` in place.
- The file location is `tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs` (next to the observability tests it serves). RESEARCH Validation Architecture suggests this layout (line 984).
- If the planner wants stricter category-trait filtering, the constructor can accept a flag to skip Postgres spin-up for facts that don't need DB writes (drop ~2s per fact). Optional refinement.

---

### `tests/BaseApi.Tests/Observability/RoundTripE2ETests.cs` (NEW)

**Analog:** `tests/BaseApi.Tests/Integration/SchemasIntegrationTests.cs` (closest existing pattern — integration fact with `IClassFixture<Phase8WebAppFactory>`, HTTP-driving, `[Trait("PhaseNWave", "X")]`); `tests/BaseApi.Tests/Features/Orchestration/StartOrchestrationFacts.cs` (closest existing `[Trait("Phase", "N")]` shape).

**Existing `SchemasIntegrationTests` pattern** (lines 1-52) — HTTP-driving template:
```csharp
[Trait("Phase8Wave", "B")]
public sealed class SchemasIntegrationTests : IClassFixture<Phase8WebAppFactory>
{
    private readonly Phase8WebAppFactory _factory;
    public SchemasIntegrationTests(Phase8WebAppFactory factory) => _factory = factory;

    private static SchemaCreateDto NewValidCreateDto(string suffix = "") => new(
        Name: $"my-schema{suffix}",
        Version: "1.0.0",
        Description: "Integration test schema",
        Definition: "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\"}");

    [Fact]
    public async Task Create_Returns201_AndLocationHeader_WhenValid()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/schemas", NewValidCreateDto(), ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        // ...
    }
}
```

**Existing `StartOrchestrationFacts` pattern** (lines 33-39) — `[Trait("Phase","N")]` tagging:
```csharp
[Trait("Phase", "9")]
public sealed class StartOrchestrationFacts : IClassFixture<Phase8WebAppFactory>
{
    private readonly Phase8WebAppFactory _factory;
    public StartOrchestrationFacts(Phase8WebAppFactory factory) => _factory = factory;
    // ...
}
```

**Pattern to copy + adapt for `RoundTripE2ETests.cs`:**
- Use `[Trait("Phase", "11")]` per existing Phase 9 convention.
- Optionally add `[Trait("Category", "E2E")]` per Claude's Discretion (RESEARCH Open Q5 recommends adding it for the `--filter` knob).
- Use `[Collection("Observability")]` to serialize against the migrated LogExportTests / LogLevelFilterTests / MetricsExportTests (Pitfall 9).
- Use `IClassFixture<Phase11WebAppFactory>` per Open Q2 recommendation.
- Two facts per RESEARCH Open Q3 (separate logs + metrics classes recommended OR keep in one file — Claude's Discretion).
- Drive traffic via `client.PostAsJsonAsync("/api/v1/schemas", dto, ct)` matching `SchemasIntegrationTests.Create_Returns201_*` pattern. RESEARCH lines 778-784 give the verbatim DTO shape.

**RESEARCH-provided E2E fact shape (lines 759-849 verbatim):**
```csharp
[Trait("Phase", "11")]
[Trait("Category", "E2E")]
[Collection("Observability")]
public sealed class RoundTripE2ETests : IClassFixture<Phase11WebAppFactory>
{
    private readonly Phase11WebAppFactory _factory;
    private readonly HttpClient _es   = new() { BaseAddress = new Uri("http://localhost:9200/") };
    private readonly HttpClient _prom = new() { BaseAddress = new Uri("http://localhost:9090/") };

    public RoundTripE2ETests(Phase11WebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task PostSchema_Surfaces_Created_LogRecord_In_Elasticsearch_With_CorrelationId()
    {
        var ct = TestContext.Current.CancellationToken;
        var corrId = $"{Guid.NewGuid():N}";
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", corrId);
        var dto = new SchemaCreateDto(
            Name: $"E2E-Logs-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Definition: "{ \"type\": \"object\" }",
            Description: null);
        var resp = await client.PostAsJsonAsync("/api/v1/schemas", dto, ct);
        resp.EnsureSuccessStatusCode();
        var hit = await PollEsForLog(/* query string with corrId */, timeoutMs: 30_000);
        Assert.NotNull(hit);
    }

    [Fact]
    public async Task PostSchema_Increments_Http_Server_Request_Duration_Counter_In_Prometheus()
    {
        // ... 3 POSTs, then poll Prom for http_server_request_duration_seconds_count >= 3 ...
    }
}
```

**Deviation notes for `RoundTripE2ETests.cs`:**
- The Schema entity is the recommended traffic source (RESEARCH Open Q3 + Code Examples) because it has the simplest body (single jsonb field) and exercises the full HTTP → Service → Repo → AuditInterceptor → SaveChanges pipeline.
- The `correlation.id` field path under `mode: none` is `Attributes.CorrelationId` (capital A, no dot in the key) per RESEARCH Pitfall 2 + Open Q1; Wave 0 verification resolves this empirically.
- The metric name `http_server_request_duration_seconds_count` per RESEARCH Pitfall 1 — DO NOT use the OTLP form `http.server.request.duration`.
- The `http_route` label has NO leading slash per RESEARCH Pitfall 1 + sk2_1 line 95 reference.
- The `service_name="sk-api"` label only surfaces because D-07 enables `resource_to_telemetry_conversion`.
- Open Q3 recommends SEPARATING into `SchemasLogsE2ETests` + `SchemasMetricsE2ETests` (matching sk2_1 layout) for faster debug + clearer failure attribution. RoundTripE2ETests.cs is the single-file alternative — planner picks.

---

### `.gitignore` (modify — remove tests/.otel-out/ stanza)

**Analog:** `.gitignore` (self — surgical delete of lines 414-421)

**Existing pattern to delete** (lines 414-421):
```
# Phase 5 (CONTEXT.md D-10) — otel-collector file exporter host-mount target.
# The compose service `otel-collector` writes telemetry.jsonl to a host bind-mount
# at ./tests/.otel-out/. Each Plan 05-02 test class truncates the file on
# InitializeAsync and deletes it on DisposeAsync (PostgresFixture lift). The
# .gitkeep file (committed by Plan 05-01 Task 4) preserves the directory at
# clone time; this glob ignores any subsequent file the exporter writes.
tests/.otel-out/*
!tests/.otel-out/.gitkeep
```

**Pattern:** delete the 8-line stanza in its entirety (comment block + 2 glob entries). Preserve the surrounding lines (Phase 5 SK_P project additions group; the `*.received.*` Verify pattern on line 403; bin/obj lines on 406-407; .env.local lines 411-412).

**Deviation notes:**
- Whether to also `git rm -r tests/.otel-out/` (the directory itself + `.gitkeep`) is Claude's Discretion (CONTEXT). Recommend: full removal (no forensic value remains; the data stream moves to ES).

---

### `.planning/REQUIREMENTS.md` (modify — D-19 amendments)

**Analog:** `.planning/REQUIREMENTS.md` (self — apply Phase 11 amendments per D-19)

**Existing pattern: marked-complete REQ-IDs** (lines 176-177):
```markdown
- [x] **OBSERV-12
**: OTel tracing enabled (logs + metrics + traces) with `AddAspNetCoreInstrumentation`, `AddHttpClientInstrumentation`, `AddNpgsql` for DB spans
```

**Existing pattern: tabular status tracker** (line 356):
```markdown
| OBSERV-12 | Phase 5 | Complete |
```

**Existing pattern: phase summary footnote** (line 390):
```markdown
- Phase 5 (Observability + Health Probes): 14 requirements (OBSERV-01..08, OBSERV-12, HEALTH-01..05)
```

**Pattern to copy for OBSERV-12 → Out of Scope:**
- Mirror the Phase 5 OBSERV-12 entry but reverse the checkbox + amend the text:
```markdown
- [ ] **OBSERV-12 [SUPERSEDED — Phase 11 D-03]
**: Traces pipeline removed in Phase 11; OBSERV-12 moved to Out of Scope. Rationale: no traces backend in v1; mirrors sk2_1 CLAUDE.md non-negotiable #2.
```
- Update the status tracker table row to reflect `Superseded` / `Out of Scope`.
- Add Phase 11 entry to the phase summary footnote with the new REQ-IDs (OBSERV-13-LIKE, OBSERV-14-LIKE, INFRA-08-LIKE, TEST-07-LIKE — exact IDs are planner's call per D-19).

**Pattern for NEW REQ-IDs** (planner names them):
Follow the existing OBSERV-NN bullet style on lines 156-177; add new bullets numbered after the current max (OBSERV-12 → OBSERV-13 + 14). Same for INFRA-NN.

**Deviation notes for `REQUIREMENTS.md`:**
- D-19 explicitly grants the planner authority over exact REQ-ID naming.
- Phase 10 doc-first commit precedent (CONTEXT D-19) means this edit can be a separate atomic commit before any code change lands.
- Footer must be dated 2026-05-28 with Phase 11 amendment marker.
- The `INFRA-06` extension (per D-15: ES + Prom in baseapi-service depends_on) is an in-place text edit, not a new ID.

---

## Shared Patterns

### Authentication / Authorization

Not applicable — Phase 11 is dev-posture observability migration with no auth surface. All ES/Prom backends run open on `localhost` per CONTEXT Out of Scope.

### Error Handling — Test polling tolerance

**Source:** `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` lines 207-216 (existing `try/catch (JsonException)` for partial JSON line tolerance during file rotation)

**Apply to:** `ElasticsearchTestClient.cs` + `PrometheusTestClient.cs` (HTTP-side equivalent: catch `HttpRequestException` per RESEARCH Pattern 2)

**Excerpt from analog:**
```csharp
// Defensive: skip lines that don't parse — file rotation may produce truncated tails
try
{
    using var doc = JsonDocument.Parse(line);
    result.Add(doc.RootElement.Clone());
}
catch (JsonException)
{
    // partial line during write — ignore
}
```

**Excerpt from new clients (per RESEARCH Pattern 2):**
```csharp
catch (HttpRequestException) { /* ES briefly unreachable — retry. */ }
```

The "defensive swallow + retry" idiom is established sk_p convention; both new clients adopt it.

### Validation — URL parameter escaping

**Source:** `tests/BaseApi.Tests/Observability/MetricsExportTests.cs` (existing tests use type-safe `JsonElement.GetProperty(...)` access; no manual string concatenation)

**Apply to:** `PrometheusTestClient.QueryPrometheus` — use `Uri.EscapeDataString(promql)` per RESEARCH Don't Hand-Roll table.

```csharp
var url = $"api/v1/query?query={Uri.EscapeDataString(promql)}";
```

PromQL contains `{`, `}`, `"`, `=` which break unencoded URL queries — mandatory escape per RESEARCH Pitfall 1.

### Testing — `[Collection("Observability")]` + `[Trait("Phase","N")]`

**Source:** `tests/BaseApi.Tests/Observability/CollectionDefinitions.cs` (declaration) + `tests/BaseApi.Tests/Features/Orchestration/StartOrchestrationFacts.cs` line 34 (Phase trait example)

**Apply to:** ALL new + modified Phase 11 test classes — `LogExportTests`, `LogLevelFilterTests`, `MetricsExportTests`, `RoundTripE2ETests`, `Phase11WebAppFactory` (the factory itself doesn't carry the trait but its consumers do).

**Excerpt from CollectionDefinitions.cs:**
```csharp
[CollectionDefinition("Observability", DisableParallelization = true)]
public sealed class ObservabilityCollection { }
```

**Excerpt from existing Phase 9 fact (StartOrchestrationFacts.cs line 34):**
```csharp
[Trait("Phase", "9")]
public sealed class StartOrchestrationFacts : IClassFixture<Phase8WebAppFactory>
```

Phase 11 tests follow the same shape: `[Trait("Phase", "11")]` + `[Collection("Observability")]`. Optional `[Trait("Category", "E2E")]` per RESEARCH Open Q5.

### HttpClient ownership + disposal

**Source:** `tests/BaseApi.Tests/Integration/SchemasIntegrationTests.cs` lines 37-38 + `tests/BaseApi.Tests/Observability/LogExportTests.cs` line 26 — `using var client = _factory.CreateClient()`

**Apply to:** Round-trip E2E facts (single `_factory.CreateClient()` for sk_p traffic) + `ElasticsearchTestClient` (long-lived `_es` HttpClient with `BaseAddress`) + `PrometheusTestClient` (long-lived `_prom` HttpClient with `BaseAddress`).

**Excerpt from analog:**
```csharp
using var client = _factory.CreateClient();
var response = await client.GetAsync("/test-obs/ok", ct);
```

The `using var` disposal scope per-test-method matches existing convention. For the ES/Prom test clients, an `IDisposable` field + `[Class].DisposeAsync` cleanup is the right pattern (RESEARCH Code Examples lines 764-765 use field declarations + initialization).

### CancellationToken propagation

**Source:** Universal across test files. Example from `tests/BaseApi.Tests/Integration/SchemasIntegrationTests.cs` line 37:
```csharp
var ct = TestContext.Current.CancellationToken;
```

**Apply to:** ALL new + modified Phase 11 test facts. xUnit v3 idiom — every `[Fact]` body's first line.

## No Analog Found

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| `prometheus.yml` (NEW, repo root) | Prom scrape-config | metrics pull | No existing sk_p file is a scrape-target YAML. Closest is `compose/otel-collector-config.yaml` (similar bind-mount discipline, different semantics). Content is verbatim from sk2_1 per D-08. |

All other 16 new/modified files have at least a role-match analog in the sk_p codebase.

## Metadata

**Analog search scope:**
- `compose.yaml` + `compose/` directory (1 + 1 files, fully read)
- `src/BaseApi.Core/DependencyInjection/` (8 files — read ObservabilityServiceCollectionExtensions.cs in full)
- `src/BaseApi.Service/Program.cs` (full read — 18 lines)
- `tests/BaseApi.Tests/Observability/` (9 files — read 6 in full: OtelCollectorFixture, LogExportTests, LogLevelFilterTests, MetricsExportTests, TraceExportTests, OtelEndOfSuiteCleanup, CollectionDefinitions, HealthEndpointsTests partial, TestObservabilityController)
- `tests/BaseApi.Tests/Composition/` (5 files — read Phase8WebAppFactory + Phase7WebAppFactory in full)
- `tests/BaseApi.Tests/Middleware/WebAppFactory.cs` (full)
- `tests/BaseApi.Tests/Integration/SchemasIntegrationTests.cs` (read first 80 lines for pattern shape)
- `tests/BaseApi.Tests/Features/Orchestration/StartOrchestrationFacts.cs` (read first 50 lines for `[Trait("Phase","N")]` pattern)
- `.gitignore` (full read — 421 lines)
- `.planning/REQUIREMENTS.md` (partial — OBSERV-12 entry context)

**Files scanned via Glob:** ~80 (src + tests trees)
**Files Read in full or near-full:** 14
**Files Read partially:** 5

**Pattern extraction date:** 2026-05-28
