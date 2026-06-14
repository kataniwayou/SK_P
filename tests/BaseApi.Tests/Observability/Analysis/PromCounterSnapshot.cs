namespace BaseApi.Tests.Observability.Analysis;

/// <summary>
/// The OBS-03 Prometheus counter read-set as WINDOWED DELTAS (after − before, or
/// <c>increase(metric[window])</c>) — NOT raw cumulative. Prometheus counters are
/// process-lifetime cumulative and reset to zero on a process restart (and the proof crashes
/// processes deliberately), so reconciliation is only meaningful on a windowed delta over the
/// test interval (66-RESEARCH.md Pitfall 3 / A3). The fixture (Plan 03) computes the deltas from
/// two scrapes; the engine reconciles them.
///
/// <para>
/// A PURE DTO — no Prom/Http dependency. Synthetic snapshots drive the hermetic facts; real
/// scrapes feed the same shape from the fixture.
/// </para>
///
/// <para>
/// <b>Live vs dormant counters.</b> The five non-nullable deltas are LIVE (have increment sites).
/// The two nullable dedupe deltas are DORMANT — no code increments them today; <c>null</c> means
/// "absent / no series", and they feed NO reconciliation arithmetic (they are reported as
/// <c>Absent</c>, never gate PASS — 66-RESEARCH.md Counter Reality).
/// </para>
/// </summary>
public sealed record PromCounterSnapshot
{
    /// <summary>orchestrator_dispatch_sent_total — windowed delta. The trigger denominator cross-check.</summary>
    public required double DispatchSentDelta { get; init; }

    /// <summary>orchestrator_result_consumed_total — windowed delta.</summary>
    public required double ResultConsumedDelta { get; init; }

    /// <summary>processor_dispatch_consumed_total — windowed delta.</summary>
    public required double DispatchConsumedDelta { get; init; }

    /// <summary>processor_result_sent_total{outcome="completed"} — windowed delta. Expected = COMPLETE runs × 9.</summary>
    public required double ResultSentCompletedDelta { get; init; }

    /// <summary>keeper_reinject_dropped_total — windowed delta.</summary>
    public required double KeeperReinjectDroppedDelta { get; init; }

    /// <summary>
    /// orchestrator_result_deduped_total — windowed delta. NULLABLE: <c>null</c> == absent (DORMANT,
    /// no increment site). Feeds no arithmetic; reported as Absent.
    /// </summary>
    public double? ResultDedupedDelta { get; init; }

    /// <summary>
    /// processor_dispatch_deduped_total — windowed delta. NULLABLE: <c>null</c> == absent (DORMANT,
    /// no increment site). Feeds no arithmetic; reported as Absent.
    /// </summary>
    public double? DispatchDedupedDelta { get; init; }

    /// <summary>
    /// processor_result_sent_total for the NON-completed outcomes (failed/cancelled/processing),
    /// windowed deltas keyed by outcome. Expected empty / all-zero in a clean proof; any non-zero
    /// terminal outcome is report-surfaced evidence feeding fail-closed reconciliation (D-08).
    /// </summary>
    public IReadOnlyDictionary<string, double> NonCompletedOutcomes { get; init; }
        = new Dictionary<string, double>(StringComparer.Ordinal);
}
