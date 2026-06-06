using System.Net;
using System.Net.Http.Json;
using System.Reflection;
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
/// PHASE-35 SC3 end-to-end fault-intake correlation proof (D-09) — the standing RealStack guard that the
/// RUNNING Keeper container (NOT an in-test bus) consumes a real <c>Fault&lt;EntryStepDispatch&gt;</c> off
/// its durable <c>keeper-fault-recovery</c> queue and emits an Elasticsearch log CORRELATED to the original
/// execution: <c>resource.attributes.service.name = "keeper"</c>, <c>attributes.CorrelationId</c> == the
/// tripped dispatch's correlationId, <c>attributes.StepId</c> == the tripped step, and
/// <c>body.text ~ "keeper fault intake"</c> (the D-08 phrasing wired in Plan 02's
/// <see cref="global::Keeper.Consumers.FaultEntryStepDispatchConsumer"/>).
/// </summary>
/// <remarks>
/// <para>
/// SIBLING CLONE of <see cref="FaultRecoverySpikeE2ETests"/> (the Phase-33 spike is left UNTOUCHED — RESEARCH
/// Open Q1: keep its bind→unwrap→re-inject→collapse invariant isolated). This test REUSES verbatim the
/// genuine embedded-SourceHash reflection, the truthful <c>PollForHealthyLivenessAsync</c> liveness gate,
/// the <c>RealStackWebAppFactory</c> host-stack overrides (incl. <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>), the
/// proven WRONGTYPE live-trip (<c>ArmWrongTypePoisonAsync</c> on the dedup-gate <c>flag[dispatch.H]</c>), and
/// the net-zero <c>skp:*</c> teardown.
/// </para>
/// <para>
/// THE SC3 DELTA vs the spike: there is NO in-test <c>Fault&lt;T&gt;</c> probe and NO re-inject. The poison
/// is NOT cleared after arming — the WRONGTYPE trip publishes a real <c>Fault&lt;EntryStepDispatch&gt;</c>
/// that fans out to <c>keeper-fault-recovery</c>, and the deployed Keeper container's
/// <c>FaultEntryStepDispatchConsumer</c> consumes it and emits the correlated intake log. The assertion is a
/// single <c>PollEsForLog</c> readback against the Keeper service's ES log keyed on the PROPAGATED
/// correlationId + stepId — proving the manual CorrelationId scope (Plan 02) works end-to-end through the
/// container (the bus-wide filter would have fallen back to a fresh Guid; a matching <c>attributes.CorrelationId</c>
/// is the cryptographic proof it was restored from the inner message). This also confirms the INTAKE-03
/// Phase-35 separation slice: Keeper recovers off the <c>Fault&lt;T&gt;</c> pub/sub stream (its intake fires
/// off the published fault event), NEVER the <c>_error</c> queue. NO consolidated dead-letter / TTL
/// topology is built here (Pitfall 4 — that is Phase 36).
/// </para>
/// <para>
/// Tagged <c>Category=RealStack</c> so the hermetic filter excludes it (this file adds ZERO hermetic tests);
/// placed in the <c>Observability</c> collection so its env-var-in-ctor host overrides are serialized with
/// the other observability E2E fixtures. The LIVE <c>Assert.NotNull(hit)</c> GREEN against the rebuilt stack
/// is operator-gated (Phase-31..34 precedent); the Keeper container's embedded SourceHash MUST match this
/// phase's code (a stale container runs the Phase-34 placeholder and emits no intake log, Pitfall 5).
/// </para>
/// </remarks>
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]
[Collection("Observability")]
public sealed class KeeperFaultIntakeE2ETests
{
    // The Keeper-container intake log phrasing (D-08, wired in Plan 02's FaultEntryStepDispatchConsumer:40
    // "Keeper fault intake: ..."). The body.text wildcard matches this case-insensitively-lowered phrase.
    private const string KeeperIntakeMessage = "keeper fault intake";

    // The Keeper ES service.name (src/Keeper/appsettings.json Service.Name; compose sets no override).
    private const string KeeperServiceName = "keeper";

    // The dispatch payload the poisoned step carries; SampleProcessor echoes it as the single output blob.
    private const string DispatchTripPayload = "keeper-fault-intake-trip";

    // The live processor-sample container resolves identity + binds + MarkHealthy after the DB row is
    // seeded (compose start_period + identity-resolve latency); allow a generous budget.
    private const int LivenessPollTimeoutMs = 90_000;

    // The Keeper container must exhaust Immediate(N), publish Fault<EntryStepDispatch>, consume it, and
    // export the intake log via otel -> ES; tolerate flush + ingest latency on the correlated-log proof.
    private const int EsPollTimeoutMs = 120_000;

    [Fact]
    public async Task LiveWrongTypeTrip_KeeperContainer_EmitsCorrelatedIntakeLog()
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

