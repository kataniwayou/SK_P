using Xunit;

namespace BaseApi.Tests.Observability.Analysis;

/// <summary>
/// Hermetic unit facts for <see cref="PassFailEngine"/> — re-founded on the ES-binding model (67-03).
/// One fact per decision branch, proving the correctness arbiter is exercised (not vacuously green)
/// before any live stack runs (T-66-01, 66-VALIDATION.md). Every fact builds synthetic
/// <see cref="RunTrace"/> (via <see cref="RunTrace.FromLabels"/>) + a synthetic
/// <see cref="PromCounterSnapshot"/> and calls <c>new PassFailEngine().Analyze(...)</c>. No ES/Prom
/// client, no live stack, sub-second.
///
/// <para>
/// Fact → requirement map (67-03 ES-binding semantics):
/// <list type="bullet">
/// <item><c>Complete_AllStartedRuns_Yields_Pass</c> — ES-binding: every started run 9-label complete → Pass.</item>
/// <item><c>Incomplete_StartedRun_DropsStepF2_Yields_Fail</c> — OBS-02: a started-but-incomplete run → Fail (binding).</item>
/// <item><c>Duplicate_TwoStepC_Yields_FailClosed</c> — OBS-02: any duplicate → Fail (binding fail-closed).</item>
/// <item><c>PromDeadRun_ImpliesMoreRunsThanEs_Yields_NonFatalWarning</c> — 67-03: Prom excess ⇒ WARNING, NOT Fail.</item>
/// <item><c>PromWindowEdge_OneRunMismatch_WithinTolerance_StaysClean</c> — 67-03: ±1-run boundary tolerance, no warning, Pass.</item>
/// <item><c>RetiredConflation_ResultSentShort_DoesNotFailVerdict</c> — 67-03: ResultSentCompleted short no longer fails (retired #2).</item>
/// <item><c>DormantDedupeCounters_Absent_DoNotBlockPass</c> — dormant (null) dedupe deltas do not gate PASS.</item>
/// </list>
/// </para>
/// </summary>
public sealed class PassFailEngineFacts
{
    /// <summary>The full 9-label set (both sinks) — a COMPLETE run.</summary>
    private static readonly string[] AllNineLabels =
        { "Step_A", "Step_B", "Step_C", "Step_D1", "Step_E1", "Step_F1", "Step_D2", "Step_E2", "Step_F2" };

    /// <summary>
    /// A Prom snapshot CORROBORATING <paramref name="startedRuns"/> ES-observed runs: the orchestrator
    /// dispatches once per step, so DispatchSentDelta = startedRuns × 9 (⇒ impliedRuns = startedRuns).
    /// Dormant dedupe absent; no terminal non-completed outcomes. Corroboration is non-binding (67-03)
    /// — this shape simply keeps the corroboration cross-check clean so a fact isolates ONE branch.
    /// </summary>
    private static PromCounterSnapshot CorroboratingSnapshot(int startedRuns) => new()
    {
        DispatchSentDelta = startedRuns * PassFailEngine.LabelsPerRun,
        ResultConsumedDelta = startedRuns * PassFailEngine.LabelsPerRun,
        DispatchConsumedDelta = startedRuns * PassFailEngine.LabelsPerRun,
        ResultSentCompletedDelta = startedRuns * PassFailEngine.LabelsPerRun,
        KeeperReinjectDroppedDelta = 0,
        ResultDedupedDelta = null,   // DORMANT — absent
        DispatchDedupedDelta = null, // DORMANT — absent
    };

    /// <summary>triggerCount mirrors the fixture: round(DispatchSentDelta). Corroboration evidence only (67-03).</summary>
    private static int TriggerCountOf(PromCounterSnapshot s) => (int)System.Math.Round(s.DispatchSentDelta);

    [Fact]
    public void Complete_AllStartedRuns_Yields_Pass()
    {
        // 3 STARTED runs (distinct correlationIds), all 9-label complete → ES-binding Pass.
        var runs = new[]
        {
            RunTrace.FromLabels("corr-1", AllNineLabels),
            RunTrace.FromLabels("corr-2", AllNineLabels),
            RunTrace.FromLabels("corr-3", AllNineLabels),
        };
        var snap = CorroboratingSnapshot(startedRuns: 3);

        var report = new PassFailEngine().Analyze(runs, snap, TriggerCountOf(snap), "unit-test");

        Assert.Equal(3, report.StartedRuns);                                   // ES-binding denominator
        Assert.Equal(3, report.CompleteRuns);                                  // OBS-01
        Assert.Equal(0, report.Missing);
        Assert.Equal(ReconciliationOutcome.Reconciled, report.Reconciliation); // Prom corroboration clean
        Assert.Equal(Verdict.Pass, report.Verdict);
    }

    [Fact]
    public void Incomplete_StartedRun_DropsStepF2_Yields_Fail()
    {
        // The run STARTED (it logged Step_A…) but is missing the Step_F2 sink → 8 labels → incomplete.
        var eightLabels = AllNineLabels.Where(l => l != "Step_F2").ToArray();
        var run = RunTrace.FromLabels("corr-1", eightLabels);
        var snap = CorroboratingSnapshot(startedRuns: 1);

        var report = new PassFailEngine().Analyze(new[] { run }, snap, TriggerCountOf(snap), "unit-test");

        Assert.Equal(1, report.StartedRuns);     // it DID start (≥1 Step_* log)
        Assert.Equal(0, report.CompleteRuns);
        Assert.Equal(1, report.Missing);         // OBS-02: started-but-incomplete, bound to ES denominator
        Assert.Equal(Verdict.Fail, report.Verdict);
        Assert.NotEmpty(report.MissingDetail);
    }

