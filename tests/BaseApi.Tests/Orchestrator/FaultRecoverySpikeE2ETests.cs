using System.Collections.Concurrent;
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
using ExecutionResult = Messaging.Contracts.ExecutionResult;   // disambiguate from MassTransit.ExecutionResult

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// PHASE-33 fault-recovery SPIKE (D-01/D-03/D-04) — the standing RealStack regression guard that
/// de-risks the load-bearing assumption of the whole v3.7.0 Keeper milestone BEFORE any Keeper code is
/// committed: that a published <c>Fault&lt;EntryStepDispatch&gt;</c> / <c>Fault&lt;ExecutionResult&gt;</c>
/// can be (1) consumed via pub/sub by an external subscriber, (2) unwrapped to its inner message +
/// full 6-id <see cref="IExecutionCorrelated"/> tuple + <c>H</c> via <c>context.Message.Message</c>
/// (double <c>.Message</c>), (3) re-injected directly to its origin endpoint BY TYPE
/// (<c>queue:{processorId:D}</c> for dispatch, <c>queue:orchestrator-result</c> for result) verbatim
/// (same <c>H</c>) via <c>GetSendEndpoint</c> + <c>Send</c> (NOT <c>Publish</c>, no orchestrator
/// round-trip), and (4) collapsed on a deliberate duplicate by the receiver's surviving Phase-31
/// <c>flag[H]</c> dedup gate (the spike/Keeper needs no dedup of its own — PROBE-06). Plus the negative
/// proof (D-09 / INTAKE-01 negative): <c>Fault&lt;StartOrchestration&gt;</c> /
/// <c>Fault&lt;StopOrchestration&gt;</c> are demonstrably NOT delivered to the spike's two
/// execution-fault consumers (the bindings are type-scoped).
/// </summary>
/// <remarks>
/// <para>
/// PROOF spike (D-03): authored against the proven precedents (NO production <c>src/</c> changes, NO
/// Keeper code, NO metric work — D-11). CLONED from <see cref="IdempotentExactlyOnceE2ETests"/> — it
/// REUSES the genuine embedded-SourceHash reflection, the truthful <c>PollForHealthyLivenessAsync</c>
/// liveness gate, the <c>PollEsForLog</c> downstream-effect proof, the <c>RealStackWebAppFactory</c>
/// host-stack overrides, and the net-zero <c>skp:*</c> teardown. The only genuinely new machinery vs
/// the clone source is the short-lived in-test <see cref="IBusControl"/> that registers the two
/// <c>IConsumer&lt;Fault&lt;T&gt;&gt;</c> probes against live <c>sk-rabbitmq</c> to CATCH the faults
/// (the clone used the same short-lived-bus trick only to SEND).
/// </para>
/// <para>
/// The DISPATCH trip is the standalone novel-risk carrier (pub/sub bind + double-<c>.Message</c> unwrap
/// + re-inject-by-type + <c>flag[H]</c> collapse all proven by it). The RESULT trip is the second
/// endpoint/type proof (Pitfall-1 window-armed, with a D-06 synthetic fallback). Tagged
/// <c>Category=RealStack</c> so the hermetic filter excludes it (this file adds ZERO hermetic tests).
/// </para>
/// </remarks>
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]
[Collection("Observability")]
public sealed class FaultRecoverySpikeE2ETests
{
    // The processor-side content-addressed-write log line (EntryStepDispatchConsumer step 3) — the
    // downstream EFFECT marker. One per distinct dispatch identity; the deduped duplicate adds none.
    private const string DownstreamEffectMessage = "step output written content-addressed";

    // The dispatch payload the poisoned step carries; SampleProcessor echoes it as the single output blob,
    // so the output content address is HashBlob(this) — pre-creatable as a WRONGTYPE list to force the trip.
    private const string DispatchTripPayload = "spike-dispatch-trip";

    // The result-path payload; the a-priori resultH chain (HashBlob -> manifest -> HashManifest -> ComputeH)
    // is computed from this so the orchestrator-result flag key is addressable before the round-trip runs.
    private const string ResultTripPayload = "spike-result-trip";

