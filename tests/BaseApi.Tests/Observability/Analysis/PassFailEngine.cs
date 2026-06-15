namespace BaseApi.Tests.Observability.Analysis;

/// <summary>
/// The pure correctness arbiter (OBS-01/02/03), re-founded on the ES-binding model (67-03). A plain
/// object — NO ES/Prom/Http/host dependency, NO IO — so every decision branch is provable with
/// synthetic <see cref="RunTrace"/> + <see cref="PromCounterSnapshot"/> inputs (66-VALIDATION.md).
/// The RealStack fixture (Plan 03) feeds it real parsed inputs; the hermetic facts feed synthetic
/// ones so a green RealStack run is trustworthy rather than vacuously green.
///
/// <para>
/// <b>Binding arbiter = ES (per-run), 67-03.</b> Aligns the engine with the fixture's documented
/// design: "ES-primary completeness is the binding arbiter; Prom reconciliation is corroborating
/// only." The previous engine conflated three Prom counters into the binding pass gate
/// (triggerCount = DispatchSentDelta as the per-run denominator; ResultSentCompletedDelta ≥
/// complete × 9; |DispatchSentDelta − triggerCount| as a gate). The orchestrator emits one dispatch
/// per STEP, so DispatchSentDelta is ~9× the run count — using it as the per-run denominator made a
/// perfectly-complete 10-run window score 71 "missing" (81 = 9×9 vs ES 10). Those three are retired
/// from the binding gate; they survive only as corroboration math.
/// </para>
///
/// <para>
/// <b>Decision logic (ES-binding):</b>
/// <list type="number">
/// <item>STARTED (denominator): distinct (correlationId, executionId) instances with ≥1 Step_* log —
///   i.e. <c>runs.Count</c> (the fixture builds one <see cref="RunTrace"/> per (correlationId, executionId)
///   pair that emitted any Step_* hit; each spawned execution is its own run).</item>
/// <item>COMPLETE (OBS-01): a started run whose distinct StepLabel set equals the full 9-label set
///   (necessarily both sinks Step_F1 + Step_F2).</item>
/// <item>MISSING (OBS-02): <c>StartedRuns − CompleteRuns</c> — started-but-incomplete (1–8 labels);
///   &gt; 0 ⇒ Fail. A FULLY-dead run (dispatched but never logging Step_A) never started in ES, so it
///   is NOT in this count — it surfaces via the Prom corroboration warning instead.</item>
/// <item>DUPLICATE (OBS-02, fail-closed, BINDING): ANY duplicate (correlationId, StepLabel) ⇒ Fail —
///   the live dedupe counters are dormant, so no redelivery can be corroborated.</item>
/// <item>PROM CORROBORATION (OBS-03, NON-BINDING, 67-03): compute impliedRuns =
///   round(DispatchSentDelta / 9) and compare to StartedRuns within a ±1-run boundary tolerance. A
///   positive excess beyond tolerance (impliedRuns − StartedRuns &gt; tolerance) — a dispatched run
///   ES never observed — is a WARNING, NOT a Fail. Any non-completed terminal outcome is also a
///   warning. The documented ~1-run window-edge mismatch (81 = 9×9 vs ES 10) is inside tolerance.</item>
/// <item>VERDICT: Pass iff (every started run complete) AND (no duplicate). Prom corroboration is
///   non-fatal — it never flips a green ES verdict.</item>
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

    /// <summary>Both sinks Step_F1 + Step_F2 required for COMPLETE → 9 distinct labels / dispatches per run.</summary>
    public const int LabelsPerRun = 9;

    /// <summary>
    /// Boundary tolerance (in RUNS) for the Prometheus corroboration cross-check. The orchestrator
    /// emits one dispatch per step, so a window-edge run can be counted by Prom but fall just outside
    /// the ES range (or vice versa) — the documented ~1-run Prom/ES window-edge mismatch (e.g. Prom
    /// 81 = 9×9 ⇒ impliedRuns 9 vs ES StartedRuns 10). A ±1-run tolerance prevents that boundary
    /// artifact from raising a spurious corroboration warning. (67-03 — replaces the old 0.5-count
    /// DeltaTolerance, which operated on the now-retired binding delta arithmetic.)
    /// </summary>
    public const int CorroborationRunTolerance = 1;

    /// <summary>
    /// Score a set of per-correlationId traces against the live Prometheus counter deltas, producing
    /// the single per-scenario <see cref="AnalyzerReport"/>. PURE: no IO — the caller (fixture) writes
    /// the report.
    /// </summary>
    /// <param name="runs">
    /// One <see cref="RunTrace"/> per distinct correlationId that emitted ≥1 Step_* log — the
    /// ES-binding STARTED set (the denominator).
    /// </param>
    /// <param name="prom">The windowed Prometheus counter deltas — corroboration evidence only (67-03).</param>
    /// <param name="triggerCount">
    /// The dispatch-derived count (round(DispatchSentDelta)) — kept as Prom corroboration evidence,
    /// NOT the binding denominator. The orchestrator dispatches per step, so this is ~9× the run count.
    /// </param>
    /// <param name="scenarioId">The scenario id for the report + path.</param>
    /// <param name="spawnExtra">
    /// SPAWN-AWARE OBS-03 corroboration (non-binding): the number of EXTRA results the entry fan-out emits
    /// beyond the dispatch count. The entry step now spawns 2 results from 1 dispatch, so
    /// <c>ResultConsumedDelta ≈ DispatchSentDelta + spawnExtra</c> where <c>spawnExtra</c> = the number of
    /// entry dispatches = cron fires = distinct correlationIds. Derived from data by the caller (the fixture
    /// passes <c>traces.Select(t =&gt; t.CorrelationId).Distinct().Count()</c>) — NEVER hard-coded. Default 0
    /// keeps every pre-spawn caller's behaviour identical.
    /// </param>
    public AnalyzerReport Analyze(IReadOnlyList<RunTrace> runs, PromCounterSnapshot prom,
                                  int triggerCount, string scenarioId, int spawnExtra = 0)
    {
        // ── ES-BINDING ARBITER (67-03) ────────────────────────────────────────────────────────────

        // STARTED (denominator): distinct (correlationId, executionId) instances with ≥1 Step_* log = one
        // RunTrace each (each spawned execution is its own run).
        var startedRuns = runs.Count;

        // COMPLETE (OBS-01): distinct StepLabel set equals the full 9-label set (both sinks).
        var complete = runs.Where(r => r.DistinctLabels.SetEquals(AllLabels)).ToList();

        // MISSING (OBS-02): started-but-incomplete. Bound against the ES STARTED denominator — NOT the
        // Prom dispatch count. A fully-dead run (never started in ES) is invisible here and surfaces
        // only in the Prom corroboration warning below.
        var missing = startedRuns - complete.Count;
        var missingDetail = new List<string>();
        if (missing > 0)
        {
            missingDetail.Add(
                $"{missing} of {startedRuns} STARTED run(s) (distinct correlationId with ≥1 Step_* log) did NOT reach " +
                "COMPLETE (all 9 labels incl. both sinks Step_F1 + Step_F2).");
            missingDetail.Add(
                "A fully-dead run (dispatched but never logging Step_A) never started in ES and is NOT in this count; " +
                "it surfaces as a Prom corroboration WARNING (impliedRuns > startedRuns). The specific missing " +
                "correlationId for such a run is NOT recoverable from telemetry (research item #1).");
        }

        // DUPLICATE (OBS-02, fail-closed, BINDING): any duplicate (correlationId, StepLabel) is a FAIL.
        // No live dedupe counter can corroborate a redelivery (dormant) → un-corroboratable → fail-closed.
        var duplicates = runs.Where(r => r.HasAnyDuplicateLabel).ToList();
        var dupFail = duplicates.Count > 0;

        // ── PROM CORROBORATION (OBS-03, NON-BINDING, 67-03) ────────────────────────────────────────
        // The three retired conflations (#1 DispatchSentDelta as per-run denom; #2 ResultSentCompleted
        // ≥ complete × 9 as binding; #3 |DispatchSentDelta − triggerCount| as binding) survive ONLY as
        // corroboration math here — they never gate the verdict.
        //
        //   impliedRuns = round(DispatchSentDelta / 9): the run count IMPLIED by the per-step dispatch
        //   counter. A positive excess over StartedRuns beyond tolerance means Prom saw more runs
        //   dispatched than ES observed start — i.e. a fully-dead run. WARNING, not a fail.
        // Math.Round defaults to MidpointRounding.ToEven (banker's rounding). A half-step delta is
        // already pathological (DispatchSentDelta is an integer counter delta, so /9 lands on a half only
        // for non-multiples), and the ±1-run CorroborationRunTolerance absorbs any single-run rounding
        // wobble — corroboration never gates the verdict — so ToEven is intentionally accepted here (IN-01).
        var promImpliedRuns = (int)Math.Round(prom.DispatchSentDelta / LabelsPerRun);
        var corroborationDetail = new List<string>();

        // ASYMMETRY (intentional, 67-03 / WR-01): only the POSITIVE direction (Prom implies MORE
        // dispatched runs than ES observed STARTED) raises a warning, because that is precisely the
        // dead-run signal this harness is built to detect (a run dispatched but never logging Step_A).
        // The negative direction (ES STARTED > Prom implied — a dispatch under-count, a scrape gap, or
        // ES double-counting correlationIds) is deliberately NOT a warning here: it cannot indicate a
        // dead run, and Prom is corroboration-only (it never gates the verdict), so flagging it would
        // add noise without protecting the binding outcome. If a future need arises to surface telemetry
        // under-counting, guard `Math.Abs(deadRunExcess)` with a distinct, clearly-labelled message — do
        // NOT fold it into this dead-run warning.
        var deadRunExcess = promImpliedRuns - startedRuns;
        if (deadRunExcess > CorroborationRunTolerance)
        {
            corroborationDetail.Add(
                $"Prom corroboration WARNING: DispatchSentDelta={prom.DispatchSentDelta} ⇒ ~{promImpliedRuns} dispatched run(s), " +
                $"but ES observed only {startedRuns} STARTED run(s) (excess {deadRunExcess} > ±{CorroborationRunTolerance} tolerance). " +
                "This is how a fully-dead run (dispatched, Step_A never logged) surfaces. NON-FATAL (Prom is corroborating only, 67-03).");
        }

        // Terminal non-completed processor outcomes (failed/cancelled/processing) are corroboration
        // evidence (D-08) — surfaced as a WARNING, no longer fail-closed binding.
        var nonCompletedOutcomes = prom.NonCompletedOutcomes.Where(kv => kv.Value != 0).ToList();
        if (nonCompletedOutcomes.Count > 0)
        {
            var detail = string.Join(", ", nonCompletedOutcomes.Select(kv => $"{kv.Key}={kv.Value}"));
            corroborationDetail.Add(
                $"Prom corroboration WARNING: non-completed terminal outcome(s) observed ({detail}). " +
                "NON-FATAL (Prom is corroborating only, 67-03).");
        }

        // SPAWN-AWARE OBS-03 (NON-BINDING): the entry step now emits 2 results from 1 dispatch, so the
        // result counter runs AHEAD of the dispatch counter by exactly one extra result per entry dispatch.
        // Reconcile ResultConsumedDelta against DispatchSentDelta + spawnExtra (spawnExtra = entry-dispatch
        // count = distinct correlationIds, derived from data by the caller — never hard-coded). The excess is
        // measured in RESULT units; allow the same ±1-run boundary slack (CorroborationRunTolerance × 9
        // result-emitting steps) so a window-edge run does not raise a spurious warning. A mismatch beyond
        // that slack is a WARNING only — it never flips a green ES verdict.
        var expectedResultConsumed = prom.DispatchSentDelta + spawnExtra;
        var spawnReconExcess = prom.ResultConsumedDelta - expectedResultConsumed;
        var spawnReconSlack = CorroborationRunTolerance * LabelsPerRun;
        if (Math.Abs(spawnReconExcess) > spawnReconSlack)
        {
            corroborationDetail.Add(
                $"Prom corroboration WARNING (spawn-aware OBS-03): ResultConsumedDelta={prom.ResultConsumedDelta} " +
                $"vs expected DispatchSentDelta({prom.DispatchSentDelta}) + spawnExtra({spawnExtra}) = {expectedResultConsumed} " +
                $"(off by {spawnReconExcess}, beyond ±{spawnReconSlack} result-step slack). The entry fan-out emits one " +
                "extra result per entry dispatch; an unexpected gap means a result/dispatch imbalance. " +
                "NON-FATAL (Prom is corroborating only, 67-03).");
        }

        var recon = corroborationDetail.Count == 0
            ? ReconciliationOutcome.Reconciled
            : ReconciliationOutcome.Unreconciled;

        // ── VERDICT (ES-binding; Prom corroboration is non-fatal) ──────────────────────────────────
        var pass = missing == 0 && !dupFail;
        var verdict = pass ? Verdict.Pass : Verdict.Fail;

        // Build the report (no IO).
        return new AnalyzerReport
        {
            ScenarioId = scenarioId,
            Verdict = verdict,
            StartedRuns = startedRuns,
            TriggerCount = triggerCount,
            CompleteRuns = complete.Count,
            Missing = missing,
            MissingDetail = missingDetail,
            Duplicates = duplicates,
            PromImpliedRuns = promImpliedRuns,
            SpawnExtra = spawnExtra,
            ExpectedResultConsumed = expectedResultConsumed,
            Reconciliation = recon,
            CorroborationDetail = corroborationDetail,
            Prom = prom,
            Traces = runs,
            HumanSummary = BuildSummary(
                scenarioId, verdict, startedRuns, complete.Count, missing, dupFail, recon, corroborationDetail),
        };
    }

    private static string BuildSummary(string scenarioId, Verdict verdict, int startedRuns,
        int completeRuns, int missing, bool dupFail, ReconciliationOutcome recon,
        IReadOnlyList<string> corroborationDetail)
    {
        var reasons = new List<string>();
        if (missing > 0) reasons.Add($"{missing} started-but-incomplete");
        if (dupFail) reasons.Add("unaccountable duplicate (fail-closed)");

        var driver = verdict == Verdict.Pass
            ? "every started run complete, no duplicate (ES-binding)"
            : string.Join("; ", reasons);

        // Prom corroboration is reported alongside the (ES-binding) verdict, never as its cause.
        var corroboration = recon == ReconciliationOutcome.Reconciled
            ? "Prom corroboration clean"
            : $"Prom corroboration WARNING [{corroborationDetail.Count}] (non-fatal)";

        return $"[{scenarioId}] {verdict}: {completeRuns}/{startedRuns} started runs complete — {driver}; {corroboration}.";
    }
}
