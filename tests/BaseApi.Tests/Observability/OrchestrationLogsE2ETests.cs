using System.Net;
using System.Net.Http.Json;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using BaseApi.Tests.Observability.Helpers;
using BaseApi.Tests.TestHelpers;
using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// Phase 15 OBSERV-REDIS-02 — round-trip E2E for the Redis-op log lifecycle: drives a real
/// <c>POST /api/v1/orchestration/start</c> against the in-process Kestrel host (via
/// <see cref="Phase11WebAppFactory"/>, the same OTLP → collector → elasticsearch wiring the
/// Schema logs E2E uses) with a per-test unique <c>X-Correlation-Id</c>, then waits for the
/// resulting request-lifecycle log doc — which spans the L1→L2 Redis projection write — to be
/// ingested into Elasticsearch and asserts the inbound correlation id round-trips through to
/// the log doc.
///
/// <para>
/// This proves T-15-17 (Redis ops are traceable to a request): the correlationId set by
/// <c>CorrelationIdMiddleware</c> flows via the Phase 4 MEL <c>BeginScope</c> AsyncLocal into
/// every log emitted during the Start — including the Redis projection-write log points
/// (Plan 15-02) — and reaches the observability backend. No OpenTelemetry Redis instrumentation
/// is involved (OBSERV-REDIS-01); correlation is carried by the MEL scope alone.
/// </para>
///
/// <para>
/// Structure mirrors <see cref="SchemasLogsE2ETests"/> VERBATIM (traits, Observability
/// collection, <see cref="Phase11WebAppFactory"/> class fixture, per-test unique corrId via
/// <c>X-Correlation-Id</c>, <c>PollEsForLog</c> on
/// <see cref="EsIndexNames.CorrelationIdFieldPath"/>). The Schema POST is swapped for a minimal
/// known-good orchestration graph seeded via the public entity endpoints, then a Start. Per
/// CHECKER WARNING #7 this asserts on the round-tripped correlationId, NOT on the version string.
/// </para>
/// </summary>
[Trait("Phase", "15")]
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]
[Collection("Observability")]
public sealed class OrchestrationLogsE2ETests : IClassFixture<OrchestrationLogsE2ETests.RealBrokerLogsWebAppFactory>
{
    private readonly RealBrokerLogsWebAppFactory _factory;

    public OrchestrationLogsE2ETests(RealBrokerLogsWebAppFactory factory) => _factory = factory;

    /// <summary>
    /// Phase 20 (20-04 fix): this E2E asserts the WebApi's OWN request-lifecycle log (spanning the
    /// Redis projection write) round-trips its <c>X-Correlation-Id</c> into Elasticsearch — it does
    /// NOT assert orchestrator consumption. But the Start POST publishes <c>StartOrchestration</c> on
    /// the bus, and the base <see cref="Phase11WebAppFactory"/> points the broker at the compose-DNS
    /// host <c>rabbitmq:5672</c> (unresolvable from the host test process) → the publish hangs ~1m40s
    /// → TestHost aborts the request. Unlike the pure feature facts, this test can't move to the
    /// in-memory <c>HarnessWebAppFactory</c> (that lives on Phase8 and would lose Phase11's OTLP→ES +
    /// 1s metric-export wiring this E2E needs). So we keep Phase11's full observability wiring and
    /// only re-point the broker at the host-mapped port 5673 — mirroring the 20-03
    /// <c>CorrelationPropagationE2ETests.RealStackWebAppFactory</c> broker override. Tagged
    /// <c>Category=RealStack</c> to match its now-real-broker dependency.
    /// </summary>
    public sealed class RealBrokerLogsWebAppFactory : Phase11WebAppFactory
    {
        private readonly Dictionary<string, string?> _prior = new();

        public RealBrokerLogsWebAppFactory()
        {
            try
            {
                // Broker only: reach the host-mapped broker (compose maps host 5673 -> container 5672)
                // so the Start publish completes instead of hanging on the unresolvable rabbitmq:5672.
                // Postgres/Redis stay on Phase11's throwaway fixtures — this test reads its OWN logs
                // from ES, not orchestrator state, so it needs no host DB/Redis. OTLP is already
                // pinned to http://localhost:4317 by the Phase11WebAppFactory ctor.
                Set("RabbitMq__Host", "localhost");
                Set("RabbitMq__Port", "5673");
                Set("RabbitMq__Username", "guest");
                Set("RabbitMq__Password", "guest");
            }
            catch
            {
                Restore();
                throw;
            }
        }

