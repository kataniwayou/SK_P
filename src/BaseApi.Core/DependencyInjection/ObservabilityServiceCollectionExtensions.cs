using BaseApi.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace BaseApi.Core.DependencyInjection;

/// <summary>
/// OTel wiring: logs via MEL bridge (Pitfall 8 / OBSERV-02 / OBSERV-06 / OBSERV-07) +
/// metrics with AspNetCore/HttpClient/Runtime instrumentation. Traces pipeline REMOVED
/// in Phase 11 (D-03) — OBSERV-12 superseded to Out of Scope (REQUIREMENTS.md Phase 11
/// amendment). The collector receives no traces (Plan 11-03 deletes the pipeline);
/// the SDK no longer emits them (Plan 11-05 stripped the prior tracer-provider block).
/// </summary>
public static class ObservabilityServiceCollectionExtensions
{
    /// <summary>
    /// Takes the host builder because <c>builder.Logging.AddOpenTelemetry</c> requires the
    /// <see cref="ILoggingBuilder"/> surface (not <see cref="IServiceCollection"/>). The
    /// host builder gives access to both <c>.Logging</c> and <c>.Services</c>. This is the
    /// engineering necessity behind the CONTEXT D-13 amendment (see plan body).
    ///
    /// <para>
    /// DEVIATION (Rule 3 — plan-gap fix-forward): the plan body specified <c>internal static</c>,
    /// but this method is invoked from <c>BaseApi.Service/Program.cs</c> across the assembly
    /// boundary (D-13 amendment requires separate invocation on IHostApplicationBuilder).
    /// Promoting to <c>public static</c> matches the visibility of the two other top-level
    /// entries (AddBaseApi, UseBaseApi) + already-public Phase 6 extensions
    /// (AddBaseApiValidation, AddBaseApiMapping). The alternative (InternalsVisibleTo) adds
    /// indirection without value.
    /// </para>
    /// </summary>
    public static IHostApplicationBuilder AddBaseApiObservability(
        this IHostApplicationBuilder builder, IConfiguration cfg)
    {
        // WR-03: fail fast at the boundary with an actionable message rather than letting
        // null propagate into ResourceBuilder.AddService(null, null) → OTel SDK ArgumentNullException.
        var serviceName    = cfg.Require("Service:Name");
        var serviceVersion = cfg.Require("Service:Version");

        // Phase 30 (METRIC-01/D-10): resolve the per-replica instance id ONCE per process, then
        // apply it as a service.instance.id resource attribute to BOTH the logs and metrics
        // resources below. Resolving ONCE (a single local) is a correctness requirement — calling
        // the resolver twice risks the Guid fallback differing between the two resources, so the
        // logs and metrics signals would carry different service_instance_id labels.
        var instanceId    = ResolveInstanceId();
        var instanceAttrs = new[] { new KeyValuePair<string, object>("service.instance.id", instanceId) };

        // OTel LOGS — MEL bridge (Phase 5 D-09 / OBSERV-02). MUST be builder.Logging.AddOpenTelemetry
        // — NOT services.AddOpenTelemetry().WithLogging() (creates a parallel provider that
        // bypasses MEL filtering per Phase 5 Pitfall 9).
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

        // OTel METRICS. ConfigureResource MUST come before WithMetrics so the resource
        // propagates to the metrics provider. Traces pipeline REMOVED in Phase 11 (D-03).
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(serviceName: serviceName, serviceVersion: serviceVersion)
                .AddAttributes(instanceAttrs))    // Phase 30 METRIC-01/02/03 — every metric carries service.instance.id
            .WithMetrics(m => m
                // OpenTelemetry.Instrumentation.AspNetCore 1.15.0's metrics-side
                // AddAspNetCoreInstrumentation is parameterless (no opts.Filter overload on
                // the MeterProviderBuilder). /health metrics filtered at the Collector via
                // filterprocessor + OTTL — see compose/otel-collector-config.yaml. Carried
                // from Phase 5 Plan 05-01 deviation + preserved Phase 11 D-04.
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter());

        return builder;
    }

    /// <summary>
    /// Phase 30 (METRIC-01/D-09/D-10) — resolves the per-replica <c>service.instance.id</c> from
    /// the env precedence <c>POD_NAME → HOSTNAME → MachineName → GUID</c>. DUPLICATED independently
    /// in <c>BaseConsole.Core</c>'s <c>BaseConsoleObservabilityExtensions</c> (D-09 —
    /// <c>BaseConsole.Core</c> is hard-forbidden from referencing <c>BaseApi.Core</c>, and a ~6-line
    /// helper is not worth a shared lib; <c>Messaging.Contracts</c> is the wrong home).
    /// </summary>
    private static string ResolveInstanceId() =>
        Environment.GetEnvironmentVariable("POD_NAME")
        ?? Environment.GetEnvironmentVariable("HOSTNAME")
        ?? Environment.MachineName
        ?? Guid.NewGuid().ToString("N");   // MachineName is effectively non-null; GUID is the documented final fallback (D-10)
}
