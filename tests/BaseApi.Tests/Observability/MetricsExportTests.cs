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
        var samples = await prom.PollPrometheusUntilSumAtLeast(query, threshold: RequestCount, ct: ct);

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
        await Task.Delay(15_000, ct);
        var samples = await prom.QueryPrometheus(query, ct);

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
        await Task.Delay(15_000, ct);

        using var prom = new PrometheusTestClient();
        var dotnetSamples  = await prom.QueryPrometheus(queryDotnet, ct);
        var procRtSamples  = await prom.QueryPrometheus(queryProcRt, ct);

        var hasRuntimeMetric = dotnetSamples.Count > 0 || procRtSamples.Count > 0;
        Assert.True(hasRuntimeMetric,
            "Expected at least one runtime metric (dotnet_* OR process_runtime_dotnet_*) "
            + "in Prometheus; got 0 samples in either family.");
    }
}
