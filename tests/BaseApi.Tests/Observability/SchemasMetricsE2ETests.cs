using System.Net;
using System.Net.Http.Json;
using BaseApi.Service.Features.Schema;
using BaseApi.Tests.Observability.Helpers;
using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// Phase 11 D-17 / TEST-07 — round-trip E2E for the metrics half:
/// drives N (=3) <c>POST /api/v1/schemas</c> requests against the in-process Kestrel
/// host (via <see cref="Phase11WebAppFactory"/>), waits for the OTel SDK to export
/// metrics (1-second interval per Phase 11 D-18 / Pattern 4 fixture override + 15-second
/// Prom scrape cycle per Phase 11 D-08), then polls Prometheus until the cumulative
/// <c>http_server_request_duration_seconds_count</c> sample for the
/// <c>service_name="sk-api"</c>, <c>http_route="api/v1/schemas"</c> labels reaches at
/// least N.
///
/// <para>
/// RESEARCH Pitfall 1 — OTel-to-Prom name translation reference:
/// <list type="bullet">
///   <item>OTLP metric <c>http.server.request.duration</c> (Histogram, unit "s")</item>
///   <item>→ Prom names <c>http_server_request_duration_seconds_count</c> /
///         <c>_sum</c> / <c>_bucket</c> (the histogram triplet)</item>
///   <item><c>service.name</c> resource attr → <c>service_name</c> Prom label (lives
///         because <c>resource_to_telemetry_conversion: true</c> set in collector D-07)</item>
///   <item><c>http_route</c> label is the ASP.NET Core route template VERBATIM
///         WITHOUT leading slash — for <c>POST /api/v1/schemas</c> the controller
///         is decorated with <c>[Route("api/v{version:apiVersion}/[controller]")]</c>
///         (BaseController.cs line 22) so the emitted label value is
///         <c>"api/v{version:apiVersion}/Schemas"</c> (route-template literal with
///         <c>Asp.Versioning</c> constraint preserved + <c>[controller]</c> token
///         resolved to PascalCase). Confirmed empirically against
///         <c>http://localhost:8889/metrics</c> 2026-05-28 (collector
///         resource_to_telemetry_conversion: true + ASP.NET Core HTTP instrumentation
///         pass the literal route template, NOT the resolved request URL).</item>
/// </list>
/// </para>
///
/// <para>
/// RESEARCH Pitfall 7 + Pattern 4 — the SDK metric export interval defaults to 60s;
/// <see cref="Phase11WebAppFactory"/> overrides it to 1s. Combined with the 15s Prom
/// scrape interval, the worst-case wait from "fact issues last POST" to "Prom holds the
/// sample" is ~16s. The poll helper's <c>InitialSleepMs = 15_000</c> + <c>PollIntervalMs = 3_000</c>
/// + <c>PollTimeoutMs = 60_000</c> gives ample margin.
/// </para>
///
/// <para>
/// RESEARCH Pitfall 5 analog — cumulative cleanliness: assert <c>&gt;= N</c> not <c>== N</c>
/// because previous test runs may have left samples for the same label combination in
/// Prometheus. Prom data is in-memory + ephemeral per <c>docker compose down -v</c>, but
/// within a single suite run multiple facts may contribute samples.
/// </para>
/// </summary>
[Trait("Phase", "11")]
[Trait("Category", "E2E")]
[Collection("Observability")]
public sealed class SchemasMetricsE2ETests : IClassFixture<Phase11WebAppFactory>
{
    private readonly Phase11WebAppFactory _factory;

    public SchemasMetricsE2ETests(Phase11WebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task PostSchema_Increments_HttpServerRequestDurationCount_In_Prometheus()
    {
        var ct = TestContext.Current.CancellationToken;
        const int RequestCount = 3;

        using var client = _factory.CreateClient();
        for (var i = 0; i < RequestCount; i++)
        {
            var dto = new SchemaCreateDto(
                Name:        $"E2E-Metrics-{Guid.NewGuid():N}",
                Version:     "1.0.0",
                Description: null,
                Definition:  "{ \"$schema\": \"https://json-schema.org/draft/2020-12/schema\", \"type\": \"object\" }");
            var resp = await client.PostAsJsonAsync("/api/v1/schemas", dto, ct);
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        }

        // OTLP `http.server.request.duration` (Histogram, unit "s") translates to Prom
        // `http_server_request_duration_seconds_count` (the histogram triplet's count
        // dimension — load-bearing for the "≥ N requests happened" assertion).
        // service_name="sk-api" surfaces because Phase 11 D-07 sets
        // resource_to_telemetry_conversion: true on the collector's prometheus exporter.
        //
        // http_route="api/v{version:apiVersion}/Schemas" is the ROUTE TEMPLATE LITERAL
        // emitted by ASP.NET Core HTTP instrumentation — NOT the resolved request URL.
        // Two preservations of the controller decoration:
        //   1. `{version:apiVersion}` — the Asp.Versioning route constraint (Phase 7
        //      sets [Route("api/v{version:apiVersion}/[controller]")] on BaseController),
        //   2. `Schemas` — PascalCase from the controller class name ("SchemasController"
        //      minus suffix), NOT the lowercase user-facing path segment.
        // Empirically verified 2026-05-28 via curl http://localhost:8889/metrics — the
        // collector emits the literal http_route="api/v{version:apiVersion}/Schemas"
        // label value (Rule 1 fix-forward attributed to this task: the plan body assumed
        // the URL-path form "api/v1/schemas" which produces an empty result vector and a
        // 60s polling timeout; Task 3 troubleshooting step 5 explicitly anticipated this
        // discrepancy as a possible diagnostic).
        const string query =
            """http_server_request_duration_seconds_count{service_name="sk-api",http_route="api/v{version:apiVersion}/Schemas"}""";

        using var promClient = new PrometheusTestClient();
        var samples = await promClient.PollPrometheusUntilSumAtLeast(query, threshold: RequestCount, ct: ct);

        Assert.NotEmpty(samples);
        var totalCount = PrometheusTestClient.SumSampleValues(samples);
        Assert.True(totalCount >= RequestCount,
            $"Expected http_server_request_duration_seconds_count >= {RequestCount} for "
            + $"service_name=sk-api, http_route=api/v{{version:apiVersion}}/Schemas; got {totalCount}.");
    }
}