        // A single source step the trip targets (no input; SampleProcessor echoes the payload), driven by a
        // "* * * * *" cron so the orchestrator's one-shot Quartz job actually dispatches.
        var stepId = await SeedStepAsync(client, procId, name: "KeeperIntakeStep", nextStepIds: null, ct);
        var wfId = await SeedWorkflowAsync(client, new List<Guid> { stepId }, cron: "* * * * *", ct);

        // Pitfall 3 / D-07: POLL host Redis for the REAL container's skp:{procId:D} Healthy heartbeat — only
        // proceed once it is fresh (the live container resolved identity + bound + MarkHealthy).
        await PollForHealthyLivenessAsync(procId, ct);

        // Register the L2 root/step keys + parent-index member for net-zero teardown (D-12).
        factory.ParentIndexMembersToSrem.Add(wfId.ToString("D"));
        factory.L2KeysToCleanup.Add($"skp:{wfId}");
        factory.L2KeysToCleanup.Add($"skp:{wfId}:{stepId}");

        // Snapshot skp:data:* + skp:flag:* BEFORE the trip so net-zero teardown registers every fresh key.
        var dataKeysBefore = ScanKeys("data:*");
        var flagKeysBefore = ScanKeys("flag:*");

        // ====================================================================================================
        // DISPATCH WRONGTYPE trip (the proven Phase-33 live recipe). Build the entry-step dispatch (mirrors
        // what WorkflowFireJob sends for the entry step) and CAPTURE its CorrelationId (dCorr) + StepId so the
        // Keeper-container ES log can be asserted to carry the PROPAGATED ids. Poison the inbound dedup-gate
        // flag[dispatch.H] (the consumer's FIRST L2 op, a StringGetAsync/GET) as a LIST -> WRONGTYPE on EVERY
        // delivery -> Immediate(N) exhausts -> MassTransit publishes Fault<EntryStepDispatch> to the fault
        // exchanges -> the RUNNING Keeper container's keeper-fault-recovery binding consumes it.
        //
        // SC3 DELTA: the poison is NOT cleared. We WANT the fault published and consumed by the Keeper
        // container — there is no in-test probe and no re-inject. The poison key is registered for net-zero
        // teardown (the only run-state it leaves is TTL-bounded skp:data:/skp:flag: + this LIST key).
        // ====================================================================================================
        var dCorr = NewId.NextGuid();
        var dEntryId = MessageIdentity.EntryEntryId(dCorr, stepId);
        var dispatchH = MessageIdentity.ComputeH(dCorr, wfId, stepId, procId, dEntryId);
        var trippedDispatch = new EntryStepDispatch(wfId, stepId, procId, Payload: JsonSerializer.Serialize(DispatchTripPayload))
        {
            CorrelationId = dCorr,
            ExecutionId = Guid.Empty,
            EntryId = dEntryId,
            H = dispatchH,
        };

        // Poison the inbound dedup-gate flag[dispatch.H] (a GET) as a LIST -> WRONGTYPE on every delivery.
        var dispatchPoisonKey = L2ProjectionKeys.Flag(dispatchH);
        await ArmWrongTypePoisonAsync(dispatchPoisonKey, ct);
        factory.L2KeysToCleanup.Add(dispatchPoisonKey);

