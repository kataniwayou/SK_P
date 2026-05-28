---
phase: 11-migrate-prometheus-and-elastic-containers-from-compose-stack
plan: 08b
type: execute
wave: 6
depends_on:
  - "11-08a"
files_modified:
  - tests/BaseApi.Tests/Observability/LogExportTests.cs
  - tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs
  - tests/BaseApi.Tests/Observability/MetricsExportTests.cs
autonomous: true
requirements:
  - OBSERV-03
  - OBSERV-06
  - OBSERV-08
  - OBSERV-13
  - OBSERV-14
must_haves:
  truths:
    - "`LogExportTests.cs` 2 facts migrated to `IClassFixture<Phase11WebAppFactory>` + `ElasticsearchTestClient.PollEsForLog` + `EsIndexNames.CorrelationIdFieldPath`; no references to `OtelCollectorFixture` or file-exporter API"
    - "`LogLevelFilterTests.cs` 2 facts migrated to direct `new Phase11WebAppFactory(logLevelDefaultOverride: \"Warning\")` (internal ctor); positive 30s budget + negative 8s budget asymmetric polling shape (PATTERNS option a per RESEARCH)"
    - "`MetricsExportTests.cs` 3 facts migrated to `IClassFixture<Phase11WebAppFactory>` + `PrometheusTestClient.QueryPrometheus` / `PollPrometheusUntilSumAtLeast`; OBSERV-08 + D-04 health-filter invariant preserved via PromQL regex match; runtime metric fact accepts either `dotnet_*` or `process_runtime_dotnet_*`"
    - "All 3 migrated classes carry `[Trait(\"Phase\", \"11\")]` + `[Collection(\"Observability\")]`; per-test unique correlation IDs (Pitfall 5 isolation; T-11-03 mitigation)"
    - "`OtelCollectorFixture.cs` is PRESERVED at this commit's HEAD (Plan 11-08c performs the final deletion after all 3 + HealthEndpointsTests consumers have migrated)"
    - "`dotnet test SK_P.sln --no-restore -c Release --filter \"FullyQualifiedName~LogExportTests\\|FullyQualifiedName~LogLevelFilterTests\\|FullyQualifiedName~MetricsExportTests\"` exits 0 with all 7 facts GREEN (2+2+3=7) against the live stack"
    - "Solution builds zero-warning Release+Debug"
  artifacts:
    - path: "tests/BaseApi.Tests/Observability/LogExportTests.cs"
      provides: "Migrated logs facts — correlation-id round-trip + sanitization regression via ES polling"
      contains: "PollEsForLog"
      absent_pattern: "OtelCollectorFixture|ReadExportedLogs"
    - path: "tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs"
      provides: "Migrated log-level facts — OBSERV-06 single-MEL-filter invariant via ES polling; asymmetric positive/negative budgets"
      contains: "Phase11WebAppFactory"
    - path: "tests/BaseApi.Tests/Observability/MetricsExportTests.cs"
      provides: "Migrated metrics facts — OBSERV-03 + OBSERV-08 + D-04 health-filter invariant via Prom polling; runtime metric naming-era tolerant"
      contains: "PrometheusTestClient"
      absent_pattern: "ReadExportedMetrics|FlushAsync"
  key_links:
    - from: "LogExportTests / LogLevelFilterTests"
      to: "tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs"
      via: "each [Fact] body constructs `new ElasticsearchTestClient()` then calls `PollEsForLog(queryBody, timeoutMs)`"
      pattern: "PollEsForLog"
    - from: "MetricsExportTests"
      to: "tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs"
      via: "each [Fact] body constructs `new PrometheusTestClient()` then calls `QueryPrometheus(promql)` OR `PollPrometheusUntilSumAtLeast(promql, threshold)`"
      pattern: "PrometheusTestClient"
---

<objective>
Migrate the 3 file-exporter-coupled fact classes from Phase 5 to the new ES + Prom polling helpers. This is the body of Plan 11-08 from the original phase plan, split out per checker WARNING #3 (scope_sanity — too many tasks in a single closing plan).

File-by-file migration:

1. **`LogExportTests.cs`** (2 facts) — `IClassFixture<OtelCollectorFixture>` → `IClassFixture<Phase11WebAppFactory>`; replace `_fixture.ReadExportedLogs()` and `_fixture.FlushAsync()` with `new ElasticsearchTestClient().PollEsForLog(queryBody, 30_000)`. Each fact uses a per-test unique correlation id and a term query on `EsIndexNames.CorrelationIdFieldPath`. Preserves the existing assertions on service.name=sk-api + correlation-id round-trip + sanitization regression (adapted to verified Wave 0 field shape).

2. **`LogLevelFilterTests.cs`** (2 facts) — `new OtelCollectorFixture(connectionString: null, logLevelDefaultOverride: "Warning")` → `new Phase11WebAppFactory(logLevelDefaultOverride: "Warning")` (internal ctor). Same ES polling helper. The negative-assertion fact (Test_Information_Log_Suppressed_When_Default_Warning) uses an 8s timeout (asymmetric — positive control uses 30s) per RESEARCH PATTERNS option a.

