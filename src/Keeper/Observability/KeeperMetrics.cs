using System.Diagnostics.Metrics;

namespace Keeper.Observability;

/// <summary>
/// KMET-01 + KMET-03 — the Keeper console's code-owned <see cref="Meter"/> ("Keeper") holding all eight
/// Phase-39 instruments: six monotonic <see cref="Counter{T}"/>, one <see cref="UpDownCounter{T}"/>
/// (<c>keeper_in_flight</c> — the in-flight recovery gauge), and one <see cref="Histogram{T}"/>
/// (<c>keeper_recovery_duration</c> — intake→terminal latency, the first histogram in the repo).
/// <para>
/// Built via <see cref="IMeterFactory"/> (the .NET 8 blessed DI pattern, auto-registered by the generic
/// host) — NEVER a <c>static Meter</c> field, which would leak across tests in the shared hermetic process
/// (D-01 isolation invariant). <see cref="MeterName"/> is the single const referenced by BOTH this
/// <c>meterFactory.Create</c> call AND the <c>ConfigureOpenTelemetryMeterProvider(mp =&gt;
/// mp.AddMeter(KeeperMetrics.MeterName))</c> registration in <c>Program.cs</c> (the house const-to-AddMeter
/// symmetry, mirroring <c>OrchestratorMetrics</c>).
/// </para>
/// <para>
/// Instrument names are snake_case with NO Prometheus suffix: the collector's prometheus exporter
/// <c>add_metric_suffixes</c> default appends <c>_total</c> (counters) / <c>_seconds</c> + <c>_count</c>
/// (histogram) itself, so embedding them here would double the suffix. Each instrument is tagged with the
/// closed-enum labels in <see cref="KeeperMetricTags"/> at the increment site — bounded cardinality only
/// (NO <c>workflowId</c>/<c>correlationId</c> label — KMET-02 / T-39-01 — to prevent unbounded Prometheus
/// series growth).
/// </para>
/// <para>
/// The histogram carries second-scale custom bucket boundaries <c>{1,5,10,30,60,120}</c> via
/// <see cref="InstrumentAdvice{T}"/> (Route A — the Wave-0 gate confirmed transitive
/// <c>System.Diagnostics.DiagnosticSource</c> resolves to 10.0.0, so the advice route compiles on net8.0
/// and keeps the bucket config co-located with the instrument rather than split into an OTel <c>AddView</c>).
/// </para>
/// </summary>
public sealed class KeeperMetrics
{
    /// <summary>The meter name — MUST equal the <c>AddMeter("Keeper")</c> registration (const-to-AddMeter symmetry).</summary>
    public const string MeterName = "Keeper";

    /// <summary><c>keeper_fault_consumed</c> — a fault envelope entered a Keeper consumer (tagged <c>fault_type</c>).</summary>
    public Counter<long> FaultConsumed { get; }

    /// <summary><c>keeper_recovered</c> — a faulted message was re-injected after a clean L2 probe.</summary>
    public Counter<long> Recovered { get; }

    /// <summary><c>keeper_dlq_pushed</c> — an unrecoverable envelope was parked to <c>keeper-dlq</c>.</summary>
    public Counter<long> DlqPushed { get; }

    /// <summary><c>keeper_workflow_paused</c> — a PauseWorkflow signal was published at intake.</summary>
    public Counter<long> WorkflowPaused { get; }

    /// <summary><c>keeper_workflow_resumed</c> — a ResumeWorkflow signal was published on recovery.</summary>
    public Counter<long> WorkflowResumed { get; }

    /// <summary><c>keeper_l2_probe_failed</c> — a single L2 health-probe attempt failed (per attempt).</summary>
    public Counter<long> L2ProbeFailed { get; }

    /// <summary><c>keeper_in_flight</c> — in-flight recoveries currently inside the probe loop (up on intake, down on terminal).</summary>
    public UpDownCounter<long> InFlight { get; }

    /// <summary><c>keeper_recovery_duration</c> — intake→terminal latency (seconds), second-scale custom buckets.</summary>
    public Histogram<double> RecoveryDuration { get; }

