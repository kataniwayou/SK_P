using System.Diagnostics.Metrics;

namespace BaseProcessor.Core.Observability;

/// <summary>
/// METRIC-05 — the processor framework's code-owned <see cref="Meter"/> ("BaseProcessor") holding two
/// monotonic <see cref="Counter{T}"/> instruments for the per-processor dispatch-bottleneck PromQL.
/// Owned by <c>BaseProcessor.Core</c> and inherited by EVERY concrete <c>Processor.*</c> (the meter is
/// registered inside <c>AddBaseProcessor</c>).
/// <para>
/// Built via <see cref="IMeterFactory"/> (the .NET 8 blessed DI pattern, auto-registered by the
/// generic host) — NEVER a <c>static Meter</c> field, which would leak across tests in the shared
/// hermetic process. <see cref="MeterName"/> is the single const referenced by BOTH this
/// <c>meterFactory.Create</c> call AND the <c>ConfigureOpenTelemetryMeterProvider(mp =&gt;
/// mp.AddMeter(ProcessorMetrics.MeterName))</c> registration inside <c>AddBaseProcessor</c> (D-02 symmetry).
/// </para>
/// <para>
/// The counter names are snake_case with NO Prometheus counter suffix (D-03): the collector's
/// prometheus exporter <c>add_metric_suffixes</c> default appends the suffix itself, so embedding it
/// in the instrument name here would double it. Each counter is tagged <c>ProcessorId</c> (+ <c>outcome</c>
/// on the result counter) at the increment site and inherits the ambient <c>service_instance_id</c>
/// resource label from Plan 01.
/// </para>
/// </summary>
public sealed class ProcessorMetrics
{
    /// <summary>The meter name — MUST equal the <c>AddMeter("BaseProcessor")</c> registration (D-02).</summary>
    public const string MeterName = "BaseProcessor";

    /// <summary><c>processor_dispatch_consumed</c> — incremented at the TOP of EntryStepDispatchConsumer.Consume.</summary>
    public Counter<long> DispatchConsumed { get; }

    /// <summary><c>processor_result_sent</c> — incremented AFTER <c>endpoint.Send</c> in SendResult.</summary>
    public Counter<long> ResultSent { get; }

    /// <summary>
    /// Phase 32 (D-10): <c>processor_dispatch_deduped</c> — incremented at the existing
    /// <c>flag[H]=="Ack"</c> drop gate in EntryStepDispatchConsumer (Plan 04 wires the increment).
    /// </summary>
    public Counter<long> DispatchDeduped { get; }

    /// <summary>
    /// Phase 32 (D-11): <c>workflow_cancelled</c> — incremented when the breaker trips on
    /// infra-fault retry-budget exhaustion (the trip is processor-side per D-01; Plan 04 wires it).
    /// </summary>
    public Counter<long> WorkflowCancelled { get; }

    public ProcessorMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        DispatchConsumed  = meter.CreateCounter<long>("processor_dispatch_consumed");   // D-03 — collector appends the suffix
        ResultSent        = meter.CreateCounter<long>("processor_result_sent");         // D-03 — collector appends the suffix
        DispatchDeduped   = meter.CreateCounter<long>("processor_dispatch_deduped");    // Phase 32 D-10 — collector appends the suffix
        WorkflowCancelled = meter.CreateCounter<long>("workflow_cancelled");            // Phase 32 D-11 — collector appends the suffix
    }
}
