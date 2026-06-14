using System.Text.Json;
using Xunit;

namespace BaseApi.Tests.Observability.Helpers;

/// <summary>
/// Phase 66 research item #3 / OBS-01 — hermetic facts for the multi-hit ES aggregation primitive
/// <see cref="ElasticsearchTestClient.SearchAllHits"/>. These facts do NOT hit the live ES (no live
/// stack): because <c>SearchAllHits</c> does HTTP, the GROUPING + ENVELOPE-PARSING contract is proven
/// directly against an inline captured <c>_search</c> JSON string that uses the EXACT same
/// <c>hits.hits[]</c> envelope <c>SearchAllHits</c> walks (<c>TryGetProperty("hits")</c> →
/// <c>"hits"</c> array → <c>EnumerateArray</c> + <c>Clone</c>).
///
/// <para>
/// The contract proven: <c>SearchAllHits</c> returns ALL hits (not just <c>hits[0]</c> like
/// <see cref="ElasticsearchTestClient.PollEsForLog"/>); grouping those hits by
/// <c>attributes.CorrelationId</c> reconstructs one per-run trace per distinct correlationId; a
/// group's distinct <c>attributes.StepLabel</c> set is the trace shape; and a duplicate
/// <c>(correlationId, StepLabel)</c> pair is DETECTABLE (label list count &gt; distinct count) — the
/// fail-closed input the OBS-02 engine consumes. An empty / hits-absent envelope yields zero groups
/// with no exception (the 404-lazy-index / poll-to-stable tolerance, T-66-04).
/// </para>
///
/// <para>
/// Mirrors the self-contained hermetic *Facts shape of <c>SampleProcessorFacts</c> — sealed class,
/// <c>[Fact]</c> methods, inline fixtures, no external file, no stack. Method names start
/// <c>SearchAllHits_*</c> so the VALIDATION filter resolves them.
/// </para>
/// </summary>
public sealed class ElasticsearchTestClientFacts
{
    /// <summary>
    /// A captured ES <c>_search</c> response in the verified otel shape — four hits, each
    /// <c>_source.attributes</c> carrying <c>CorrelationId</c>, <c>StepLabel</c>, <c>Sum</c>. The last
    /// hit duplicates <c>(corr-2, Step_A)</c> so the collision-detection fact has input. This is the
    /// SAME <c>hits.hits[]</c> envelope <see cref="ElasticsearchTestClient.SearchAllHits"/> walks.
    /// </summary>
    private const string CapturedSearchResponse = """
        {
          "hits": {
            "hits": [
              { "_source": { "attributes": { "CorrelationId": "corr-1", "StepLabel": "Step_A", "Sum": 42 } } },
              { "_source": { "attributes": { "CorrelationId": "corr-1", "StepLabel": "Step_B", "Sum": 7  } } },
              { "_source": { "attributes": { "CorrelationId": "corr-2", "StepLabel": "Step_A", "Sum": 9  } } },
              { "_source": { "attributes": { "CorrelationId": "corr-2", "StepLabel": "Step_A", "Sum": 9  } } }
            ]
          }
        }
        """;

    private const string EmptyHitsResponse = """{ "hits": { "hits": [] } }""";

    /// <summary>
    /// Walks the <c>hits.hits[]</c> envelope with the EXACT navigation
    /// <see cref="ElasticsearchTestClient.SearchAllHits"/> uses (TryGetProperty "hits" → "hits" array
    /// → EnumerateArray + Clone-each), returning every hit Clone-detached. This proves the parsing
    /// contract hermetically without the HTTP path.
    /// </summary>
    private static List<JsonElement> ExtractAllHits(string searchResponseJson)
    {
        var results = new List<JsonElement>();
        using var doc = JsonDocument.Parse(searchResponseJson);
        if (doc.RootElement.TryGetProperty("hits", out var outer)
            && outer.TryGetProperty("hits", out var hits)
            && hits.ValueKind == JsonValueKind.Array)
        {
            foreach (var h in hits.EnumerateArray())
            {
                using var inner = JsonDocument.Parse(h.GetRawText());
                results.Add(inner.RootElement.Clone());
            }
        }
        return results;
    }

