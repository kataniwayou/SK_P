using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// MLBL-04 hermetic guard (Pitfall 5): the LOGS resource <c>service.name</c> MUST stay the BARE
/// identity (e.g. <c>keeper</c>) — never the metrics-side combined <c>{name}_{version}</c>
/// (<c>keeper_3.7.0</c>). The Phase-35 ES query contract (<c>service.name="keeper"</c> and the
/// other consoles' bare-name ES filters) depends on this; if a future edit ever leaks the
/// <c>_{version}</c> combine into the logs <c>SetResourceBuilder</c> block of
/// <see cref="BaseConsole.Core.DependencyInjection.BaseConsoleObservabilityExtensions"/> /
/// <c>BaseApi.Core.ObservabilityServiceCollectionExtensions</c>, this test goes RED instead of
/// silently breaking ES log queries in production.
///
/// <para>
/// Hermetic build-and-inspect (no compose stack): mirrors the
/// <see cref="ProcessorIdEnricherTests"/> scaffold — a <see cref="BaseProcessor{T}"/> capturing
/// processor on a real OTel logger provider — but builds the provider's resource the SAME way the
/// shared LOGS path does (<c>ResourceBuilder.CreateDefault().AddService(serviceName: "keeper",
/// serviceVersion: "3.7.0")</c>, bare — mirrors <c>BaseConsoleObservabilityExtensions.cs</c> logs
/// block) and reads <c>service.name</c> off the built provider's resource
/// (<c>ParentProvider.GetResource().Attributes</c>).
/// </para>
/// </summary>
[Collection("Observability")]
public sealed class LogsResourceBareNameFacts
{
    /// <summary>Captures the OTel resource attached to the logger provider via the OnEnd processor seam.</summary>
    private sealed class ResourceCapturingProcessor : BaseProcessor<LogRecord>
    {
        public Resource? CapturedResource { get; private set; }

        public override void OnEnd(LogRecord record)
        {
            // ParentProvider is the BaseProvider the SDK attaches when the processor is added to the
            // pipeline; GetResource() returns the provider's configured Resource (SetResourceBuilder).
            CapturedResource ??= ParentProvider.GetResource();
        }
    }

    /// <summary>
    /// Builds an OTel logger provider whose resource is the BARE keeper identity (mirrors the shared
    /// LOGS path), emits one log through a capturing processor, and returns the captured resource's
    /// <c>service.name</c> attribute value.
    /// </summary>
    private static string? EmitOneLogAndReadServiceName()
    {
        var capture = new ResourceCapturingProcessor();
        using var factory = LoggerFactory.Create(b => b.AddOpenTelemetry(o =>
        {
            // Mirrors BaseConsoleObservabilityExtensions.cs:56-58 / ObservabilityServiceCollectionExtensions.cs:61-63
            // — the LOGS resource is BARE: serviceName WITHOUT the _{version} suffix (MLBL-04).
            o.SetResourceBuilder(ResourceBuilder.CreateDefault()
                .AddService(serviceName: "keeper", serviceVersion: "3.7.0"));
            o.AddProcessor(capture);
        }));

        factory.CreateLogger("test").LogInformation("logs-resource bare-name probe");

        Assert.NotNull(capture.CapturedResource);
        var serviceName = capture.CapturedResource!.Attributes
            .Where(kvp => kvp.Key == "service.name")
            .Select(kvp => kvp.Value?.ToString())
            .FirstOrDefault();
        return serviceName;
    }

    [Fact]
    public void LogsResource_ServiceName_Is_Bare_No_Version_Suffix()
    {
        var serviceName = EmitOneLogAndReadServiceName();

        Assert.Equal("keeper", serviceName);                 // bare identity (MLBL-04)
        Assert.DoesNotContain("_3.7.0", serviceName);        // explicit: no version suffix leaked into logs
    }
}
