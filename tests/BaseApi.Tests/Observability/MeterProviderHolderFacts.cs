using BaseProcessor.Core.Observability;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// MLBL-03 (D-02/D-03, Model A1) — the highest-value hermetic proof of the
/// <see cref="MeterProviderHolder"/> swap, with NO compose stack / live boot-window race:
/// <list type="bullet">
///   <item>provider #1 (the host-built placeholder) carries <c>service.name == "processor-sample_3.5.0"</c>
///         and <c>service.instance.id == "unit-instance-42"</c>;</item>
///   <item>after <c>SwapTo("db-name_9.9.9")</c>, the NEW provider carries
///         <c>service.name == "db-name_9.9.9"</c> and the SAME
///         <c>service.instance.id == "unit-instance-42"</c> (Pitfall 4 — instance id preserved);</item>
///   <item>provider #1 is shut down/disposed by the swap (its reader's <c>OnShutdown</c> fires), and a
///         second <c>holder.Dispose()</c> does not throw (A1 double-dispose idempotency).</item>
/// </list>
///
/// <para>
/// <b>Inspection seam (PATTERNS flags no in-repo precedent for reading a built MeterProvider's resource):</b>
/// the public <c>OpenTelemetry.ProviderExtensions.GetResource(BaseProvider)</c> extension reads the
/// configured <see cref="Resource"/> straight off any built provider — the metrics analog of the logs
/// test's <c>ParentProvider.GetResource()</c>. Building each provider does NOT require a reachable OTLP
/// collector (the gRPC channel is lazy), so the holder's real <c>Build</c>/<c>SwapTo</c> path runs as-is.
/// Disposal of provider #1 is proven via a custom <see cref="MetricReader"/> on the host provider whose
/// <c>OnShutdown</c> flips a flag when <c>MeterProvider.Dispose()</c> shuts the pipeline down.
/// </para>
/// </summary>
[Collection("Observability")]
public sealed class MeterProviderHolderFacts
{
    private const string InstanceId       = "unit-instance-42";
    private const string PlaceholderName  = "processor-sample_3.5.0";
    private const string DbName           = "db-name_9.9.9";
    private const string ServiceVersion   = "3.5.0";

    /// <summary>A reader whose <c>OnShutdown</c> sets a flag — fired when the owning provider is Disposed.</summary>
    private sealed class ShutdownSentinelReader : MetricReader
    {
        public bool ShutdownCalled { get; private set; }
        protected override bool OnShutdown(int timeoutMilliseconds)
        {
            ShutdownCalled = true;
            return true;
        }
    }

    private static string? ServiceName(MeterProvider provider) =>
        provider.GetResource().Attributes
            .Where(kvp => kvp.Key == "service.name")
            .Select(kvp => kvp.Value?.ToString())
            .FirstOrDefault();

    private static string? InstanceIdOf(MeterProvider provider) =>
        provider.GetResource().Attributes
            .Where(kvp => kvp.Key == "service.instance.id")
            .Select(kvp => kvp.Value?.ToString())
            .FirstOrDefault();

    [Fact]
    public void SwapTo_Swaps_ServiceName_Preserves_InstanceId_And_Disposes_Provider1()
    {
        // --- Arrange: provider #1 = the host-built placeholder resource (A1) + a shutdown sentinel. ---
        var sentinel = new ShutdownSentinelReader();
        var provider1 = Sdk.CreateMeterProviderBuilder()
            .ConfigureResource(r => r
                .AddService(serviceName: PlaceholderName, serviceVersion: ServiceVersion)
                .AddAttributes(new[] { new KeyValuePair<string, object>("service.instance.id", InstanceId) }))
            .AddMeter(ProcessorMetrics.MeterName)
            .AddReader(sentinel)
            .Build();

        // provider #1 resource: placeholder name + the captured instance id.
        Assert.Equal(PlaceholderName, ServiceName(provider1));
        Assert.Equal(InstanceId, InstanceIdOf(provider1));

        var holder = new MeterProviderHolder(provider1, InstanceId, ServiceVersion);

        // --- Act: swap to the DB-sourced service.name. The holder builds provider #2 + disposes #1. ---
        holder.SwapTo(DbName);

        // --- Assert: provider #1 was shut down by the swap (its reader's OnShutdown fired). ---
        Assert.True(sentinel.ShutdownCalled, "provider #1 must be disposed/shut down by SwapTo (A1).");

        // provider #2 (the holder's _current) carries the DB name but the SAME instance id (Pitfall 4).
        var provider2 = CurrentProviderOf(holder);
        Assert.Equal(DbName, ServiceName(provider2));
        Assert.Equal(InstanceId, InstanceIdOf(provider2));   // instance id preserved across the swap

        // A1 double-dispose safety: the holder's Dispose (which disposes #2) plus the already-disposed
        // #1 must not throw — exercising the idempotency the A1 ownership model relies on.
        var ex = Record.Exception(() =>
        {
            holder.Dispose();   // disposes provider #2
            provider1.Dispose(); // second dispose of provider #1 — must be a safe no-op
        });
        Assert.Null(ex);
    }

    /// <summary>Reads the holder's current provider (#2 after the swap) via its private field — the only seam.</summary>
    private static MeterProvider CurrentProviderOf(MeterProviderHolder holder)
    {
        var field = typeof(MeterProviderHolder)
            .GetField("_current", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (MeterProvider)field!.GetValue(holder)!;
    }
}
