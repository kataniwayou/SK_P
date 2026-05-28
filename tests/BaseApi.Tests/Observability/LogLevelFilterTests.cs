using System.Net;
using BaseApi.Tests.Observability.Helpers;
using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// Phase 11 D-16 migration of Phase 5 SC#2 (OBSERV-06): setting
/// <c>Logging:LogLevel:Default = "Warning"</c> suppresses <c>Information</c> logs
/// from BOTH the console sink AND the OTLP-exported records. This proves the single
/// MEL filter path (Pitfall 9 / single source of truth) — the filter runs BEFORE
/// either sink, so both behave identically.
///
/// <para>
/// Migration: was Phase 5 file-exporter + per-test fixture instances (deleted by
/// Plan 11-05 / 11-08c). Now uses <see cref="Phase11WebAppFactory"/> + ES polling.
/// The negative-assertion fact uses a shorter timeout (~8s) as the "no hit" proof —
/// long enough for the ES indexing pipeline to flush any hit that DID exist, short
/// enough to keep the suite wall-clock manageable. PATTERNS option a per RESEARCH.
/// </para>
/// </summary>
[Trait("Phase", "11")]
[Collection("Observability")]
public sealed class LogLevelFilterTests
{
    [Fact]
    public async Task Test_Information_Log_Suppressed_When_Default_Warning()
    {
        var ct = TestContext.Current.CancellationToken;

        // Spin up a fixture with the LogLevel override (internal 1-arg ctor on
        // Phase11WebAppFactory — same assembly so accessible). Each test gets its OWN
        // fixture because the LogLevel needs to be applied at host-build time.
        await using var factory = new Phase11WebAppFactory(logLevelDefaultOverride: "Warning");
        await factory.InitializeAsync();

        // Per-test unique correlation id so the ES query filter is unambiguous.
        var thisRequestCorrId = $"{Guid.NewGuid():N}";

        using var client = factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/test-obs/ok");
        req.Headers.Add("X-Correlation-Id", thisRequestCorrId);
        var response = await client.SendAsync(req, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Poll ES for any log doc carrying THIS request's correlation id.
        // Expected: NO hit — LogInformation was filtered by MEL before reaching OTel.
        // Shorter budget (8s) for negative assertion per RESEARCH PATTERNS option a.
        using var es = new ElasticsearchTestClient();
        var queryBody = $$"""
          {
            "size": 10,
            "query": { "term": { "{{EsIndexNames.CorrelationIdFieldPath}}": "{{thisRequestCorrId}}" } }
          }
          """;
        var hit = await es.PollEsForLog(queryBody, timeoutMs: 8_000, ct: ct);
        Assert.Null(hit);
    }

    [Fact]
    public async Task Test_Information_Log_Present_When_Default_Information()
    {
        var ct = TestContext.Current.CancellationToken;

        // Default (no override) — appsettings.json declares Logging:LogLevel:Default=Information.
        await using var factory = new Phase11WebAppFactory();
        await factory.InitializeAsync();

        var thisRequestCorrId = $"{Guid.NewGuid():N}";

        using var client = factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/test-obs/ok");
        req.Headers.Add("X-Correlation-Id", thisRequestCorrId);
        var response = await client.SendAsync(req, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Poll ES — expected: a hit IS present (positive control).
        using var es = new ElasticsearchTestClient();
        var queryBody = $$"""
          {
            "size": 10,
            "query": { "term": { "{{EsIndexNames.CorrelationIdFieldPath}}": "{{thisRequestCorrId}}" } }
          }
          """;
        var hit = await es.PollEsForLog(queryBody, timeoutMs: 30_000, ct: ct);
        Assert.NotNull(hit);
    }
}