    public KeeperMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        FaultConsumed   = meter.CreateCounter<long>("keeper_fault_consumed");        // collector appends _total
        Recovered       = meter.CreateCounter<long>("keeper_recovered");             // collector appends _total
        DlqPushed       = meter.CreateCounter<long>("keeper_dlq_pushed");            // collector appends _total
        WorkflowPaused  = meter.CreateCounter<long>("keeper_workflow_paused");       // collector appends _total
        WorkflowResumed = meter.CreateCounter<long>("keeper_workflow_resumed");      // collector appends _total
        L2ProbeFailed   = meter.CreateCounter<long>("keeper_l2_probe_failed");       // collector appends _total

        InFlight        = meter.CreateUpDownCounter<long>("keeper_in_flight");

        // Route A (DiagnosticSource 10.0.0, Wave-0 locked): the {1,5,10,30,60,120}-second custom buckets
        // ride on the instrument's advice — co-located here, no OTel AddView needed in Program.cs.
        RecoveryDuration = meter.CreateHistogram<double>(
            "keeper_recovery_duration",
            unit: "s",
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = new double[] { 1, 5, 10, 30, 60, 120 },
            });
    }
}

/// <summary>
/// Interned, closed-enum Prometheus tag keys + label values for the Keeper meter — copied from the
/// <c>OutcomeLabel</c> interned-switch shape in <c>EntryStepDispatchConsumer</c> so Plan 02's two fault
/// consumers stay DRY and never allocate per-emit. The literals are decoupled from any C# enum member
/// name (a rename can no longer silently change an emitted label) and are the SOLE bounded-cardinality
/// surface (KMET-02 / T-39-01): there is deliberately NO <c>workflowId</c>/<c>correlationId</c> key here.
/// </summary>
public static class KeeperMetricTags
{
    // ---- Tag keys ----
    /// <summary>Closed enum: <see cref="FaultTypeDispatch"/> / <see cref="FaultTypeResult"/>.</summary>
    public const string FaultType = "fault_type";

    /// <summary>Closed enum: <see cref="OutcomeRecovered"/> / <see cref="OutcomeGaveUp"/>.</summary>
    public const string Outcome = "outcome";

    /// <summary>Closed enum: <see cref="ReasonProbeExhausted"/> / <see cref="ReasonRecoverCap"/>.</summary>
    public const string Reason = "reason";

    /// <summary>Bounded id label — value is <c>inner.ProcessorId.ToString("D")</c> at the call site
    /// (the E2E asserts <c>ProcessorId="{guid:D}"</c>).</summary>
    public const string ProcessorId = "ProcessorId";

    // ---- fault_type values ----
    /// <summary><c>Fault&lt;EntryStepDispatch&gt;</c> intake.</summary>
    public const string FaultTypeDispatch = "dispatch";

    /// <summary><c>Fault&lt;ExecutionResult&gt;</c> intake.</summary>
    public const string FaultTypeResult = "result";

    // ---- outcome values ----
    /// <summary>Re-injected after a clean L2 probe.</summary>
    public const string OutcomeRecovered = "recovered";

    /// <summary>Parked to <c>keeper-dlq</c> after probe exhaustion.</summary>
    public const string OutcomeGaveUp = "gave_up";

    // ---- reason values ----
    /// <summary>The bounded probe loop exhausted its attempts without a clean read.</summary>
    public const string ReasonProbeExhausted = "probe_exhausted";

    /// <summary>The OUTER recover→reinject cap was hit for this H — a persistent fault parked instead of looping (KHARD-01).</summary>
    public const string ReasonRecoverCap = "recover_cap";

    /// <summary>
    /// DRY tag-builder for the fault-intake counters: <c>fault_type</c> + bounded <c>ProcessorId</c>
    /// (the <c>"D"</c>-formatted guid). Keeps Plan 02's two consumers from re-spelling the tag array.
    /// </summary>
    public static KeyValuePair<string, object?>[] FaultTags(string faultType, string procId) =>
        new KeyValuePair<string, object?>[]
        {
            new(FaultType, faultType),
            new(ProcessorId, procId),
        };
}
