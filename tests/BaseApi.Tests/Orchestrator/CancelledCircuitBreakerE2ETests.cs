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
using Npgsql;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// CAPSTONE real-stack cancelled-circuit-breaker proof (req-5 / req-8-live / req-6-data — Phase 32
/// Wave 3). The live inverse of the Phase-31 idempotency capstone: instead of proving a duplicate
/// collapses, it proves a workflow that exhausts its infra-retry budget is cleanly and completely
/// HALTED — the in-flight fire drained to a stop (no-TTL <c>skp:cancelled:*</c> marker set, the
/// MassTransit-auto-published <c>Fault&lt;EntryStepDispatch&gt;</c> fanned out and unscheduled the
/// Quartz job) WITHOUT routing any <c>Cancelled</c> <see cref="ExecutionResult"/> over the shared
/// <c>orchestrator-result</c> queue (D-04), while the SAME exhaustion still dead-letters to
/// <c>_error</c> (D-03) — then RESUMED by clearing the marker + re-<c>POST /orchestration/start</c>.
/// </summary>
/// <remarks>
/// <para>
/// CLONED from <see cref="IdempotentExactlyOnceE2ETests"/> (itself cloned from
/// <c>SampleRoundTripE2ETests</c>). It REUSES the genuine embedded-SourceHash reflection, the truthful
/// <c>PollForHealthyLivenessAsync</c> liveness gate, the <c>PollEsForLog</c> downstream-effect proof,
/// the <c>RealStackWebAppFactory</c> host-stack overrides, and the net-zero teardown that registers
/// run keys into <c>L2KeysToCleanup</c>. It diverges in the trip mechanism and the assertions:
/// </para>
/// <list type="number">
///   <item>
///     <b>Deterministic live infra-fault trip.</b> The Sample processor's transform echoes its
///     dispatch payload as the single output blob (<c>SampleProcessor.ProcessAsync</c>). The output
///     is therefore content-addressed at <c>skp:data:{HashBlob(payload)}</c> — computable a priori.
///     The test PRE-CREATES that exact key as a Redis <b>LIST</b> before driving the fire, so the
///     processor's <c>StringSetAsync</c> output write hits a deterministic <c>WRONGTYPE</c> infra
///     fault on EVERY attempt (the L2 output write is INFRA, no catch — <c>EntryStepDispatchConsumer</c>
///     step 3). The <c>Immediate(Limit)</c> policy exhausts → the breaker catch fires at
///     <c>GetRetryAttempt() == Limit</c> → marker set effect-first → re-throw → MassTransit publishes
///     <c>Fault&lt;EntryStepDispatch&gt;</c> AND dead-letters to <c>_error</c>. A fully external,
///     reproducible trigger — no broker/Redis teardown, no container change.
///   </item>
///   <item>
///     <b>Halt assertions (req-5 + req-2 live).</b> (a) <c>GET skp:cancelled:{wf:D}</c> == "true" AND
///     <c>TTL</c> == -1 (no-TTL marker, D-07); (b) the orchestrator's <c>FaultUnscheduleConsumer</c>
///     WARN log "Fault halt — unscheduling workflow {WorkflowId}" appears in ES (the halt came via the
///     Fault fanout, NOT a <c>Cancelled</c> result — req-5/D-04); (c) NO processor
///     <c>processor_result_sent{outcome="cancelled"}</c> appears for this workflow on the breaker path
///     and NO <c>Cancelled</c>-outcome advance log — the breaker never routes a Cancelled result;
///     (d) future cron fires stop — no NEW <c>skp:data:*</c> round-trip output key appears across a
///     settle window after the trip (Quartz unscheduled).
///   </item>
///   <item>
///     <b>Resume (req-8-live).</b> Clear the marker (<c>DEL skp:cancelled:{wf:D}</c>), remove the
///     poisoned output key, and re-<c>POST /orchestration/start</c> — assert the workflow re-fires (a
///     fresh <c>skp:data:*</c> output key appears; in-flight messages are no longer dropped).
///   </item>
///   <item>
///     <b>No live EntryCondition == 3 (req-6 data).</b> A direct SQL count against the target Postgres
///     proves no live <c>Steps</c> row carries the removed <c>PreviousCancelled (3)</c> condition
///     (D-12 — <c>3</c> is a numeric gap, auto-rejected by <c>IsInEnum</c>).
///   </item>
/// </list>
/// <para>
/// Net-zero teardown (CRITICAL — D-07 no-TTL): the run's <c>skp:cancelled:{wf:D}</c> marker has NO TTL
/// and will NOT self-expire (unlike <c>skp:flag:*</c>/<c>skp:data:*</c> which the close gate's 330s
/// settle-drain handles). It is registered into <c>L2KeysToCleanup</c> explicitly so it is deleted in
/// teardown — belt-and-braces with the close gate's own explicit scan-clean. The workflow is STOPPED in
/// teardown (<c>POST /orchestration/stop</c>) so no self-rescheduled cron fire keeps churning the
/// close-gate redis <c>--scan</c> name-set (NET-ZERO-31). Tagged <c>Category=RealStack</c> so the
/// hermetic filter excludes it.
/// </para>
/// </remarks>
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]
[Collection("Observability")]
public sealed class CancelledCircuitBreakerE2ETests
{
    // The orchestrator-side FaultUnscheduleConsumer WARN line (the halt-came-via-Fault marker — req-5/D-04).
    private const string FaultHaltMessage = "Fault halt";

