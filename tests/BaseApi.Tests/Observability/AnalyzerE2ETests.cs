using System.Text.Json;
using System.Text.RegularExpressions;
using BaseApi.Tests.Observability.Analysis;
using BaseApi.Tests.Observability.Helpers;
using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// OBS-04 — the self-verifying RealStack analyzer fixture (Phase 66 Plan 03, Wave 2). The thin
/// integration shell that gathers REAL telemetry, feeds the pure <see cref="PassFailEngine"/>
/// (Plan 01), writes the JSON report, and asserts the verdict. It exercises OBS-01 (per-run traces
/// from <c>Step_*</c> ES hits), OBS-02 (zero-missing + fail-closed duplicate, via the engine), and
/// OBS-03 (live Prometheus counters reconciled as WINDOWED DELTAS) end-to-end against the live stack.
///
/// <remarks>
/// <para>
/// <b>Harness contract.</b> This is the OBS-04 fixture the Phase 67/68 fault-injection harness invokes
/// via <c>dotnet test --filter "Category=RealStack&amp;FullyQualifiedName~Analyzer"</c>, reading the
/// process EXIT CODE (a FAIL verdict ⇒ failed assert ⇒ non-zero exit) PLUS the
/// <c>analyzer-reports/{scenarioId}.json</c> artifact. The harness parameterizes the scenario id +
/// window timing; for THIS phase the single fact proves the analyzer pipeline produces a green verdict
/// against a clean live window (TEST-01-shaped happy path).
/// </para>
/// <para>
/// <b>RealStack, NOT hermetic.</b> Tagged <c>Category=RealStack</c> so the hermetic filter
/// (<c>Category!=RealStack</c>) excludes it (D-01, Pitfall 6). <c>[Collection("Observability")]</c>
/// serializes against the shared ES/Prom backends. PRECONDITION: the full compose stack must be up
/// healthy (collector → Prometheus scraping; orchestrator + processor-sample producing increments;
/// the fan-out workflow seeded + firing per Phase 65 bring-up). Fails LOUD (never silently passes) if
/// a backend is unreachable — <see cref="PrometheusTestClient.QueryPrometheus"/> Assert.Fails on
/// non-success.
/// </para>
/// <para>
/// <b>Prom windowing (A3 / Pitfall 3).</b> Task 1 confirmed <c>scripts/phase-65-reset.ps1</c> does
/// FLUSHALL + heal with NO container restart, so the counters are process-lifetime CUMULATIVE. This
/// fixture therefore reads each counter as a WINDOWED DELTA (snapshot-before − snapshot-after), never
/// raw cumulative. (Phase 67 open question: a crashed+restarted tier mid-window resets its counters,
/// breaking delta continuity — which is WHY ES-primary completeness, counter-independent, is the binding
/// arbiter and Prom reconciliation is corroborating only. The harness/Phase 67 resolves that; here the
/// happy-path window has no restart.)
/// </para>
/// <para>
/// <b>Read-only.</b> The analyzer writes NO Redis state (the seeder owns those writes), so this factory
/// OMITS the <c>L2KeysToCleanup</c> / parent-index / composite-key net-zero sweep that
/// <see cref="Orchestrator.MetricsRoundTripE2ETests"/> needs. Its only write is the JSON report file.
/// </para>
/// </remarks>
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]
[Collection("Observability")]
public sealed class AnalyzerE2ETests
{
    // D-05 drain: bounded settle after the observation window closes so in-flight runs finish their
    // 9-step traversal before scoring (Pitfall 4 — scoring mid-traversal would mis-flag a run MISSING).
    private const int DrainMs = 60_000;                // > worst-case 9-step traversal
    private const int PollToStableBudgetMs = 60_000;   // poll-to-stable budget, separate from DrainMs
                                                       //   (WR-03 fix — 66-REVIEW.md). Total worst-case
                                                       //   wall-clock = DrainMs + PollToStableBudgetMs = 120 s.
    private const int PollToStableMs = 5_000;          // re-poll interval; snapshot when the ES hit count is
                                                       //   unchanged across two consecutive polls

