using Xunit;

namespace BaseApi.Tests.Observability.Analysis;

/// <summary>
/// Hermetic unit facts for <see cref="PassFailEngine"/> — one fact per decision branch, proving
/// the correctness arbiter is exercised (not vacuously green) before any live stack runs (T-66-01,
/// 66-VALIDATION.md). Every fact builds synthetic <see cref="RunTrace"/> (via
/// <see cref="RunTrace.FromLabels"/>) + a synthetic <see cref="PromCounterSnapshot"/> and calls
/// <c>new PassFailEngine().Analyze(...)</c>. No ES/Prom client, no live stack, sub-second.
///
/// <para>
/// Fact → requirement map (mirrors SampleProcessorFacts.cs):
/// <list type="bullet">
/// <item><c>Complete_AllNineLabels_Yields_Pass</c> — OBS-01: a 9-label trace scores COMPLETE.</item>
/// <item><c>Missing_DropsStepF2_Yields_Fail</c> — OBS-02: a dropped label scores MISSING → Fail.</item>
/// <item><c>Duplicate_TwoStepC_Yields_FailClosed</c> — OBS-02: any duplicate → Fail (fail-closed).</item>
/// <item><c>Reconcile_ResultSentShortByOne_Yields_Unreconciled_Fail</c> — OBS-03: short delta → Unreconciled → Fail.</item>
/// <item><c>Reconcile_Balanced_AllComplete_Yields_Pass</c> — OBS-03: balanced snapshot → Pass.</item>
/// <item><c>DormantDedupeCounters_Absent_DoNotBlockPass</c> — dormant (null) dedupe deltas do not gate PASS.</item>
/// </list>
/// </para>
/// </summary>
public sealed class PassFailEngineFacts
{
    /// <summary>The full 9-label set (both sinks) — a COMPLETE run.</summary>
    private static readonly string[] AllNineLabels =
        { "Step_A", "Step_B", "Step_C", "Step_D1", "Step_E1", "Step_F1", "Step_D2", "Step_E2", "Step_F2" };

    /// <summary>A balanced snapshot for <paramref name="triggerCount"/> all-complete runs (dormant dedupe absent).</summary>
    private static PromCounterSnapshot BalancedSnapshot(int triggerCount, int completeRuns) => new()
    {
        DispatchSentDelta = triggerCount,
        ResultConsumedDelta = triggerCount,
        DispatchConsumedDelta = triggerCount,
        ResultSentCompletedDelta = completeRuns * PassFailEngine.LabelsPerRun,
        KeeperReinjectDroppedDelta = 0,
        ResultDedupedDelta = null,   // DORMANT — absent
        DispatchDedupedDelta = null, // DORMANT — absent
    };

    [Fact]
    public void Complete_AllNineLabels_Yields_Pass()
    {
        var run = RunTrace.FromLabels("corr-1", AllNineLabels);
        var report = new PassFailEngine().Analyze(
            new[] { run }, BalancedSnapshot(triggerCount: 1, completeRuns: 1), triggerCount: 1, "unit-test");

        Assert.Equal(1, report.CompleteRuns);   // OBS-01: distinct 9-label set → COMPLETE
        Assert.Equal(Verdict.Pass, report.Verdict);
    }

    [Fact]
    public void Missing_DropsStepF2_Yields_Fail()
    {
        // 8 labels: the full set MINUS the Step_F2 sink → NOT complete.
        var eightLabels = AllNineLabels.Where(l => l != "Step_F2").ToArray();
        var run = RunTrace.FromLabels("corr-1", eightLabels);

        var report = new PassFailEngine().Analyze(
            new[] { run }, BalancedSnapshot(triggerCount: 1, completeRuns: 0), triggerCount: 1, "unit-test");

        Assert.True(report.Missing >= 1);        // OBS-02: one triggered run never reached COMPLETE
        Assert.Equal(Verdict.Fail, report.Verdict);
        Assert.NotEmpty(report.MissingDetail);   // the count + unrecoverable-identity caveat
    }

    [Fact]
    public void Duplicate_TwoStepC_Yields_FailClosed()
    {
        // All 9 distinct labels PRESENT, but Step_C appears twice → HasAnyDuplicateLabel true.
        var labelsWithDuplicate = AllNineLabels.Concat(new[] { "Step_C" }).ToArray();
        var run = RunTrace.FromLabels("corr-1", labelsWithDuplicate);

        var report = new PassFailEngine().Analyze(
            new[] { run }, BalancedSnapshot(triggerCount: 1, completeRuns: 1), triggerCount: 1, "unit-test");

        Assert.Equal(Verdict.Fail, report.Verdict);   // fail-closed even though every distinct label is present
        Assert.NotEmpty(report.Duplicates);           // proves the fail-closed wiring (item #2)
    }

    [Fact]
    public void Reconcile_ResultSentShortByOne_Yields_Unreconciled_Fail()
    {
        var run = RunTrace.FromLabels("corr-1", AllNineLabels);
        var snapshot = BalancedSnapshot(triggerCount: 1, completeRuns: 1) with
        {
            ResultSentCompletedDelta = 8, // short of the required 9 (1 complete run × 9 labels)
        };

        var report = new PassFailEngine().Analyze(
            new[] { run }, snapshot, triggerCount: 1, "unit-test");

        Assert.Equal(ReconciliationOutcome.Unreconciled, report.Reconciliation);
        Assert.Equal(Verdict.Fail, report.Verdict);
    }

    [Fact]
    public void Reconcile_Balanced_AllComplete_Yields_Pass()
    {
        var runs = new[]
        {
            RunTrace.FromLabels("corr-1", AllNineLabels),
            RunTrace.FromLabels("corr-2", AllNineLabels),
            RunTrace.FromLabels("corr-3", AllNineLabels),
        };

        var report = new PassFailEngine().Analyze(
            runs, BalancedSnapshot(triggerCount: 3, completeRuns: 3), triggerCount: 3, "unit-test");

        Assert.Equal(3, report.CompleteRuns);
        Assert.Equal(ReconciliationOutcome.Reconciled, report.Reconciliation);
        Assert.Equal(Verdict.Pass, report.Verdict);
    }

    [Fact]
    public void DormantDedupeCounters_Absent_DoNotBlockPass()
    {
        var run = RunTrace.FromLabels("corr-1", AllNineLabels);
        var snapshot = BalancedSnapshot(triggerCount: 1, completeRuns: 1) with
        {
            ResultDedupedDelta = null,   // absent / dormant
            DispatchDedupedDelta = null, // absent / dormant
        };

        var report = new PassFailEngine().Analyze(
            new[] { run }, snapshot, triggerCount: 1, "unit-test");

        // Dormant counters feed no arithmetic and do not gate PASS.
        Assert.Equal(Verdict.Pass, report.Verdict);
    }
}