    // The live processor-sample container resolves identity + binds + MarkHealthy after the DB row is
    // seeded (compose start_period + identity-resolve latency); allow a generous budget.
    private const int LivenessPollTimeoutMs = 90_000;

    // The orchestrator fires the dispatch at the next "* * * * *" occurrence (top of the next minute),
    // then the processor round-trips and writes output; allow > 60s plus round-trip slack.
    private const int OutputPollTimeoutMs = 120_000;

    // otel/log export is async; tolerate flush + ingest latency on the downstream-effect ES proof.
    private const int EsPollTimeoutMs = 120_000;

    // The capture tuple the two Fault<T> probes append to: the inner message's full 6-id
    // IExecutionCorrelated set + H + the VERBATIM inner instance (boxed object — re-cast on re-inject).
    private readonly ConcurrentBag<(string h, Guid corr, Guid wf, Guid step, Guid proc, string entry, Guid exec, object inner)>
        _capturedDispatch = new();

    private readonly ConcurrentBag<(string h, Guid corr, Guid wf, Guid step, Guid proc, string entry, Guid exec, object inner)>
        _capturedResult = new();

    [Fact]
    public async Task FaultRecover_ReinjectByType_CollapsesDuplicate()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var factory = new RealStackWebAppFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        // D-08 (clone): read the GENUINE embedded SourceHash off the BUILT Processor.Sample assembly — the
        // same way AssemblyMetadataSourceHashProvider does at runtime. NOT synthetic, NOT recomputed.
        var hash = typeof(global::Processor.Sample.SampleProcessor).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "SourceHash").Value!;

        // Register the Processor DB row with THAT genuine hash + null schemas. The live container resolves
        // THIS id by GetProcessorBySourceHash(hash) (idempotent GET-or-create across runs).
        var procId = await SeedProcessorAsync(client, hash, ct);

        // A single source step the spike trips (no input; SampleProcessor echoes the payload), driven by a
        // "* * * * *" cron so the orchestrator's one-shot Quartz job actually dispatches.
        var stepId = await SeedStepAsync(client, procId, name: "SpikeStep", nextStepIds: null, ct);
        var wfId = await SeedWorkflowAsync(client, new List<Guid> { stepId }, cron: "* * * * *", ct);

        // Pitfall 3 / D-07: POLL host Redis for the REAL container's skp:{procId:D} Healthy heartbeat — only
        // proceed once it is fresh (the live container resolved identity + bound + MarkHealthy).
        await PollForHealthyLivenessAsync(procId, ct);

        // Register the L2 root/step keys + parent-index member for net-zero teardown (D-12).
        factory.ParentIndexMembersToSrem.Add(wfId.ToString("D"));
        factory.L2KeysToCleanup.Add($"skp:{wfId}");
        factory.L2KeysToCleanup.Add($"skp:{wfId}:{stepId}");

        // TODO Task 2: arm the dispatch + result WRONGTYPE trips, compute a-priori H/resultH, send the trips.
        // TODO Task 3: re-inject verbatim x2 (collapse proof), publish the negative command-faults, net-zero teardown.
    }

    // ====================================================================================================
    // GRAFT 2 — dual Fault<T> capture probes (INTAKE-01 bind half + INTAKE-02 unwrap)
    // Shape lifted VERBATIM from git 3aca386 FaultConsumerBindingFacts.cs:42-52 (proven double-.Message
    // round-trip, NO fallback). Both EntryStepDispatch and ExecutionResult implement IExecutionCorrelated,
    // so the inner message carries CorrelationId/WorkflowId/StepId/ProcessorId/EntryId/ExecutionId + H.
    // ====================================================================================================

    /// <summary>
    /// Binds the <c>Fault&lt;EntryStepDispatch&gt;</c> fanout exchange. Unwraps the VERBATIM inner instance
    /// via <c>context.Message.Message</c> (double <c>.Message</c>) into the shared capture bag — NO header
    /// parsing, NO re-deserialize, NO fallback (D-06 holds: <c>Fault.Message</c> IS the original instance).
    /// </summary>
    private sealed class FaultDispatchProbe(
        ConcurrentBag<(string h, Guid corr, Guid wf, Guid step, Guid proc, string entry, Guid exec, object inner)> captured)
        : IConsumer<Fault<EntryStepDispatch>>
    {
        public Task Consume(ConsumeContext<Fault<EntryStepDispatch>> context)
        {
            var m = context.Message.Message;   // double .Message — the VERBATIM original instance
            captured.Add((m.H, m.CorrelationId, m.WorkflowId, m.StepId, m.ProcessorId, m.EntryId, m.ExecutionId, m));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Binds the <c>Fault&lt;ExecutionResult&gt;</c> fanout exchange — identical to
    /// <see cref="FaultDispatchProbe"/> with the inner type swapped to <see cref="ExecutionResult"/>.
    /// </summary>
    private sealed class FaultResultProbe(
        ConcurrentBag<(string h, Guid corr, Guid wf, Guid step, Guid proc, string entry, Guid exec, object inner)> captured)
        : IConsumer<Fault<ExecutionResult>>
    {
        public Task Consume(ConsumeContext<Fault<ExecutionResult>> context)
        {
            var m = context.Message.Message;   // double .Message — the VERBATIM original instance
            captured.Add((m.H, m.CorrelationId, m.WorkflowId, m.StepId, m.ProcessorId, m.EntryId, m.ExecutionId, m));
            return Task.CompletedTask;
        }
    }

    // ---- Induced-duplicate sender helpers (clone :221-260): symmetric flag pre-write + Ack-wait ----

    // SENDER pre-write (D-06, symmetric inbound analog of the processor's outbound seed): write
    // flag[H]="Pending" so the consumer's effect-first When.Exists flip Pending->Ack has a key to flip.
    // Production StepDispatcher does this EXACTLY ONCE before its single Send; a broker redelivery never
    // repeats it. Called once (before delivery 1), NOT per send. TTL bounds the key.
    private static async Task PrewriteFlagPendingAsync(string h)
    {
        await using var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
        await mux.GetDatabase().StringSetAsync(
            L2ProjectionKeys.Flag(h), "Pending", expiry: TimeSpan.FromSeconds(300));
    }

    // Poll host Redis until the receiver flips flag[H] Pending->Ack (effect produced). Makes the
    // redelivery deterministic: delivery 2 is sent only AFTER delivery 1's effect completed, so it
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
            "re-injected message did not produce its effect, so the redelivery collapse cannot be proven.");
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
            $"a REBUILT processor-sample is up healthy.");
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
    /// addressed a priori — enumerate the family.
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

    // ---- HTTP seeding helpers (Processor -> Steps -> Workflow) — mirrors the clone source ----

    private static async Task<Guid> SeedProcessorAsync(HttpClient client, string sourceHash, CancellationToken ct)
    {
        // Register the GENUINE embedded hash; null schemas. GET-or-create (idempotent) — the fixed genuine
        // hash is guarded by the unique uq_processor_source_hash constraint that persists in host Postgres
        // across runs, so a blind POST collides; resolve+reuse the existing stable row (the one the live
        // container already heartbeats against).
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
            Name: $"spike-wf-{Guid.NewGuid():N}",
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
    /// <see cref="DisposeAsync"/>. REUSED VERBATIM from <see cref="IdempotentExactlyOnceE2ETests"/> — the
    /// env-var-in-ctor host overrides + L2KeysToCleanup / ParentIndexMembersToSrem discipline are identical.
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
        /// L2 keys (production "skp:" prefix) the spike registers for deletion on teardown — every armed
        /// WRONGTYPE poison key + every run-minted skp:data:{64hex} / skp:flag:{64hex} key so the close-gate
        /// <c>redis-cli --scan</c> net-zero invariant holds. The steady-state <c>skp:{procId:D}</c> liveness
        /// key is NOT registered (the live container keeps it fresh).
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