    // The default scenario id for the phase fixture. The Phase 67/68 harness passes its own per-run id.
    private const string DefaultScenarioId = "TEST-01";

    // V5 / T-66-07 — scenario-id whitelist. Validated BEFORE composing any filesystem path so a
    // caller-supplied id can never traverse out of the fixed reports dir (no '/', '\', '.', etc.).
    private static readonly Regex ScenarioIdPattern = new(@"^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    // D-16 env-var seam — try to parse a harness-supplied round-trip ("o"-format) UTC timestamp,
    // reporting SUCCESS/FAILURE. The fixture uses the result to decide whether the D-16 window seam is
    // genuinely PRESENT (both WINDOW_*_UTC parsed) and so whether to time-pin the Prom counter reads
    // (67-03 / OBS-04 denominator fix) vs. fall back to the standalone live before/after snapshots.
    // AssumeUniversal|AdjustToUniversal normalizes the PowerShell-emitted ISO-8601 offset form to UTC;
    // a null/empty/malformed value yields false ⇒ the standalone live-snapshot path (T-67-04: a bad
    // WINDOW_*_UTC never crashes, it reverts to the self-window default).
    private static bool TryParseUtc(string? value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out parsed);

    [Fact]
    public async Task Analyze_HappyPath_Window_Yields_Pass()
    {
        var ct = TestContext.Current.CancellationToken;
        var scenarioId = Environment.GetEnvironmentVariable("SCENARIO_ID") ?? DefaultScenarioId;

        // ── 1. SCENARIO ID + PATH-TRAVERSAL GUARD (Security V5 / T-66-07) ────────────────────────────
        //    Validate against the ^[A-Za-z0-9_-]+$ whitelist BEFORE composing any path. Compose the
        //    report path ONLY under a fixed reports dir — no caller-controlled directory component.
        Assert.True(
            ScenarioIdPattern.IsMatch(scenarioId),
            $"scenarioId '{scenarioId}' must match ^[A-Za-z0-9_-]+$ (path-traversal guard).");

        var reportsDir = Path.Combine(AppContext.BaseDirectory, "analyzer-reports");
        Directory.CreateDirectory(reportsDir);
        var reportPath = Path.Combine(reportsDir, $"{scenarioId}.json");

        using var es = new ElasticsearchTestClient();
        using var prom = new PrometheusTestClient();

        // ── 2. WINDOW SEAM DETECTION + PROM BEFORE-SNAPSHOT (windowing, A3 / Pitfall 3) ───────────────
        //    The D-16 window seam is PRESENT only when BOTH WINDOW_*_UTC env vars parse (harness mode).
        //    In that mode we TIME-PIN the Prom reads (step 5) to the recorded window bounds — the fixture
        //    runs at window CLOSE, so a live "now" before-snapshot would already include all ~10 in-window
        //    fires, collapsing DispatchSentDelta to a ~60 s tail and under-counting the trigger denominator
        //    (67-03 / OBS-04). Pinning gives delta = counter@windowEnd − counter@windowStart, matching the
        //    ES [windowStart, windowEnd] cohort.
        //
        //    When the seam is ABSENT (standalone Phase 66 run — no env vars), windowPinned is false and the
        //    path below is byte-for-byte the original behavior: ES window defaults to UtcNow, and the Prom
        //    BEFORE snapshot is a live "now" read taken HERE (pre-drain), the AFTER a live read post-poll.
        var windowPinned =
            TryParseUtc(Environment.GetEnvironmentVariable("WINDOW_START_UTC"), out var pinnedWindowStart)
            & TryParseUtc(Environment.GetEnvironmentVariable("WINDOW_END_UTC"), out var pinnedWindowEnd);

        var windowStartUtc = windowPinned ? pinnedWindowStart : DateTimeOffset.UtcNow;

        // Standalone (seam absent): read the live BEFORE counter set NOW (window start). In window-pinned
        // mode this live read is SKIPPED — both counter sets are time-pinned reads taken in step 5.
        var before = windowPinned ? null : await ReadCounterSetAsync(prom, ct);

        // ── 3. DRAIN (D-05 / Pitfall 4) + POLL-TO-STABLE ─────────────────────────────────────────────
        //    After the observation window closes (harness-controlled wall-clock; here a bounded delay),
        //    poll SearchAllHits until the hit count is unchanged across two consecutive polls so no
        //    in-flight run is scored MISSING. (Kept for ES COMPLETENESS in BOTH modes — correct as-is.)
        await Task.Delay(DrainMs, ct);

        // snapshotUtc is the ES range upper bound. In window-pinned mode it is the recorded windowEnd
        // (so the ES range exactly matches the time-pinned Prom delta cohort). In standalone mode it is
        // captured HERE — before poll-to-stable — so the ES window is bounded by a stable timestamp that
        // does not shift during polling. NOTE (standalone IN-04): the live Prom AFTER read (step 5) is
        // taken after poll-to-stable completes, so a run dispatched between snapshotUtc and the AFTER read
        // would be counted in DispatchSentDelta yet excluded from the ES range → scored MISSING. In the
        // happy-path window this tail gap is negligible. (Window-pinned mode has no such gap — both Prom
        // reads are pinned to the recorded bounds.)
        var snapshotUtc = windowPinned ? pinnedWindowEnd : DateTimeOffset.UtcNow;
        var stepHits = await PollHitsToStableAsync(es, windowStartUtc, snapshotUtc, ct);

        // ── 4. ES READ (OBS-01) — group Step_* hits into per-run RunTraces by attributes.CorrelationId ─
        var traces = BuildRunTraces(stepHits);

        // ── 5. PROM SNAPSHOTS + WINDOWED DELTAS (OBS-03) ─────────────────────────────────────────────
        //    Window-pinned (harness): instant-query BOTH counter sets AT the recorded window bounds.
        //    Standalone (seam absent): keep the live BEFORE (step 2) and take a live AFTER read NOW.
        var (beforeSet, afterSet) = windowPinned
            ? (await ReadCounterSetAsync(prom, ct, pinnedWindowStart),
               await ReadCounterSetAsync(prom, ct, pinnedWindowEnd))
            : (before!, await ReadCounterSetAsync(prom, ct));
        var promSnapshot = BuildSnapshot(beforeSet, afterSet);

        // ── 6. PROM CORROBORATION INPUT (67-03 — NO LONGER the per-run denominator) ──────────────────
        //    Derive triggerCount from the orchestrator_dispatch_sent_total WINDOWED DELTA (rounded).
        //    67-03: this is CORROBORATION evidence only — the orchestrator dispatches once per STEP, so
        //    DispatchSentDelta is ~9× the run count and must NOT be used as the per-run denominator (the
        //    old conflation scored a perfect 10-run window as 71 "missing": 81 = 9×9 vs ES 10). The
        //    BINDING denominator is the ES started-run count (distinct correlationIds with ≥1 Step_*
        //    log) computed inside the engine from `traces`. The engine derives impliedRuns =
        //    round(DispatchSentDelta / 9) for the corroboration cross-check. There is NO per-fire
        //    correlationId orchestrator log (item #1), so the IDENTITY of a fully-dead run is NOT
        //    recoverable — it surfaces as a non-fatal Prom corroboration warning, never named.
        var triggerCount = (int)Math.Round(promSnapshot.DispatchSentDelta);

        // Precondition: at least one dispatch must have fired in the window. A zero DispatchSentDelta
        // AND zero ES traces would let the ES-binding verdict pass vacuously (0 started, 0 missing) even
        // though the fan-out workflow precondition is broken. Fail LOUD here instead. (WR-04 fix —
        // re-anchored on dispatch presence as the firing precondition; the ES started count remains the
        // verdict denominator inside the engine.)
        Assert.True(triggerCount > 0,
            $"No dispatches observed in the window (DispatchSentDelta={promSnapshot.DispatchSentDelta}); " +
            "the fan-out workflow precondition is not satisfied.");

        // ── 7. RUN THE ENGINE (pure — no IO) ─────────────────────────────────────────────────────────
        var report = new PassFailEngine().Analyze(traces, promSnapshot, triggerCount, scenarioId);

        // ── 8. WRITE-THEN-ASSERT (D-02 / OBS-04 / T-66-11) ───────────────────────────────────────────
        //    Serialize + write the JSON report FIRST so the artifact exists even on a red run, and the
        //    persisted report + the exit code always reflect the SAME report object.
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(reportPath, json, ct);                 // FIRST — exists on red
        await File.WriteAllTextAsync(
            Path.Combine(reportsDir, $"{scenarioId}.txt"), report.HumanSummary, ct);

        Assert.True(report.Verdict == Verdict.Pass, report.HumanSummary);   // THEN — FAIL ⇒ non-zero exit
    }

    // ── ES → RunTrace grouping (OBS-01) ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Build the window-bounded <c>Step_*</c>-family <c>_search</c> body: a STATIC raw-string template
    /// (T-66-08) with only the Wave-0-verified field paths (from <see cref="EsIndexNames"/> consts — NEVER
    /// a <c>.keyword</c> sub-field) and the validated window timestamps interpolated. Size-bounded to 2000
    /// (~10 runs × 9 steps ≪ 2000), sorted ascending on the window timestamp field.
    /// </summary>
    private static string BuildStepSearchBody(DateTimeOffset windowStart, DateTimeOffset snapshot) => $$"""
      {
        "size": 2000,
        "query": {
          "bool": {
            "filter": [
              { "exists": { "field": "{{EsIndexNames.StepLabelFieldPath}}" } },
              { "range": { "{{EsIndexNames.WindowTimestampFieldPath}}": {
                  "gte": "{{windowStart:o}}", "lte": "{{snapshot:o}}" } } }
            ]
          }
        },
        "sort": [ { "{{EsIndexNames.WindowTimestampFieldPath}}": "asc" } ]
      }
      """;

    /// <summary>
    /// Poll <see cref="ElasticsearchTestClient.SearchAllHits"/> over the window until the returned hit
    /// count is unchanged across two consecutive polls AND the count is non-zero (D-05 poll-to-stable).
    /// This prevents scoring an in-flight run as MISSING.
    /// <para>
    /// Stability requires a non-zero count: a transient empty result (ES 404 lazy-index, backend blip)
    /// returning <c>0 == 0</c> across two polls would be incorrectly accepted as stable, producing
    /// <c>Missing = triggerCount - 0 > 0</c> → Fail on a backend hiccup rather than a real defect.
    /// If the window genuinely contains zero runs (e.g. no dispatches fired), the loop exhausts its
    /// budget and returns the empty list — the precondition assert (WR-04) then surfaces the root cause.
    /// </para>
    /// <para>Bounded by <see cref="PollToStableBudgetMs"/> total wall-clock (separate from
    /// <see cref="DrainMs"/> — see WR-03).</para>
    /// </summary>
    private static async Task<List<JsonElement>> PollHitsToStableAsync(
        ElasticsearchTestClient es, DateTimeOffset windowStart, DateTimeOffset snapshot, CancellationToken ct)
    {
        var body = BuildStepSearchBody(windowStart, snapshot);
        var last = await es.SearchAllHits(body, ct: ct);
        var deadline = DateTime.UtcNow.AddMilliseconds(PollToStableBudgetMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(PollToStableMs, ct);
            var current = await es.SearchAllHits(body, ct: ct);
            if (current.Count == last.Count && current.Count > 0)
            {
                // stable across two polls AND we actually have hits — safe to score.
                // Requiring Count > 0 prevents a transient empty ES result (404 lazy-index,
                // backend blip) from being accepted as "stable" and producing a spurious
                // triggerCount-missing FAIL. (WR-02 fix — 66-REVIEW.md.)
                return current;
            }
            last = current;
        }
        return last;
    }

    /// <summary>
    /// Group raw ES hits by <c>_source.attributes.CorrelationId</c> into per-run
    /// <see cref="RunTrace"/>s, collecting the <c>attributes.StepLabel</c> list (duplicates RETAINED so
    /// the engine's fail-closed duplicate signal fires). Hits missing either attribute are skipped
    /// defensively (T-66-09 — odd-shaped JSON is dropped, never thrown).
    /// </summary>
    private static List<RunTrace> BuildRunTraces(List<JsonElement> hits)
    {
        var byCorrelation = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var hit in hits)
        {
            if (!hit.TryGetProperty("_source", out var source)) continue;
            if (!source.TryGetProperty("attributes", out var attrs)) continue;

            if (!attrs.TryGetProperty("CorrelationId", out var corrEl)
                || corrEl.ValueKind != JsonValueKind.String) continue;
            if (!attrs.TryGetProperty("StepLabel", out var labelEl)
                || labelEl.ValueKind != JsonValueKind.String) continue;

            var correlationId = corrEl.GetString()!;
            var label = labelEl.GetString()!;

            // Read Sum defensively (A1) — informational only, never a completeness gate; not thrown on.
            _ = TryReadSum(attrs, out _);

            if (!byCorrelation.TryGetValue(correlationId, out var labels))
            {
                labels = new List<string>();
                byCorrelation[correlationId] = labels;
            }
            labels.Add(label);
        }

        return byCorrelation
            .Select(kv => RunTrace.FromLabels(kv.Key, kv.Value))
            .ToList();
    }

