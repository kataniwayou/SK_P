using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BaseApi.Service.Features.Orchestration.Projection;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using BaseApi.Tests.Observability.Helpers;
using BaseApi.Tests.TestHelpers;
using Messaging.Contracts.Projections;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// CAPSTONE cross-process correlation proof (CORR-04 / TEST-RMQ-02) — the single
/// stack-dependent test of the milestone, and the automated form of the Phase 19 human-UAT
/// correlation item.
/// </summary>
/// <remarks>
/// <para>
/// Chain proven (D-06 / D-07 / D-08): a real <c>POST /api/v1/orchestration/start</c> carrying an
/// <c>X-Correlation-Id</c> header drives an in-process WebApi pointed at the REAL host stack —
/// host RabbitMQ (<c>localhost:5673</c>), host Redis (<c>localhost:6380</c>), host Postgres
/// (<c>localhost:5433</c>) and the host otel-collector (<c>localhost:4317</c>). The WebApi:
/// </para>
/// <list type="number">
///   <item>seeds + projects the workflow's L2 root into the SAME host Redis the orchestrator reads
///   (the Start path writes the flat L2 root <c>skp:{id}</c>) so the orchestrator reaches the SUCCESS seam;</item>
///   <item>mints a fresh body <c>CorrelationId</c> Guid (G1) and logs the D-07 line
///   <c>"Published StartOrchestration CorrelationId={G1}"</c> (OrchestrationService.cs:171);</item>
///   <item>publishes <c>StartOrchestration { CorrelationId = G1 }</c> to the real broker.</item>
/// </list>
/// <para>
/// The Orchestrator CONTAINER (consuming from the same broker, reading the same Redis) opens a
/// <c>"CorrelationId"=G1</c> log scope from the inbound message body (BaseConsole.Core inbound
/// filter) and logs the seam <c>"Start reload for WorkflowId={WorkflowId}"</c>
/// (StartOrchestrationConsumer.cs:35 — renamed from the pre-24.1 "Scheduler job start (seam)" by
/// feat(24.1) ORCH-START-RELOAD-01). Both logs flow via otel-collector to Elasticsearch
/// (<c>logs-generic.otel-default</c>) under <c>attributes.CorrelationId</c>.
/// </para>
/// <para>
/// The test reads the orchestrator SEAM doc, extracts its body Guid G1, asserts G1 differs from the
/// HTTP <c>X-Correlation-Id</c> (per-stage handoff, NOT one value across all hops), then reads the
/// WebApi PUBLISHED doc under the SAME G1 — proving equality across the per-stage boundary. The
/// shared <c>term</c> on <c>attributes.CorrelationId</c> = G1 establishes equality; the
/// <c>Assert.NotEqual(httpCorr, G1)</c> establishes distinctness.
/// </para>
/// <para>
/// Tagged <c>Category=RealStack</c> so the hermetic quick-run filter (<c>Category!=RealStack</c>)
/// excludes it; placed in the <c>Observability</c> collection so its env-var-in-ctor host overrides
/// are serialized with the other observability E2E fixtures (no env-var nesting race — T-20-08).
/// </para>
/// </remarks>
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]
[Collection("Observability")]
public sealed class CorrelationPropagationE2ETests
{
    private const string SeamMessage = "Start reload for WorkflowId=";
    private const string PublishedMessage = "Published StartOrchestration";

    // otel/log export is async; tolerate flush + ingest latency with a generous poll budget.
    private const int PollTimeoutMs = 90_000;

    [Fact]
    public async Task BodyMintedCorrelationId_PropagatesAcrossStages_IntoBothEsDocs()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var factory = new RealStackWebAppFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        // Per-run HTTP-stage id; MUST differ from the server-minted body Guid (per-stage handoff).
        var httpCorr = $"{Guid.NewGuid():N}";
        client.DefaultRequestHeaders.Add("X-Correlation-Id", httpCorr);

