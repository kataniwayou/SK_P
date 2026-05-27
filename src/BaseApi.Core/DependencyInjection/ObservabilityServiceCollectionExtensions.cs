using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;                                       // TracerProviderBuilderExtensions.AddNpgsql
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace BaseApi.Core.DependencyInjection;

/// <summary>
/// Phase 5 OTel wiring: logs via MEL bridge (Pitfall 8 / OBSERV-02 / OBSERV-06 / OBSERV-07) +
/// metrics with AspNetCore/HttpClient/Runtime instrumentation + traces with AspNetCore (filtered
/// to exclude <c>/health/*</c> per OBSERV-08 / HEALTH-05 / Pitfall 10) + HttpClient + Npgsql DB
/// spans (OBSERV-12 / T-05-PII — bare <c>.AddNpgsql()</c> per Phase 5 D-05 corrected — the
/// 8.0.4 package default already does NOT capture parameter values).
/// </summary>
internal static class ObservabilityServiceCollectionExtensions
{
    /// <summary>
    /// Takes the host builder because <c>builder.Logging.AddOpenTelemetry</c> requires the
    /// <see cref="ILoggingBuilder"/> surface (not <see cref="IServiceCollection"/>). The
    /// host builder gives access to both <c>.Logging</c> and <c>.Services</c>. This is the
    /// engineering necessity behind the CONTEXT D-13 amendment (see plan body).
    /// </summary>
    internal static IHostApplicationBuilder AddBaseApiObservability(
        this IHostApplicationBuilder builder, IConfiguration cfg)
    {
        var serviceName    = cfg["Service:Name"]!;
        var serviceVersion = cfg["Service:Version"]!;

        // OTel LOGS — MEL bridge (Phase 5 D-09 / OBSERV-02). MUST be builder.Logging.AddOpenTelemetry
        // — NOT services.AddOpenTelemetry().WithLogging() (creates a parallel provider that
        // bypasses MEL filtering per Phase 5 Pitfall 9).
        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.IncludeScopes           = true;
            o.ParseStateValues        = true;
            o.SetResourceBuilder(ResourceBuilder.CreateDefault()
                .AddService(serviceName: serviceName, serviceVersion: serviceVersion));
            o.AddOtlpExporter();
        });

        // OTel METRICS + TRACES. ConfigureResource MUST come before WithMetrics/WithTracing so
        // the resource propagates to both branches.
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(
                serviceName: serviceName,
                serviceVersion: serviceVersion))
            .WithMetrics(m => m
                // OpenTelemetry.Instrumentation.AspNetCore 1.15.0's metrics-side
                // AddAspNetCoreInstrumentation is parameterless (no opts.Filter overload on
                // the MeterProviderBuilder). /health metrics filtered at the Collector via
                // filterprocessor + OTTL — see compose/otel-collector-config.yaml. Carried
                // from Phase 5 Plan 05-01 deviation.
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter())
            .WithTracing(t => t
                .SetSampler(new AlwaysOnSampler())          // Phase 5 D-04 — 100% sample in v1
                .AddAspNetCoreInstrumentation(opts =>
                    opts.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddNpgsql()                                // T-05-PII — default-secure (no parameter values captured)
                .AddOtlpExporter());

        return builder;
    }
}
