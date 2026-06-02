using System.Diagnostics.Metrics;

namespace Orchestrator.Observability;

/// <summary>
/// METRIC-04 — the Orchestrator's code-owned <see cref="Meter"/> ("Orchestrator") holding two
/// monotonic <see cref="Counter{T}"/> instruments for the per-processor dispatch-bottleneck PromQL.
/// <para>
/// Built via <see cref="IMeterFactory"/> (the .NET 8 blessed DI pattern, auto-registered by the
/// generic host) — NEVER a <c>static Meter</c> field, which would leak across tests in the shared
/// hermetic process. <see cref="MeterName"/> is the single const referenced by BOTH this
/// <c>meterFactory.Create</c> call AND the <c>ConfigureOpenTelemetryMeterProvider(mp =&gt;
/// mp.AddMeter(OrchestratorMetrics.MeterName))</c> registration in <c>Program.cs</c> (D-02 symmetry).
/// </para>
/// <para>
/// The counter names are snake_case with NO <c>_total</c> suffix (D-03): the collector's prometheus
/// exporter <c>add_metric_suffixes</c> default appends <c>_total</c>, so naming them here with
/// <c>_total</c> would yield <c>_total_total</c>. Each counter is tagged <c>ProcessorId</c> at the
/// increment site and inherits the ambient <c>service_instance_id</c> resource label from Plan 01.
/// </para>
/// </summary>
public sealed class OrchestratorMetrics
{
    /// <summary>The meter name — MUST equal the <c>AddMeter("Orchestrator")</c> registration (D-02).</summary>
    public const string MeterName = "Orchestrator";

    /// <summary><c>orchestrator_dispatch_sent</c> — incremented AFTER <c>endpoint.Send</c> in StepDispatcher.</summary>
    public Counter<long> DispatchSent { get; }

    /// <summary><c>orchestrator_result_consumed</c> — incremented at the TOP of ResultConsumer.Consume.</summary>
    public Counter<long> ResultConsumed { get; }

    public OrchestratorMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        DispatchSent = meter.CreateCounter<long>("orchestrator_dispatch_sent");       // D-03 — no _total
        ResultConsumed = meter.CreateCounter<long>("orchestrator_result_consumed");   // D-03 — no _total
    }
}
