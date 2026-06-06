using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace BaseApi.Tests.Observability.Helpers;

/// <summary>
/// Phase 11 D-17 / D-18 — async HTTP polling helper for Prometheus
/// <c>/api/v1/query</c>. Encapsulates: (a) the mandatory sleep-then-poll pattern per
/// RESEARCH Pattern 3 + Pitfall 7 — Prometheus is pull-based with a 15-second scrape
/// interval (Phase 11 D-08 prometheus.yml), so a naive "poll from t=0" loop returns
/// empty for the entire first 15 seconds; the correct discipline is sleep one scrape
/// cycle then poll until the cumulative sample crosses the expected threshold;
/// (b) <see cref="Uri.EscapeDataString"/> on the PromQL parameter — PromQL contains
/// <c>{</c>, <c>}</c>, <c>"</c>, <c>=</c> which break unencoded URL queries (RESEARCH
/// Don't Hand-Roll table); (c) result-vector summation across multi-label samples
/// (the result vector typically contains multiple samples for different method /
/// status_code label combinations; summing keeps the assertion robust).
///
/// <para>
/// <b>OTel → Prometheus naming reminder (RESEARCH Pitfall 1):</b> test code must use the
/// Prom-form metric name. Examples:
/// <list type="bullet">
///   <item><c>http.server.request.duration</c> (Histogram, unit "s") →
///         <c>http_server_request_duration_seconds_count</c> / <c>_sum</c> / <c>_bucket</c></item>
///   <item>monotonic Sum types gain <c>_total</c> suffix</item>
///   <item><c>service.name</c> resource attribute → <c>service_name</c> label (only because
///         <c>resource_to_telemetry_conversion: true</c> is set on the collector's Prom
///         exporter per Phase 11 D-07). Since Phase 38 (MLBL-01) the METRICS resource
///         <c>service.name</c> is the combined <c>{name}_{version}</c>, so the label value is
///         e.g. <c>sk-api_3.2.0</c> (NOT the bare <c>sk-api</c>); <c>service_version</c> is kept
///         as a standalone label (D-07). NOTE: the LOGS resource <c>service.name</c> stays BARE
///         (MLBL-04) — this combine is metrics-only.</item>
/// </list>
/// </para>
/// </summary>
public sealed class PrometheusTestClient : IDisposable
{
    private const int InitialSleepMs = 15_000;   // one Prom scrape_interval per D-08
    private const int PollTimeoutMs  = 60_000;
    private const int PollIntervalMs = 3_000;

    private readonly HttpClient _prom;

    public PrometheusTestClient()
    {
        _prom = new HttpClient { BaseAddress = new Uri("http://localhost:9090/") };
    }

    public void Dispose() => _prom.Dispose();

    // ── Phase 30 (D-11/D-12) — predicate-driven poll for the metrics round-trip E2E ──────────
    //
    // Backoff constants mirror ElasticsearchTestClient (200ms → 3.2s cap). This is the
    // generic predicate variant the Wave 2 MetricsRoundTripE2ETests consumes: it returns the
    // matching `data` JsonElement (detached via Clone) the instant `predicate(data)` is true,
    // rather than the Phase-11 sum-threshold loop above (which the Phase-11 metrics tests use
    // and which is preserved verbatim for them).

    private const int BackoffInitialDelayMs = 200;
    private const int BackoffMaxDelayMs     = 3_200;