    /// <summary>
    /// Defensive <c>attributes.Sum</c> read (A1): the field surfaces numeric (<c>long</c>) once Step_*
    /// docs land, but the Wave-0 probe found it unmapped, so read it tolerantly — <c>TryGetInt32</c>
    /// then <c>GetString</c>+parse — and never throw (Sum is informational, not a gate).
    /// </summary>
    private static bool TryReadSum(JsonElement attrs, out int sum)
    {
        sum = 0;
        if (!attrs.TryGetProperty("Sum", out var sumEl)) return false;
        if (sumEl.ValueKind == JsonValueKind.Number && sumEl.TryGetInt32(out sum)) return true;
        if (sumEl.ValueKind == JsonValueKind.String
            && int.TryParse(sumEl.GetString(), out sum)) return true;
        return false;
    }

    // ── Prometheus windowed-delta counter set (OBS-03) ───────────────────────────────────────────────

    /// <summary>
    /// The raw counter values read at one snapshot point. The fixture takes two (before/after) and
    /// subtracts to get the WINDOWED DELTAS the engine reconciles (A3 — counters are cumulative).
    /// Nullable members are the DORMANT dedupe counters: <c>null</c> == no series present (absent).
    /// </summary>
    private sealed record CounterSet
    {
        public required double DispatchSent { get; init; }
        public required double ResultConsumed { get; init; }
        public required double DispatchConsumed { get; init; }
        public required double ResultSentCompleted { get; init; }
        public required double KeeperReinjectDropped { get; init; }
        public double? ResultDeduped { get; init; }
        public double? DispatchDeduped { get; init; }
        public required IReadOnlyDictionary<string, double> NonCompletedOutcomes { get; init; }
    }