3. **`MetricsExportTests.cs`** (3 facts) — `IClassFixture<OtelCollectorFixture>` → `IClassFixture<Phase11WebAppFactory>`; `_fixture.ReadExportedMetrics()` → `new PrometheusTestClient().QueryPrometheus(promql)` or `PollPrometheusUntilSumAtLeast`. Metric names adapted from OTLP to Prom form per Pitfall 1.

**NOT in this plan's scope** (deferred to Plan 11-08c):
- Deletion of `OtelCollectorFixture.cs` (after this plan lands, the last remaining consumers of `OtelCollectorFixture` are exactly these 3 classes' obsolete references — but since this plan replaces all 3, the file becomes orphaned but is NOT deleted here; the deletion happens in Plan 11-08c for cleanliness of the commit sequence).
- 3-consecutive-GREEN closing cadence (Phase 3 D-18) — Plan 11-08c.
- psql `\l` SHA-256 BEFORE/AFTER snapshot (Phase 3 D-15 carry-forward) — Plan 11-08c.
- Phase 11 closing commit narrative + SUMMARY — Plan 11-08c.

Purpose: brings the Phase 5 baseline 7-fact observability suite back to GREEN (currently RED post-Plan-11-05 because the 3 facts still reference the deleted file-exporter path). Closes OBSERV-13 + OBSERV-14 + TEST-07 behavioral coverage for the migrated subset.

Output: A single atomic commit `test(observability): migrate LogExportTests + LogLevelFilterTests + MetricsExportTests to ES/Prom polling` modifying 3 source files. After commit, `dotnet test --filter "FullyQualifiedName~LogExportTests\|FullyQualifiedName~LogLevelFilterTests\|FullyQualifiedName~MetricsExportTests"` exits 0 with 7 facts GREEN.
</objective>

<execution_context>
@$HOME/.claude/get-shit-done/workflows/execute-plan.md
@$HOME/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/STATE.md
@.planning/ROADMAP.md
@.planning/REQUIREMENTS.md
@.planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-CONTEXT.md
@.planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-RESEARCH.md
@.planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-PATTERNS.md
@tests/BaseApi.Tests/Observability/LogExportTests.cs
@tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs
@tests/BaseApi.Tests/Observability/MetricsExportTests.cs
@tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs
@tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs
@tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs
@tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs

<interfaces>
<!-- Existing Phase 5 fact body shapes — see original Plan 11-08 interfaces section for full detail -->
<!-- Wave 0 constants verified by Plan 11-06 Task 0 (post-revision EsIndexNames.cs structure) -->
EsIndexNames.LogsDataStream             — index path
EsIndexNames.CorrelationIdFieldPath     — for ES term queries
EsIndexNames.ResourceAttributesFieldPath — for resource-attr assertions
EsIndexNames.FieldShape                 — "raw" or "otel" — guides assertion construction

<!-- Phase 11 D-17 round-trip helpers (landed by Plan 11-06) -->
Phase11WebAppFactory                    — IClassFixture base (parameterless ctor) OR direct construction with internal logLevelDefaultOverride ctor
ElasticsearchTestClient.PollEsForLog    — exponential backoff + 404/empty-hits tolerance
PrometheusTestClient.QueryPrometheus     — single-shot Prom query
PrometheusTestClient.PollPrometheusUntilSumAtLeast — sleep-then-poll for cumulative threshold
PrometheusTestClient.SumSampleValues     — multi-label result-vector aggregation

<!-- HealthEndpointsTests was rebased in Plan 11-08a (Wave 6 predecessor) — confirm at execution time -->
HealthEndpointsTests inherits the 4 nested fixtures from Phase8WebAppFactory + 1 from Phase11WebAppFactory. No OtelCollectorFixture references remain in HealthEndpointsTests.
</interfaces>
</context>

<tasks>

<task type="auto" tdd="true">
  <name>Task 1: Migrate LogExportTests.cs to ES polling</name>
  <files>tests/BaseApi.Tests/Observability/LogExportTests.cs</files>
  <read_first>
    - tests/BaseApi.Tests/Observability/LogExportTests.cs (full file — Phase 5 baseline, 2 facts, ~139 lines)
    - tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs (Wave 0 constants)
    - tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs (PollEsForLog signature)
    - tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs (fixture base)
    - tests/BaseApi.Tests/Observability/SchemasLogsE2ETests.cs (Plan 11-07 landed — closest analog for ES query body shape)
  </read_first>
  <behavior>
    - Test_LogRecord_Has_CorrelationId_And_ServiceResource: drives GET `/test-obs/ok`, captures corrId from response header (Phase 4 OBSERV-11 echo invariant), polls ES via PollEsForLog with a `term` query on `EsIndexNames.CorrelationIdFieldPath`; asserts:
      - hit is NOT null within 30s budget
      - hit body contains "test-obs ok ran" verbatim
      - hit body contains the corrId verbatim (defensive — confirms we filtered on the right field)
      - hit body contains "sk-api" (service.name — load-bearing per D-07)
      - (version assertion DROPPED per WARNING #7 — couples to appsettings.json Service.Version)
    - Test_LogRecord_CorrelationId_Survives_Sanitization: drives GET with malformed `X-Correlation-Id: invalid\rinjected` header (via TryAddWithoutValidation); captures the SANITIZED 32-hex value from response header; polls ES for log doc with the sanitized id; asserts:
      - hit is NOT null
      - hit body contains the sanitized id
      - hit body does NOT contain `\r` or "injected" (T-05-PII-INJECT regression)
  </behavior>
  <action>
    Rewrite `LogExportTests.cs` in place to use `Phase11WebAppFactory` + `ElasticsearchTestClient`. The file body is ~90% file-exporter-coupled and requires wholesale rewrite (Write tool, NOT incremental Edit).

    Final file content (Task 1 of original Plan 11-08 lifted verbatim, version assertion dropped per WARNING #7):
    ```csharp
    using System.Net;
    using BaseApi.Tests.Observability.Helpers;
    using Xunit;

    namespace BaseApi.Tests.Observability;

    /// <summary>
    /// Phase 11 D-16 migration of Phase 5 SC#1 (OBSERV-02 / OBSERV-05 / OBSERV-07) +
    /// T-05-PII-INJECT regression: Phase 4 <c>CorrelationIdMiddleware</c>'s
    /// <c>BeginScope("CorrelationId", id)</c> propagates through the MEL bridge into
    /// the OTel LoggerProvider (<c>IncludeScopes = true</c>) and surfaces on the
    /// OTLP-exported log doc landed in Elasticsearch under the
    /// <see cref="EsIndexNames.LogsDataStream"/> data stream.
    ///
    /// <para>
    /// Migration: was Phase 5 file-exporter + position-marker fixture (deleted by
    /// Plan 11-05 / 11-08c). Now uses <see cref="Phase11WebAppFactory"/> + ES polling
    /// via <see cref="ElasticsearchTestClient.PollEsForLog"/>.
    /// </para>
    /// </summary>
    [Trait("Phase", "11")]
    [Collection("Observability")]
    public sealed class LogExportTests : IClassFixture<Phase11WebAppFactory>
    {
        private readonly Phase11WebAppFactory _factory;

        public LogExportTests(Phase11WebAppFactory factory) => _factory = factory;

        [Fact]
        public async Task Test_LogRecord_Has_CorrelationId_And_ServiceResource()
        {
            var ct = TestContext.Current.CancellationToken;
            using var client = _factory.CreateClient();

            // The middleware GENERATES a corrId when none is supplied — capture it from the
            // response header. Phase 4 OBSERV-09/10/11 guarantees the same value echoes back.
            var response = await client.GetAsync("/test-obs/ok", ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var corrId = response.Headers.GetValues("X-Correlation-Id").Single();
            Assert.Matches("^[a-f0-9]{32}$", corrId);

            // Poll ES for the log doc carrying this corrId.
            using var es = new ElasticsearchTestClient();
            var queryBody = $$"""
              {
                "size": 10,
                "query": { "term": { "{{EsIndexNames.CorrelationIdFieldPath}}": "{{corrId}}" } }
              }
              """;
            var hit = await es.PollEsForLog(queryBody, timeoutMs: 30_000);
            Assert.NotNull(hit);

            // Field-shape-agnostic sanity probes against the hit's _source raw JSON:
            //   (a) the doc body string "test-obs ok ran" appears verbatim somewhere
            //   (b) the correlation id appears (defensive — confirms field path was correct)
            //   (c) service.name = sk-api appears (load-bearing per D-07 resource_to_telemetry_conversion)
            // service.version is intentionally NOT asserted (checker WARNING #7) — couples to
            // appsettings.json Service.Version and breaks the test on future version bumps.
            var rawJson = hit!.Value.GetRawText();
            Assert.Contains("test-obs ok ran", rawJson);
            Assert.Contains(corrId, rawJson);
            Assert.Contains("sk-api", rawJson);
        }

        [Fact]
        public async Task Test_LogRecord_CorrelationId_Survives_Sanitization()
        {
            var ct = TestContext.Current.CancellationToken;
            using var client = _factory.CreateClient();

            // Send a deliberately-malformed correlation id (Phase 4 Pitfall 3 — control chars).
            // TryAddWithoutValidation bypasses HttpClient header validation so the middleware
            // (D-02 IsValid) is what rejects the value.
            using var req = new HttpRequestMessage(HttpMethod.Get, "/test-obs/ok");
            req.Headers.TryAddWithoutValidation("X-Correlation-Id", "invalid\rinjected");
            var response = await client.SendAsync(req, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Phase 4 sanitization replaces the input with a fresh 32-hex UUID.
            var sanitized = response.Headers.GetValues("X-Correlation-Id").Single();
            Assert.Matches("^[a-f0-9]{32}$", sanitized);

            // Poll ES for the log doc carrying the SANITIZED corrId.
            using var es = new ElasticsearchTestClient();
            var queryBody = $$"""
              {
                "size": 10,
                "query": { "term": { "{{EsIndexNames.CorrelationIdFieldPath}}": "{{sanitized}}" } }
              }
              """;
            var hit = await es.PollEsForLog(queryBody, timeoutMs: 30_000);
            Assert.NotNull(hit);

            // The log doc MUST carry the sanitized value, NOT the raw \r-injected input.
            var rawJson = hit!.Value.GetRawText();
            Assert.Contains(sanitized, rawJson);
            // T-05-PII-INJECT regression — no \r, \n, "injected" anywhere in the doc.
            Assert.DoesNotContain("\\r", rawJson);     // JSON-escaped \r form
            Assert.DoesNotContain("invalid\\rinjected", rawJson);
            Assert.DoesNotContain("injected", rawJson);
        }
    }
    ```

    Use the Write tool (the file exists; Write overwrites).
  </action>
  <verify>
    <automated>! grep "OtelCollectorFixture" tests/BaseApi.Tests/Observability/LogExportTests.cs (negation — old fixture gone); ! grep "ReadExportedLogs\|FlushAsync\|GetLogRecords" tests/BaseApi.Tests/Observability/LogExportTests.cs (negation — old methods gone); grep "IClassFixture<Phase11WebAppFactory>" tests/BaseApi.Tests/Observability/LogExportTests.cs returns 1; grep "ElasticsearchTestClient" tests/BaseApi.Tests/Observability/LogExportTests.cs returns at least 1; grep "EsIndexNames.CorrelationIdFieldPath" tests/BaseApi.Tests/Observability/LogExportTests.cs returns at least 2 (2 facts); grep -c "PollEsForLog" tests/BaseApi.Tests/Observability/LogExportTests.cs returns at least 2; grep -E "^\[Trait\(\"Phase\", \"11\"\)\]" tests/BaseApi.Tests/Observability/LogExportTests.cs returns 1; ! grep "3.2.0" tests/BaseApi.Tests/Observability/LogExportTests.cs (negation — version assertion dropped per WARNING #7); dotnet build SK_P.sln -c Release --no-restore exits 0 zero-warning; dotnet test SK_P.sln --no-restore -c Release --filter "FullyQualifiedName~LogExportTests" exits 0 with 2 facts green</automated>
  </verify>
  <done>LogExportTests has 2 facts using Phase11WebAppFactory + ElasticsearchTestClient + EsIndexNames; no references to OtelCollectorFixture or file-exporter API; version assertion dropped; both facts GREEN against live stack.</done>
</task>

<task type="auto" tdd="true">
  <name>Task 2: Migrate LogLevelFilterTests.cs to ES polling with asymmetric positive/negative budgets</name>
  <files>tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs</files>
  <read_first>
    - tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs (full file — Phase 5 baseline, 2 facts, ~105 lines)
    - tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs
    - tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs
    - tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs (note: has internal Phase11WebAppFactory(string? logLevelDefaultOverride) ctor — accessible from same-assembly test code)
    - .planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-PATTERNS.md (lines 368-371 — negative-assertion asymmetric polling shape recommendation; option a chosen)
  </read_first>
  <behavior>
    - Test_Information_Log_Suppressed_When_Default_Warning: constructs `new Phase11WebAppFactory(logLevelDefaultOverride: "Warning")` (internal ctor); GET `/test-obs/ok` with unique corrId; polls ES with 8s budget; asserts NULL hit (Information log suppressed by MEL filter before reaching OTel).
    - Test_Information_Log_Present_When_Default_Information: constructs `new Phase11WebAppFactory()` (parameterless ctor — default Information); same GET + poll pattern with 30s budget; asserts NOT NULL hit (positive control).
    - Asymmetric polling budgets: positive cases get 30s (Pattern 2 standard); negative gets 8s (RESEARCH PATTERNS option a — long enough for ES indexing pipeline to flush any hit, short enough to keep suite wall-clock manageable).
  </behavior>
  <action>
    Rewrite `LogLevelFilterTests.cs` in place (wholesale Write).

    Final file content (Task 2 of original Plan 11-08 lifted verbatim — no version-coupling issues here, no other revision targets):
    ```csharp
    using System.Net;
    using BaseApi.Tests.Observability.Helpers;
    using Xunit;

    namespace BaseApi.Tests.Observability;

    /// <summary>
    /// Phase 11 D-16 migration of Phase 5 SC#2 (OBSERV-06): setting
    /// <c>Logging:LogLevel:Default = "Warning"</c> suppresses <c>Information</c> logs
    /// from BOTH the console sink AND the OTLP-exported records. This proves the single
    /// MEL filter path (Pitfall 9 / single source of truth) — the filter runs BEFORE
    /// either sink, so both behave identically.
    ///
    /// <para>
    /// Migration: was Phase 5 file-exporter + per-test <c>OtelCollectorFixture</c>
    /// instances (deleted). Now uses <see cref="Phase11WebAppFactory"/> + ES polling.
    /// The negative-assertion fact uses a shorter timeout (~8s) as the "no hit" proof —
    /// long enough for the ES indexing pipeline to flush any hit that DID exist, short
    /// enough to keep the suite wall-clock manageable. PATTERNS option a per RESEARCH.
    /// </para>
    /// </summary>
    [Trait("Phase", "11")]
    [Collection("Observability")]
    public sealed class LogLevelFilterTests
    {
        [Fact]
        public async Task Test_Information_Log_Suppressed_When_Default_Warning()
        {
            var ct = TestContext.Current.CancellationToken;

            // Spin up a fixture with the LogLevel override (internal 1-arg ctor on
            // Phase11WebAppFactory — same assembly so accessible). Each test gets its OWN
            // fixture because the LogLevel needs to be applied at host-build time.
            await using var factory = new Phase11WebAppFactory(logLevelDefaultOverride: "Warning");
            await factory.InitializeAsync();

            // Per-test unique correlation id so the ES query filter is unambiguous.
            var thisRequestCorrId = $"{Guid.NewGuid():N}";

            using var client = factory.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, "/test-obs/ok");
            req.Headers.Add("X-Correlation-Id", thisRequestCorrId);
            var response = await client.SendAsync(req, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Poll ES for any log doc carrying THIS request's correlation id.
            // Expected: NO hit — LogInformation was filtered by MEL before reaching OTel.
            // Shorter budget (8s) for negative assertion per RESEARCH PATTERNS option a.
            using var es = new ElasticsearchTestClient();
            var queryBody = $$"""
              {
                "size": 10,
                "query": { "term": { "{{EsIndexNames.CorrelationIdFieldPath}}": "{{thisRequestCorrId}}" } }
              }
              """;
            var hit = await es.PollEsForLog(queryBody, timeoutMs: 8_000);
            Assert.Null(hit);
        }

        [Fact]
        public async Task Test_Information_Log_Present_When_Default_Information()
        {
            var ct = TestContext.Current.CancellationToken;

            // Default (no override) — appsettings.json declares Logging:LogLevel:Default=Information.
            await using var factory = new Phase11WebAppFactory();
            await factory.InitializeAsync();

            var thisRequestCorrId = $"{Guid.NewGuid():N}";

            using var client = factory.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, "/test-obs/ok");
            req.Headers.Add("X-Correlation-Id", thisRequestCorrId);
            var response = await client.SendAsync(req, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Poll ES — expected: a hit IS present (positive control).
            using var es = new ElasticsearchTestClient();
            var queryBody = $$"""
              {
                "size": 10,
                "query": { "term": { "{{EsIndexNames.CorrelationIdFieldPath}}": "{{thisRequestCorrId}}" } }
              }
              """;
            var hit = await es.PollEsForLog(queryBody, timeoutMs: 30_000);
            Assert.NotNull(hit);
        }
    }
    ```

    Use the Write tool.
  </action>
  <verify>
    <automated>! grep "OtelCollectorFixture" tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs (negation); grep -c "Phase11WebAppFactory" tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs returns at least 2 (2 facts each construct one); grep "logLevelDefaultOverride: \"Warning\"" tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs returns 1; grep "ElasticsearchTestClient" tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs returns at least 1; grep "Assert.Null(hit)" tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs returns 1; grep "Assert.NotNull(hit)" tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs returns 1; grep "timeoutMs: 8_000" tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs returns 1; grep "timeoutMs: 30_000" tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs returns 1; dotnet build SK_P.sln -c Release --no-restore exits 0 zero-warning; dotnet test SK_P.sln --no-restore -c Release --filter "FullyQualifiedName~LogLevelFilterTests" exits 0 with 2 facts green</automated>
  </verify>
  <done>LogLevelFilterTests has 2 facts using Phase11WebAppFactory + ElasticsearchTestClient; negative-assertion fact uses 8s timeout; positive-control fact uses 30s; both facts GREEN against live stack.</done>
</task>

<task type="auto" tdd="true">
  <name>Task 3: Migrate MetricsExportTests.cs to Prom polling (D-04 invariant preserved)</name>
  <files>tests/BaseApi.Tests/Observability/MetricsExportTests.cs</files>
  <read_first>
    - tests/BaseApi.Tests/Observability/MetricsExportTests.cs (full file — Phase 5 baseline, 3 facts, ~149 lines)
    - tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs
    - tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs
    - tests/BaseApi.Tests/Observability/SchemasMetricsE2ETests.cs (Plan 11-07 landed — closest analog for Prom query shape)
    - .planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-RESEARCH.md (Pitfall 1 OTel→Prom name table)
  </read_first>
  <behavior>
    - Test_HttpServerRequestDuration_Present_For_App_Endpoint: issues 5 GETs to `/test-obs/ok`; polls Prom for `http_server_request_duration_seconds_count{service_name="sk-api",http_route="test-obs/ok"}`; asserts cumulative count ≥ 5 (>= cleanliness per Pitfall 5).
    - Test_HealthPath_Absent_From_HttpServerMetrics: issues 10 GETs to `/health/live`; waits 15s scrape cycle; queries Prom for `http_server_request_duration_seconds_count{service_name="sk-api",http_route=~".*health.*"}`; asserts STRICT EMPTY result vector (D-04 filter/health_metrics invariant).
    - Test_RuntimeMetric_ProcessRuntimeDotnet_Exported: issues 1 warmup request; waits 15s; queries Prom for `{__name__=~"dotnet_.*"}` OR `{__name__=~"process_runtime_dotnet_.*"}`; asserts at least one match (naming-era tolerant per D-16).
  </behavior>
  <action>
    Rewrite `MetricsExportTests.cs` in place (wholesale Write).

    Final file content (Task 3 of original Plan 11-08 lifted verbatim — no other revision targets):
    ```csharp
    using System.Net;
    using System.Text.Json;
    using BaseApi.Tests.Observability.Helpers;
    using Xunit;

    namespace BaseApi.Tests.Observability;

    /// <summary>
    /// Phase 11 D-16 migration of Phase 5 SC#4 metrics-half (OBSERV-03 / OBSERV-08 /
    /// HEALTH-05 / D-04 invariant): HTTP server metrics surface in Prometheus for app
    /// endpoints (<c>/test-obs/ok</c>) but NOT for <c>/health/*</c> (filter/health_metrics
    /// processor on the collector drops them before reaching the Prom exporter).
    ///
    /// <para>
    /// Migration: was Phase 5 file-exporter + position-marker fixture (deleted). Now uses
    /// <see cref="Phase11WebAppFactory"/> + Prom polling via <see cref="PrometheusTestClient"/>.
    /// Metric names translated from OTLP form (e.g., <c>http.server.request.duration</c>)
    /// to Prom form (e.g., <c>http_server_request_duration_seconds_count</c>) per RESEARCH
    /// Pitfall 1. service_name label surfaces because <c>resource_to_telemetry_conversion: true</c>
    /// (Phase 11 D-07).
    /// </para>
    /// </summary>
    [Trait("Phase", "11")]
    [Collection("Observability")]
    public sealed class MetricsExportTests : IClassFixture<Phase11WebAppFactory>
    {
        private readonly Phase11WebAppFactory _factory;

        public MetricsExportTests(Phase11WebAppFactory factory) => _factory = factory;

        [Fact]
        public async Task Test_HttpServerRequestDuration_Present_For_App_Endpoint()
        {
            var ct = TestContext.Current.CancellationToken;
            using var client = _factory.CreateClient();

            // Issue 5 requests so the histogram has a meaningful sample count.
            const int RequestCount = 5;
            for (var i = 0; i < RequestCount; i++)
            {
                _ = await client.GetAsync("/test-obs/ok", ct);
            }

            // Prom-form name (Pitfall 1):
            //   OTLP http.server.request.duration (Histogram, unit "s")
            //   → http_server_request_duration_seconds_count (+ _sum + _bucket)
            // service_name="sk-api" because D-07 resource_to_telemetry_conversion: true.
            // http_route="test-obs/ok" (NO leading slash — ASP.NET Core route template).
            const string query = """http_server_request_duration_seconds_count{service_name="sk-api",http_route="test-obs/ok"}""";

            using var prom = new PrometheusTestClient();
            var samples = await prom.PollPrometheusUntilSumAtLeast(query, threshold: RequestCount);

            Assert.NotEmpty(samples);
            var totalCount = PrometheusTestClient.SumSampleValues(samples);
            Assert.True(totalCount >= RequestCount,
                $"Expected http_server_request_duration_seconds_count >= {RequestCount} for "
                + $"service_name=sk-api, http_route=test-obs/ok; got {totalCount}.");
        }

        [Fact]
        public async Task Test_HealthPath_Absent_From_HttpServerMetrics()
        {
            // D-04 invariant — filter/health_metrics processor on the collector drops
            // /health/* data points BEFORE the Prom exporter. STRICT EMPTY assertion.
            var ct = TestContext.Current.CancellationToken;
            using var client = _factory.CreateClient();

            // 10 probe hits to /health/live — would produce http_server_* samples tagged
            // http_route="health/live" if SDK-side filtering existed; instead the Collector's
            // filter/health_metrics processor drops them per Phase 5 Plan 05-02 + Phase 11 D-04.
            for (var i = 0; i < 10; i++)
            {
                _ = await client.GetAsync("/health/live", ct);
            }

            // Wait one Prom scrape cycle (15s) for any leaked samples to appear.
            // PromQL regex match: http_route =~ ".*health.*"
            const string query = """http_server_request_duration_seconds_count{service_name="sk-api",http_route=~".*health.*"}""";

            using var prom = new PrometheusTestClient();
            // Single-shot query after a 15s wait — no need for the threshold poll
            // (we're asserting EMPTY, not asserting a threshold is reached).
            await Task.Delay(15_000);
            var samples = await prom.QueryPrometheus(query);

            Assert.Empty(samples);
        }

        [Fact]
        public async Task Test_RuntimeMetric_ProcessRuntimeDotnet_Exported()
        {
            var ct = TestContext.Current.CancellationToken;
            using var client = _factory.CreateClient();

            // Warm a request so the runtime instrumentation has fired at least once.
            _ = await client.GetAsync("/test-obs/ok", ct);

            // OpenTelemetry.Instrumentation.Runtime 1.15.0 ships newer semantic-convention
            // names; D-16 prescribed process.runtime.dotnet.* but the SDK uses dotnet.* in
            // some versions. Accept EITHER prefix — the point is that SOME runtime metric
            // landed in Prom. Query both with PromQL `or` operator.
            const string queryDotnet  = """{__name__=~"dotnet_.*"}""";
            const string queryProcRt  = """{__name__=~"process_runtime_dotnet_.*"}""";

            // Wait one Prom scrape cycle so any runtime sample has been collected.
            await Task.Delay(15_000);

            using var prom = new PrometheusTestClient();
            var dotnetSamples  = await prom.QueryPrometheus(queryDotnet);
            var procRtSamples  = await prom.QueryPrometheus(queryProcRt);

            var hasRuntimeMetric = dotnetSamples.Count > 0 || procRtSamples.Count > 0;
            Assert.True(hasRuntimeMetric,
                "Expected at least one runtime metric (dotnet_* OR process_runtime_dotnet_*) "
                + "in Prometheus; got 0 samples in either family.");
        }
    }
    ```

    Use the Write tool.
  </action>
  <verify>
    <automated>! grep "OtelCollectorFixture\|ReadExportedMetrics\|FlushAsync\|EnumerateMetricNames\|EnumerateMetricNodes\|GetAllDataPointAttributes" tests/BaseApi.Tests/Observability/MetricsExportTests.cs (negation — old fixture and helpers gone); grep "IClassFixture<Phase11WebAppFactory>" tests/BaseApi.Tests/Observability/MetricsExportTests.cs returns 1; grep -c "PrometheusTestClient" tests/BaseApi.Tests/Observability/MetricsExportTests.cs returns at least 3 (3 facts); grep "http_server_request_duration_seconds_count" tests/BaseApi.Tests/Observability/MetricsExportTests.cs returns at least 2 (facts 1 + 2); grep "http_route=~\".*health.*\"" tests/BaseApi.Tests/Observability/MetricsExportTests.cs returns 1 (fact 2 regex); grep -E "process_runtime_dotnet_\.\*|dotnet_\.\*" tests/BaseApi.Tests/Observability/MetricsExportTests.cs returns at least 1 (fact 3 either-name accept); dotnet build SK_P.sln -c Release --no-restore exits 0 zero-warning; dotnet test SK_P.sln --no-restore -c Release --filter "FullyQualifiedName~MetricsExportTests" exits 0 with 3 facts green</automated>
  </verify>
  <done>MetricsExportTests has 3 facts using Phase11WebAppFactory + PrometheusTestClient; D-04 health-filter invariant preserved (fact 2 strict empty assertion); fact 3 accepts either dotnet_* or process_runtime_dotnet_* naming; all 3 facts GREEN against live stack.</done>
</task>

<task type="auto">
  <name>Task 4: Build + commit 3-file migration</name>
  <files>tests/BaseApi.Tests/Observability/LogExportTests.cs, tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs, tests/BaseApi.Tests/Observability/MetricsExportTests.cs</files>
  <read_first>
    - git status (confirm scope: exactly 3 files modified)
  </read_first>
  <action>
    Stage exactly 3 files:
    - `git add tests/BaseApi.Tests/Observability/LogExportTests.cs`
    - `git add tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs`
    - `git add tests/BaseApi.Tests/Observability/MetricsExportTests.cs`

    Verify scope before committing:
    ```bash
    git status --porcelain | grep -v "^M  tests/BaseApi.Tests/Observability/" && echo "UNEXPECTED" || echo "scope clean"
    ```

    Create commit with the exact message:
    ```
    test(observability): migrate LogExportTests + LogLevelFilterTests + MetricsExportTests to ES/Prom polling
    ```

    Use a HEREDOC for the commit message body. Verify `git status --porcelain` returns empty after commit. Do NOT push. Do NOT delete `OtelCollectorFixture.cs` (Plan 11-08c handles the final deletion).

    After this commit lands, the only remaining consumer of OtelCollectorFixture in the entire repo is... none (HealthEndpointsTests was rebased by Plan 11-08a; these 3 classes are rebased here). But the file itself stays on disk for Plan 11-08c's atomic close.
  </action>
  <verify>
    <automated>git log -1 --format=%s returns "test(observability): migrate LogExportTests + LogLevelFilterTests + MetricsExportTests to ES/Prom polling"; git show --stat HEAD lists exactly 3 files modified (LogExportTests.cs + LogLevelFilterTests.cs + MetricsExportTests.cs); git status --porcelain returns empty; test -f tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs exits 0 (PRESERVED for Plan 11-08c); ! grep -rn "OtelCollectorFixture" tests/BaseApi.Tests/Observability/LogExportTests.cs tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs tests/BaseApi.Tests/Observability/MetricsExportTests.cs tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs (negation — 4 migrated consumer files have ZERO references to the about-to-be-deleted fixture)</automated>
  </verify>
  <done>3-file migration commit landed; working tree clean; OtelCollectorFixture.cs preserved on disk but ORPHANED (zero consumers in tests/ or src/); Plan 11-08c performs the final deletion + Phase 11 close.</done>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| (no new) | Same boundaries as Plans 11-06 + 11-07 + 11-08a. This plan migrates existing facts; no new attack surface. |

## STRIDE Threat Register

| Threat ID | Category | Component | Disposition | Mitigation Plan |
|-----------|----------|-----------|-------------|-----------------|
| T-11-08b-T1 (negative-assertion fact false-positive in LogLevelFilterTests — Information log actually present but 8s budget too short to see it) | T (Test correctness) | LogLevelFilterTests.Test_Information_Log_Suppressed_When_Default_Warning | mitigate | 8s budget exceeds typical ES indexing lag (1-3s per RESEARCH Pattern 2 + Pitfall 5). If a hit existed, it would be visible within the budget. PATTERNS option a — asymmetric polling shape (positive=30s, negative=8s) documented inline. **Verify:** Task 2 verify gate runs the fact GREEN against the live stack. |
| T-11-08b-T2 (D-04 health filter regression — health-route data points leak to Prom) | I (Information Disclosure — health probe noise) | MetricsExportTests.Test_HealthPath_Absent_From_HttpServerMetrics | mitigate | Strict empty assertion on `http_route=~".*health.*"` PromQL match. If the collector's `filter/health_metrics` processor regressed (e.g., dropped in a future plan), this fact fails loudly. Plan 11-03 preserves the processor byte-identical. **Verify:** Task 3 verify gate runs the fact GREEN. |
| T-11-08b-T3 (LogExportTests body-content match false-positive — "sk-api" appears in unrelated doc) | T (Test correctness) | LogExportTests.Test_LogRecord_Has_CorrelationId_And_ServiceResource | accept | ES query filters on `EsIndexNames.CorrelationIdFieldPath` term match — only docs with this specific 32-hex corrId are returned. Probability of `sk-api` substring appearing in an unrelated doc that happens to share the corrId is negligible (128-bit corrId collision space + service_name="sk-api" is the only string of that form in the SDK output). **Verify:** Task 1 verify gate runs the fact GREEN. |
| T-11-08b-T4 (orphan OtelCollectorFixture.cs causes build warnings if it has unused-class detection enabled) | A (Availability — build noise) | tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs | accept | OtelCollectorFixture is `public class` — not flagged as unused by C# compiler regardless of usage count. The file stays as dead-but-compilable code until Plan 11-08c's `git rm`. **Verify:** Task 4 verify gate confirms `dotnet build` exits 0 zero-warning. |
</threat_model>

<verification>
- `! grep "OtelCollectorFixture" tests/BaseApi.Tests/Observability/LogExportTests.cs`.
- `! grep "OtelCollectorFixture" tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs`.
- `! grep "OtelCollectorFixture" tests/BaseApi.Tests/Observability/MetricsExportTests.cs`.
- `test -f tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` exits 0 (preserved for Plan 11-08c).
- `grep "IClassFixture<Phase11WebAppFactory>" tests/BaseApi.Tests/Observability/LogExportTests.cs` returns 1.
- `grep "IClassFixture<Phase11WebAppFactory>" tests/BaseApi.Tests/Observability/MetricsExportTests.cs` returns 1.
- `grep "new Phase11WebAppFactory(logLevelDefaultOverride:" tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` returns 1.
- `grep "EsIndexNames.CorrelationIdFieldPath" tests/BaseApi.Tests/Observability/LogExportTests.cs` returns at least 2.
- `grep "EsIndexNames.CorrelationIdFieldPath" tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` returns at least 2.
- `grep "http_route=~\".*health.*\"" tests/BaseApi.Tests/Observability/MetricsExportTests.cs` returns 1.
- `! grep "3.2.0" tests/BaseApi.Tests/Observability/LogExportTests.cs` (version assertion dropped per WARNING #7).
- `dotnet build SK_P.sln -c Release --no-restore` exits 0 zero-warning.
- `dotnet test SK_P.sln --no-restore -c Release --filter "FullyQualifiedName~LogExportTests\|FullyQualifiedName~LogLevelFilterTests\|FullyQualifiedName~MetricsExportTests"` exits 0 with 7 facts green (2+2+3).
- `git log -1 --format=%s` matches `test(observability): migrate LogExportTests + LogLevelFilterTests + MetricsExportTests to ES/Prom polling`.
- `git show --stat HEAD` shows exactly 3 files modified.
- `git status --porcelain` returns empty post-commit.
</verification>

<success_criteria>
1. `LogExportTests.cs` 2 facts migrated to `Phase11WebAppFactory` + `ElasticsearchTestClient` + `EsIndexNames.CorrelationIdFieldPath`; no references to OtelCollectorFixture; version assertion dropped per WARNING #7.
2. `LogLevelFilterTests.cs` 2 facts migrated; positive 30s budget + negative 8s budget asymmetric polling shape (PATTERNS option a).
3. `MetricsExportTests.cs` 3 facts migrated; D-04 health-filter invariant preserved via strict empty regex match; runtime metric fact accepts either naming era.
4. `OtelCollectorFixture.cs` PRESERVED at this commit's HEAD (Plan 11-08c performs the final deletion).
5. Solution builds zero-warning Release+Debug.
6. All 7 migrated facts GREEN against the live stack (2+2+3); HealthEndpointsTests 7 facts unaffected (Plan 11-08a's commit still HEAD-1).
7. Single git commit `test(observability): migrate LogExportTests + LogLevelFilterTests + MetricsExportTests to ES/Prom polling` exists at HEAD; modifies exactly 3 files; working tree clean post-commit.
</success_criteria>

<output>
After completion, create `.planning/phases/11-migrate-prometheus-and-elastic-containers-from-compose-stack/11-08b-SUMMARY.md`.
</output>
</content>