        public override async ValueTask DisposeAsync()
        {
            try { Restore(); } finally { await base.DisposeAsync(); }
        }

        private void Set(string key, string? value)
        {
            _prior[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }

        private void Restore()
        {
            foreach (var kv in _prior)
            {
                Environment.SetEnvironmentVariable(kv.Key, kv.Value);
            }
        }
    }

    // ---- HTTP seeding helpers (Processor → Step → Workflow) — minimal known-good graph ----

    private static async Task<Guid> SeedProcessorAsync(HttpClient client, CancellationToken ct)
    {
        var dto = new ProcessorCreateDto(
            Name: $"ole-proc-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            SourceHash: HashHelpers.RandomSha256Hex(),
            InputSchemaId: null,
            OutputSchemaId: null,
            ConfigSchemaId: null);
        var resp = await client.PostAsJsonAsync("/api/v1/processors", dto, ct);
        resp.EnsureSuccessStatusCode();
        var proc = await resp.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);
        return proc!.Id;
    }

    private static async Task<Guid> SeedStepAsync(HttpClient client, Guid processorId, CancellationToken ct)
    {
        var dto = new StepCreateDto(
            Name: $"ole-step-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            ProcessorId: processorId,
            NextStepIds: null,
            EntryCondition: StepEntryCondition.Always);
        var resp = await client.PostAsJsonAsync("/api/v1/steps", dto, ct);
        resp.EnsureSuccessStatusCode();
        var step = await resp.Content.ReadFromJsonAsync<StepReadDto>(cancellationToken: ct);
        return step!.Id;
    }

    private static async Task<Guid> SeedWorkflowAsync(HttpClient client, List<Guid> entryStepIds, CancellationToken ct)
    {
        var dto = new WorkflowCreateDto(
            Name: $"ole-wf-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            EntryStepIds: entryStepIds,
            AssignmentIds: null,
            CronExpression: null);
        var resp = await client.PostAsJsonAsync("/api/v1/workflows", dto, ct);
        resp.EnsureSuccessStatusCode();
        var wf = await resp.Content.ReadFromJsonAsync<WorkflowReadDto>(cancellationToken: ct);
        return wf!.Id;
    }

    [Fact]
    public async Task Start_Surfaces_RedisWrite_LogRecord_In_Elasticsearch_With_CorrelationId()
    {
        var ct = TestContext.Current.CancellationToken;

        // Per-test unique correlation ID — Pitfall 5 isolation discipline; T-15-17 mitigation.
        var corrId = $"{Guid.NewGuid():N}";

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", corrId);

        // Seed a minimal known-good single-step workflow (no schemas → no schema-edge/payload
        // gate to satisfy; a Start projects the root + one per-step key into Redis).
        var procId = await SeedProcessorAsync(client, ct);
        var stepId = await SeedStepAsync(client, procId, ct);
        var wfId = await SeedWorkflowAsync(client, new List<Guid> { stepId }, ct);

        // Drive the orchestration Start — this is the request whose lifecycle log(s), including
        // the Redis L1→L2 projection-write, must carry corrId through to Elasticsearch.
        var resp = await client.PostAsJsonAsync("/api/v1/orchestration/start", new List<Guid> { wfId }, ct);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        // Sanity-check the response echo header — Phase 4 OBSERV-11 invariant.
        Assert.Equal(corrId, resp.Headers.GetValues("X-Correlation-Id").Single());

        // Poll ES for a log doc whose correlation-id matches. The field path shape was verified
        // Wave 0 (Plan 11-06) — EsIndexNames.CorrelationIdFieldPath == "attributes.CorrelationId".
        using var esClient = new ElasticsearchTestClient();

        var queryBody = $$"""
          {
            "size": 10,
            "query": { "term": { "{{EsIndexNames.CorrelationIdFieldPath}}": "{{corrId}}" } }
          }
          """;

        var hit = await esClient.PollEsForLog(queryBody, timeoutMs: 30_000, ct: ct);

        // OBSERV-REDIS-02 — the Start's request-lifecycle log (spanning the Redis projection
        // write) reached ES carrying the inbound X-Correlation-Id via the Phase 4 MEL scope.
        Assert.NotNull(hit);

        var rawJson = hit!.Value.GetRawText();

        // Defensive — confirm the correlation id is the value the matched doc actually carries.
        // CHECKER WARNING #7 — assert on the round-tripped correlationId, NOT on the version
        // string (version is incidental; hardcoding it would break on any future version bump).
        Assert.Contains(corrId, rawJson);
    }
}
