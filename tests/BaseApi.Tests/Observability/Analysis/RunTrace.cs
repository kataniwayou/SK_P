namespace BaseApi.Tests.Observability.Analysis;

/// <summary>
/// Per-execution-instance aggregate built from the per-step COMPLETED-effect ES logs
/// (SampleProcessor — <c>"step completed {StepLabel} sum {Sum}"</c> → ES
/// <c>attributes.StepLabel</c> + <c>attributes.CorrelationId</c> + <c>attributes.ExecutionId</c>). One
/// <see cref="RunTrace"/> represents one execution instance (one <c>(correlationId, executionId)</c> pair —
/// each spawned execution is its own run) and the set of <c>StepLabel</c>s that emitted a COMPLETED log
/// for that instance.
///
/// <para>
/// This is a PURE model — no ES/Prom/host dependency. Both the live RealStack fixture (Plan 03,
/// from parsed ES hits) and the hermetic facts (Plan 01 Task 3, from synthetic label lists) build
/// traces identically through <see cref="FromLabels"/>, so the duplicate/distinct arithmetic is
/// proven once and shared.
/// </para>
///
/// <para>
/// <b>Duplicates are retained in <see cref="Labels"/>.</b> A redelivered/double-effect Step_C
/// appears TWICE in <see cref="Labels"/> but ONCE in <see cref="DistinctLabels"/>; the gap is the
/// fail-closed duplicate signal (<see cref="HasAnyDuplicateLabel"/>). Label comparison is
/// <see cref="StringComparer.Ordinal"/> — the labels are fixed verbatim identifiers (Step_A …
/// Step_F2), never culture-folded.
/// </para>
/// </summary>
public sealed record RunTrace
{
    /// <summary>The per-fire correlationId this trace aggregates.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>The per-instance executionId this trace aggregates (each spawned execution is its own run).</summary>
    public required string ExecutionId { get; init; }

    /// <summary>
    /// Every StepLabel that emitted a COMPLETED log for this correlationId, duplicates RETAINED
    /// (a double-effect Step_C appears twice). The raw evidence the distinct/duplicate facts derive.
    /// </summary>
    public required IReadOnlyList<string> Labels { get; init; }

    /// <summary>The distinct StepLabel set for this correlationId (Ordinal). Drives COMPLETE (OBS-01).</summary>
    public required HashSet<string> DistinctLabels { get; init; }

    /// <summary>
    /// True iff any StepLabel appears more than once for this correlationId
    /// (<c>Labels.Count != DistinctLabels.Count</c>). The fail-closed duplicate signal (OBS-02):
    /// there is no live dedupe counter to corroborate a redelivery, so any duplicate FAILS.
    /// </summary>
    public required bool HasAnyDuplicateLabel { get; init; }

    /// <summary>The specific labels that collided (appeared &gt; once), for report surfacing.</summary>
    public required IReadOnlyList<string> DuplicateLabels { get; init; }

    /// <summary>
    /// Build a <see cref="RunTrace"/> from a (correlationId, executionId) instance + its raw
    /// (duplicate-retaining) label list. Computes the distinct set + duplicate flags so both the live
    /// fixture and the hermetic facts construct traces identically. A duplicate is now WITHIN one instance
    /// — a label appearing &gt;1× for the same (correlationId, executionId).
    /// </summary>
    public static RunTrace FromLabels(string correlationId, string executionId, IReadOnlyList<string> labels)
    {
        var distinct = new HashSet<string>(labels, StringComparer.Ordinal);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var dupes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            if (!seen.Add(label))
            {
                dupes.Add(label);
            }
        }

        return new RunTrace
        {
            CorrelationId = correlationId,
            ExecutionId = executionId,
            Labels = labels,
            DistinctLabels = distinct,
            HasAnyDuplicateLabel = labels.Count != distinct.Count,
            // Sort deterministically (Ordinal) so report diffs and any future snapshot comparisons
            // are stable across runs — HashSet enumeration order is unspecified. (IN-01 fix.)
            DuplicateLabels = dupes.OrderBy(s => s, StringComparer.Ordinal).ToList(),
        };
    }
}