        // Seed a minimal known-good single-step workflow against the host Postgres; the Start path
        // then projects the L2 root into the host Redis the orchestrator container reads.
        var procId = await SeedProcessorAsync(client, ct);
        var stepId = await SeedStepAsync(client, procId, ct);
        var wfId = await SeedWorkflowAsync(client, new List<Guid> { stepId }, ct);

        // PROC-LIVE-01 + PROC-NOCREATE-01 (Phase 22): the Start path now runs a processor-liveness gate
        // before UpsertAsync, and the writer no longer creates the processor key — it is owned by external
        // self-registration. Seed the participating processor LIVE directly into the HOST Redis (the same
        // instance the orchestrator reads) so the real Start reaches the success seam.
        await factory.SeedHostProcessorLiveAsync(procId, ct);

        // Start endpoint takes a BARE List<Guid> body (OrchestrationController.Start). 204 on success
        // means the L2 root was written, the body Guid minted + D-07 logged, and the publish succeeded.
        var startResp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start", new List<Guid> { wfId }, ct);
        Assert.Equal(HttpStatusCode.NoContent, startResp.StatusCode);
        // The HTTP-stage id is echoed back (OBSERV-11) — confirm it is the value we will assert AGAINST.
        Assert.Equal(httpCorr, startResp.Headers.GetValues("X-Correlation-Id").Single());

        // TEST-REDIS-04 net-zero teardown: this real Start projected three L2 keys under the
        // production "skp:" prefix into the host Redis. RealStackWebAppFactory runs with
        // skipRedisFixture=true, so the base Phase8WebAppFactory SCAN+DEL cleanup does NOT cover the
        // skp: prefix — register the exact keys for deletion in the factory's DisposeAsync (fires via
        // `await using` even if an assertion below throws). Key shapes per HappyPathE2EFacts:
        // root skp:{wfId}, per-step skp:{wfId}:{stepId}, per-processor skp:{procId}.
        // Phase 22 (T-22-15): the Start also SADDs wfId into the shared skp: parent-index SET. Register
        // it for a targeted SREM on teardown (NOT a KeyDelete of skp: — that would wipe sibling members)
        // so the close-gate redis-cli --scan SHA returns to its empty BEFORE state.
        factory.ParentIndexMembersToSrem.Add(wfId.ToString("D"));
        factory.L2KeysToCleanup.Add($"skp:{wfId}");
        factory.L2KeysToCleanup.Add($"skp:{wfId}:{stepId}");
        factory.L2KeysToCleanup.Add($"skp:{procId}");

        using var es = new ElasticsearchTestClient();

        // 1. Find the Orchestrator SEAM doc and read back its body-minted Guid (G1).
        //    Discover by the WorkflowId the test itself seeded (a clean term-queryable attribute) on
        //    the ORCHESTRATOR service, requiring the doc to carry attributes.CorrelationId (the
        //    inbound filter's body scope). Since feat(24.1)'s conditionless reload, the success path
        //    emits MORE than one correlated orchestrator log for the wfId (the seam, then the
        //    HydrateAndScheduleAsync business log — e.g. the null-cron "skipping hydration" line this
        //    test's single-step workflow triggers), so a bare WorkflowId+CorrelationId match is NOT
        //    unambiguous and newest-first would return the business log, not the seam. Pin the seam
        //    deterministically via a term on its structured template attributes.{OriginalFormat} =
        //    "Start reload for WorkflowId={WorkflowId}" (verified the unique discriminator in ES).
        //    NOTE: we do NOT query the "body" field — otel maps the message under the nested
        //    "body.text" object, which is not phrase-searchable; the proven precedent
        //    (OrchestrationLogsE2ETests) queries attributes via `term`. The distinct seam STRING is
        //    asserted in C# below via GetRawText() (which includes body.text + {OriginalFormat}),
        //    keeping the Stop-seam-cannot-be-conflated guarantee.
        var seamQuery = $$"""
          {
            "size": 5,
            "sort": [ { "@timestamp": { "order": "desc" } } ],
            "query": {
              "bool": {
                "must": [
                  { "term": { "attributes.WorkflowId": "{{wfId}}" } },
                  { "term": { "resource.attributes.service.name": "orchestrator" } },
                  { "exists": { "field": "{{EsIndexNames.CorrelationIdFieldPath}}" } },
                  { "term": { "attributes.{OriginalFormat}": "Start reload for WorkflowId={WorkflowId}" } }
                ]
              }
            }
          }
          """;
        var seam = await es.PollEsForLog(seamQuery, timeoutMs: PollTimeoutMs, ct: ct);
        Assert.NotNull(seam);

