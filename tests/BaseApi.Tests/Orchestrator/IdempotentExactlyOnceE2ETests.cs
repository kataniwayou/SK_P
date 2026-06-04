using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using BaseApi.Tests.Observability.Helpers;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Hashing;
using Messaging.Contracts.Projections;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// CAPSTONE real-stack exactly-once-EFFECT proof (req-8 / SPEC Tier-3) — the milestone's only
/// end-to-end guarantee that the deterministic-identity + effect-first-dedup protocol actually
/// COLLAPSES a duplicate against real containers. It is the live inverse of the historical
/// <c>StepB4 ×2</c> over-execution bug: a redelivered dispatch carrying the SAME content-addressed
/// identity <c>H</c> produces ZERO extra downstream effect.
/// </summary>
/// <remarks>
/// <para>
/// CLONED from <see cref="SampleRoundTripE2ETests"/> (it REUSES the genuine embedded SourceHash
/// reflection, the truthful <c>PollForHealthyLivenessAsync</c> liveness gate, the <c>PollEsForLog</c>
/// downstream-effect proof, the <c>skp:data:*</c> scan + net-zero teardown, and the
/// <c>RealStackWebAppFactory</c> host-stack overrides), then diverges in three load-bearing ways:
/// </para>
/// <list type="number">
///   <item>
///     <b>Merge topology (Open Q3).</b> Instead of one entry step, it seeds TWO entry steps
///     (<c>StepA1</c>, <c>StepA2</c>) whose <c>NextStepIds</c> BOTH point to a single successor merge
///     step <c>StepB</c> (<c>EntryCondition = Always</c>). The workflow lists both A-steps as entry
///     steps. There is NO named StepB4 fixture — the topology is built test-side via the existing
///     Step/Workflow seeding helpers.
///   </item>
///   <item>
///     <b>Induced duplicate (D-11).</b> After driving the workflow live, the test SENDS the SAME
///     <c>EntryStepDispatch</c> TWICE to <c>queue:{procId:D}</c> — reconstructed with the SAME
///     correlationId/workflowId/stepId/processorId/EntryId so its deterministic <c>H</c> is IDENTICAL
///     (a simulated broker redelivery, the lighter mechanism the CONTEXT chose over broker fault
///     injection). The processor's effect-first <c>flag[dispatch.H]</c> gate drops the second delivery.
///   </item>
///   <item>
///     <b>Zero downstream duplication.</b> Via <c>PollEsForLog</c> + a parallel hit-count probe, the
///     test asserts that per <c>CorrelationId</c> the processor-side downstream-effect log
///     ("step output written content-addressed") appears EXACTLY the expected per-fire count — once
///     per distinct dispatch identity, NOT twice-from-the-duplicate. The duplicate (same <c>H</c>) is
///     collapsed by the effect-first dedup gate, so it emits no extra effect.
///   </item>
/// </list>
/// <para>
/// Net-zero teardown (D-12): the run's <c>skp:data:{64hex}</c> AND <c>skp:flag:{64hex}</c> keys are
/// scanned + registered into the factory's <c>L2KeysToCleanup</c> (drained in <c>DisposeAsync</c>) so
/// the close-gate triple-SHA <c>redis-cli --scan</c> BEFORE==AFTER invariant holds across BOTH new L2
/// namespaces. The steady-state <c>skp:{procId:D}</c> liveness key is LEFT (the live container keeps it
/// fresh). Tagged <c>Category=RealStack</c> so the hermetic filter excludes it.
/// </para>
/// </remarks>
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]
[Collection("Observability")]
public sealed class IdempotentExactlyOnceE2ETests
{
    // The processor-side content-addressed-write log line (EntryStepDispatchConsumer step 3) — the
    // downstream EFFECT marker. One per distinct dispatch identity; the deduped duplicate adds none.
    private const string DownstreamEffectMessage = "step output written content-addressed";

    // The live processor-sample container resolves identity + binds + MarkHealthy after the DB row is
    // seeded (compose start_period + identity-resolve latency); allow a generous budget.
    private const int LivenessPollTimeoutMs = 90_000;

    // The orchestrator fires the dispatch at the next "* * * * *" occurrence (top of the next minute),
    // then the processor round-trips and writes output; allow > 60s plus round-trip slack.
    private const int OutputPollTimeoutMs = 120_000;

