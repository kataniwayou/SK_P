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
/// CAPSTONE Phase-30 metrics round-trip proof (METRIC-01..06 live + METRIC-07 gate). Drives the SAME
/// real-stack seed→liveness→Start→round-trip as <see cref="SampleRoundTripE2ETests"/>, then queries the
/// Prometheus SERVER (<c>localhost:9090/api/v1/query</c>) to prove the four business series exist for the
/// exercised <c>ProcessorId</c>, that the by-<c>ProcessorId</c> bottleneck PromQL evaluates numerically
/// (METRIC-06), that a runtime metric carries a non-empty <c>service_instance_id</c> (METRIC-01/02), and
/// that the business counters carry <c>ProcessorId</c> + <c>service_instance_id</c> with NO
/// <c>workflowId</c> label (METRIC-04/05) — <c>processor_result_sent_total</c> additionally carrying an
/// <c>outcome</c> ∈ {completed, failed, cancelled}.
/// </summary>
/// <remarks>
/// <para>
/// REUSES the <see cref="SampleRoundTripE2ETests"/> harness shape (D-14): a minimally-duplicated
/// <c>RealStackWebAppFactory</c> with the host-port env overrides (RMQ 5673, Redis 6380, Postgres 5433,
/// otel 4317) and the net-zero teardown discipline (<c>L2KeysToCleanup</c> /
/// <c>ParentIndexMembersToSrem</c>). The factory does NOT override port 9090 — the
/// <see cref="PrometheusTestClient"/> connects to the Prometheus server directly (Pitfall 5/6).
/// </para>
/// <para>
/// PRECONDITION (Pitfall 6): the FULL compose stack must be up healthy (incl. the <c>prometheus</c>
/// container scraping the collector's exporter endpoint, plus the <c>orchestrator</c> and <c>processor-sample</c>
/// containers that produce the counter increments). Tagged <c>Category=RealStack</c> so the hermetic
/// filter (<c>Category!=RealStack</c> / <c>--filter-not-trait "Category=RealStack"</c>) excludes it.
/// </para>
/// <para>
/// Net-zero teardown (the close-gate triple-SHA invariant): the run's fresh <c>skp:data:*</c> key is
/// drained; the steady-state <c>skp:{procId:D}</c> liveness key is LEFT (the live container keeps
/// refreshing it). Metrics are append-only telemetry — NOT part of the triple-SHA — so they do not
/// affect the close gate.
/// </para>
/// </remarks>
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]
[Collection("Observability")]
public sealed class MetricsRoundTripE2ETests
{
    // The live processor-sample container resolves identity + binds + MarkHealthy after the DB row is
    // seeded (compose start_period 30s + identity-resolve latency); allow a generous budget.
    private const int LivenessPollTimeoutMs = 90_000;

    // The orchestrator fires the dispatch at the next "* * * * *" occurrence (top of the next minute),
    // then the processor round-trips and writes output; allow > 60s plus round-trip slack.
    private const int OutputPollTimeoutMs = 120_000;

    // Prometheus is pull-based: SDK→collector OTLP export interval (up to ~60s) + collector→Prometheus
    // 15s scrape. A single immediate query MISSES the sample (Pitfall 4). Budget ≥ the ES E2E's 120s.
    private const int PromPollTimeoutMs = 120_000;