    [Fact]
    public void Duplicate_TwoStepC_Yields_FailClosed()
    {
        // All 9 distinct labels PRESENT, but Step_C appears twice → HasAnyDuplicateLabel true.
        var labelsWithDuplicate = AllNineLabels.Concat(new[] { "Step_C" }).ToArray();
        var run = RunTrace.FromLabels("corr-1", labelsWithDuplicate);
        var snap = CorroboratingSnapshot(startedRuns: 1);

        var report = new PassFailEngine().Analyze(new[] { run }, snap, TriggerCountOf(snap), "unit-test");

        Assert.Equal(Verdict.Fail, report.Verdict);   // BINDING fail-closed even with every distinct label present
        Assert.NotEmpty(report.Duplicates);
    }

    [Fact]
    public void PromDeadRun_ImpliesMoreRunsThanEs_Yields_NonFatalWarning()
    {
        // ES observed 1 started+complete run, but Prom dispatched ~3 runs' worth of steps (27 / 9 = 3):
        // 2 fully-dead runs (dispatched, Step_A never logged). Under the OLD binding model this was a
        // hard Fail; under 67-03 it is a NON-FATAL corroboration warning — the ES-binding verdict (every
        // started run complete, no duplicate) still PASSES.
        var run = RunTrace.FromLabels("corr-1", AllNineLabels);
        var snap = CorroboratingSnapshot(startedRuns: 1) with
        {
            DispatchSentDelta = 3 * PassFailEngine.LabelsPerRun, // implies 3 runs vs ES 1
        };

        var report = new PassFailEngine().Analyze(new[] { run }, snap, TriggerCountOf(snap), "unit-test");

        Assert.Equal(1, report.StartedRuns);
        Assert.Equal(3, report.PromImpliedRuns);                                 // dispatch-implied run count
        Assert.Equal(ReconciliationOutcome.Unreconciled, report.Reconciliation); // corroboration WARNING raised
        Assert.NotEmpty(report.CorroborationDetail);
        Assert.Equal(Verdict.Pass, report.Verdict);                              // NON-FATAL — ES-binding still passes
    }

    [Fact]
    public void PromWindowEdge_OneRunMismatch_WithinTolerance_StaysClean()
    {
        // The documented ~1-run window-edge mismatch (e.g. Prom 81 = 9×9 ⇒ implied 9 vs ES 10, or the
        // mirror: implied 11 vs ES 10). A ±1-run tolerance keeps corroboration clean and the verdict green.
        var runs = Enumerable.Range(1, 10)
            .Select(i => RunTrace.FromLabels($"corr-{i}", AllNineLabels))
            .ToArray();
        var snap = CorroboratingSnapshot(startedRuns: 10) with
        {
            DispatchSentDelta = 11 * PassFailEngine.LabelsPerRun, // implies 11 vs ES 10 → excess 1 == tolerance
        };

        var report = new PassFailEngine().Analyze(runs, snap, TriggerCountOf(snap), "unit-test");

        Assert.Equal(10, report.StartedRuns);
        Assert.Equal(11, report.PromImpliedRuns);
        Assert.Equal(ReconciliationOutcome.Reconciled, report.Reconciliation); // within ±1 — no warning
        Assert.Empty(report.CorroborationDetail);
        Assert.Equal(Verdict.Pass, report.Verdict);
    }

    [Fact]
    public void RetiredConflation_ResultSentShort_DoesNotFailVerdict()
    {
        // Retired conflation #2: under the OLD model ResultSentCompletedDelta < complete × 9 forced an
        // Unreconciled FAIL. Under 67-03 that arithmetic is gone from the binding gate — a complete
        // ES-binding cohort PASSES regardless of the ResultSentCompleted counter value.
        var run = RunTrace.FromLabels("corr-1", AllNineLabels);
        var snap = CorroboratingSnapshot(startedRuns: 1) with
        {
            ResultSentCompletedDelta = 8, // short of 9 — would have failed the OLD binding gate
        };

        var report = new PassFailEngine().Analyze(new[] { run }, snap, TriggerCountOf(snap), "unit-test");

        Assert.Equal(1, report.CompleteRuns);
        Assert.Equal(Verdict.Pass, report.Verdict); // ES-binding pass; the retired counter no longer gates
    }

    [Fact]
    public void DormantDedupeCounters_Absent_DoNotBlockPass()
    {
        var run = RunTrace.FromLabels("corr-1", AllNineLabels);
        var snap = CorroboratingSnapshot(startedRuns: 1) with
        {
            ResultDedupedDelta = null,   // absent / dormant
            DispatchDedupedDelta = null, // absent / dormant
        };

        var report = new PassFailEngine().Analyze(new[] { run }, snap, TriggerCountOf(snap), "unit-test");

        // Dormant counters feed no arithmetic and do not gate PASS.
        Assert.Equal(Verdict.Pass, report.Verdict);
    }
}
