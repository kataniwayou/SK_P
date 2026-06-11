using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using BaseApi.Tests.Observability.Helpers;
using Messaging.Contracts.Projections;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Phase-49 SC1 RealStack round-trip proof (TEST-01, round-trip half) — the live-proof E2E that
/// exercises the full v4 Pre→In→Post round trip against real containers: a workflow fire drives a
/// dispatch the live <c>processor-sample</c> consumes, runs <c>ProcessAsync</c>, and writes its OUTPUT
/// to <c>skp:data:{entryId}</c>; the orchestrator then ADVANCES on the per-item result.
/// </summary>
/// <remarks>
/// <para>
/// This file is a near-wholesale clone of <see cref="SampleRoundTripE2ETests"/> (which already proves
/// this exact round trip), re-tagged under <c>[Trait("Phase","49")]</c> so the Phase-49 close gate's
/// live run includes it while the hermetic suite (<c>Category!=RealStack</c>) still excludes it. The
/// load-bearing clauses are preserved verbatim from the analog:
/// </para>
/// <list type="number">
///   <item>
///     <b>Genuine embedded SourceHash.</b> The Processor DB row is registered with the GENUINE hash
///     reflected off the built <c>Processor.Sample.dll</c> (<see cref="AssemblyMetadataAttribute"/> off
///     the assembly) — NOT a synthetic random hash, NOT recomputed — so the live container resolves THIS
///     processor id by querying <c>GetProcessorBySourceHash(hash)</c> (the identity loop is closed).
///   </item>
///   <item>
///     <b>NO synthetic liveness seed.</b> The test POLLS host Redis for the REAL <c>processor-sample</c>
///     container's <c>skp:{procId:D}</c> Healthy heartbeat and POSTs Start ONLY once that real key is
///     fresh — so the liveness gate passes TRUTHFULLY (a false-green with the container stopped is
///     impossible).
///   </item>
/// </list>
/// <para>
/// The round-trip is asserted on two clauses: (a) the dispatched step produced OUTPUT — a fresh
/// <c>skp:data:*</c> execution-data key appears in host Redis that was NOT present before Start (the
/// <c>EntryStepDispatchConsumer</c> mints a server-side <c>NewId.NextGuid()</c> entryId and writes the
/// output there); and (b) the ORCHESTRATOR ADVANCED — its container's <c>"Start reload for
/// WorkflowId={wfId}"</c> seam log flows via otel → Elasticsearch (the proven precedent), proving the
/// orchestrator consumed the published <c>StartOrchestration</c> and hydrated+scheduled the workflow
/// whose fire drove the dispatch.
/// </para>
/// <para>
/// Net-zero teardown: every minted L2 key (<c>skp:{wfId}</c>, <c>skp:{wfId}:{stepId}</c>, the round's
/// fresh <c>skp:data:*</c> output key) is registered into the factory's <c>L2KeysToCleanup</c> (drained
/// in <c>DisposeAsync</c>) and the parent-index member is SREMed, so a leak surfaces as a close-gate
/// redis <c>--scan</c> SHA mismatch. The steady-state <c>skp:{procId:D}</c> liveness key is LEFT (the
/// live container keeps refreshing it — it is in BOTH close-gate snapshots). Tagged
/// <c>Category=RealStack</c> + <c>Phase=49</c> so the hermetic filter excludes it and the close gate runs it.
/// </para>
/// </remarks>
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]   // hermetic filter (Category!=RealStack) excludes it
[Trait("Phase", "49")]             // tag new Phase-49 facts (canonical_refs test-runner note)
[Collection("Observability")]
public sealed class SC1RoundTripE2ETests
{
    private const string StartReloadMessage = "Start reload for WorkflowId=";

    // The live processor-sample container resolves identity + binds + MarkHealthy after the DB row is
    // seeded (compose start_period 30s + identity-resolve latency); allow a generous budget.
    private const int LivenessPollTimeoutMs = 90_000;

    // The orchestrator fires the dispatch at the next "* * * * *" occurrence (top of the next minute),
    // then the processor round-trips and writes output; allow > 60s plus round-trip slack.
    private const int OutputPollTimeoutMs = 120_000;