        // Confirm the matched orchestrator doc is the SUCCESS seam (distinct from the Stop seam and
        // the "absent from L2" business-failure warning) — semantic guard on the message text.
        Assert.Contains(SeamMessage, seam!.Value.GetRawText());

        var bodyGuid = ExtractCorrelationId(seam.Value);
        Assert.False(
            string.IsNullOrEmpty(bodyGuid),
            "the orchestrator seam doc must carry attributes.CorrelationId (the body-minted Guid)");
        Assert.True(Guid.TryParse(bodyGuid, out _), "the body CorrelationId must be a minted Guid");

        // Per-stage handoff: the body Guid is freshly minted server-side, NOT the HTTP header id.
        Assert.NotEqual(httpCorr, bodyGuid);

        // 2. Find the WebApi PUBLISHED (D-07) doc under the SAME body Guid: a term on
        //    attributes.CorrelationId DIRECTLY (Pitfall 4 — never .keyword), scoped to the WebApi
        //    service (sk-api) so the orchestrator's own G1-scoped seam doc cannot satisfy it. Both
        //    docs surfacing under the SAME G1 proves equality across the stage boundary. The publish
        //    message text is asserted in C# (GetRawText covers body.text + {OriginalFormat}) rather
        //    than as an ES `match_phrase` on the nested "body" object (not phrase-searchable).
        var publishedQuery = $$"""
          {
            "size": 5,
            "query": {
              "bool": {
                "must": [
                  { "term": { "{{EsIndexNames.CorrelationIdFieldPath}}": "{{bodyGuid}}" } },
                  { "term": { "resource.attributes.service.name": "sk-api" } }
                ]
              }
            }
          }
          """;
        var published = await es.PollEsForLog(publishedQuery, timeoutMs: PollTimeoutMs, ct: ct);
        Assert.NotNull(published);

        // EQUALITY proof: the publishedQuery filters `term attributes.CorrelationId == bodyGuid`, so a
        // returned hit cryptographically carries the SAME G1 the orchestrator seam doc carried — that
        // term match IS the cross-stage equality (mirrors the proven OrchestrationLogsE2ETests pattern,
        // which asserts NotNull + Contains rather than re-extracting). Confirm the body Guid and the
        // publish message text are both present in the matched doc's raw source.
        var publishedRaw = published!.Value.GetRawText();
        Assert.Contains(bodyGuid!, publishedRaw);
        Assert.Contains(PublishedMessage, publishedRaw);

