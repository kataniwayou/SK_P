using BaseConsole.Core.Configuration;
using MassTransit.Monitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace BaseConsole.Core.DependencyInjection;

/// <summary>
/// Console-flavored OpenTelemetry wiring (CONSOLE-02): logs via the MEL bridge
/// (<c>builder.Logging.AddOpenTelemetry</c>, IncludeScopes load-bearing for the CorrelationId
/// scope serialization) + metrics with the MassTransit meter + runtime instrumentation + OTLP.
///
/// <para>
/// Deltas vs the API base library analog: the worker host has no inbound HTTP request surface
/// beyond the minimal health probes, so AspNetCore + HttpClient instrumentation are REMOVED and
/// the MassTransit meter is added instead. No tracer provider is ever created — no trace
/// pipeline and no activity-source registration here (no traces backend in this milestone).
/// </para>
/// </summary>
public static class BaseConsoleObservabilityExtensions
{
    /// <summary>
    /// Takes the host builder because <c>builder.Logging.AddOpenTelemetry</c> requires the
    /// <see cref="ILoggingBuilder"/> surface (not <see cref="IServiceCollection"/>); the host
    /// builder exposes both <c>.Logging</c> and <c>.Services</c>. Service name/version are read
    /// from the console's own configuration (D-07 — nothing hardcoded).
    /// </summary>
    public static IHostApplicationBuilder AddBaseConsoleObservability(
        this IHostApplicationBuilder builder, IConfiguration cfg)
    {
        // Fail fast at the boundary with an actionable message rather than letting null
        // propagate into ResourceBuilder.AddService(null, null).
        var serviceName    = cfg.Require("Service:Name");
        var serviceVersion = cfg.Require("Service:Version");

        // OTel LOGS — MEL bridge. IncludeScopes=true is load-bearing: it serializes the
        // inbound consume filter's "CorrelationId" log scope as a telemetry attribute.
        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.IncludeScopes           = true;
            o.ParseStateValues        = true;
            o.SetResourceBuilder(ResourceBuilder.CreateDefault()
                .AddService(serviceName: serviceName, serviceVersion: serviceVersion));
            o.AddOtlpExporter();
        });

        // OTel METRICS. ConfigureResource MUST come before WithMetrics so the resource
        // propagates to the metrics provider. No tracer provider (CONSOLE-02).
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(
                serviceName: serviceName,
                serviceVersion: serviceVersion))
            .WithMetrics(m => m
                // REMOVED vs the API base library: AspNetCore + HttpClient instrumentation
                // (the worker host has no inbound HTTP request surface beyond health probes).
                .AddMeter(InstrumentationOptions.MeterName)   // "MassTransit" (CONSOLE-02)
                .AddRuntimeInstrumentation()
                .AddOtlpExporter());

        return builder;   // never add a tracer provider
    }
}