    // otel/log export is async; tolerate flush + ingest latency on the orchestrator-advance ES proof.
    private const int EsPollTimeoutMs = 120_000;

    [Fact]
    public async Task LiveSampleProcessor_PreInPostRoundTrip_AdvancesOrchestrator_Phase49()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var factory = new RealStackWebAppFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        // Read the GENUINE embedded SourceHash off the BUILT Processor.Sample assembly — the same way
        // AssemblyMetadataSourceHashProvider does at runtime. NOT a synthetic random hash, NOT recomputed.
        var hash = typeof(global::Processor.Sample.SampleProcessor).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "SourceHash").Value!;

        // Register the Processor DB row with THAT genuine hash + null schemas. The WebApi assigns procId
        // at row create; the live container resolves THIS id by GetProcessorBySourceHash(hash).
        var procId = await SeedProcessorAsync(client, hash, ct);
        var stepId = await SeedStepAsync(client, procId, ct);
        // Seed WITH a cron so the orchestrator's one-shot job actually fires the dispatch (null cron is a
        // business-skip in HydrateAndScheduleAsync — the round-trip would never run).
        var wfId = await SeedWorkflowAsync(client, new List<Guid> { stepId }, cron: "* * * * *", ct);

        // DO NOT seed the liveness key. POLL host Redis for the REAL container's skp:{procId:D} Healthy
        // heartbeat — only proceed once it exists + is fresh (the live container went Healthy). If it
        // never appears, identity diverged OR the container is down — fail with a clear message.
        await PollForHealthyLivenessAsync(procId, ct);

        // Snapshot the skp:data:* keys present BEFORE Start so we can detect the round-trip's fresh output
        // key (the server mints the entryId via NewId.NextGuid(), so the key name is unknown a priori).
        var dataKeysBefore = ScanExecutionDataKeys();

        // Drive Start. 204 NoContent means the L2 root was written, the body Guid minted + published, and
        // the processor-liveness gate PASSED — and it passed ONLY because the live container's heartbeat
        // is fresh (truthful gate; no synthetic seed backs it).
        var startResp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start", new List<Guid> { wfId }, ct);
        Assert.Equal(HttpStatusCode.NoContent, startResp.StatusCode);

        // Register the L2 root/step keys + parent-index member the Start created for net-zero teardown
        // (drained in DisposeAsync even if an assertion below throws). The steady-state skp:{procId:D}
        // liveness key is NOT registered — the live container keeps refreshing it (both gate snapshots).
        factory.ParentIndexMembersToSrem.Add(wfId.ToString("D"));
        factory.L2KeysToCleanup.Add($"skp:{wfId}");
        factory.L2KeysToCleanup.Add($"skp:{wfId}:{stepId}");

        // ---- Round-trip clause (a): OUTPUT WRITTEN TO L2 ----
        // The orchestrator fires the dispatch at the next minute; the live processor consumes it, runs
        // ProcessAsync, and writes the output to skp:data:{newEntryId}. Poll for a NEW skp:data:* key.
        var newDataKey = await PollForNewExecutionDataKeyAsync(dataKeysBefore, ct);
        Assert.NotNull(newDataKey);
        // Net-zero teardown: register the run's minted execution-data key so the close gate holds even
        // within any TTL window.
        factory.L2KeysToCleanup.Add(newDataKey!.Value);   // net-zero: register the minted skp:data:* key

        // ---- Round-trip clause (b): ORCHESTRATOR ADVANCES ----
        // The orchestrator CONTAINER consumed the published StartOrchestration and logged the seam
        // "Start reload for WorkflowId={wfId}" (StartOrchestrationConsumer), proving it hydrated +
        // scheduled the workflow whose fire drove the dispatch we just observed land in L2. Read it back
        // from Elasticsearch via the proven otel→ES precedent (term on the seeded WorkflowId attribute,
        // scoped to the orchestrator service; the distinct message text asserted in C#).
        using var es = new ElasticsearchTestClient();
        var advanceQuery = $$"""
          {
            "size": 5,
            "sort": [ { "@timestamp": { "order": "desc" } } ],
            "query": {
              "bool": {
                "must": [
                  { "term": { "attributes.WorkflowId": "{{wfId}}" } },
                  { "term": { "resource.attributes.service.name": "orchestrator" } }
                ]
              }
            }
          }
          """;
        var advance = await es.PollEsForLog(advanceQuery, timeoutMs: EsPollTimeoutMs, ct: ct);
        Assert.NotNull(advance);
        Assert.Contains(StartReloadMessage, advance!.Value.GetRawText());

        // ---- Round-trip clause (c): WorkflowId reached ES FROM A SCOPE on a PROCESSOR-side log ----
        // Prove WorkflowId reached ES FROM A SCOPE, not from a template. The processor-sample service
        // consumes EntryStepDispatch (IExecutionCorrelated), so its logs get attributes.WorkflowId ONLY
        // via the InboundExecutionScopeConsumeFilter — this assertion fails if the scope work is reverted.
        // (The orchestrator hit above is template-sourced — StartOrchestration is NOT IExecutionCorrelated
        // — and would pass even without scopes.)
        var scopeProofQuery = $$"""
          {
            "size": 5,
            "sort": [ { "@timestamp": { "order": "desc" } } ],
            "query": {
              "bool": {
                "must": [
                  { "term": { "attributes.WorkflowId": "{{wfId}}" } },
                  { "term": { "resource.attributes.service.name": "processor-sample" } }
                ]
              }
            }
          }
          """;
        var scopeProof = await es.PollEsForLog(scopeProofQuery, timeoutMs: EsPollTimeoutMs, ct: ct);
        Assert.NotNull(scopeProof);   // WorkflowId round-tripped to ES from the new scope on a processor log

        // Net-zero: stop the workflow so its self-rescheduling cron fire ceases — left running it mints a
        // fresh per-fire key every minute, churning the close-gate redis --scan name-set. Best-effort: a
        // stop hiccup must not fail an otherwise-green E2E assertion.
        try { await client.PostAsJsonAsync("/api/v1/orchestration/stop", new List<Guid> { wfId }, ct); }
        catch { /* best-effort net-zero teardown */ }
    }

    // ---- Liveness poll: wait for the REAL container's skp:{procId:D} Healthy heartbeat ----

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
                // Freshness: the container is the source of truth; presence of a non-stale Healthy key
                // means it resolved identity, bound its queue, and MarkHealthy'd (heartbeat writes only
                // when IsHealthy). The interval is in SECONDS — accept the key if its timestamp is within
                // a generous 2× interval window (mirrors the gate's freshness model).
                if (projection?.Liveness is { } live)
                {
                    var age = DateTime.UtcNow - live.Timestamp.ToUniversalTime();
                    var staleAfter = TimeSpan.FromSeconds(Math.Max(live.Interval, 1) * 3);
                    if (age <= staleAfter)
                    {
                        return; // the REAL container is Healthy — Start's liveness gate will pass truthfully.
                    }
                }
            }

            await Task.Delay(Math.Min(delay, 2_000), ct);
            delay = Math.Min(delay * 2, 2_000);
        }

        Assert.Fail(
            $"The processor-sample container never wrote a fresh Healthy liveness key {key} within " +
            $"{LivenessPollTimeoutMs}ms. Either the container is down, or its embedded SourceHash diverges " +
            $"from the host-built hash registered as the DB row (identity never resolved). Ensure the full " +
            $"compose stack incl. processor-sample is up healthy.");
    }

    // ---- Round-trip output poll: a NEW skp:data:* key appears after Start ----

    private static async Task<RedisKey?> PollForNewExecutionDataKeyAsync(
        HashSet<string> before, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(OutputPollTimeoutMs);
        var delay = 1_000;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var key in ScanExecutionDataKeys())
            {
                if (!before.Contains(key))
                {
                    return key; // the round-trip's output landed in L2 (EntryStepDispatchConsumer write).
                }
            }

            await Task.Delay(Math.Min(delay, 3_000), ct);
            delay = Math.Min(delay * 2, 3_000);
        }

        Assert.Fail(
            $"No new skp:data:* execution-data key appeared within {OutputPollTimeoutMs}ms — the live " +
            $"round-trip (orchestrator fire → dispatch → ProcessAsync → output write) did not complete. " +
            $"Confirm the processor-sample container bound queue:{{id:D}} and the workflow cron fired.");
        return null; // unreachable (Assert.Fail throws) — keeps the compiler happy.
    }

    /// <summary>
    /// SCAN host Redis for all keys under the execution-data discriminator
    /// (<c>skp:data:*</c> = <see cref="L2ProjectionKeys.ExecutionData"/>). The entryId is server-minted
    /// (<c>NewId.NextGuid()</c>), so we cannot address the key directly — enumerate the family.
    /// </summary>
    private static HashSet<string> ScanExecutionDataKeys()
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

            foreach (var key in server.Keys(pattern: $"{L2ProjectionKeys.Prefix}data:*"))
            {
                keys.Add(key.ToString());
            }
        }

        return keys;
    }

    // ---- HTTP seeding helpers (Processor → Step → Workflow) ----

    private static async Task<Guid> SeedProcessorAsync(HttpClient client, string sourceHash, CancellationToken ct)
    {
        // Register the GENUINE embedded hash (satisfies the DB ^[a-f0-9]{64}$ validator); null schema Ids
        // (Processor.Sample runs schema-less).
        //
        // GET-or-create (idempotent): the genuine embedded hash is FIXED, and the processor row is guarded
        // by a unique uq_processor_source_hash constraint that persists in host Postgres across runs. A
        // blind POST collides (23505) on every run after the first. Resolve the existing row by its hash
        // and reuse THAT id — which is exactly the row the live processor-sample container has already
        // resolved + is heartbeating against (the identity loop is stable). Only create when no row exists
        // yet (first run / fresh DB).
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

    private static async Task<Guid> SeedStepAsync(HttpClient client, Guid processorId, CancellationToken ct)
    {
        var dto = new StepCreateDto(
            Name: $"sample-step-{Guid.NewGuid():N}",
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

    private static async Task<Guid> SeedWorkflowAsync(
        HttpClient client, List<Guid> entryStepIds, string cron, CancellationToken ct)
    {
        var dto = new WorkflowCreateDto(
            Name: $"sample-wf-{Guid.NewGuid():N}",
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
    /// <see cref="DisposeAsync"/>. CLONED WHOLESALE from <see cref="SampleRoundTripE2ETests"/> — the
    /// env-var-in-ctor host overrides + L2KeysToCleanup / ParentIndexMembersToSrem discipline are
    /// identical (the real container heartbeats — no synthetic liveness seed).
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
        /// AFTER the real Start projects them + the round-trip mints them. Drained in
        /// <see cref="DisposeAsync"/> so the close-gate <c>redis-cli --scan</c> net-zero invariant holds.
        /// The steady-state <c>skp:{procId:D}</c> liveness key is NOT registered (the live container
        /// keeps it fresh in both snapshots).
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

                // GAP-49-8: sweep any composite backup keys (skp:{corr}:{wf}:{proc}:{exec}) the live Keeper
                // left for this run's workflows. The composite is a bounded 2-day crash-backstop normally
                // deleted by the happy-path CLEANUP/INJECT, but a race across the 2 keeper replicas can orphan
                // one (CLEANUP processed before its UPDATE). Scan-delete by workflowId (the 2nd key segment)
                // so the close-gate redis net-zero holds without waiting out the 2-day TTL.
                foreach (var srv in cleanupMux.GetEndPoints())
                {
                    var server = cleanupMux.GetServer(srv);
                    foreach (var wfId in ParentIndexMembersToSrem)
                        foreach (var compositeKey in server.Keys(pattern: $"skp:*:{wfId}:*"))
                            await db.KeyDeleteAsync(compositeKey);
                }
            }
            Restore();
            await base.DisposeAsync();
        }
    }
}
