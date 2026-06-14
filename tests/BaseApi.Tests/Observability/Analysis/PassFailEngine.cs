namespace BaseApi.Tests.Observability.Analysis;

/// <summary>
/// The pure correctness arbiter (OBS-01/02/03). A plain object — NO ES/Prom/Http/host dependency,
/// NO IO — so every decision branch is provable with synthetic <see cref="RunTrace"/> +
/// <see cref="PromCounterSnapshot"/> inputs (66-VALIDATION.md). The RealStack fixture (Plan 03)
/// feeds it real parsed inputs; the hermetic facts (Plan 01 Task 3) feed synthetic ones and prove
/// every branch fires, so a green RealStack run is trustworthy rather than vacuously green.
///
/// <para>
/// <b>Decision logic</b> (D-03..D-08 + 66-RESEARCH Counter Reality):
/// <list type="number">
/// <item>COMPLETE (OBS-01): a run whose distinct StepLabel set equals the full 9-label set
///   (necessarily both sinks Step_F1 + Step_F2).</item>
/// <item>MISSING (OBS-02): <c>triggerCount − CompleteRuns</c>; &gt; 0 ⇒ Fail. The specific missing
///   correlationId is NOT recoverable from telemetry (caveat, never named).</item>
/// <item>DUPLICATE (OBS-02, fail-closed): ANY duplicate (correlationId, StepLabel) ⇒ Fail — the
///   live dedupe counters are dormant, so no redelivery can be corroborated (D-06 → D-07).</item>
/// <item>RECONCILE (OBS-03, D-08): on the LIVE counter set only — dispatch_sent == triggers AND
///   result_sent_completed ≥ complete × 9 AND no unaccounted delta AND no non-completed terminal
///   outcome. Dormant dedupe deltas feed NO arithmetic (reported Absent).</item>
/// <item>VERDICT: Pass iff Missing == 0 AND no duplicate AND Reconciled.</item>
/// </list>
/// </para>
/// </summary>
public sealed class PassFailEngine
{
    /// <summary>
    /// The 9-label completeness set (verbatim — FanOutSeederE2ETests.cs NodeNumbers keys):
    /// A→B→C→{D1→E1→F1, D2→E2→F2}. A COMPLETE run's distinct set equals this exactly (both sinks).
    /// </summary>
    private static readonly HashSet<string> AllLabels = new(StringComparer.Ordinal)
    { "Step_A", "Step_B", "Step_C", "Step_D1", "Step_E1", "Step_F1", "Step_D2", "Step_E2", "Step_F2" };

    /// <summary>
    /// Tolerance for floating-point delta comparisons. The fixture derives triggerCount as
    /// (int)Math.Round(promSnapshot.DispatchSentDelta), then the engine compares the un-rounded
    /// double against that int. Using 0.5 tolerance instead of exact == prevents a fractional
    /// residue (multi-series summation, float accumulation) from forcing a spurious Unreconciled
    /// outcome. (WR-01 fix — 66-REVIEW.md.)
    /// </summary>
    private const double DeltaTolerance = 0.5;

    /// <summary>Both sinks Step_F1 + Step_F2 required for COMPLETE → 9 COMPLETED effects per run.</summary>
    public const int LabelsPerRun = 9;

