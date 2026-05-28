using System.Net;
using BaseApi.Tests.Observability.Helpers;
using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// Phase 11 D-16 migration of Phase 5 SC#1 (OBSERV-02 / OBSERV-05 / OBSERV-07) +
/// T-05-PII-INJECT regression: Phase 4 <c>CorrelationIdMiddleware</c>'s
/// <c>BeginScope("CorrelationId", id)</c> propagates through the MEL bridge into
/// the OTel LoggerProvider (<c>IncludeScopes = true</c>) and surfaces on the
/// OTLP-exported log doc landed in Elasticsearch under the
/// <see cref="EsIndexNames.LogsDataStream"/> data stream.
///
/// <para>
/// Migration: was Phase 5 file-exporter + position-marker fixture (deleted by
/// Plan 11-05 / 11-08c). Now uses <see cref="Phase11WebAppFactory"/> + ES polling
/// via <see cref="ElasticsearchTestClient.PollEsForLog"/>.
/// </para>
/// </summary>
[Trait("Phase", "11")]
[Collection("Observability")]
public sealed class LogExportTests : IClassFixture<Phase11WebAppFactory>
{
    private readonly Phase11WebAppFactory _factory;

    public LogExportTests(Phase11WebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Test_LogRecord_Has_CorrelationId_And_ServiceResource()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // The middleware GENERATES a corrId when none is supplied — capture it from the
        // response header. Phase 4 OBSERV-09/10/11 guarantees the same value echoes back.
        var response = await client.GetAsync("/test-obs/ok", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var corrId = response.Headers.GetValues("X-Correlation-Id").Single();
        Assert.Matches("^[a-f0-9]{32}$", corrId);

        // Poll ES for the log doc carrying this corrId. Use a bool/must query that
        // combines (a) the corrId term filter (per-test isolation) AND (b) a phrase
        // match on body.text="test-obs ok ran" — Rule 1 fix-forward at execution time:
        // a single request produces MULTIPLE log records sharing the same corrId scope
        // (controller's LogInformation + framework's Hosting.Diagnostics "Request started"
        // + "Request finished"); without the body filter PollEsForLog returns hits[0]
        // which may be a framework log lacking "test-obs ok ran" in body.text.
        using var es = new ElasticsearchTestClient();
        var queryBody = $$"""
          {
            "size": 10,
            "query": {
              "bool": {
                "must": [
                  { "term":         { "{{EsIndexNames.CorrelationIdFieldPath}}": "{{corrId}}" } },
                  { "match_phrase": { "body.text": "test-obs ok ran" } }
                ]
              }
            }
          }
          """;
        var hit = await es.PollEsForLog(queryBody, timeoutMs: 30_000, ct: ct);
        Assert.NotNull(hit);

        // Field-shape-agnostic sanity probes against the hit's _source raw JSON:
        //   (a) the doc body string "test-obs ok ran" appears verbatim somewhere
        //   (b) the correlation id appears (defensive — confirms field path was correct)
        //   (c) service.name = sk-api appears (load-bearing per D-07 resource_to_telemetry_conversion)
        // service.version is intentionally NOT asserted (checker WARNING #7) — couples to
        // appsettings.json Service.Version and breaks the test on future version bumps.
        var rawJson = hit!.Value.GetRawText();
        Assert.Contains("test-obs ok ran", rawJson);
        Assert.Contains(corrId, rawJson);
        Assert.Contains("sk-api", rawJson);
    }

    [Fact]
    public async Task Test_LogRecord_CorrelationId_Survives_Sanitization()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // Send a deliberately-malformed correlation id (Phase 4 Pitfall 3 — control chars).
        // TryAddWithoutValidation bypasses HttpClient header validation so the middleware
        // (D-02 IsValid) is what rejects the value.
        using var req = new HttpRequestMessage(HttpMethod.Get, "/test-obs/ok");
        req.Headers.TryAddWithoutValidation("X-Correlation-Id", "invalid\rinjected");
        var response = await client.SendAsync(req, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Phase 4 sanitization replaces the input with a fresh 32-hex UUID.
        var sanitized = response.Headers.GetValues("X-Correlation-Id").Single();
        Assert.Matches("^[a-f0-9]{32}$", sanitized);

        // Poll ES for the log doc carrying the SANITIZED corrId. Same bool/must pattern
        // as Test_LogRecord_Has_CorrelationId_And_ServiceResource — pin to the controller's
        // "test-obs ok ran" body so the returned hit is the one proving the sanitized
        // scope value flowed through OTel (vs framework Hosting.Diagnostics logs which
        // also share the scope but carry different body text).
        using var es = new ElasticsearchTestClient();
        var queryBody = $$"""
          {
            "size": 10,
            "query": {
              "bool": {
                "must": [
                  { "term":         { "{{EsIndexNames.CorrelationIdFieldPath}}": "{{sanitized}}" } },
                  { "match_phrase": { "body.text": "test-obs ok ran" } }
                ]
              }
            }
          }
          """;
        var hit = await es.PollEsForLog(queryBody, timeoutMs: 30_000, ct: ct);
        Assert.NotNull(hit);

        // The log doc MUST carry the sanitized value, NOT the raw \r-injected input.
        var rawJson = hit!.Value.GetRawText();
        Assert.Contains(sanitized, rawJson);
        // T-05-PII-INJECT regression — no \r, \n, "injected" anywhere in the doc.
        Assert.DoesNotContain("\\r", rawJson);     // JSON-escaped \r form
        Assert.DoesNotContain("invalid\\rinjected", rawJson);
        Assert.DoesNotContain("injected", rawJson);
    }
}