        // NET-ZERO-31 (Phase 31.1): stop the workflow so its self-rescheduling cron fire ceases — left
        // running it mints a fresh per-fire skp:flag:{H} every minute, churning the close-gate redis
        // --scan name-set. Best-effort: a stop hiccup must not fail an otherwise-green E2E assertion.
        try { await client.PostAsJsonAsync("/api/v1/orchestration/stop", new List<Guid> { wfId }, ct); }
        catch { /* best-effort net-zero teardown */ }
    }

    /// <summary>
    /// Extracts <c>attributes.CorrelationId</c> from an ES log <c>_source</c>. The otel-collector
    /// maps structured log properties under the nested <c>attributes</c> object (Wave 0 finding,
    /// <see cref="EsIndexNames.CorrelationIdFieldPath"/>); a defensive flat-key fallback is included.
    /// Returns null if absent.
    /// </summary>
    private static string? ExtractCorrelationId(JsonElement hitOrSource)
    {
        // PollEsForLog returns the whole ES hit ({_index,_id,_source,sort}); descend into _source
        // when present so we read the real attributes object rather than the hit envelope.
        var root = hitOrSource;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("_source", out var src))
        {
            root = src;
        }

        if (root.TryGetProperty("attributes", out var attrs) &&
            attrs.ValueKind == JsonValueKind.Object &&
            attrs.TryGetProperty("CorrelationId", out var corr))
        {
            return ReadStringScalar(corr);
        }

        // Defensive flat-key fallback (some otel mapping modes flatten dotted keys).
        if (root.TryGetProperty("attributes.CorrelationId", out var flat))
        {
            return ReadStringScalar(flat);
        }

        return null;
    }

    /// <summary>
    /// Reads a single string from an ES field that may be a String or (when a property is stamped by
    /// more than one source — e.g. a structured log property AND a log scope) an Array. Returns the
    /// first array element, avoiding the concatenated/duplicated value a naive ToString() produces.
    /// </summary>
    private static string? ReadStringScalar(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Array => el.GetArrayLength() > 0 ? el[0].GetString() : null,
        _ => el.ToString(),
    };

    // ---- HTTP seeding helpers (Processor → Step → Workflow) — mirrors OrchestrationLogsE2ETests ----

    private static async Task<Guid> SeedProcessorAsync(HttpClient client, CancellationToken ct)
    {
        var dto = new ProcessorCreateDto(
            Name: $"corr-proc-{Guid.NewGuid():N}",
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
            Name: $"corr-step-{Guid.NewGuid():N}",
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
            Name: $"corr-wf-{Guid.NewGuid():N}",
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

    /// <summary>
    /// Points the in-process WebApi at the REAL host stack. As the single exception to the in-memory
    /// default (D-06), the broker / Redis / Postgres / otel endpoints are the host-mapped dev
    /// services. The broker host+port and the OTLP endpoint are bound at MassTransit / OpenTelemetry
    /// registration time (captured by value), so they MUST be set via env vars in the ctor BEFORE the
    /// base <c>WebApplicationFactory&lt;TEntryPoint&gt;</c> ctor runs — <c>ConfigureAppConfiguration</c>
    /// is too late (the documented DEVIATION pattern from <c>HealthEndpointsTests</c>). Every prior
    /// value is restored in <see cref="DisposeAsync"/>; the ctor restores on throw.
    /// <para>
    /// Derives from <see cref="Composition.Phase8WebAppFactory"/> so the host boots with real Postgres
    /// migration + Redis wiring; the env-var overrides REPLACE the throwaway-DB connection strings the
    /// base would otherwise inject (env-var config source wins over the base's AddInMemoryCollection
    /// because Program.cs reads the env-var-derived value, and the broker/otel keys the base never
    /// sets are sourced solely from these env vars).
    /// </para>
    /// </summary>
    private sealed class RealStackWebAppFactory : Composition.Phase8WebAppFactory
    {
        private readonly Dictionary<string, string?> _prior = new();

        public RealStackWebAppFactory()
            : base(
                skipPostgresFixture: true,
                connectionStringOverride: HostPostgres,
                skipRedisFixture: true,
                redisConnectionStringOverride: HostRedis)
        {
            try
            {
                // Broker: reach the host-mapped broker (compose maps host 5673 -> container 5672).
                Set("RabbitMq__Host", "localhost");
                Set("RabbitMq__Port", "5673");
                Set("RabbitMq__Username", "guest");
                Set("RabbitMq__Password", "guest");

                // Redis + Postgres: the SAME host-mapped instances the orchestrator container reads
                // (compose redis 6380->6379; postgres 5433->5432). Set as env vars too so any config
                // read that bypasses the base AddInMemoryCollection still resolves the host endpoints.
                Set("ConnectionStrings__Redis", HostRedis);
                Set("ConnectionStrings__Postgres", HostPostgres);

                // OTLP: export to the SAME host otel-collector -> ES the orchestrator uses (base
                // appsettings points at otel-collector:4317, unresolvable from the host). The
                // exporter is wired bare (AddOtlpExporter() with no endpoint), so it reads the
                // STANDARD OTEL_EXPORTER_OTLP_ENDPOINT env var — NOT the OpenTelemetry:Endpoint
                // appsettings key (dead config, consumed nowhere). Matches Phase11WebAppFactory.
                Set("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317");
            }
            catch
            {
                Restore();
                throw;
            }
        }

        /// <summary>
        /// Phase 22: seeds a processor's self-registered L2 liveness entry (<c>skp:{procId}</c>) LIVE
        /// directly into the HOST Redis (the same instance the orchestrator container reads). The Start
        /// path's processor-liveness gate (Plan 04, runs before UpsertAsync) requires it, and the writer
        /// no longer creates it (Plan 03 / PROC-NOCREATE-01). interval is SECONDS (now + 300*2 &gt; now).
        /// The key is registered for net-zero teardown so the close-gate <c>redis-cli --scan</c> SHA holds.
        /// </summary>
        public async Task SeedHostProcessorLiveAsync(Guid procId, CancellationToken ct)
        {
            await using var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
            var projection = new ProcessorProjection(
                null, null, new LivenessProjection(DateTime.UtcNow, 300, "Live"));
            await mux.GetDatabase().StringSetAsync(
                L2ProjectionKeys.Processor(procId),
                JsonSerializer.Serialize(projection));
            L2KeysToCleanup.Add(L2ProjectionKeys.Processor(procId));
        }

        private const string HostRedis = "localhost:6380,abortConnect=false,connectTimeout=5000";
        private const string HostPostgres =
            "Host=localhost;Port=5433;Database=stepsdb;Username=postgres;Password=postgres;Maximum Pool Size=20;Timeout=15";

        private void Set(string key, string value)
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

        /// <summary>
        /// L2 keys (production "skp:" prefix) the test registers for deletion on teardown — populated
        /// AFTER the real Start projects them. This factory runs with skipRedisFixture=true, so the
        /// base <see cref="Composition.Phase8WebAppFactory"/> SCAN+DEL cleanup does NOT cover the skp:
        /// prefix; without this, the close gate's TEST-REDIS-04 redis-cli --scan BEFORE==AFTER
        /// net-zero invariant fails. Drained in <see cref="DisposeAsync"/> (runs via `await using`
        /// even if a mid-test assertion throws — true teardown semantics).
        /// </summary>
        public List<RedisKey> L2KeysToCleanup { get; } = new();

        /// <summary>
        /// Phase 22 (T-22-15): shared skp: parent-index members this test SADDed (via a passing Start)
        /// that must be SREMed — NOT KeyDeleted — on teardown so sibling members survive and the
        /// close-gate redis-cli --scan SHA returns to its empty BEFORE state.
        /// </summary>
        public List<RedisValue> ParentIndexMembersToSrem { get; } = new();

        public override async ValueTask DisposeAsync()
        {
            if (L2KeysToCleanup.Count > 0 || ParentIndexMembersToSrem.Count > 0)
            {
                await using var cleanupMux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
                var db = cleanupMux.GetDatabase();
                if (L2KeysToCleanup.Count > 0)
                {
                    await db.KeyDeleteAsync(L2KeysToCleanup.ToArray());
                }
                if (ParentIndexMembersToSrem.Count > 0)
                {
                    await db.SetRemoveAsync(L2ProjectionKeys.ParentIndex(), ParentIndexMembersToSrem.ToArray());
                }
            }
            Restore();
            await base.DisposeAsync();
        }
    }
}