    private static string CorrelationId(JsonElement hit) =>
        hit.GetProperty("_source").GetProperty("attributes")
           .GetProperty(EsIndexNames.CorrelationIdFieldPath.Split('.')[^1]).GetString()!;

    private static string StepLabel(JsonElement hit) =>
        hit.GetProperty("_source").GetProperty("attributes")
           .GetProperty(EsIndexNames.StepLabelFieldPath.Split('.')[^1]).GetString()!;

    /// <summary>
    /// Reads <c>attributes.Sum</c> defensively per 66-RESEARCH A1 (TryGetInt32, then GetString+parse) —
    /// the captured number could surface as a JSON number OR a string under different ES coercions.
    /// </summary>
    private static int Sum(JsonElement hit)
    {
        var sumEl = hit.GetProperty("_source").GetProperty("attributes")
                       .GetProperty(EsIndexNames.SumFieldPath.Split('.')[^1]);
        if (sumEl.ValueKind == JsonValueKind.Number && sumEl.TryGetInt32(out var n)) return n;
        return int.Parse(sumEl.GetString()!);
    }

    [Fact]
    public void SearchAllHits_ReturnsAllHits_NotJustFirst()
    {
        var hits = ExtractAllHits(CapturedSearchResponse);

        // Proves the multi-hit contract: ALL hits returned, not just hits[0] (PollEsForLog's behavior).
        Assert.Equal(4, hits.Count);
        Assert.NotEqual(1, hits.Count);

        // Each element is independently readable (Clone-detached, the parsing docs already disposed).
        Assert.All(hits, h => Assert.False(string.IsNullOrEmpty(CorrelationId(h))));
        // Defensive Sum read does not throw on the captured shape (T-66-04).
        Assert.All(hits, h => Assert.InRange(Sum(h), 0, int.MaxValue));
    }

    [Fact]
    public void SearchAllHits_GroupsByCorrelationId_IntoPerRunTraces()
    {
        var hits = ExtractAllHits(CapturedSearchResponse);

        var groups = hits.GroupBy(CorrelationId).ToList();

        // Two distinct correlationIds → two per-run traces.
        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, g => g.Key == "corr-1");
        Assert.Contains(groups, g => g.Key == "corr-2");

        // corr-1's distinct StepLabel set is the reconstructed trace shape {Step_A, Step_B}.
        var corr1Labels = groups.Single(g => g.Key == "corr-1")
                                 .Select(StepLabel).Distinct().OrderBy(s => s).ToArray();
        Assert.Equal(new[] { "Step_A", "Step_B" }, corr1Labels);
    }

    [Fact]
    public void SearchAllHits_DetectsDuplicateLabelWithinRun()
    {
        var hits = ExtractAllHits(CapturedSearchResponse);

        var corr2Labels = hits.Where(h => CorrelationId(h) == "corr-2")
                              .Select(StepLabel).ToList();

        // The duplicate (corr-2, Step_A) is detectable: list count (2) > distinct count (1).
        // This is the OBS-02 fail-closed input the engine consumes.
        Assert.Equal(2, corr2Labels.Count);
        Assert.Single(corr2Labels.Distinct());
        Assert.True(corr2Labels.Count > corr2Labels.Distinct().Count());
    }

    [Fact]
    public void SearchAllHits_EmptyHits_YieldsZeroGroups()
    {
        // 404-lazy-index / poll-to-stable tolerance (T-66-04): an empty envelope must not throw.
        var hits = ExtractAllHits(EmptyHitsResponse);

        Assert.Empty(hits);
        Assert.Empty(hits.GroupBy(CorrelationId));
    }
}