    /// <summary>
    /// Phase 30 (D-12) — polls <c>GET /api/v1/query?query={promQL}</c> with exponential
    /// backoff (200ms → 3.2s cap) until <c>status == "success"</c> AND
    /// <paramref name="predicate"/> returns true over the response <c>data</c> element, OR
    /// <paramref name="timeoutMs"/> elapses. Returns the matching <c>data</c>
    /// <see cref="JsonElement"/> (cloned — safe to retain) or <c>null</c> on timeout.
    ///
    /// <para>
    /// The PromQL parameter is the only interpolated value and is escaped via
    /// <see cref="Uri.EscapeDataString"/> (T-30-01 Tampering mitigation); callers build the
    /// query from static C# templates with only a validated GUID procId interpolated.
    /// </para>
    /// </summary>
    public async Task<JsonElement?> PollPromForQuery(
        string promQL, Func<JsonElement, bool> predicate, int timeoutMs, CancellationToken ct = default)
    {
        var sw    = Stopwatch.StartNew();
        var delay = BackoffInitialDelayMs;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // T-30-01 — escape the interpolated PromQL (contains { } " = which break URLs).
                var url = $"api/v1/query?query={Uri.EscapeDataString(promQL)}";
                using var resp = await _prom.GetAsync(url, ct);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("status", out var s) && s.GetString() == "success"
                        && root.TryGetProperty("data", out var data)
                        && predicate(data))
                    {
                        // Clone() detaches from the using-var doc parsing scope — see
                        // ElasticsearchTestClient for the rationale.
                        using var detached = JsonDocument.Parse(data.GetRawText());
                        return detached.RootElement.Clone();
                    }
                }
                // Else: not-yet-scraped sample, malformed envelope, or predicate-not-met — keep polling.
            }
            catch (HttpRequestException)
            {
                // Prometheus briefly unreachable (compose-network blip; container restart) — retry.
            }

            var remaining = (int)(timeoutMs - sw.ElapsedMilliseconds);
            if (remaining <= 0) break;
            await Task.Delay(Math.Min(delay, remaining), ct);
            delay = Math.Min(delay * 2, BackoffMaxDelayMs);
        }
        return null;
    }

    /// <summary>
    /// Phase 30 predicate — true when the query <c>data</c> is a non-empty instant vector
    /// (<c>resultType == "vector"</c> with at least one result sample). Used for
    /// series-existence assertions (METRIC-04/05).
    /// </summary>
    public static bool VectorNonEmpty(JsonElement data) =>
        data.TryGetProperty("resultType", out var rt) && rt.GetString() == "vector"
        && data.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Array
        && r.GetArrayLength() > 0;

    /// <summary>
    /// Phase 30 predicate — true when the query <c>data</c> carries at least one parseable
    /// numeric sample value. Prometheus sample values are QUOTED JSON strings (<c>"1"</c>),
    /// so parse via <see cref="JsonElement.GetString"/> + <see cref="double.TryParse(string?, out double)"/>.
    /// Handles both scalar (<c>result == [ts, "value"]</c>) and vector
    /// (<c>result[i].value == [ts, "value"]</c>) result types. Used for the bottleneck
    /// expression (METRIC-06).
    /// </summary>
    public static bool HasNumericValue(JsonElement data)
    {
        if (!data.TryGetProperty("resultType", out var rt)) return false;
        var resultType = rt.GetString();

        if (resultType == "scalar")
        {
            return data.TryGetProperty("result", out var scalar)
                && scalar.ValueKind == JsonValueKind.Array
                && scalar.GetArrayLength() >= 2
                && double.TryParse(scalar[1].GetString(), out _);
        }

        if (resultType == "vector")
        {
            return data.TryGetProperty("result", out var vec)
                && vec.ValueKind == JsonValueKind.Array
                && vec.EnumerateArray().Any(e =>
                    e.TryGetProperty("value", out var v)
                    && v.ValueKind == JsonValueKind.Array
                    && v.GetArrayLength() >= 2
                    && double.TryParse(v[1].GetString(), out _));
        }

        return false;
    }

    /// <summary>
    /// Polls Prometheus with the supplied PromQL query until the SUM of result-vector
    /// sample values meets or exceeds <paramref name="threshold"/>, OR the timeout
    /// expires. Returns the last observed sample list (may be empty on timeout).
    ///
    /// <para>
    /// Caller is responsible for asserting on the returned list — e.g.,
    /// <c>Assert.True(SumSampleValues(samples) &gt;= threshold)</c> or
    /// <c>Assert.NotEmpty(samples)</c>.
    /// </para>
    /// </summary>
    public async Task<List<JsonElement>> PollPrometheusUntilSumAtLeast(
        string promql, double threshold, CancellationToken ct = default)
    {
        // RESEARCH Pitfall 7 / Pattern 3 — MANDATORY initial sleep. Without it, the
        // first 15s of polling returns empty result vectors (scrape cycle has not run
        // yet); time wasted in the loop instead of in the sleep.
        await Task.Delay(InitialSleepMs, ct);

        var lastSamples = await QueryPrometheus(promql, ct);
        var elapsed     = InitialSleepMs;
        while (elapsed < PollTimeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            // IN-04 review fix: dropped the `lastSamples.Count > 0 &&` gate. With
            // threshold == 0 the desired "trivially satisfied" exit path was being
            // blocked by an empty result vector. SumSampleValues over an empty list
            // returns 0, which correctly compares to threshold (0 >= 0 is true; for
            // any positive threshold, the empty-vector path still rejects via the
            // arithmetic alone). Current callers all use positive thresholds, but the
            // latent edge case is now closed.
            if (SumSampleValues(lastSamples) >= threshold)
            {
                return lastSamples;
            }
            await Task.Delay(PollIntervalMs, ct);
            elapsed += PollIntervalMs;
            lastSamples = await QueryPrometheus(promql, ct);
        }
        return lastSamples;
    }

    /// <summary>
    /// Single-shot Prometheus query. Returns the <c>data.result</c> array as a
    /// <see cref="List{JsonElement}"/> of cloned elements (safe to retain).
    /// Calls <see cref="Assert.Fail(string)"/> on non-success responses.
    /// </summary>
    public async Task<List<JsonElement>> QueryPrometheus(string promql, CancellationToken ct = default)
    {
        // RESEARCH Don't Hand-Roll table — EscapeDataString mandatory; PromQL contains
        // { } " = which break unencoded URL queries.
        var url   = $"api/v1/query?query={Uri.EscapeDataString(promql)}";
        using var resp = await _prom.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("status", out var statusEl)
            || statusEl.GetString() != "success"
            || !doc.RootElement.TryGetProperty("data", out var dataEl)
            || !dataEl.TryGetProperty("result", out var results))
        {
            Assert.Fail($"Prometheus query failed. Query: {promql}. Response: {json}");
            return new List<JsonElement>();
        }
        var list = new List<JsonElement>();
        foreach (var r in results.EnumerateArray())
        {
            // Clone() detaches from parsing scope — see ElasticsearchTestClient for rationale.
            using var inner = JsonDocument.Parse(r.GetRawText());
            list.Add(inner.RootElement.Clone());
        }
        return list;
    }

    /// <summary>
    /// Sums the sample VALUES across a result vector. Each Prom sample shape is
    /// <c>{"metric":{...labels...},"value":[&lt;unix_ts&gt;,"&lt;double-as-string&gt;"]}</c>; this method
    /// extracts the numeric value (sample[1]) from each sample and returns the sum.
    /// </summary>
    public static double SumSampleValues(List<JsonElement> samples)
    {
        double total = 0;
        foreach (var sample in samples)
        {
            if (!sample.TryGetProperty("value", out var value)) continue;
            if (value.ValueKind != JsonValueKind.Array) continue;
            if (value.GetArrayLength() < 2) continue;
            var v = value[1].GetString();
            if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
            {
                total += d;
            }
        }
        return total;
    }
}
