using MassTransit.Monitoring;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace BaseProcessor.Core.Observability;

/// <summary>
/// MLBL-03 (D-02/D-03, Model A1): bridges the frozen-at-Build metrics Resource to the async DB
/// identity. The HOST builds provider #1 with the appsettings placeholder resource (shared path,
/// unchanged — D-06); on identity-resolve in Loop A this holder builds provider #2 with the
/// DB-sourced <c>service.name</c> and disposes provider #1. Meters are NOT recreated — provider #2
/// re-subscribes by meter NAME (<c>BaseProcessor</c> / <c>MassTransit</c> / runtime). The single
/// resolved <c>service.instance.id</c> (Phase 30 D-10 single-resolve invariant) is captured ONCE
/// (ctor param) and reused across the swap so logs + metrics #1 + metrics #2 carry the identical
/// value (Pitfall 4). Dispose of provider #1 at swap + the DI container's shutdown Dispose are both
/// safe — <c>MeterProvider.Dispose()</c> is idempotent (OTel .NET 1.15.3 contract / A1).
///
/// <para>
/// <b>Ownership (Model A1):</b> the holder owns ONLY provider #2. Provider #1 is the host's shared
/// MeterProvider (built by the unchanged <c>AddBaseConsoleObservability</c> path + the additive
/// <c>ConfigureOpenTelemetryMeterProvider(mp =&gt; mp.AddMeter(ProcessorMetrics.MeterName))</c> in
/// <c>AddBaseProcessor</c>); the holder receives it via ctor as the initial <see cref="_current"/>.
/// At swap time the holder disposes the host's #1 and installs its own #2; the DI container disposes
/// #1 a second time at shutdown — a SAFE no-op.
/// </para>
/// </summary>
public sealed class MeterProviderHolder : IDisposable
{
    private readonly string _instanceId;     // captured ONCE — never re-resolve (Pitfall 4 / Phase 30 D-10)
    private readonly string _serviceVersion; // appsettings Service:Version — kept as service.version attr (D-07)
    private MeterProvider _current;          // starts as the host-built provider #1 (A1)

    /// <param name="hostProvider">Provider #1 — the host's shared MeterProvider (placeholder resource). A1.</param>
    /// <param name="instanceId">The already-resolved <c>service.instance.id</c> — reused for #2 (never re-resolve).</param>
    /// <param name="serviceVersion">The appsettings <c>Service:Version</c> — kept as the <c>service.version</c> attr (D-07).</param>
    public MeterProviderHolder(MeterProvider hostProvider, string instanceId, string serviceVersion)
    {
        _current        = hostProvider;      // A1: provider #1 is the host's; holder will swap to #2
        _instanceId     = instanceId;
        _serviceVersion = serviceVersion;
    }

    /// <summary>
    /// Builds a STANDALONE provider via <see cref="Sdk.CreateMeterProviderBuilder"/> (NOT host-owned,
    /// so it is safe for this holder to own + dispose). Meters are referenced BY NAME so provider #2
    /// re-subscribes to the existing <c>BaseProcessor</c> / <c>MassTransit</c> / runtime meters. The
    /// resource carries the combined <c>{name}_{version}</c> <c>service.name</c> (passed in) + the
    /// captured <c>service.instance.id</c>. The OTLP exporter is BARE — it inherits
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> (the appsettings endpoint key is dead).
    /// </summary>
    private MeterProvider Build(string serviceName) =>
        Sdk.CreateMeterProviderBuilder()
           .ConfigureResource(r => r
               .AddService(serviceName: serviceName, serviceVersion: _serviceVersion)   // service.version kept (D-07)
               .AddAttributes(new[]
               {
                   new KeyValuePair<string, object>("service.instance.id", _instanceId) // SAME id across swap (Pitfall 4)
               }))
           .AddMeter(ProcessorMetrics.MeterName)               // "BaseProcessor" — const, NOT a literal (D-02)
           .AddMeter(InstrumentationOptions.MeterName)         // "MassTransit"
           .AddRuntimeInstrumentation()
           .AddOtlpExporter()                                  // BARE — inherits OTEL_EXPORTER_OTLP_ENDPOINT (do NOT hardcode)
           .Build();

    /// <summary>
    /// Called from Loop A immediately after <c>SetIdentity</c>, BEFORE the queue-bind / <c>MarkHealthy</c>.
    /// Build-before-dispose ordering is load-bearing for race-safety (Pitfall 2): <see cref="_current"/>
    /// is ALWAYS a live provider, so the runtime / MassTransit meters always write to a current provider.
    /// </summary>
    public void SwapTo(string resolvedServiceName)
    {
        var next  = Build(resolvedServiceName);   // (1) build #2 first — a live provider exists before #1 dies
        var prior = _current;
        _current  = next;                          // (2) repoint BEFORE disposing prior
        prior.ForceFlush(timeoutMilliseconds: 5000); // (3) push the placeholder window's in-flight batch
        prior.Dispose();                           // (4) flush+shutdown reader, release OTLP gRPC channel
    }

    /// <summary>Host shutdown — idempotent if also disposed by DI (A1 double-dispose safe).</summary>
    public void Dispose() => _current?.Dispose();
}
