using System.Text.Json.Serialization;

namespace BaseApi.Tests.Observability.Analysis;

/// <summary>The single per-scenario correctness verdict.</summary>
public enum Verdict
{
    /// <summary>Zero-missing AND no duplicate AND Prom reconciled.</summary>
    Pass,

    /// <summary>Any missing run, any duplicate, or an unreconciled Prom delta (fail-closed).</summary>
    Fail,
}

/// <summary>The outcome of the OBS-03 Prometheus reconciliation step.</summary>
public enum ReconciliationOutcome
{
    /// <summary>The live counter deltas balance against triggers + completed runs.</summary>
    Reconciled,

    /// <summary>A short / unaccounted delta (or a non-completed terminal outcome) — forces Verdict.Fail.</summary>
    Unreconciled,

    /// <summary>Reserved: no live counter series available to reconcile (not used for the live set today).</summary>
    Absent,
}

/// <summary>
/// JSON-serializable analyzer report (D-09 schema). Produced by <see cref="PassFailEngine.Analyze"/>
/// BEFORE any assert — a plain serializable record carrying the full evidence set so the report is
/// trustworthy on its own (the fixture in Plan 03 writes it to disk; the engine does NO IO).
///
/// <para>
/// Carries the verdict, the COMPLETE/MISSING accounting (with the unrecoverable-missing-identity
/// caveat in <see cref="MissingDetail"/>), the loud duplicate evidence, the reconciliation outcome,
/// the full <see cref="PromCounterSnapshot"/> (including dormant/absent dedupe deltas), every trace,
/// and a human-readable one-liner.
/// </para>
/// </summary>
public sealed record AnalyzerReport
{
    /// <summary>The scenario under analysis (e.g. "TEST-01-happy-path" or "unit-test").</summary>
    public required string ScenarioId { get; init; }

    /// <summary>The single correctness verdict. Serializes as its string name ("Pass"/"Fail").</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required Verdict Verdict { get; init; }

    /// <summary>The expected number of triggered runs (from orchestrator_dispatch_sent_total + cadence).</summary>
    public required int TriggerCount { get; init; }

    /// <summary>The number of runs whose distinct StepLabel set equals the full 9-label set (OBS-01).</summary>
    public required int CompleteRuns { get; init; }

    /// <summary>TriggerCount − CompleteRuns. Missing &gt; 0 forces Verdict.Fail (OBS-02).</summary>
    public required int Missing { get; init; }

    /// <summary>
    /// Human notes on the missing COUNT plus the unrecoverable-identity caveat: the SPECIFIC missing
    /// correlationId is NOT recoverable from existing telemetry (no per-fire correlationId log —
    /// research item #1), so the analyzer reports the count, never names the missing run.
    /// </summary>
    public required IReadOnlyList<string> MissingDetail { get; init; }

    /// <summary>The traces that carried any duplicate (correlationId, StepLabel) — the fail-closed evidence (OBS-02).</summary>
    public required IReadOnlyList<RunTrace> Duplicates { get; init; }

    /// <summary>The Prometheus reconciliation outcome (OBS-03). Serializes as its string name.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required ReconciliationOutcome Reconciliation { get; init; }

    /// <summary>The full counter snapshot under analysis (live deltas + dormant/absent dedupe deltas).</summary>
    public required PromCounterSnapshot Prom { get; init; }

    /// <summary>Every per-correlationId trace fed to the engine.</summary>
    public required IReadOnlyList<RunTrace> Traces { get; init; }

    /// <summary>A human-readable one-line summary of the verdict and its drivers.</summary>
    public required string HumanSummary { get; init; }
}