    // otel/log export is async; tolerate flush + ingest latency on the downstream-effect ES proof.
    private const int EsPollTimeoutMs = 120_000;

    [Fact]
    public async Task MergeTopology_InducedRedelivery_ProducesExactlyOnceDownstreamEffect()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var factory = new RealStackWebAppFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        // D-08: read the GENUINE embedded SourceHash off the BUILT Processor.Sample assembly — the same
        // way AssemblyMetadataSourceHashProvider does at runtime. NOT synthetic, NOT recomputed.
        var hash = typeof(global::Processor.Sample.SampleProcessor).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "SourceHash").Value!;

        // Register the Processor DB row with THAT genuine hash + null schemas (D-05). The live container
        // resolves THIS id by GetProcessorBySourceHash(hash) (idempotent GET-or-create across runs).
        var procId = await SeedProcessorAsync(client, hash, ct);

        // ---- Merge topology (Open Q3): StepA1, StepA2 -> StepB ----
        // StepB is the single successor; both A-steps carry NextStepIds -> StepB. StepB must exist first
        // so the A-steps can reference it. All three run on the same processor-sample (one live container).
        var stepBId = await SeedStepAsync(client, procId, name: "StepB", nextStepIds: null, ct);
        var stepA1Id = await SeedStepAsync(
            client, procId, name: "StepA1", nextStepIds: new List<Guid> { stepBId }, ct);
        var stepA2Id = await SeedStepAsync(
            client, procId, name: "StepA2", nextStepIds: new List<Guid> { stepBId }, ct);

        // The workflow lists BOTH A-steps as entry steps; the * * * * * cron drives the fire so the
        // orchestrator's one-shot Quartz job actually dispatches (a null-cron workflow is a business-skip).
        var wfId = await SeedWorkflowAsync(
            client, new List<Guid> { stepA1Id, stepA2Id }, cron: "* * * * *", ct);

        // Pitfall 3 / D-07: POLL host Redis for the REAL container's skp:{procId:D} Healthy heartbeat —
        // only proceed once it is fresh (the live container resolved identity + bound + MarkHealthy).
        await PollForHealthyLivenessAsync(procId, ct);

        // Snapshot skp:data:* + skp:flag:* BEFORE Start so we detect the round-trip's fresh keys and can
        // register BOTH new namespaces for net-zero teardown (D-12).
        var dataKeysBefore = ScanKeys("data:*");
        var flagKeysBefore = ScanKeys("flag:*");

        // Drive Start. 204 means the L2 root was written, the body Guid minted + published, and the
        // processor-liveness gate PASSED truthfully (the live container's heartbeat is fresh).
        var startResp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start", new List<Guid> { wfId }, ct);
        Assert.Equal(HttpStatusCode.NoContent, startResp.StatusCode);

        // Register the L2 root/step keys + parent-index member the Start created for net-zero teardown.
        factory.ParentIndexMembersToSrem.Add(wfId.ToString("D"));
        factory.L2KeysToCleanup.Add($"skp:{wfId}");
        factory.L2KeysToCleanup.Add($"skp:{wfId}:{stepA1Id}");
        factory.L2KeysToCleanup.Add($"skp:{wfId}:{stepA2Id}");
        factory.L2KeysToCleanup.Add($"skp:{wfId}:{stepBId}");

        // ---- Live round-trip lands: a NEW skp:data:* key appears ----
        // The orchestrator fires at the next minute; the live processor consumes each entry dispatch,
        // runs ProcessAsync, and writes content-addressed output to skp:data:{blobHash}. Poll for it.
        var newDataKey = await PollForNewKeyAsync("data:*", dataKeysBefore, ct);
        Assert.NotNull(newDataKey);

        // ---- Induce a duplicate (D-11): re-send the SAME EntryStepDispatch identity TWICE ----
        // Reconstruct the entry-step dispatch the orchestrator's WorkflowFireJob sends for StepA1: a
        // fresh per-fire correlationId, EntryId = EntryEntryId(corr, stepId) (req-2), H = ComputeH(...)
        // over the same five identity fields. The two sends carry the IDENTICAL H — so the processor's
        // effect-first flag[dispatch.H] gate collapses the second to a drop+ack with NO extra effect.
        var dupCorrelationId = NewId.NextGuid();
        var dupEntryId = MessageIdentity.EntryEntryId(dupCorrelationId, stepA1Id);
        var dupH = MessageIdentity.ComputeH(dupCorrelationId, wfId, stepA1Id, procId, dupEntryId);

        var dispatch = new EntryStepDispatch(wfId, stepA1Id, procId, Payload: "\"StepA1\"")
        {
            CorrelationId = dupCorrelationId,
            ExecutionId = Guid.Empty,
            EntryId = dupEntryId,
            H = dupH,
        };

        // The test plays the SENDER role: production StepDispatcher pre-writes flag[H]="Pending" EXACTLY
        // ONCE before its single Send (so the consumer's When.Exists Pending->Ack flip has a key to flip).
        // A genuine broker REDELIVERY re-delivers the same already-enqueued message WITHOUT re-running the
        // sender pre-write — so we pre-write once here, NOT per send. (Re-writing it before delivery 2
        // would reset the gate Ack->Pending and re-arm it, which a real redelivery never does — letting
        // the duplicate leak. That faithful single-pre-write is the crux of the exactly-once proof.)
        await PrewriteFlagPendingAsync(dupH);

        await SendDispatchAsync(procId, dispatch, ct);  // delivery 1 — produces the effect, flips flag->Ack

        // Wait until delivery 1 has produced its effect and flipped flag[H] Pending->Ack before
        // redelivering, so delivery 2 deterministically observes "Ack" (a true post-effect redelivery,
        // not an in-flight race where both deliveries clear the gate before either flips it).
        await PollForFlagAckAsync(dupH, ct);

        await SendDispatchAsync(procId, dispatch, ct);  // delivery 2 — SAME H, flag==Ack -> dropped

        // ---- Assert ZERO downstream duplication for the induced redelivery ----
        // The processor emits the content-addressed-write log ONCE per produced effect, scoped to
        // attributes.CorrelationId. With the duplicate collapsed, EXACTLY ONE downstream effect appears
        // for dupCorrelationId (the StepB4-x2 inverse: NOT two). Poll until the effect is visible, then
        // assert the hit COUNT == 1 (the duplicate added none).
        using var es = new ElasticsearchTestClient();

        // Scope the effect probe to the DIRECTLY-REDELIVERED step (StepA1) under dupCorrelationId. StepA1's
        // result fans out to its successor StepB (the merge topology), which ALSO logs an effect under the
        // SAME correlationId — so a correlationId-only count would conflate the redelivered step with its
        // downstream successor. Scoping to attributes.StepId isolates the exactly-once proof to the
        // redelivered identity itself: StepA1 logs its content-addressed write EXACTLY ONCE despite the two
        // deliveries (the duplicate, same H, is collapsed by the effect-first gate).
        var effectQuery = BuildEffectQuery(dupCorrelationId, stepA1Id);

        var firstEffect = await es.PollEsForLog(effectQuery, timeoutMs: EsPollTimeoutMs, ct: ct);
        Assert.NotNull(firstEffect);
        Assert.Contains(DownstreamEffectMessage, firstEffect!.Value.GetRawText());

        // One StepA1 identity (same H) re-delivered twice -> EXACTLY ONE StepA1 effect. A second hit here
        // would be the duplicate leaking through — the live inverse of the historical StepB4 x2 bug.
        var effectCount = await CountEsHitsAsync(effectQuery, ct);
        Assert.Equal(1, effectCount);

        // ---- Net-zero teardown (D-12): register the run's new skp:data:* AND skp:flag:* keys ----
        // Scan BOTH namespaces post-run and register every key NOT present before Start. The processor's
        // content-addressed data writes, the manifest, and the flag[H] dedup state must all drain so the
        // close-gate triple-SHA redis --scan BEFORE==AFTER holds across both new namespaces.
        foreach (var key in ScanKeys("data:*"))
            if (!dataKeysBefore.Contains(key))
                factory.L2KeysToCleanup.Add(key);
        foreach (var key in ScanKeys("flag:*"))
            if (!flagKeysBefore.Contains(key))
                factory.L2KeysToCleanup.Add(key);
    }

    // ---- Induced-duplicate sender (D-11): mirror StepDispatcher's symmetric flag pre-write + Send ----

    // SENDER pre-write (D-06, symmetric inbound analog of the processor's outbound seed): write
    // flag[H]="Pending" so the consumer's effect-first When.Exists flip Pending->Ack has a key to flip.
    // Production StepDispatcher does this EXACTLY ONCE before its single Send; a broker redelivery never
    // repeats it. Called once here (before delivery 1), NOT per send. TTL bounds the key.
    private static async Task PrewriteFlagPendingAsync(string h)
    {
        await using var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
        await mux.GetDatabase().StringSetAsync(
            L2ProjectionKeys.Flag(h), "Pending", expiry: TimeSpan.FromSeconds(300));
    }

    // Poll host Redis until the consumer flips flag[H] Pending->Ack (effect produced + result sent). Makes
    // the redelivery deterministic: delivery 2 is sent only AFTER delivery 1's effect completed, so it
    // genuinely observes "Ack" and is dropped — rather than racing delivery 1 through the gate.
    private static async Task PollForFlagAckAsync(string h, CancellationToken ct)
    {
        await using var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
        var db = mux.GetDatabase();
        var key = L2ProjectionKeys.Flag(h);

        var deadline = DateTime.UtcNow.AddMilliseconds(OutputPollTimeoutMs);
        var delay = 500;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if ((string?)await db.StringGetAsync(key) == "Ack")
            {
                return; // delivery 1's effect completed and the gate is armed against the redelivery.
            }

            await Task.Delay(Math.Min(delay, 2_000), ct);
            delay = Math.Min(delay * 2, 2_000);
        }

        Assert.Fail(
            $"flag[{h}] never flipped Pending->Ack within {OutputPollTimeoutMs}ms — delivery 1 of the " +
            "induced dispatch did not produce its effect, so the redelivery dedup cannot be proven.");
    }

    // Send (NOT publish) the dispatch to the processor's bare {id:D} dispatch queue — the same queue the
    // live processor-sample container binds + consumes (queue: scheme is sender-only, bare name).
    // IBusControl is not IAsyncDisposable, so Start/Stop are bracketed explicitly in try/finally.
    private static async Task SendDispatchAsync(Guid procId, EntryStepDispatch dispatch, CancellationToken ct)
    {
        var bus = Bus.Factory.CreateUsingRabbitMq(cfg =>
            cfg.Host("localhost", 5673, "/", h => { h.Username("guest"); h.Password("guest"); }));
        await bus.StartAsync(ct);
        try
        {
            var endpoint = await bus.GetSendEndpoint(new Uri($"queue:{procId:D}"));
            await endpoint.Send(dispatch, ct);
        }
        finally
        {
            await bus.StopAsync(ct);
        }
    }

    // ---- ES downstream-effect query + hit count (the zero-duplicate assertion) ----

    private static string BuildEffectQuery(Guid correlationId, Guid stepId) => $$"""
      {
        "size": 20,
        "track_total_hits": true,
        "sort": [ { "@timestamp": { "order": "desc" } } ],
        "query": {
          "bool": {
            "must": [
              { "term": { "{{EsIndexNames.CorrelationIdFieldPath}}": "{{correlationId:D}}" } },
              { "term": { "attributes.StepId": "{{stepId:D}}" } },
              { "term": { "resource.attributes.service.name": "processor-sample" } },
              { "wildcard": { "body.text": "*{{DownstreamEffectMessage}}*" } }
            ]
          }
        }
      }
      """;

    /// <summary>
    /// Counts the downstream-effect hits for the query (hits.total.value). Polls briefly past the first
    /// hit so a (hypothetical) duplicate effect — if the dedup gate ever failed — would also be ingested
    /// before the count is read, keeping the zero-duplicate assertion honest rather than racy.
    /// </summary>
    private static async Task<int> CountEsHitsAsync(string query, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri("http://localhost:9200/") };

        // Give otel a settle window so a leaked duplicate would have been ingested (otherwise a too-early
        // read could report 1 simply because the dup had not arrived yet). The dedup makes the SECOND
        // never emit; this window ensures we are not under-counting a real leak.
        await Task.Delay(8_000, ct);

        var total = 0;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Post, $"{EsIndexNames.LogsDataStream}/_search")
            {
                Content = new StringContent(query, Encoding.UTF8, "application/json"),
            };
            using var resp = await http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("hits", out var outer)
                    && outer.TryGetProperty("total", out var totalEl)
                    && totalEl.TryGetProperty("value", out var valueEl))
                {
                    total = valueEl.GetInt32();
                    if (total > 0) return total;
                }
            }

            await Task.Delay(1_500, ct);
        }

        return total;
    }

    // ---- Liveness poll (Pitfall 3): wait for the REAL container's skp:{procId:D} Healthy heartbeat ----

    private static async Task PollForHealthyLivenessAsync(Guid procId, CancellationToken ct)
    {
        await using var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
        var db = mux.GetDatabase();
        var key = L2ProjectionKeys.Processor(procId);

        var deadline = DateTime.UtcNow.AddMilliseconds(LivenessPollTimeoutMs);
        var delay = 500;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var raw = await db.StringGetAsync(key);
            if (!raw.IsNullOrEmpty)
            {
                var projection = JsonSerializer.Deserialize<ProcessorProjection>(raw!);
                if (projection?.Liveness is { } live)
                {
                    var age = DateTime.UtcNow - live.Timestamp.ToUniversalTime();
                    var staleAfter = TimeSpan.FromSeconds(Math.Max(live.Interval, 1) * 3);
                    if (age <= staleAfter)
                    {
                        return; // the REAL container is Healthy — Start's liveness gate passes truthfully.
                    }
                }
            }

            await Task.Delay(Math.Min(delay, 2_000), ct);
            delay = Math.Min(delay * 2, 2_000);
        }

        Assert.Fail(
            $"The processor-sample container never wrote a fresh Healthy liveness key {key} within " +
            $"{LivenessPollTimeoutMs}ms. Either the container is down, or its embedded SourceHash diverges " +
            $"from the host-built hash registered as the DB row. Ensure the full compose stack incl. " +
            $"a REBUILT processor-sample (new string-EntryId+H wire contract) is up healthy.");
    }

    // ---- Round-trip output poll: a NEW skp:{discriminator} key appears after Start ----

    private static async Task<RedisKey?> PollForNewKeyAsync(
        string discriminator, HashSet<string> before, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(OutputPollTimeoutMs);
        var delay = 1_000;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var key in ScanKeys(discriminator))
            {
                if (!before.Contains(key))
                {
                    return key; // the round-trip's output landed in L2.
                }
            }

            await Task.Delay(Math.Min(delay, 3_000), ct);
            delay = Math.Min(delay * 2, 3_000);
        }

        Assert.Fail(
            $"No new skp:{discriminator} key appeared within {OutputPollTimeoutMs}ms — the live round-trip " +
            $"(orchestrator fire -> dispatch -> ProcessAsync -> output write) did not complete. Confirm the " +
            $"processor-sample container bound queue:{{id:D}} and the workflow cron fired.");
        return null; // unreachable (Assert.Fail throws) — keeps the compiler happy.
    }

    /// <summary>
    /// SCAN host Redis for all keys under a <c>skp:{discriminator}</c> family (e.g. <c>data:*</c> =
    /// <see cref="L2ProjectionKeys.ExecutionData(string)"/>; <c>flag:*</c> =
    /// <see cref="L2ProjectionKeys.Flag"/>). Content addresses are server-derived, so the keys cannot be
    /// addressed a priori — enumerate the family (D-12 adds the parallel <c>flag:*</c> scan to the
    /// historical <c>data:*</c> scan).
    /// </summary>
    private static HashSet<string> ScanKeys(string discriminator)
    {
        using var mux = ConnectionMultiplexer.Connect(HostRedis);
        var endpoints = mux.GetEndPoints();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ep in endpoints)
        {
            var server = mux.GetServer(ep);
            if (!server.IsConnected || server.IsReplica)
            {
                continue;
            }

            foreach (var key in server.Keys(pattern: $"{L2ProjectionKeys.Prefix}{discriminator}"))
            {
                keys.Add(key.ToString());
            }
        }

        return keys;
    }

    // ---- HTTP seeding helpers (Processor -> Steps -> Workflow) — mirrors SampleRoundTripE2ETests ----

    private static async Task<Guid> SeedProcessorAsync(HttpClient client, string sourceHash, CancellationToken ct)
    {
        // D-08: register the GENUINE embedded hash; D-05: null schemas. GET-or-create (idempotent) — the
        // fixed genuine hash is guarded by the unique uq_processor_source_hash constraint that persists in
        // host Postgres across runs, so a blind POST collides; resolve+reuse the existing stable row (the
        // one the live container already heartbeats against).
        var lookup = await client.GetAsync($"/api/v1/processors/by-source-hash/{sourceHash}", ct);
        if (lookup.StatusCode == HttpStatusCode.OK)
        {
            var existing = await lookup.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);
            return existing!.Id;
        }

        var dto = new ProcessorCreateDto(
            Name: $"sample-proc-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            SourceHash: sourceHash,
            InputSchemaId: null,
            OutputSchemaId: null,
            ConfigSchemaId: null);
        var resp = await client.PostAsJsonAsync("/api/v1/processors", dto, ct);
        resp.EnsureSuccessStatusCode();
        var proc = await resp.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);
        return proc!.Id;
    }

    private static async Task<Guid> SeedStepAsync(
        HttpClient client, Guid processorId, string name, List<Guid>? nextStepIds, CancellationToken ct)
    {
        var dto = new StepCreateDto(
            Name: $"{name}-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            ProcessorId: processorId,
            NextStepIds: nextStepIds,
            EntryCondition: StepEntryCondition.Always);
        var resp = await client.PostAsJsonAsync("/api/v1/steps", dto, ct);
        resp.EnsureSuccessStatusCode();
        var step = await resp.Content.ReadFromJsonAsync<StepReadDto>(cancellationToken: ct);
        return step!.Id;
    }

    private static async Task<Guid> SeedWorkflowAsync(
        HttpClient client, List<Guid> entryStepIds, string cron, CancellationToken ct)
    {
        var dto = new WorkflowCreateDto(
            Name: $"merge-wf-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            EntryStepIds: entryStepIds,
            AssignmentIds: null,
            CronExpression: cron);
        var resp = await client.PostAsJsonAsync("/api/v1/workflows", dto, ct);
        resp.EnsureSuccessStatusCode();
        var wf = await resp.Content.ReadFromJsonAsync<WorkflowReadDto>(cancellationToken: ct);
        return wf!.Id;
    }

    private const string HostRedis = "localhost:6380,abortConnect=false,connectTimeout=5000";

    /// <summary>
    /// Points the in-process WebApi at the REAL host stack (RMQ localhost:5673, Redis localhost:6380,
    /// Postgres localhost:5433, otel localhost:4317) and drains net-zero teardown in
    /// <see cref="DisposeAsync"/>. REUSED from <see cref="SampleRoundTripE2ETests"/> — the
    /// env-var-in-ctor host overrides + L2KeysToCleanup / ParentIndexMembersToSrem discipline are
    /// identical (D-12 extends the registered set to the new skp:flag:* namespace, populated by the test).
    /// </summary>
    private sealed class RealStackWebAppFactory : Composition.Phase8WebAppFactory
    {
        private readonly Dictionary<string, string?> _prior = new();

        public RealStackWebAppFactory()
            : base(
                skipPostgresFixture: true,
                connectionStringOverride: HostPostgres,
                skipRedisFixture: true,
                redisConnectionStringOverride: HostRedisFull)
        {
            try
            {
                Set("RabbitMq__Host", "localhost");
                Set("RabbitMq__Port", "5673");
                Set("RabbitMq__Username", "guest");
                Set("RabbitMq__Password", "guest");

                Set("ConnectionStrings__Redis", HostRedisFull);
                Set("ConnectionStrings__Postgres", HostPostgres);

                Set("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317");
            }
            catch
            {
                Restore();
                throw;
            }
        }

        private const string HostRedisFull = "localhost:6380,abortConnect=false,connectTimeout=5000";
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
        /// AFTER the real Start projects them + the round-trip + induced redelivery mint them. D-12: the
        /// test registers BOTH the new skp:data:{64hex} AND skp:flag:{64hex} keys here so the close-gate
        /// <c>redis-cli --scan</c> net-zero invariant holds across both new namespaces. The steady-state
        /// <c>skp:{procId:D}</c> liveness key is NOT registered (the live container keeps it fresh).
        /// </summary>
        public List<RedisKey> L2KeysToCleanup { get; } = new();

        /// <summary>Shared <c>skp:</c> parent-index members this test SADDed (via Start) to SREM on teardown.</summary>
        public List<RedisValue> ParentIndexMembersToSrem { get; } = new();

        public override async ValueTask DisposeAsync()
        {
            if (L2KeysToCleanup.Count > 0 || ParentIndexMembersToSrem.Count > 0)
            {
                await using var cleanupMux = await ConnectionMultiplexer.ConnectAsync(HostRedisFull);
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
