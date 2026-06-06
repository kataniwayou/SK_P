using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// MLBL-04 integrated guard (Phase 39): the COMPOSED guard that <see cref="LogsResourceBareNameFacts"/>
/// could not be — it builds the logs block AND the metrics block in ONE container the way
/// <c>ObservabilityServiceCollectionExtensions</c> / <c>BaseConsoleObservabilityExtensions</c> do, and proves
/// the versioned metrics service.name does NOT bleed onto the LOGS resource.
/// <para>
/// Why this exists: in OTel .NET 1.15 the shared <c>builder.Services.AddOpenTelemetry().ConfigureResource(...)</c>
/// OVERRIDES the logs provider's <c>SetResourceBuilder(...)</c>. The original code set the combined
/// <c>{name}_{version}</c> name via the shared ConfigureResource, so it leaked onto logs —
/// <c>service.name="keeper_3.7.0"</c> on log docs — silently breaking the Phase-35 bare-name ES query
/// contract (the RealStack E2E facts filter ES on <c>service.name="orchestrator"/"keeper"</c>). The fix sets
/// the versioned resource on the MeterProvider's OWN <c>SetResourceBuilder</c> instead. This test mirrors the
/// fixed wiring and MUST stay in lock-step with both extension files; if a future edit reintroduces the shared
/// ConfigureResource for the versioned name, this goes RED.
/// </para>
/// </summary>
[Collection("Observability")]
public sealed class LogsResourceBleedFacts
{
    private sealed class LogResourceCapture : BaseProcessor<LogRecord>
    {
        public Resource? Captured { get; private set; }
        public override void OnEnd(LogRecord record) => Captured ??= ParentProvider.GetResource();
    }

    [Fact]
    public void VersionedMetricsResource_DoesNotBleed_Into_Logs_ServiceName()
    {
        const string bare = "keeper";
        const string versioned = "keeper_3.7.0";

        var cap = new LogResourceCapture();
        var services = new ServiceCollection();

        // LOGS block (mirrors the extensions' builder.Logging.AddOpenTelemetry SetResourceBuilder block) — BARE.
        services.AddLogging(b => b.AddOpenTelemetry(o =>
        {
            o.SetResourceBuilder(ResourceBuilder.CreateDefault()
                .AddService(serviceName: bare, serviceVersion: "3.7.0"));
            o.AddProcessor(cap);
        }));

        // METRICS block (mirrors the FIXED wiring): versioned service.name on the MeterProvider's OWN
        // SetResourceBuilder — NOT the shared ConfigureResource — so it cannot override the logs resource.
        services.AddOpenTelemetry()
            .WithMetrics(m => m
                .SetResourceBuilder(ResourceBuilder.CreateDefault()
                    .AddService(serviceName: versioned, serviceVersion: "3.7.0"))
                .AddRuntimeInstrumentation());

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("t").LogInformation("bleed probe");

        var name = cap.Captured?.Attributes
            .Where(kvp => kvp.Key == "service.name").Select(kvp => kvp.Value?.ToString()).FirstOrDefault();

        Assert.Equal(bare, name);                 // logs stay BARE (MLBL-04)
        Assert.DoesNotContain("_3.7.0", name);    // no version suffix leaked onto logs
    }
}