    /// <summary>
    /// Score a set of per-correlationId traces against the live Prometheus counter deltas and the
    /// expected trigger count, producing the single per-scenario <see cref="AnalyzerReport"/>.
    /// PURE: no IO — the caller (fixture) writes the report.
    /// </summary>
    public AnalyzerReport Analyze(IReadOnlyList<RunTrace> runs, PromCounterSnapshot prom,
                                  int triggerCount, string scenarioId)
    {
        // 1. COMPLETE (OBS-01): distinct StepLabel set equals the full 9-label set (both sinks).
        var complete = runs.Where(r => r.DistinctLabels.SetEquals(AllLabels)).ToList();

        // 2. MISSING (OBS-02): count from the trigger denominator; identity is NOT recoverable.
        var missing = triggerCount - complete.Count;
        var missingDetail = new List<string>();
        if (missing > 0)
        {
            missingDetail.Add(
                $"{missing} of {triggerCount} triggered run(s) did NOT reach COMPLETE (all 9 labels incl. both sinks Step_F1 + Step_F2).");
            missingDetail.Add(
                "The SPECIFIC missing correlationId is NOT recoverable from telemetry (no per-fire correlationId log — research item #1); the count is reported, the identity is not.");
        }

        // 3. DUPLICATE (OBS-02, fail-closed): any duplicate (correlationId, StepLabel) is a FAIL.
        //    No live dedupe counter can corroborate a redelivery (dormant) → un-corroboratable → fail-closed.
        var duplicates = runs.Where(r => r.HasAnyDuplicateLabel).ToList();
        var dupFail = duplicates.Count > 0;

        // 4. RECONCILE (OBS-03, D-08): LIVE counter set only; dormant dedupe deltas feed no arithmetic.
        //    Epsilon tolerance (DeltaTolerance = 0.5) instead of exact == because the fixture derives
        //    triggerCount as (int)Math.Round(DispatchSentDelta) and the engine re-compares the un-rounded
        //    double. Any fractional residue from multi-series summation would otherwise force Unreconciled.
        var nonCompletedTerminal = prom.NonCompletedOutcomes.Values.Any(v => v != 0);
        var reconciled =
            Math.Abs(prom.DispatchSentDelta - triggerCount) < DeltaTolerance
            && prom.ResultSentCompletedDelta >= complete.Count * LabelsPerRun
            && Math.Abs(UnaccountedDelta(prom)) < DeltaTolerance
            && !nonCompletedTerminal;
        var recon = reconciled ? ReconciliationOutcome.Reconciled : ReconciliationOutcome.Unreconciled;

        // 5. VERDICT: Pass iff zero-missing AND no duplicate AND reconciled.
        var pass = missing == 0 && !dupFail && recon == ReconciliationOutcome.Reconciled;
        var verdict = pass ? Verdict.Pass : Verdict.Fail;

        // 6. Build the report (no IO).
        return new AnalyzerReport
        {
            ScenarioId = scenarioId,
            Verdict = verdict,
            TriggerCount = triggerCount,
            CompleteRuns = complete.Count,
            Missing = missing,
            MissingDetail = missingDetail,
            Duplicates = duplicates,
            Reconciliation = recon,
            Prom = prom,
            Traces = runs,
            HumanSummary = BuildSummary(scenarioId, verdict, triggerCount, complete.Count, missing, dupFail, recon),
        };
    }

    /// <summary>
    /// The dispatched-but-never-completed gap on the LIVE counters: dispatch_sent − result_consumed
    /// that is NOT mirrored by reported redelivery. A non-zero imbalance is itself an UNRECONCILED
    /// FAIL (D-08 fail-closed). Dormant dedupe deltas (null) are NOT subtracted — they feed no arithmetic.
    /// </summary>
    private static double UnaccountedDelta(PromCounterSnapshot prom)
        => prom.DispatchSentDelta - prom.ResultConsumedDelta;

    private static string BuildSummary(string scenarioId, Verdict verdict, int triggerCount,
        int completeRuns, int missing, bool dupFail, ReconciliationOutcome recon)
    {
        var reasons = new List<string>();
        if (missing > 0) reasons.Add($"{missing} missing");
        if (dupFail) reasons.Add("unaccountable duplicate (fail-closed)");
        if (recon == ReconciliationOutcome.Unreconciled) reasons.Add("Prom unreconciled");

        var driver = verdict == Verdict.Pass
            ? "zero-missing, no duplicate, Prom reconciled"
            : string.Join("; ", reasons);

        return $"[{scenarioId}] {verdict}: {completeRuns}/{triggerCount} complete — {driver}.";
    }
}