    /// <summary>
    /// Read the counter set once. Counters sum across all label combinations (no ProcessorId filter —
    /// the analyzer reconciles the whole window). DORMANT dedupe counters: query and map an EMPTY series
    /// to <c>null</c> (absent), feeding NO reconciliation arithmetic. Non-completed processor_result_sent
    /// outcomes (failed/cancelled/processing) are read per-outcome (expect zero).
    /// <para>
    /// <paramref name="evalTime"/> (67-03 / OBS-04): when non-null every counter is read via an INSTANT
    /// query pinned to that instant (delta@windowEnd − delta@windowStart aligns the trigger denominator
    /// with the ES cohort). When <c>null</c> (standalone Phase 66) every read is the live "now" query,
    /// byte-for-byte the original behavior.
    /// </para>
    /// </summary>
    private static async Task<CounterSet> ReadCounterSetAsync(
        PrometheusTestClient prom, CancellationToken ct, DateTimeOffset? evalTime = null)
    {
        var nonCompleted = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var outcome in new[] { "failed", "cancelled", "processing" })
        {
            nonCompleted[outcome] = await SumOrZeroAsync(
                prom, $"processor_result_sent_total{{outcome=\"{outcome}\"}}", ct, evalTime);
        }

        return new CounterSet
        {
            DispatchSent = await SumOrZeroAsync(prom, "orchestrator_dispatch_sent_total", ct, evalTime),
            ResultConsumed = await SumOrZeroAsync(prom, "orchestrator_result_consumed_total", ct, evalTime),
            DispatchConsumed = await SumOrZeroAsync(prom, "processor_dispatch_consumed_total", ct, evalTime),
            ResultSentCompleted = await SumOrZeroAsync(
                prom, "processor_result_sent_total{outcome=\"completed\"}", ct, evalTime),
            KeeperReinjectDropped = await SumOrZeroAsync(prom, "keeper_reinject_dropped_total", ct, evalTime),
            // DORMANT (no increment site) — absent series ⇒ null ⇒ reported Absent, feeds no arithmetic.
            ResultDeduped = await SumOrNullAsync(prom, "orchestrator_result_deduped_total", ct, evalTime),
            DispatchDeduped = await SumOrNullAsync(prom, "processor_dispatch_deduped_total", ct, evalTime),
            NonCompletedOutcomes = nonCompleted,
        };
    }

    /// <summary>Sum the series value (0 for an absent/empty vector — a counter just hasn't moved). When
    /// <paramref name="evalTime"/> is non-null the value is read as of that instant (67-03 / OBS-04).</summary>
    private static async Task<double> SumOrZeroAsync(
        PrometheusTestClient prom, string promql, CancellationToken ct, DateTimeOffset? evalTime = null)
        => PrometheusTestClient.SumSampleValues(await prom.QueryPrometheus(promql, ct, evalTime));

    /// <summary>
    /// Sum the series value, or <c>null</c> when the vector is EMPTY — for the DORMANT dedupe counters
    /// where absence is meaningful (no series exists at all), distinct from a present-but-zero counter.
    /// When <paramref name="evalTime"/> is non-null the value is read as of that instant (67-03 / OBS-04).
    /// </summary>
    private static async Task<double?> SumOrNullAsync(
        PrometheusTestClient prom, string promql, CancellationToken ct, DateTimeOffset? evalTime = null)
    {
        var samples = await prom.QueryPrometheus(promql, ct, evalTime);
        return samples.Count == 0 ? null : PrometheusTestClient.SumSampleValues(samples);
    }

    /// <summary>
    /// Build the <see cref="PromCounterSnapshot"/> from the before/after counter sets as WINDOWED DELTAS
    /// (after − before). Dormant dedupe deltas are <c>null</c> when EITHER snapshot lacks the series
    /// (absent stays absent). Non-completed outcome deltas are computed per outcome.
    /// </summary>
    private static PromCounterSnapshot BuildSnapshot(CounterSet before, CounterSet after)
    {
        var nonCompletedDelta = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var outcome in after.NonCompletedOutcomes.Keys)
        {
            var b = before.NonCompletedOutcomes.TryGetValue(outcome, out var bv) ? bv : 0;
            nonCompletedDelta[outcome] = after.NonCompletedOutcomes[outcome] - b;
        }

        return new PromCounterSnapshot
        {
            DispatchSentDelta = after.DispatchSent - before.DispatchSent,
            ResultConsumedDelta = after.ResultConsumed - before.ResultConsumed,
            DispatchConsumedDelta = after.DispatchConsumed - before.DispatchConsumed,
            ResultSentCompletedDelta = after.ResultSentCompleted - before.ResultSentCompleted,
            KeeperReinjectDroppedDelta = after.KeeperReinjectDropped - before.KeeperReinjectDropped,
            ResultDedupedDelta = DeltaOrNull(before.ResultDeduped, after.ResultDeduped),
            DispatchDedupedDelta = DeltaOrNull(before.DispatchDeduped, after.DispatchDeduped),
            NonCompletedOutcomes = nonCompletedDelta,
        };
    }

    /// <summary>A dormant-counter delta is null unless BOTH snapshots carried the series (absent ⇒ null).</summary>
    private static double? DeltaOrNull(double? before, double? after)
        => before is { } b && after is { } a ? a - b : null;
}
