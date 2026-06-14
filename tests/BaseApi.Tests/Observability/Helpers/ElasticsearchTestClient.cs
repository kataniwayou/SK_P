using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BaseApi.Tests.Observability.Helpers;

/// <summary>
/// Phase 11 D-17 / D-18 — async HTTP polling helper for Elasticsearch _search.
/// Encapsulates: (a) exponential backoff (200ms → 3200ms cap) per RESEARCH Pattern 2;
/// (b) HTTP 404 + empty-hits tolerance — ES creates the data stream lazily on first write
/// (RESEARCH Pitfall 5); (c) Long-lived <see cref="HttpClient"/> with
/// <c>BaseAddress = http://localhost:9200/</c> (RESEARCH Pattern 5 — host-DNS for
/// out-of-compose test process).
///
/// <para>
/// <b>Per-test isolation discipline (Pitfall 5):</b> the ES data stream is shared across
/// the entire test suite (Plan 11-05 D-05 removed the file-exporter cleanup; this analog
/// for ES is per-test unique correlation IDs). Each test SHOULD generate
/// <c>$"{Guid.NewGuid():N}"</c> and query on that specific id rather than asserting on
/// cumulative counts. The single-fact assertion shape <c>Assert.NotNull(hit)</c> against
/// a unique-id filter is robust to prior test history.
/// </para>
///
/// <para>
/// <b>Disposal:</b> the inner <see cref="HttpClient"/> is created in the ctor and disposed
/// in <see cref="Dispose"/>. Consumers should call <c>Dispose()</c> in their test class's
/// teardown OR (preferred) construct the client in a <c>using</c> block per fact.
/// </para>
/// </summary>
public sealed class ElasticsearchTestClient : IDisposable
{
    private const int InitialDelayMs = 200;
    private const int MaxDelayMs     = 3_200;

    private readonly HttpClient _es;

    public ElasticsearchTestClient()
    {
        _es = new HttpClient { BaseAddress = new Uri("http://localhost:9200/") };
    }

    public void Dispose() => _es.Dispose();

    /// <summary>
    /// Polls the ES <c>_search</c> endpoint with the supplied query body until a hit is
    /// returned OR the timeout expires. Returns the first hit's full
    /// <see cref="JsonElement"/> (cloned — safe to retain after the inner doc is disposed)
    /// or <c>null</c> on timeout.
    ///
    /// <para>
    /// <c>queryBody</c> is the JSON body sent in the POST request (e.g.,
    /// <c>{"size":10,"query":{"term":{"&lt;EsIndexNames.CorrelationIdFieldPath&gt;":"abc..."}}}</c>).
    /// Use a static-template raw-string-literal in the caller (RESEARCH Don't Hand-Roll table)
    /// rather than manual string concatenation over user input.
    /// </para>
    ///
    /// <para>
    /// <c>indexPath</c> defaults to <see cref="EsIndexNames.LogsDataStream"/> (verified Wave 0
    /// constant). Override only for cross-index probes.
    /// </para>
    /// </summary>
    public async Task<JsonElement?> PollEsForLog(
        string queryBody, int timeoutMs, string? indexPath = null, CancellationToken ct = default)
    {
        indexPath ??= EsIndexNames.LogsDataStream;

        var sw    = Stopwatch.StartNew();
        var delay = InitialDelayMs;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{indexPath}/_search")
                {
                    Content = new StringContent(queryBody, Encoding.UTF8, "application/json"),
                };
                using var resp = await _es.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("hits", out var outer)
                        && outer.TryGetProperty("hits", out var hits)
                        && hits.ValueKind == JsonValueKind.Array
                        && hits.GetArrayLength() > 0)
                    {
                        // Clone() detaches the element from the parsing scope (the using-var
                        // doc disposes at end of block); the returned element is safe to
                        // retain. Pattern lifted verbatim from the retired Phase 5 fixture line 211.
                        using var inner = JsonDocument.Parse(hits[0].GetRawText());
                        return inner.RootElement.Clone();
                    }
                }
                // Else: 404 (index not yet created), empty hits (doc not indexed yet), or
                // malformed envelope — keep polling per RESEARCH Pitfall 5.
            }
            catch (HttpRequestException)
            {
                // ES briefly unreachable (compose-network blip; container restart) — retry.
            }

            var remaining = (int)(timeoutMs - sw.ElapsedMilliseconds);
            if (remaining <= 0) break;
            await Task.Delay(Math.Min(delay, remaining), ct);
            delay = Math.Min(delay * 2, MaxDelayMs);
        }
        return null;
    }

    /// <summary>
    /// Phase 66 research item #3 / OBS-01 — the multi-hit aggregation primitive. Issues a SINGLE
    /// size-bounded <c>_search</c> and returns ALL hits as a <see cref="List{JsonElement}"/>, unlike
    /// <see cref="PollEsForLog"/> which returns only <c>hits[0]</c>. Used to read the ~90 step logs in a
    /// 5-minute window in one request (the caller sizes the query body, e.g. <c>"size":2000</c>) and group
    /// them in C# by <c>attributes.CorrelationId</c> + <c>attributes.StepLabel</c> into per-run traces.
    ///
    /// <para>
    /// <b>Single request, no backoff loop:</b> unlike <see cref="PollEsForLog"/> this does ONE request — the
    /// CALLER polls-to-stable (re-invokes until the hit count stops growing) rather than this method blocking.
    /// </para>
    ///
    /// <para>
    /// <b>404 / empty tolerance:</b> a non-success response (e.g. the data stream not yet lazily created on
    /// first write — RESEARCH Pitfall 5) returns an EMPTY list rather than throwing, so the caller's
    /// poll-to-stable loop sees zero hits and keeps polling instead of failing RED on a transient absence.
    /// </para>
    ///
    /// <para>
    /// <b>Clone discipline:</b> each returned element is <c>Clone()</c>-detached from its parsing
    /// <see cref="JsonDocument"/> (which disposes at end of the enumeration), so every element is safe to
    /// retain after this method returns — the SAME discipline <see cref="PollEsForLog"/> uses for <c>hits[0]</c>.
    /// </para>
    /// </summary>
    public async Task<List<JsonElement>> SearchAllHits(
        string queryBody, string? indexPath = null, CancellationToken ct = default)
    {
        indexPath ??= EsIndexNames.LogsDataStream;
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{indexPath}/_search")
        {
            Content = new StringContent(queryBody, Encoding.UTF8, "application/json"),
        };
        using var resp = await _es.SendAsync(req, ct);
        var results = new List<JsonElement>();
        if (!resp.IsSuccessStatusCode) return results;   // 404 lazy-index tolerance (caller polls-to-stable)
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("hits", out var outer)
            && outer.TryGetProperty("hits", out var hits)
            && hits.ValueKind == JsonValueKind.Array)
        {
            foreach (var h in hits.EnumerateArray())
            {
                using var inner = JsonDocument.Parse(h.GetRawText());
                results.Add(inner.RootElement.Clone());   // detach — safe to retain
            }
        }
        return results;
    }
}
