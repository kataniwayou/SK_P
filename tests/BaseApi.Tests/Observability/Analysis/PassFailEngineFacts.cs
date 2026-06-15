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
/// <item><c>SpawnAware_ResultExceedsDispatchByExactlySpawnExtra_StaysClean</c> — spawn-aware OBS-03: the entry fan-out's extra result reconciles CLEAN.</item>
/// <item><c>SpawnAware_ResultMismatch_RaisesNonFatalWarning</c> — spawn-aware OBS-03: a wrong result/dispatch gap is a non-fatal WARNING.</item>
/// </list>
/// </para>
///
/// <para>
/// Each spawned execution is its own run, so traces are keyed per (correlationId, executionId);
/// <see cref="RunTrace.FromLabels"/> takes a distinct executionId per instance. A duplicate label WITHIN
/// one (correlationId, executionId) still drives the fail-closed duplicate branch.
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
            RunTrace.FromLabels("corr-1", "exec-1", AllNineLabels),
            RunTrace.FromLabels("corr-2", "exec-2", AllNineLabels),
            RunTrace.FromLabels("corr-3", "exec-3", AllNineLabels),
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
        var run = RunTrace.FromLabels("corr-1", "exec-1", eightLabels);
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
        // Duplicate WITHIN one (correlationId, executionId) instance → fail-closed.
        var run = RunTrace.FromLabels("corr-1", "exec-1", labelsWithDuplicate);
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
        var run = RunTrace.FromLabels("corr-1", "exec-1", AllNineLabels);
        var snap = CorroboratingSnapshot(startedRuns: 1) with
        {
            DispatchSentDelta = 3 * PassFailEngine.LabelsPerRun, // implies 3 runs vs ES 1
            ResultConsumedDelta = 3 * PassFailEngine.LabelsPerRun, // keep spawn-aware recon clean (excess isolates the dead-run branch)
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
        // mirror implied 11 vs ES 10). A ±1-run tolerance keeps corroboration clean and the verdict green
        // for a ±1 mismatch in EITHER direction. NOTE (WR-01): the warning that fires OUTSIDE tolerance is
        // one-directional by design — only a POSITIVE excess (implied > started, the dead-run signal) is
        // flagged; an out-of-tolerance NEGATIVE excess (started > implied) is intentionally not a warning
        // (see the asymmetry note in PassFailEngine.cs). Within ±1, both directions stay clean regardless.
        var runs = Enumerable.Range(1, 10)
            .Select(i => RunTrace.FromLabels($"corr-{i}", $"exec-{i}", AllNineLabels))
            .ToArray();
        var snap = CorroboratingSnapshot(startedRuns: 10) with
        {
            DispatchSentDelta = 11 * PassFailEngine.LabelsPerRun, // implies 11 vs ES 10 → excess 1 == tolerance
            ResultConsumedDelta = 11 * PassFailEngine.LabelsPerRun, // keep spawn-aware recon clean (excess isolates the window-edge branch)
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
        var run = RunTrace.FromLabels("corr-1", "exec-1", AllNineLabels);
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
        var run = RunTrace.FromLabels("corr-1", "exec-1", AllNineLabels);
        var snap = CorroboratingSnapshot(startedRuns: 1) with
        {
            ResultDedupedDelta = null,   // absent / dormant
            DispatchDedupedDelta = null, // absent / dormant
        };

        var report = new PassFailEngine().Analyze(new[] { run }, snap, TriggerCountOf(snap), "unit-test");

        // Dormant counters feed no arithmetic and do not gate PASS.
        Assert.Equal(Verdict.Pass, report.Verdict);
    }

    [Fact]
    public void SpawnAware_ResultExceedsDispatchByExactlySpawnExtra_StaysClean()
    {
        // Two cron fires (2 distinct correlationIds), each spawning 2 execution instances → 4 runs. The entry
        // step emits 2 results from 1 dispatch, so ResultConsumedDelta runs ahead of DispatchSentDelta by
        // exactly spawnExtra = the number of entry dispatches = distinct correlationIds = 2. Spawn-aware OBS-03
        // reconciles CLEAN (no warning); the ES-binding verdict (every run complete, no duplicate) PASSES.
        var runs = new[]
        {
            RunTrace.FromLabels("corr-1", "exec-1a", AllNineLabels),
            RunTrace.FromLabels("corr-1", "exec-1b", AllNineLabels),
            RunTrace.FromLabels("corr-2", "exec-2a", AllNineLabels),
            RunTrace.FromLabels("corr-2", "exec-2b", AllNineLabels),
        };
        var spawnExtra = runs.Select(r => r.CorrelationId).Distinct().Count();   // derived from data = 2
        var dispatch = 4 * PassFailEngine.LabelsPerRun;                          // 4 instances' worth of step dispatches
        var snap = CorroboratingSnapshot(startedRuns: 4) with
        {
            DispatchSentDelta = dispatch,
            ResultConsumedDelta = dispatch + spawnExtra,                         // entry fan-out's extra results
        };

        var report = new PassFailEngine().Analyze(runs, snap, TriggerCountOf(snap), "unit-test", spawnExtra);

        Assert.Equal(4, report.StartedRuns);
        Assert.Equal(2, report.SpawnExtra);
        Assert.Equal(dispatch + spawnExtra, report.ExpectedResultConsumed);
        Assert.Equal(ReconciliationOutcome.Reconciled, report.Reconciliation);   // spawn-aware recon clean
        Assert.Empty(report.CorroborationDetail);
        Assert.Equal(Verdict.Pass, report.Verdict);
    }

    [Fact]
    public void SpawnAware_ResultMismatch_RaisesNonFatalWarning()
    {
        // Same 4-instance cohort, but the result counter is OFF by far more than the spawnExtra + slack (a
        // result/dispatch imbalance). Spawn-aware OBS-03 raises a NON-FATAL warning; the ES-binding verdict
        // (every run complete, no duplicate) still PASSES — Prom is corroborating only.
        var runs = new[]
        {
            RunTrace.FromLabels("corr-1", "exec-1a", AllNineLabels),
            RunTrace.FromLabels("corr-1", "exec-1b", AllNineLabels),
            RunTrace.FromLabels("corr-2", "exec-2a", AllNineLabels),
            RunTrace.FromLabels("corr-2", "exec-2b", AllNineLabels),
        };
        var spawnExtra = runs.Select(r => r.CorrelationId).Distinct().Count();   // 2
        var dispatch = 4 * PassFailEngine.LabelsPerRun;
        var snap = CorroboratingSnapshot(startedRuns: 4) with
        {
            DispatchSentDelta = dispatch,
            // Expected = dispatch + 2; supply dispatch + 2 + (3 runs' worth) far beyond the ±1-run slack.
            ResultConsumedDelta = dispatch + spawnExtra + (3 * PassFailEngine.LabelsPerRun),
        };

        var report = new PassFailEngine().Analyze(runs, snap, TriggerCountOf(snap), "unit-test", spawnExtra);

        Assert.Equal(ReconciliationOutcome.Unreconciled, report.Reconciliation); // spawn-aware warning raised
        Assert.NotEmpty(report.CorroborationDetail);
        Assert.Equal(Verdict.Pass, report.Verdict);                              // NON-FATAL — ES-binding still passes
    }
}