        // The short-lived in-test IBusControl (D-02) connected to live sk-rabbitmq is the vehicle that SENDS
        // the trip dispatch to queue:{procId:D}. It binds NO Fault<T> consumer — the RUNNING Keeper container
        // is the sole subscriber of the published Fault<EntryStepDispatch> (D-09). IBusControl is NOT
        // IAsyncDisposable, so Start/Stop are bracketed explicitly in try/finally.
        var bus = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            cfg.Host("localhost", 5673, "/", h => { h.Username("guest"); h.Password("guest"); });
        });
        await bus.StartAsync(ct);
        try
        {
            // ---- DISPATCH trip: Send to queue:{procId:D}; the dedup-gate GET hits the WRONGTYPE poison on
            //      every delivery -> Immediate(N) exhausts -> Fault<EntryStepDispatch> fans out to the fault
            //      exchanges -> the Keeper container consumes it off keeper-fault-recovery + emits the log. ----
            var dispatchEndpoint = await bus.GetSendEndpoint(new Uri($"queue:{procId:D}"));
            await dispatchEndpoint.Send(trippedDispatch, ct);

            // Phase-39 LOOP-BREAK: Phase 36 added recover+re-inject to the Keeper consumer. The original
            // SC3 recipe (written in Phase 35, "no re-inject") left the WRONGTYPE poison armed forever — which
            // now turns intake into an UNBOUNDED recover->reinject->refault storm (the keeper's generic L2
            // probe sees redis healthy → re-injects → the processor re-faults on the still-armed flag[H] →
            // republishes → ∞), flooding OTLP→ES so the bare-service.name intake log never surfaces within the
            // poll budget. The intake log we assert is emitted on the FIRST cycle; give that cycle time to
            // publish+consume, then clear the poison so the keeper's next re-inject succeeds and the loop ends.
            // (Source hardening — a keeper recover-attempt cap — is tracked as a separate item; see MEMORY.)
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            await ClearPoisonAsync(dispatchPoisonKey, ct);

            // ================================================================================================
            // SC3 ASSERTION — the Keeper-container correlated intake log. PollEsForLog the Keeper service's
            // ES log keyed on the PROPAGATED correlationId (dCorr) + the tripped stepId + the D-08 body.text
            // phrase. A returned hit cryptographically carries attributes.CorrelationId == dCorr — proving the
            // running container restored the propagated id from the inner message (the bus-wide filter would
            // have fallen back to a fresh Guid for a Fault<T> envelope). This IS the SC3 + INTAKE-03-separation
            // proof: the Keeper log fired off the Fault<T> EVENT (not the _error queue) with the correlated id.
            // ================================================================================================
            using var es = new ElasticsearchTestClient();
            var keeperIntakeQuery = BuildKeeperIntakeQuery(dCorr, stepId);
            var hit = await es.PollEsForLog(keeperIntakeQuery, timeoutMs: EsPollTimeoutMs, ct: ct);
            Assert.NotNull(hit);

            // Semantic guard: the matched Keeper doc is the fault-intake line (the term on service.name=keeper
            // + the body.text wildcard already pin it; GetRawText covers body.text + {OriginalFormat}).
            Assert.Contains(KeeperIntakeMessage, hit!.Value.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await bus.StopAsync(ct);
        }

        // ====================================================================================================
        // Net-zero teardown (Shared Patterns). Stop the workflow so its self-rescheduling cron fire ceases
        // (NET-ZERO-31), then register every run-minted skp:data:*/skp:flag:* key for deletion so the
        // close-gate triple-SHA redis --scan BEFORE==AFTER holds. The WRONGTYPE poison flag key was already
        // registered. The IBusControl was bracketed Start/try/finally Stop above.
        // ====================================================================================================
        try { await client.PostAsJsonAsync("/api/v1/orchestration/stop", new List<Guid> { wfId }, ct); }
        catch { /* best-effort net-zero teardown */ }

        foreach (var key in ScanKeys("data:*"))
            if (!dataKeysBefore.Contains(key))
                factory.L2KeysToCleanup.Add(key);
        foreach (var key in ScanKeys("flag:*"))
            if (!flagKeysBefore.Contains(key))
                factory.L2KeysToCleanup.Add(key);
    }

    // ---- WRONGTYPE poison arm (git a6c6825 circuit-breaker E2E recipe, verbatim — same as the spike) ----

    // A LIST key makes any subsequent String op (StringSetAsync / StringGetAsync) throw WRONGTYPE on EVERY
    // attempt — a genuine, deterministic infra fault. Poison ONLY the named INFRA op (Pitfall 3): the
    // processor's effect-first dedup-gate flag[dispatch.H] first read (StringGetAsync/GET).
    private static async Task ArmWrongTypePoisonAsync(string key, CancellationToken ct)
    {
        await using var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
        var db = mux.GetDatabase();
        await db.KeyDeleteAsync(key);                 // start clean (a leftover string would not throw)
        await db.ListRightPushAsync(key, "poison");   // LIST type -> a subsequent String op throws WRONGTYPE
    }

    // Disarm the WRONGTYPE poison (delete the LIST key) so a subsequent GET returns nil rather than throwing —
    // lets the keeper's recover+re-inject succeed and terminates the recover->reinject->refault loop (Phase-39).
    private static async Task ClearPoisonAsync(string key, CancellationToken ct)
    {
        await using var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
        await mux.GetDatabase().KeyDeleteAsync(key);
    }

    // ---- The Keeper-container correlated-intake ES query (the SC3 readback) ----

    // Filter the Keeper service's intake log on the PROPAGATED correlationId + the tripped stepId + the D-08
    // body.text phrase. attributes.CorrelationId is the Plan-02 manual scope value (inner.CorrelationId
    // .ToString() == D-format); attributes.StepId is ExecutionLogScope.BuildState's ec.StepId.ToString().
    private static string BuildKeeperIntakeQuery(Guid correlationId, Guid stepId) => $$"""
      {
        "size": 20,
        "track_total_hits": true,
        "sort": [ { "@timestamp": { "order": "desc" } } ],
        "query": {
          "bool": {
            "must": [
              { "term": { "{{EsIndexNames.CorrelationIdFieldPath}}": "{{correlationId:D}}" } },
              { "term": { "attributes.StepId": "{{stepId:D}}" } },
              { "term": { "resource.attributes.service.name": "{{KeeperServiceName}}" } },
              { "wildcard": { "body.text": { "value": "*{{KeeperIntakeMessage}}*", "case_insensitive": true } } }
            ]
          }
        }
      }
      """;

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
                        return; // the REAL container is Healthy — the dispatch will be processed truthfully.
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
            Name: $"keeper-intake-wf-{Guid.NewGuid():N}",
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
    /// <see cref="DisposeAsync"/>. REUSED VERBATIM from <see cref="FaultRecoverySpikeE2ETests"/> — the
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
        /// L2 keys (production "skp:" prefix) the test registers for deletion on teardown — the armed
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
