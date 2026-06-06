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

        // Phase 30 (METRIC-01/D-10): resolve the per-replica instance id ONCE per process, then
        // apply it as a service.instance.id resource attribute to BOTH the logs and metrics
        // resources below. Resolving ONCE (a single local) is a correctness requirement — calling
        // the resolver twice risks the Guid fallback differing between the two resources, so the
        // logs and metrics signals would carry different service_instance_id labels.
        var instanceId    = ResolveInstanceId();
        var instanceAttrs = new[] { new KeyValuePair<string, object>("service.instance.id", instanceId) };

        // OTel LOGS — MEL bridge. IncludeScopes=true is load-bearing: it serializes the
        // inbound consume filter's "CorrelationId" log scope as a telemetry attribute.
        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.IncludeScopes           = true;
            o.ParseStateValues        = true;
            o.SetResourceBuilder(ResourceBuilder.CreateDefault()
                .AddService(serviceName: serviceName, serviceVersion: serviceVersion)
                .AddAttributes(instanceAttrs));   // Phase 30 METRIC-01 — every log carries service.instance.id
            o.AddOtlpExporter();
        });

        // OTel METRICS. No tracer provider (CONSOLE-02).
        //
        // MLBL-04 FIX (Phase 39): the versioned {name}_{version} service.name is set on the MeterProvider's
        // OWN resource via SetResourceBuilder — NOT via the shared ConfigureResource. In OTel .NET 1.15 the
        // shared ConfigureResource OVERRIDES the logs provider's SetResourceBuilder (proven by
        // LogsResourceBleedFacts), so the versioned name leaked onto LOGS too — emitting
        // service.name="{name}_{version}" (e.g. keeper_3.7.0) on logs and breaking the Phase-35 bare-name ES
        // query contract. A per-provider SetResourceBuilder keeps metrics versioned (MLBL-01/D-01) while logs
        // stay BARE (MLBL-04 / Pitfall 5).
        builder.Services.AddOpenTelemetry()
            .WithMetrics(m => m
                .SetResourceBuilder(ResourceBuilder.CreateDefault()
                    // MLBL-01/D-01: combined {name}_{version} (e.g. keeper_3.7.0) so every Prom series
                    // carries a single human label; service.version still set standalone (D-07).
                    .AddService(serviceName: $"{serviceName}_{serviceVersion}", serviceVersion: serviceVersion)
                    .AddAttributes(instanceAttrs))    // Phase 30 METRIC-01/02 — every metric carries service.instance.id; service_name={name}_{version} (MLBL-01)
                // REMOVED vs the API base library: AspNetCore + HttpClient instrumentation
                // (the worker host has no inbound HTTP request surface beyond health probes).
                .AddMeter(InstrumentationOptions.MeterName)   // "MassTransit" (CONSOLE-02)
                .AddRuntimeInstrumentation()
                .AddOtlpExporter());

        return builder;   // never add a tracer provider
    }

    /// <summary>
    /// Phase 30 (METRIC-01/D-09/D-10) — resolves the per-replica <c>service.instance.id</c> from
    /// the env precedence <c>POD_NAME → HOSTNAME → MachineName → GUID</c>. DUPLICATED independently
    /// from <c>BaseApi.Core</c>'s <c>ObservabilityServiceCollectionExtensions</c> (D-09 —
    /// <c>BaseConsole.Core</c> is hard-forbidden from referencing <c>BaseApi.Core</c>, and a ~6-line
    /// helper is not worth a shared lib; <c>Messaging.Contracts</c> is the wrong home).
    /// <para>
    /// DRIFT GUARD (IN-03) — this precedence expression is mirrored byte-for-byte in THREE places that
    /// MUST change in lock-step: (1) here, (2) <c>BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs</c>
    /// (<c>ResolveInstanceId</c>), (3) the hermetic mirror <c>tests/BaseApi.Tests/Observability/ResolveInstanceIdFacts.cs</c>
    /// (<c>Resolve</c>), which exists to catch precedence drift. Edit all three together.
    /// </para>
    /// </summary>
    private static string ResolveInstanceId() =>
        Environment.GetEnvironmentVariable("POD_NAME")
        ?? Environment.GetEnvironmentVariable("HOSTNAME")
        ?? Environment.MachineName
        ?? Guid.NewGuid().ToString("N");   // MachineName is effectively non-null; GUID is the documented final fallback (D-10)
}
