using System.Diagnostics.Metrics;

namespace Keeper.Observability;

/// <summary>D-07 — the Keeper's code-owned <see cref="Meter"/> ("Keeper") holding the recovery-state
/// observability instruments. Modeled EXACTLY on <c>BaseProcessor.Core.Observability.ProcessorMetrics</c>:
/// built via <see cref="IMeterFactory"/> (the .NET 8 blessed DI pattern) — NEVER a <c>static Meter</c>,
/// which would leak across tests in the shared hermetic process. <see cref="MeterName"/> is the single
/// const referenced by BOTH this <c>meterFactory.Create</c> call AND the
/// <c>ConfigureOpenTelemetryMeterProvider(mp =&gt; mp.AddMeter(KeeperMetrics.MeterName))</c> registration
/// (the meter is wired in Plan 02's Program.cs edit).
/// <para>
/// The counter name is snake_case with NO Prometheus counter suffix: the collector's prometheus exporter
/// <c>add_metric_suffixes</c> default appends the suffix itself (matches <c>processor_dispatch_deduped</c>).
/// </para></summary>
public sealed class KeeperMetrics
{
    /// <summary>The meter name — MUST equal the <c>AddMeter("Keeper")</c> registration.</summary>
    public const string MeterName = "Keeper";

    /// <summary>D-07: <c>keeper_reinject_dropped</c> — incremented at the REINJECT by-design absent-data
    /// drop (L2[entryId] absent/empty, no Redis exception) so a drop spike is distinguishable from
    /// healthy expected drops.</summary>
    public Counter<long> ReinjectDropped { get; }

    public KeeperMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        ReinjectDropped = meter.CreateCounter<long>("keeper_reinject_dropped");   // collector appends the suffix
    }
}