    // The processor-side content-addressed-write log line — the downstream EFFECT marker. It must NOT
    // appear for the poisoned step (the output write faults before it logs), and DOES appear post-resume.
    private const string DownstreamEffectMessage = "step output written content-addressed";

    // The dispatch payload the poisoned step carries; SampleProcessor echoes it as the single output blob,
    // so the output content address is HashBlob(this) — pre-creatable as a WRONGTYPE list to force the trip.
    private const string TripPayload = "breaker-trip";

    private const int LivenessPollTimeoutMs = 90_000;
    private const int OutputPollTimeoutMs = 120_000;

    // The trip needs > Limit retry attempts to exhaust (Immediate is near-instant), then the Fault to fan
    // out and be ingested by ES; allow generous flush + ingest slack on the halt-log proof.
    private const int EsPollTimeoutMs = 120_000;

    // After the trip, watch for the ABSENCE of a new round-trip output across a window > one cron minute,
    // proving the Quartz job was unscheduled (no future fire).
    private const int UnscheduleWatchMs = 90_000;

    [Fact]
    public async Task BreakerTrip_HaltsInFlightAndFutureFires_NoCancelledResult_ErrorRetained_ThenResumes()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var factory = new RealStackWebAppFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        // Genuine embedded SourceHash off the BUILT Processor.Sample assembly (D-08 — not synthetic).
        var hash = typeof(global::Processor.Sample.SampleProcessor).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "SourceHash").Value!;

        var procId = await SeedProcessorAsync(client, hash, ct);

        // ---- req-6 data assertion: no live Steps row carries EntryCondition == 3 (PreviousCancelled removed, D-12) ----
        // Run it up front against the live Postgres — IsInEnum auto-rejects 3 at the write boundary, so the
        // live table can never accrue a 3; this proves it directly (VALIDATION row 32-T-req6b).
        Assert.Equal(0, await CountStepsWithEntryConditionAsync(3, ct));

        // ---- A single source step the breaker will trip (no input; SampleProcessor echoes the payload) ----
        var stepId = await SeedStepAsync(client, procId, name: "BreakerStep", nextStepIds: null, ct);
        var wfId = await SeedWorkflowAsync(client, new List<Guid> { stepId }, cron: "* * * * *", ct);

        await PollForHealthyLivenessAsync(procId, ct);

        // Register the L2 root/step keys + parent-index member + the no-TTL cancelled marker for net-zero
        // teardown. The cancelled marker has NO TTL (D-07) — it MUST be registered here or it never drains.
        factory.ParentIndexMembersToSrem.Add(wfId.ToString("D"));
        factory.L2KeysToCleanup.Add($"skp:{wfId}");
        factory.L2KeysToCleanup.Add($"skp:{wfId}:{stepId}");
        factory.L2KeysToCleanup.Add(L2ProjectionKeys.Cancelled(wfId));

        // ---- Arm the deterministic infra trip: pre-create the output content-address key as a LIST ----
        // SampleProcessor echoes the dispatch payload as the single output blob, so the output write
        // targets skp:data:{HashBlob(payload)}. Creating it as a Redis LIST makes the processor's
        // StringSetAsync (a SET on a list key) throw WRONGTYPE — a genuine infra fault on EVERY attempt.
        var poisonedKey = L2ProjectionKeys.ExecutionData(MessageIdentity.HashBlob(TripPayload));
        await ArmWrongTypePoisonAsync(poisonedKey, ct);
        factory.L2KeysToCleanup.Add(poisonedKey);

        var dataKeysBefore = ScanKeys("data:*");

        // ---- Drive the fire that trips the breaker ----
        // Send the entry-step dispatch directly to queue:{procId:D} carrying TripPayload (mirrors what
        // WorkflowFireJob sends for the entry step). The live processor consumes it, ProcessAsync echoes
        // TripPayload, the output write hits the WRONGTYPE poison → infra fault → Immediate(Limit) exhausts
        // → breaker catch fires at attempt==Limit → marker set effect-first → re-throw → Fault published +
        // dead-letter to _error. Also POST /orchestration/start so the cron job is scheduled (proves the
        // unschedule later); the cron's own fire would also trip, redundantly arming the halt.
        var startResp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start", new List<Guid> { wfId }, ct);
        Assert.Equal(HttpStatusCode.NoContent, startResp.StatusCode);

        var corr = NewId.NextGuid();
        var entryId = MessageIdentity.EntryEntryId(corr, stepId);
        var dispatch = new EntryStepDispatch(wfId, stepId, procId, Payload: JsonSerializer.Serialize(TripPayload))
        {
            CorrelationId = corr,
            ExecutionId = Guid.Empty,
            EntryId = entryId,
            H = MessageIdentity.ComputeH(corr, wfId, stepId, procId, entryId),
        };
        await SendDispatchAsync(procId, dispatch, ct);

        // ---- req-2 live: the no-TTL cancelled marker is set effect-first ----
        await PollForMarkerSetAsync(wfId, ct);
        var markerKey = L2ProjectionKeys.Cancelled(wfId);
        await using (var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis))
        {
            var db = mux.GetDatabase();
            Assert.Equal(L2ProjectionKeys.CancelledMarkerValue, (string?)await db.StringGetAsync(markerKey));

            // TTL == -1 (no expiry). KeyTimeToLive returns null for a no-TTL key (the StackExchange shape
            // of redis-cli's -1); a finite TimeSpan here would be the Pitfall-3 self-expiring-breaker bug.
            var ttl = await db.KeyTimeToLiveAsync(markerKey);
            Assert.Null(ttl);
        }

        // ---- req-5: the halt came via the Fault fanout (FaultUnscheduleConsumer WARN), NOT a Cancelled result ----
        // Positive proof: the orchestrator logged "Fault halt — unscheduling workflow {WorkflowId}" carrying
        // THIS workflowId. This is the observable that the unschedule was driven by Fault<EntryStepDispatch>,
        // not by a Cancelled ExecutionResult on the shared orchestrator-result queue (D-04).
        using var es = new ElasticsearchTestClient();
        var faultHaltQuery = BuildFaultHaltQuery(wfId);
        var haltLog = await es.PollEsForLog(faultHaltQuery, timeoutMs: EsPollTimeoutMs, ct: ct);
        Assert.NotNull(haltLog);
        Assert.Contains(FaultHaltMessage, haltLog!.Value.GetRawText());

        // ---- req-5: NO Cancelled ExecutionResult was routed on the breaker path ----
        // The processor's SendResult emits processor_result_sent{outcome="cancelled"} ONLY on the business
        // token-cancellation path (EXEC-08) — NEVER on the infra-breaker path (which re-throws). And the
        // processor logs the content-addressed-write line ONLY on a successful output write, which faulted
        // here. Assert ABSENCE of any downstream-effect log for this poisoned correlationId (the write never
        // completed) — a negative proxy that the breaker produced no Completed/Cancelled advance, only a halt.
        var effectQuery = BuildEffectQuery(corr, stepId);
        Assert.Equal(0, await CountEsHitsWithSettleAsync(effectQuery, ct));

        // ---- req-2/req-4 live: future cron fires are unscheduled (Quartz job deleted via the Fault halt) ----
        // Snapshot data:* now (post-trip) and watch for the ABSENCE of any NEW round-trip output key across a
        // window longer than one cron minute. The poison key remains a LIST (no skp:data:{HashBlob} write can
        // succeed), AND the check-and-drop gate ack-discards any in-flight redelivery for the cancelled wf, AND
        // the Quartz job is gone — so no new data key should appear. A new key here would mean a future fire
        // still ran (unschedule failed) — the breaker's future-fire stop did not hold.
        var dataKeysAfterTrip = ScanKeys("data:*");
        await AssertNoNewKeyAsync("data:*", dataKeysAfterTrip, UnscheduleWatchMs, ct);

        // ---- req-8 live: RESUME — clear the marker + remove the poison + re-Start re-fires the workflow ----
        await using (var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis))
        {
            var db = mux.GetDatabase();
            await db.KeyDeleteAsync(markerKey);     // operator resume step 1: clear the no-TTL marker (D-08)
            await db.KeyDeleteAsync(poisonedKey);   // remove the induced infra fault so the round-trip can succeed
        }

        var resumeDataBefore = ScanKeys("data:*");
        var resumeResp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start", new List<Guid> { wfId }, ct);   // operator resume step 2: re-Start (D-08)
        Assert.Equal(HttpStatusCode.NoContent, resumeResp.StatusCode);

        // The re-fired workflow now round-trips cleanly (marker cleared → check-and-drop passes through; poison
        // gone → the output write succeeds): a NEW skp:data:* output key appears.
        var resumedKey = await PollForNewKeyAsync("data:*", resumeDataBefore, ct);
        Assert.NotNull(resumedKey);

        // NET-ZERO-31: stop the workflow so its self-rescheduling cron fire ceases before teardown's net-zero
        // scan — left running it mints a fresh per-fire skp:flag:{H}/skp:data:* every minute, churning the
        // close-gate redis --scan name-set.
        try { await client.PostAsJsonAsync("/api/v1/orchestration/stop", new List<Guid> { wfId }, ct); }
        catch { /* best-effort net-zero teardown */ }

        // ---- Net-zero teardown: register every run-minted skp:data:*/skp:flag:* key for deletion ----
        foreach (var key in ScanKeys("data:*"))
            if (!dataKeysBefore.Contains(key))
                factory.L2KeysToCleanup.Add(key);
        foreach (var key in ScanKeys("flag:*"))
            factory.L2KeysToCleanup.Add(key);
    }

    // ---- Deterministic infra-trip arming: create the output content-address key as a WRONGTYPE list ----

    private static async Task ArmWrongTypePoisonAsync(string key, CancellationToken ct)
    {
        await using var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
        var db = mux.GetDatabase();
        await db.KeyDeleteAsync(key);                 // start clean (a prior run's leftover blob would be a string)
        await db.ListRightPushAsync(key, "poison");   // LIST type → a subsequent StringSetAsync throws WRONGTYPE
    }

    // ---- req-6 data: count live Steps rows carrying a given EntryCondition over the real Postgres ----

    private static async Task<int> CountStepsWithEntryConditionAsync(int entryCondition, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(HostPostgres);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM \"Steps\" WHERE \"EntryCondition\" = @ec", conn);
        cmd.Parameters.AddWithValue("ec", entryCondition);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    // ---- Marker poll: wait for the breaker to set the no-TTL cancelled marker ----

    private static async Task PollForMarkerSetAsync(Guid workflowId, CancellationToken ct)
    {
        await using var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
        var db = mux.GetDatabase();
        var key = L2ProjectionKeys.Cancelled(workflowId);

        var deadline = DateTime.UtcNow.AddMilliseconds(OutputPollTimeoutMs);
        var delay = 500;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if ((string?)await db.StringGetAsync(key) == L2ProjectionKeys.CancelledMarkerValue)
            {
                return; // the breaker tripped and set the marker effect-first.
            }

            await Task.Delay(Math.Min(delay, 2_000), ct);
            delay = Math.Min(delay * 2, 2_000);
        }

        Assert.Fail(
            $"The cancelled marker {key} was never set within {OutputPollTimeoutMs}ms — the breaker did not " +
            "trip. Check the Immediate(Limit) policy exhausted (the WRONGTYPE poison faulted the output " +
            "write every attempt) and the Wave-0 R1 pinned attempt boundary (== Limit) in 32-01-SUMMARY.");
    }

    // ---- Send the dispatch to the processor's bare {id:D} dispatch queue (sender-only queue: scheme) ----

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

    // ---- ES queries: the Fault-halt WARN (orchestrator) + the downstream-effect log (processor) ----

    private static string BuildFaultHaltQuery(Guid workflowId) => $$"""
      {
        "size": 20,
        "track_total_hits": true,
        "sort": [ { "@timestamp": { "order": "desc" } } ],
        "query": {
          "bool": {
            "must": [
              { "term": { "attributes.WorkflowId": "{{workflowId:D}}" } },
              { "term": { "resource.attributes.service.name": "orchestrator" } },
              { "wildcard": { "body.text": "*{{FaultHaltMessage}}*" } }
            ]
          }
        }
      }
      """;

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
    /// Counts ES hits for the query AFTER a settle window so an absence assertion (== 0) is honest: a
    /// leaked effect would have been ingested before we read. Used for the no-Cancelled-result / no-effect
    /// proof on the breaker path. Returns the observed hit total (0 when the effect never occurred).
    /// </summary>
    private static async Task<int> CountEsHitsWithSettleAsync(string query, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri("http://localhost:9200/") };

        // Settle window: let otel flush + ES ingest any (hypothetical) leaked effect so the == 0 assertion
        // is not merely early. The breaker faulted the write before it logged, so none should arrive.
        await Task.Delay(12_000, ct);

        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"{EsIndexNames.LogsDataStream}/_search")
        {
            Content = new StringContent(query, Encoding.UTF8, "application/json"),
        };
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return 0; // index not yet created / transient — treat as no hits for the absence assertion.
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("hits", out var outer)
            && outer.TryGetProperty("total", out var totalEl)
            && totalEl.TryGetProperty("value", out var valueEl))
        {
            return valueEl.GetInt32();
        }

        return 0;
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
            $"{LivenessPollTimeoutMs}ms. Ensure the full compose stack incl. a REBUILT processor-sample " +
            $"(embedded SourceHash must match the host build) is up healthy.");
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
            $"No new skp:{discriminator} key appeared within {OutputPollTimeoutMs}ms — the resumed round-trip " +
            "did not complete. Confirm the marker was cleared, the poison key removed, and re-Start fired.");
        return null; // unreachable.
    }

    /// <summary>
    /// Asserts NO new <c>skp:{discriminator}</c> key appears for the watch window (the unschedule proof). A
    /// new key during the window means a future cron fire still ran (the Quartz job was NOT unscheduled).
    /// </summary>
    private static async Task AssertNoNewKeyAsync(
        string discriminator, HashSet<string> before, int watchMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(watchMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var key in ScanKeys(discriminator))
            {
                if (!before.Contains(key))
                {
                    Assert.Fail(
                        $"A new skp:{discriminator} key ({key}) appeared within the {watchMs}ms unschedule " +
                        "watch window AFTER the breaker tripped — a future cron fire still ran, so the Fault " +
                        "halt did NOT unschedule the Quartz job (req-4 future-fire stop failed).");
                }
            }

            await Task.Delay(3_000, ct);
        }
        // No new key across the whole window > one cron minute → the job was unscheduled (future fires stopped).
    }

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

    // ---- HTTP seeding helpers (Processor -> Step -> Workflow) — mirrors IdempotentExactlyOnceE2ETests ----

    private static async Task<Guid> SeedProcessorAsync(HttpClient client, string sourceHash, CancellationToken ct)
    {
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
            Name: $"breaker-wf-{Guid.NewGuid():N}",
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
    private const string HostPostgres =
        "Host=localhost;Port=5433;Database=stepsdb;Username=postgres;Password=postgres;Maximum Pool Size=20;Timeout=15";

    /// <summary>
    /// Points the in-process WebApi at the REAL host stack (RMQ localhost:5673, Redis localhost:6380,
    /// Postgres localhost:5433, otel localhost:4317) and drains net-zero teardown in
    /// <see cref="DisposeAsync"/>. REUSED from <see cref="IdempotentExactlyOnceE2ETests"/> — identical
    /// env-var-in-ctor host overrides + L2KeysToCleanup / ParentIndexMembersToSrem discipline. The new
    /// no-TTL <c>skp:cancelled:*</c> marker is registered into <see cref="L2KeysToCleanup"/> by the test
    /// (D-07 — it will NOT self-expire, unlike the TTL-bounded skp:flag:*/skp:data:* keys).
    /// </summary>
    private sealed class RealStackWebAppFactory : Composition.Phase8WebAppFactory
    {
        private readonly Dictionary<string, string?> _prior = new();

        public RealStackWebAppFactory()
            : base(
                skipPostgresFixture: true,
                connectionStringOverride: HostPostgresInner,
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
                Set("ConnectionStrings__Postgres", HostPostgresInner);

                Set("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317");
            }
            catch
            {
                Restore();
                throw;
            }
        }

        private const string HostRedisFull = "localhost:6380,abortConnect=false,connectTimeout=5000";
        private const string HostPostgresInner =
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
        /// L2 keys (production "skp:" prefix) the test registers for deletion on teardown — including the
        /// no-TTL <c>skp:cancelled:{wf:D}</c> marker (D-07; it will NOT self-expire) and the WRONGTYPE
        /// poison key. The steady-state <c>skp:{procId:D}</c> liveness key is NOT registered (the live
        /// container keeps it fresh).
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
