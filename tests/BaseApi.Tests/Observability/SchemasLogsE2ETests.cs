using System.Net;
using System.Net.Http.Json;
using BaseApi.Service.Features.Schema;
using BaseApi.Tests.Observability.Helpers;
using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// Phase 11 D-17 / TEST-07 — round-trip E2E for the logs half:
/// drives a real <c>POST /api/v1/schemas</c> against the in-process Kestrel host
/// (via <see cref="Phase11WebAppFactory"/>) with a per-test unique
/// <c>X-Correlation-Id</c>, waits for the OTLP → collector → elasticsearch
/// pipeline to ingest the resulting log doc, then asserts on the doc's
/// correlation-id field + resource attributes.
///
/// <para>
/// RESEARCH Open Q3 — separated from <c>SchemasMetricsE2ETests</c> (sk2_1
/// precedent) so a failure cleanly attributes to "logs broken" vs
/// "metrics broken". RESEARCH Pitfall 5 — per-test unique correlation id is
/// the ES cleanup discipline (cumulative data stream across the suite; the
/// id is the per-test isolation key).
/// </para>
///
/// <para>
/// CHECKER WARNING #7 — asserts on <c>service.name=sk-api</c> (load-bearing
/// label per D-07 <c>resource_to_telemetry_conversion: true</c>) but
/// intentionally NOT on the version string. Hardcoding a specific version
/// would couple this E2E test to <c>appsettings.json</c> Service.Version and
/// break the test on any future version bump without any
/// observability-behavior change.
/// </para>
/// </summary>
[Trait("Phase", "11")]
[Trait("Category", "E2E")]
[Collection("Observability")]
public sealed class SchemasLogsE2ETests : IClassFixture<Phase11WebAppFactory>
{
    private readonly Phase11WebAppFactory _factory;

    public SchemasLogsE2ETests(Phase11WebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task PostSchema_Surfaces_Created_LogRecord_In_Elasticsearch_With_CorrelationId()
    {
        var ct = TestContext.Current.CancellationToken;

        // Per-test unique correlation ID — Pitfall 5 isolation discipline; T-11-03 mitigation.
        var corrId = $"{Guid.NewGuid():N}";

        // Drive a real Schema POST (HTTP-01..16 pipeline; lands a row in stepsdb_test_*
        // throwaway DB and emits a request-lifecycle log doc to OTLP).
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", corrId);

        var dto = new SchemaCreateDto(
            Name:        $"E2E-Logs-{Guid.NewGuid():N}",
            Version:     "1.0.0",
            Description: null,
            Definition:  "{ \"$schema\": \"https://json-schema.org/draft/2020-12/schema\", \"type\": \"object\" }");
        var resp = await client.PostAsJsonAsync("/api/v1/schemas", dto, ct);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        // Sanity-check the response echo header — Phase 4 OBSERV-11 invariant.
        Assert.Equal(corrId, resp.Headers.GetValues("X-Correlation-Id").Single());

        // Poll ES for a log doc whose correlation-id matches.
        // The query body shape depends on EsIndexNames.FieldShape (verified Wave 0).
        // Wave 0 (Plan 11-06 Task 0) confirmed FieldShape="otel" + CorrelationIdFieldPath=
        // "attributes.CorrelationId" — see EsIndexNames.cs XML doc.
        //
        // Reference for future readers (do NOT enable both branches):
        //   raw shape (mapping.mode: none would have produced) — field path
        //       "Attributes.CorrelationId" (capital A, capital C).
        //   otel shape (live behavior — elasticsearchexporter@v0.152.0 silently falls back
        //       despite mapping.mode: none) — field path "attributes.CorrelationId"
        //       (lowercase a, capital C).
        // The live branch below consumes EsIndexNames.CorrelationIdFieldPath so a future
        // refresh of the Wave 0 constants flows through automatically without rewriting
        // test bodies.
        using var esClient = new ElasticsearchTestClient();

        var queryBody = $$"""
          {
            "size": 10,
            "query": { "term": { "{{EsIndexNames.CorrelationIdFieldPath}}": "{{corrId}}" } }
          }
          """;

        var hit = await esClient.PollEsForLog(queryBody, timeoutMs: 30_000);
        Assert.NotNull(hit);

        // The hit's _source carries the OTLP log document. Inspect resource attributes
        // to prove service.name=sk-api came through (load-bearing for OBSERV-13).
        // The simplest cross-shape sanity probe is to confirm the doc body contains the
        // literal "sk-api" string somewhere; a stricter shape-specific check follows in
        // a future hardening pass.
        var rawJson = hit!.Value.GetRawText();

        // CHECKER WARNING #7 — assert on service.name (load-bearing label per D-07
        // resource_to_telemetry_conversion: true) but NOT on the version string.
        // Hardcoding a specific version ("3.2.0") couples this E2E test to the current
        // appsettings.json Service.Version and breaks the test on any future version
        // bump without any observability-behavior change. service.name is the
        // load-bearing assertion; version is incidental.
        Assert.Contains("sk-api", rawJson);

        // Also assert the correlation id is round-tripped in the log doc (defensive — the
        // ES _search may have returned a different hit if the field path is wrong).
        Assert.Contains(corrId, rawJson);
    }
}
