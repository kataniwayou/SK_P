using System.Text.Json;
using System.Text.RegularExpressions;
using BaseApi.Tests.Observability.Analysis;
using BaseApi.Tests.Observability.Helpers;
using StackExchange.Redis;
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
    // Prometheus is pull-based: SDK→collector OTLP export (~60s) + collector→Prometheus 15s scrape.
    // A single immediate query MISSES the sample (Pitfall 5). Mirror the MetricsRoundTrip budget.
    private const int PromPollTimeoutMs = 120_000;

    // D-05 drain: bounded settle after the observation window closes so in-flight runs finish their
    // 9-step traversal before scoring (Pitfall 4 — scoring mid-traversal would mis-flag a run MISSING).
    private const int DrainMs = 60_000;        // > worst-case 9-step traversal
    private const int PollToStableMs = 5_000;  // re-poll interval; snapshot when the ES hit count is
                                               //   unchanged across two consecutive polls

    // The default scenario id for the phase fixture. The Phase 67/68 harness passes its own per-run id.
    private const string DefaultScenarioId = "TEST-01";

    // V5 / T-66-07 — scenario-id whitelist. Validated BEFORE composing any filesystem path so a
    // caller-supplied id can never traverse out of the fixed reports dir (no '/', '\', '.', etc.).
    private static readonly Regex ScenarioIdPattern = new(@"^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    private const string HostRedisFull = "localhost:6380,abortConnect=false,connectTimeout=5000";

    [Fact]
    public async Task Analyze_HappyPath_Window_Yields_Pass()
    {
        var ct = TestContext.Current.CancellationToken;
        var scenarioId = DefaultScenarioId;

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

        // ── 2. PROM BEFORE-SNAPSHOT (windowing, A3 / Pitfall 3) ──────────────────────────────────────
        //    Task 1 confirmed counters are lifetime cumulative (FLUSHALL+heal, NO restart), so read the
        //    live counter set NOW (window start) to enable delta = after − before.
        var windowStartUtc = DateTimeOffset.UtcNow;
        var before = await ReadCounterSetAsync(prom, ct);

        // ── 3. DRAIN (D-05 / Pitfall 4) + POLL-TO-STABLE ─────────────────────────────────────────────
        //    After the observation window closes (harness-controlled wall-clock; here a bounded delay),
        //    poll SearchAllHits until the hit count is unchanged across two consecutive polls so no
        //    in-flight run is scored MISSING.
        await Task.Delay(DrainMs, ct);

        var snapshotUtc = DateTimeOffset.UtcNow;
        var stepHits = await PollHitsToStableAsync(es, windowStartUtc, snapshotUtc, ct);

        // ── 4. ES READ (OBS-01) — group Step_* hits into per-run RunTraces by attributes.CorrelationId ─
        var traces = BuildRunTraces(stepHits);

        // ── 5. PROM AFTER-SNAPSHOT + WINDOWED DELTAS (OBS-03) ────────────────────────────────────────
        var after = await ReadCounterSetAsync(prom, ct);
        var promSnapshot = BuildSnapshot(before, after);

        // ── 6. TRIGGER DENOMINATOR (D-04 / item #1) ──────────────────────────────────────────────────
        //    Derive triggerCount from the orchestrator_dispatch_sent_total WINDOWED DELTA (rounded).
        //    There is NO per-fire correlationId orchestrator log (item #1): the COUNT of missing runs is
        //    detectable (dispatch_sent − complete), but the IDENTITY of a fully-missing run is NOT
        //    recoverable from telemetry — a documented, accepted limitation (the engine reports the count).
        var triggerCount = (int)Math.Round(promSnapshot.DispatchSentDelta);

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
        var deadline = DateTime.UtcNow.AddMilliseconds(DrainMs);
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
    /// Read the live counter set once. Live counters sum across all label combinations (no ProcessorId
    /// filter — the analyzer reconciles the whole window). DORMANT dedupe counters: query and map an
    /// EMPTY series to <c>null</c> (absent), feeding NO reconciliation arithmetic. Non-completed
    /// processor_result_sent outcomes (failed/cancelled/processing) are read per-outcome (expect zero).
    /// </summary>
    private static async Task<CounterSet> ReadCounterSetAsync(PrometheusTestClient prom, CancellationToken ct)
    {
        var nonCompleted = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var outcome in new[] { "failed", "cancelled", "processing" })
        {
            nonCompleted[outcome] = await SumOrZeroAsync(
                prom, $"processor_result_sent_total{{outcome=\"{outcome}\"}}", ct);
        }

        return new CounterSet
        {
            DispatchSent = await SumOrZeroAsync(prom, "orchestrator_dispatch_sent_total", ct),
            ResultConsumed = await SumOrZeroAsync(prom, "orchestrator_result_consumed_total", ct),
            DispatchConsumed = await SumOrZeroAsync(prom, "processor_dispatch_consumed_total", ct),
            ResultSentCompleted = await SumOrZeroAsync(
                prom, "processor_result_sent_total{outcome=\"completed\"}", ct),
            KeeperReinjectDropped = await SumOrZeroAsync(prom, "keeper_reinject_dropped_total", ct),
            // DORMANT (no increment site) — absent series ⇒ null ⇒ reported Absent, feeds no arithmetic.
            ResultDeduped = await SumOrNullAsync(prom, "orchestrator_result_deduped_total", ct),
            DispatchDeduped = await SumOrNullAsync(prom, "processor_dispatch_deduped_total", ct),
            NonCompletedOutcomes = nonCompleted,
        };
    }

    /// <summary>Sum the live series value (0 for an absent/empty vector — a LIVE counter just hasn't moved).</summary>
    private static async Task<double> SumOrZeroAsync(
        PrometheusTestClient prom, string promql, CancellationToken ct)
        => PrometheusTestClient.SumSampleValues(await prom.QueryPrometheus(promql, ct));

    /// <summary>
    /// Sum the series value, or <c>null</c> when the vector is EMPTY — for the DORMANT dedupe counters
    /// where absence is meaningful (no series exists at all), distinct from a present-but-zero live counter.
    /// </summary>
    private static async Task<double?> SumOrNullAsync(
        PrometheusTestClient prom, string promql, CancellationToken ct)
    {
        var samples = await prom.QueryPrometheus(promql, ct);
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