    [Fact]
    public async Task LiveRoundTrip_ProvesBusinessSeries_BottleneckPromQL_AndInstanceLabel()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var factory = new RealStackWebAppFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        // ── Drive the SAME live round-trip as SampleRoundTripE2ETests (87-137) ──────────────────────
        // D-08: read the GENUINE embedded SourceHash off the BUILT Processor.Sample assembly (NOT a
        // synthetic random hash, NOT recomputed) — the live container resolves THIS procId by it.
        var hash = typeof(global::Processor.Sample.SampleProcessor).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "SourceHash").Value!;

        var procId = await SeedProcessorAsync(client, hash, ct);
        var stepId = await SeedStepAsync(client, procId, ct);
        // Seed WITH a cron so the orchestrator's one-shot job actually fires the dispatch (null cron is a
        // business-skip in HydrateAndScheduleAsync — the round-trip would never run).
        var wfId = await SeedWorkflowAsync(client, new List<Guid> { stepId }, cron: "* * * * *", ct);

        // Pitfall 3 / D-07: POLL host Redis for the REAL container's skp:{procId:D} Healthy heartbeat —
        // only proceed once it exists + is fresh (no synthetic seed backs the liveness gate).
        await PollForHealthyLivenessAsync(procId, ct);

        // Snapshot the skp:data:* keys present BEFORE Start so we can detect the round-trip's fresh output.
        var dataKeysBefore = ScanExecutionDataKeys();

        // Drive Start. 204 NoContent means the L2 root was written, the body Guid minted + published, and
        // the processor-liveness gate PASSED truthfully (the live container's heartbeat is fresh).
        var startResp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start", new List<Guid> { wfId }, ct);
        Assert.Equal(HttpStatusCode.NoContent, startResp.StatusCode);

        // Register the L2 root/step keys + parent-index member for net-zero teardown (drained in
        // DisposeAsync even if an assertion below throws). The steady-state liveness key is NOT registered.
        factory.ParentIndexMembersToSrem.Add(wfId.ToString("D"));
        factory.L2KeysToCleanup.Add($"skp:{wfId}");
        factory.L2KeysToCleanup.Add($"skp:{wfId}:{stepId}");

        // Round-trip proof: a NEW skp:data:* execution-data key appears after Start — proves the live
        // round-trip drove a real dispatch (orchestrator dispatch_sent + processor dispatch_consumed) and
        // a real result (processor result_sent + orchestrator result_consumed), so all four counters
        // incremented for THIS procId before we query Prometheus.
        var newDataKey = await PollForNewExecutionDataKeyAsync(dataKeysBefore, ct);
        Assert.NotNull(newDataKey);
        factory.L2KeysToCleanup.Add(newDataKey!.Value);

        // ── Prometheus assertions (query the SERVER :9090, NOT the collector exporter — Pitfall 5) ───
        using var prom = new PrometheusTestClient();

        // METRIC-04 — orchestrator series exist for the exercised ProcessorId.
        var sent = await prom.PollPromForQuery(
            $"orchestrator_dispatch_sent_total{{ProcessorId=\"{procId:D}\"}}",
            PrometheusTestClient.VectorNonEmpty, PromPollTimeoutMs, ct);
        Assert.NotNull(sent);
        var resultConsumed = await prom.PollPromForQuery(
            $"orchestrator_result_consumed_total{{ProcessorId=\"{procId:D}\"}}",
            PrometheusTestClient.VectorNonEmpty, PromPollTimeoutMs, ct);
        Assert.NotNull(resultConsumed);

        // METRIC-05 — processor series exist (result_sent additionally carries the outcome tag).
        var consumed = await prom.PollPromForQuery(
            $"processor_dispatch_consumed_total{{ProcessorId=\"{procId:D}\"}}",
            PrometheusTestClient.VectorNonEmpty, PromPollTimeoutMs, ct);
        Assert.NotNull(consumed);
        var resultSent = await prom.PollPromForQuery(
            $"processor_result_sent_total{{ProcessorId=\"{procId:D}\"}}",
            PrometheusTestClient.VectorNonEmpty, PromPollTimeoutMs, ct);
        Assert.NotNull(resultSent);

        // METRIC-06 — the by-ProcessorId bottleneck PromQL evaluates to a numeric result.
        var bottleneck = await prom.PollPromForQuery(
            $"sum by (ProcessorId)(rate(orchestrator_dispatch_sent_total{{ProcessorId=\"{procId:D}\"}}[5m])) " +
            $"- sum by (ProcessorId)(rate(processor_dispatch_consumed_total{{ProcessorId=\"{procId:D}\"}}[5m]))",
            PrometheusTestClient.HasNumericValue, PromPollTimeoutMs, ct);
        Assert.NotNull(bottleneck);

        // ── Label-shape assertions on the returned (cloned) data — METRIC-04/05 acceptance ───────────
        // Business counters carry ProcessorId + service_instance_id (both non-empty) and NO workflowId.
        AssertBusinessLabels(sent!.Value, expectOutcome: false);
        AssertBusinessLabels(resultSent!.Value, expectOutcome: true);

        // METRIC-01/02 — a RUNTIME metric carries a non-empty service_instance_id. Query a broad runtime
        // selector (process_runtime_dotnet_*) and inspect result[0].metric for the instance label.
        //
        // SCOPE NOTE: this proves PRESENCE + NON-EMPTINESS of service_instance_id on a runtime series, not
        // per-replica UNIQUENESS. service.instance.id resolves per-process via POD_NAME → HOSTNAME →
        // MachineName → GUID; under the MachineName fallback two containers on the same Docker host could
        // share a value. Per-replica uniqueness (the resource attribute's purpose) holds in practice because
        // Docker sets the container id as HOSTNAME by default, so the MachineName fallback is not reached —
        // but that uniqueness is NOT asserted here. This assertion deliberately checks non-emptiness only.
        var runtime = await prom.PollPromForQuery(
            "{__name__=~\"process_runtime_dotnet_.*\"}",
            PrometheusTestClient.VectorNonEmpty, PromPollTimeoutMs, ct);
        Assert.NotNull(runtime);
        var runtimeMetric = FirstMetricObject(runtime!.Value);
        Assert.True(
            TryGetNonEmpty(runtimeMetric, "service_instance_id", out _),
            "A process_runtime_dotnet_* series must carry a non-empty service_instance_id label (METRIC-01/02) "
            + "— presence/non-emptiness only; per-replica uniqueness is not asserted here.");

        // NET-ZERO-31 (Phase 31.1): stop the workflow so its self-rescheduling cron fire ceases — left
        // running it mints a fresh per-fire skp:flag:{H} every minute, churning the close-gate redis
        // --scan name-set. Best-effort: a stop hiccup must not fail an otherwise-green E2E assertion.
        try { await client.PostAsJsonAsync("/api/v1/orchestration/stop", new List<Guid> { wfId }, ct); }
        catch { /* best-effort net-zero teardown */ }
    }

    // ── Label-shape helper (METRIC-04/05): ProcessorId + service_instance_id present & non-empty, ──
    //    NO workflowId; result_sent additionally carries a terminal `outcome`. ──────────────────────
    private static void AssertBusinessLabels(JsonElement data, bool expectOutcome)
    {
        var metric = FirstMetricObject(data);

        Assert.True(
            TryGetNonEmpty(metric, "ProcessorId", out _),
            "Business counter must carry a non-empty ProcessorId label.");
        Assert.True(
            TryGetNonEmpty(metric, "service_instance_id", out _),
            "Business counter must carry a non-empty service_instance_id label (ambient from Plan 01).");

        // Cardinality constraint (T-30-03/04): NO workflowId / WorkflowId label on the business counters.
        foreach (var prop in metric.EnumerateObject())
        {
            Assert.False(
                string.Equals(prop.Name, "workflowId", StringComparison.OrdinalIgnoreCase),
                "Business counter must NOT carry a workflowId label (cardinality DoS mitigation).");
        }

        if (expectOutcome)
        {
            Assert.True(
                TryGetNonEmpty(metric, "outcome", out var outcome),
                "processor_result_sent_total must carry an outcome label.");
            Assert.Contains(outcome, new[] { "completed", "failed", "cancelled" });
        }
    }

    /// <summary>Returns the <c>result[0].metric</c> object from a Prometheus instant-vector data element.</summary>
    private static JsonElement FirstMetricObject(JsonElement data)
    {
        var result = data.GetProperty("result");
        Assert.True(result.GetArrayLength() > 0, "Prometheus result vector was empty.");
        return result[0].GetProperty("metric");
    }

    /// <summary>True when <paramref name="metric"/> has key <paramref name="key"/> with a non-empty string value.</summary>
    private static bool TryGetNonEmpty(JsonElement metric, string key, out string value)
    {
        value = string.Empty;
        if (!metric.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = v.GetString() ?? string.Empty;
        return !string.IsNullOrEmpty(value);
    }

    // ── Liveness poll (Pitfall 3): wait for the REAL container's skp:{procId:D} Healthy heartbeat ──

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
            $"from the host-built hash registered as the DB row. Ensure the full compose stack incl. " +
            $"processor-sample is up healthy.");
    }

    // ── Round-trip output poll: a NEW skp:data:* key appears after Start ──

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
    /// SCAN host Redis for all keys under the execution-data discriminator (<c>skp:data:*</c>). The
    /// entryId is server-minted (<c>NewId.NextGuid()</c>), so enumerate the family.
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

    // ── HTTP seeding helpers (Processor → Step → Workflow) — mirrors SampleRoundTripE2ETests ──

    private static async Task<Guid> SeedProcessorAsync(HttpClient client, string sourceHash, CancellationToken ct)
    {
        // GET-or-create (idempotent): the genuine embedded hash is FIXED and guarded by the unique
        // uq_processor_source_hash constraint that persists across runs. Resolve the existing row by hash
        // and reuse THAT id — the row the live processor-sample container has already resolved + heartbeats.
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
    /// <see cref="DisposeAsync"/>. Minimally duplicated from <see cref="SampleRoundTripE2ETests"/> (D-14
    /// discretion) — the env-var-in-ctor host overrides + L2KeysToCleanup / ParentIndexMembersToSrem
    /// discipline are identical. Port 9090 is NOT overridden: the PrometheusTestClient connects to the
    /// server directly (Pitfall 6).
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
        /// <see cref="DisposeAsync"/> so the close-gate net-zero invariant holds. The steady-state
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
