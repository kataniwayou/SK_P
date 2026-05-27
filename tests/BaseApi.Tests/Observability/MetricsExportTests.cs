using System.Text.Json;
using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// SC#4 metrics-half (OBSERV-03 / OBSERV-08 / HEALTH-05): HTTP server metrics
/// (<c>http.server.request.duration</c>) appear for app endpoints (<c>/test-obs/ok</c>)
/// but NOT for <c>/health/*</c> requests. D-16: at least one
/// <c>process.runtime.dotnet.*</c> metric appears in the exported metric stream.
///
/// <para>
/// <b>Plan 05-02 fix-forward to Plan 05-01 (SC#4 metrics-half gap closed):</b>
/// Plan 05-01 Deviation #2 deferred metrics-side <c>/health/*</c> filtering because
/// OpenTelemetry.Instrumentation.AspNetCore 1.15.0's
/// <c>MeterProviderBuilder.AddAspNetCoreInstrumentation</c> is parameterless (no Filter
/// callback). The closing fix-forward is a Collector-side <c>filter/health_metrics</c>
/// processor in <c>compose/otel-collector-config.yaml</c> that drops data points whose
/// <c>metric.name == "http.server.request.duration"</c> AND whose <c>http.route</c>
/// attribute starts with <c>/health/</c>. SDK still emits, Collector drops before the
/// file exporter — observable behaviour is zero health-route data points in
/// telemetry.jsonl. <see cref="Test_HealthPath_Absent_From_HttpServerMetrics"/> now
/// asserts STRICT empty (was SOFT-PASS in Wave-2 task 6 commit).
/// </para>
/// </summary>
[Collection("Observability")]
public sealed class MetricsExportTests : IClassFixture<OtelCollectorFixture>
{
    private readonly OtelCollectorFixture _fixture;

    public MetricsExportTests(OtelCollectorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Test_HttpServerRequestDuration_Present_For_App_Endpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _fixture.CreateClient();

        // Issue a few requests to ensure the histogram has data
        for (int i = 0; i < 5; i++)
            _ = await client.GetAsync("/test-obs/ok", ct);

        await _fixture.FlushAsync(TimeSpan.FromSeconds(1));

        var metricNames = _fixture.ReadExportedMetrics()
            .SelectMany(EnumerateMetricNames)
            .ToHashSet();

        Assert.Contains("http.server.request.duration", metricNames);
    }

    [Fact]
    public async Task Test_HealthPath_Absent_From_HttpServerMetrics()
    {
        // SC#4 metrics-half — /health/* filtered from metrics via Collector-side
        // filter/health_metrics processor (Plan 05-02 fix-forward to Plan 05-01).
        var ct = TestContext.Current.CancellationToken;
        using var client = _fixture.CreateClient();

        // 10 probe hits — would generate data points with http.route="/health/{*}" tags
        // if SDK-side filtering existed; instead, the Collector drops them before file write.
        for (int i = 0; i < 10; i++)
            _ = await client.GetAsync("/health/live", ct);

        await _fixture.FlushAsync(TimeSpan.FromSeconds(1));

        var metrics = _fixture.ReadExportedMetrics();
        var serverDurationDataPoints = metrics
            .SelectMany(EnumerateMetricNodes)
            .Where(m => m.GetProperty("name").GetString() == "http.server.request.duration")
            .SelectMany(GetAllDataPointAttributes)
            .ToList();

        // Inventory http.route + url.path tags across all data points — expect ZERO health routes
        var healthRoutes = new List<string>();
        foreach (var attrs in serverDurationDataPoints)
        {
            foreach (var attr in attrs)
            {
                var key = attr.GetProperty("key").GetString();
                if (key != "http.route" && key != "url.path") continue;
                if (!attr.GetProperty("value").TryGetProperty("stringValue", out var sv)) continue;
                var val = sv.GetString();
                if (val is not null && val.Contains("/health", StringComparison.Ordinal))
                    healthRoutes.Add(val);
            }
        }

        // STRICT empty — Collector-side filter/health_metrics processor drops these
        // data points before file write. Failure here means either: (a) the filter
        // processor was removed from compose/otel-collector-config.yaml, (b) the
        // processor wiring in the metrics pipeline was dropped, or (c) the collector
        // image was downgraded below 0.95.0.
        Assert.Empty(healthRoutes);
    }

    [Fact]
    public async Task Test_RuntimeMetric_ProcessRuntimeDotnet_Exported()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _fixture.CreateClient();
        _ = await client.GetAsync("/test-obs/ok", ct);   // let the host warm a bit

        await _fixture.FlushAsync(TimeSpan.FromSeconds(2));   // runtime metrics emit on a slower cadence

        var metricNames = _fixture.ReadExportedMetrics()
            .SelectMany(EnumerateMetricNames)
            .ToHashSet();

        // D-16 prescribes process.runtime.dotnet.* names, but OpenTelemetry.Instrumentation.Runtime
        // 1.15.0 ships with the newer semantic-convention names: dotnet.gc.collections,
        // dotnet.thread_pool.thread.count, etc. Accept either flavor — the point is that
        // SOME runtime instrumentation metric is present.
        var hasRuntimeMetric =
            metricNames.Any(n => n.StartsWith("process.runtime.dotnet.", StringComparison.Ordinal)) ||
            metricNames.Any(n => n.StartsWith("dotnet.", StringComparison.Ordinal));
        Assert.True(hasRuntimeMetric,
            $"Expected a runtime metric (process.runtime.dotnet.* OR dotnet.*) — got: {string.Join(", ", metricNames)}");
    }

    private static IEnumerable<string> EnumerateMetricNames(JsonElement resourceMetricsContainer)
    {
        foreach (var rm in resourceMetricsContainer.GetProperty("resourceMetrics").EnumerateArray())
        foreach (var sm in rm.GetProperty("scopeMetrics").EnumerateArray())
        foreach (var m in sm.GetProperty("metrics").EnumerateArray())
            yield return m.GetProperty("name").GetString() ?? string.Empty;
    }

    private static IEnumerable<JsonElement> EnumerateMetricNodes(JsonElement resourceMetricsContainer)
    {
        foreach (var rm in resourceMetricsContainer.GetProperty("resourceMetrics").EnumerateArray())
        foreach (var sm in rm.GetProperty("scopeMetrics").EnumerateArray())
        foreach (var m in sm.GetProperty("metrics").EnumerateArray())
            yield return m;
    }

    private static IEnumerable<List<JsonElement>> GetAllDataPointAttributes(JsonElement metricNode)
    {
        // OTLP histogram metrics: metric.histogram.dataPoints[].attributes[]
        if (metricNode.TryGetProperty("histogram", out var hist))
        {
            foreach (var dp in hist.GetProperty("dataPoints").EnumerateArray())
            {
                if (dp.TryGetProperty("attributes", out var attrs))
                    yield return attrs.EnumerateArray().ToList();
            }
        }
    }
}
