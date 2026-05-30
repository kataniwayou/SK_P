using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace BaseApi.Tests.Console;

/// <summary>
/// CONSOLE-02 / D-02 facts #4-5 (Pitfall 1 — the console analog of the deleted API TraceExportTests):
/// <list type="bullet">
///   <item>NO <see cref="TracerProvider"/> is resolvable — the console never builds a trace pipeline
///         (T-18-07: no traces resurrection).</item>
///   <item>A <see cref="MeterProvider"/> IS resolvable — metrics-only OTel is wired.</item>
///   <item>No AspNetCore/HttpClient instrumentation is registered — the worker host has no inbound HTTP
///         request surface beyond the embedded health probes, so that instrumentation is deliberately absent
///         (vs the API base library which registers both).</item>
/// </list>
/// </summary>
public sealed class ConsoleObservabilityTests : IClassFixture<ConsoleTestHostFixture>
{
    private readonly ConsoleTestHostFixture _fixture;

    public ConsoleObservabilityTests(ConsoleTestHostFixture fixture) => _fixture = fixture;

    [Fact]
    public void No_TracerProvider_Resolvable()
    {
        // D-02 fact #4 / T-18-07: AddBaseConsoleObservability never calls WithTracing.
        Assert.Null(_fixture.Host.Services.GetService<TracerProvider>());
    }

    [Fact]
    public void MeterProvider_Resolvable()
    {
        // D-02: metrics-only OTel — MeterProvider present (MassTransit meter + runtime instrumentation).
        Assert.NotNull(_fixture.Host.Services.GetService<MeterProvider>());
    }

    [Fact]
    public void No_AspNetCore_Or_HttpClient_Instrumentation()
    {
        // D-02 fact #5: scan the captured descriptor set for any registration whose service OR
        // implementation type is sourced from the OpenTelemetry AspNetCore/Http instrumentation assemblies.
        // The console wiring adds only the MassTransit meter + runtime instrumentation, so none should appear.
        var offendingAssemblyMarkers = new[]
        {
            "OpenTelemetry.Instrumentation.AspNetCore",
            "OpenTelemetry.Instrumentation.Http",
        };

        static IEnumerable<string?> AssemblyNamesOf(ServiceDescriptor d)
        {
            yield return d.ServiceType.Assembly.GetName().Name;
            if (d.ImplementationType is not null)
                yield return d.ImplementationType.Assembly.GetName().Name;
            if (d.ImplementationInstance is not null)
                yield return d.ImplementationInstance.GetType().Assembly.GetName().Name;
        }

        var offenders = _fixture.RegisteredDescriptors
            .Where(d => AssemblyNamesOf(d).Any(asm =>
                asm is not null && offendingAssemblyMarkers.Any(m => asm.StartsWith(m, StringComparison.Ordinal))))
            .Select(d => d.ServiceType.FullName)
            .ToList();

        Assert.Empty(offenders);
    }
}
