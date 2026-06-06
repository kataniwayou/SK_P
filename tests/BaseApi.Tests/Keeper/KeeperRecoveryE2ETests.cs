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
using Keeper.Recovery;                                          // ProbeOutcome — referenced only in doc-comments / parity asserts
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Hashing;
using Messaging.Contracts.Projections;
using StackExchange.Redis;
using Xunit;
using ExecutionResult = Messaging.Contracts.ExecutionResult;   // disambiguate from MassTransit.ExecutionResult

namespace BaseApi.Tests.Keeper;

/// <summary>
/// PHASE-36 LIVE recovery proof (PROBE-03/04/05 live half) — the standing RealStack guard that the deployed
/// Keeper container's bounded probe loop (Plan 02) drives the WHOLE recovery engine end-to-end against the real
/// compose stack: (a) RECOVER-BOTH-PATHS — on L2 return the running container re-injects the VERBATIM inner
/// (<see cref="EntryStepDispatch"/> → <c>queue:{ProcessorId:D}</c>, <see cref="ExecutionResult"/> →
/// <c>queue:orchestrator-result</c>) with EXACTLY-ONCE downstream effect (the receiver's surviving Phase-31
/// <c>flag[H]</c> gate collapses any duplicate — PROBE-06, <c>CountEsHitsAsync == 1</c>); and (b) GIVE-UP — when
/// L2 stays down past <c>MaxAttempts</c> the loop exhausts and the ORIGINAL <c>Fault&lt;T&gt;</c> envelope (carries
/// <c>Exceptions[]</c> for triage) is parked to <see cref="KeeperQueues.DeadLetter"/> (<c>keeper-dlq</c>). The new
/// probe scratch-key family <c>skp:keeper:probe:*</c> (<see cref="L2ProjectionKeys.KeeperProbe(string)"/>) is
/// net-zero (its 30s TTL self-cleans).
/// </summary>
/// <remarks>
/// <para>
/// SIBLING CLONE of <see cref="global::BaseApi.Tests.Orchestrator.FaultRecoverySpikeE2ETests"/> (the Phase-33
/// spike is left UNTOUCHED) and the Phase-35
/// <see cref="global::BaseApi.Tests.Orchestrator.KeeperFaultIntakeE2ETests"/> (the running-Keeper-container
/// observe pattern, D-09). It REUSES verbatim the genuine embedded-SourceHash reflection, the truthful
/// <c>PollForHealthyLivenessAsync</c> liveness gate, the <c>RealStackWebAppFactory</c> host-stack overrides
/// (incl. <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>), the proven WRONGTYPE live-trip (<c>ArmWrongTypePoisonAsync</c> —
/// a LIST poison on a GET key), <c>PollEsForLog</c> / <c>CountEsHitsAsync</c> (ES body is <c>body.text</c>), and
/// the net-zero <c>skp:*</c> teardown.
/// </para>
/// <para>
/// THE PHASE-36 DELTA vs the Phase-35 intake test: there is NO in-test probe loop. The RUNNING Keeper container
/// (D-09, like <see cref="global::BaseApi.Tests.Orchestrator.KeeperFaultIntakeE2ETests"/>) consumes the published
/// <c>Fault&lt;T&gt;</c>, runs its OWN deployed probe loop, and re-injects | parks. This test merely INDUCES the
/// fault, controls L2 health (clear poison to simulate L2 return; leave it to force give-up), and OBSERVES the
/// container's effect — exactly-once downstream effect (ES) on recover; <c>keeper-dlq</c> depth on give-up. In-test
/// probes bind <c>queue:orchestrator-result</c> (catch the re-injected result) and <c>queue:keeper-dlq</c> (catch
/// the parked envelope, then ack-drain it → net-zero terminal queue for the Phase-39 gate).
/// </para>
/// <para>
/// OPERATOR GATE (auto-approve-human-verify precedent, Phases 33–35): the authored test is the deliverable. The
/// live GREEN run requires the rebuilt compose stack — ALL of keeper + processor-sample + orchestrator +
/// baseapi-service must be rebuilt (the Plan-03 BaseConsole.Core consolidated-error-transport change is embedded
/// in every console image, and the keeper SourceHash must match this phase's code; a stale keeper runs the
/// Phase-34 placeholder and never probes, Pitfall 5). The VALIDATION.md Manual-Only kill-mid-loop run (PROBE-05
/// at-least-once) and Phase-39's 3×GREEN triple-SHA close gate are the authoritative live signals — see the
/// 36-04-SUMMARY Pending-Verification runbook. Tagged <c>Category=RealStack</c> so the hermetic filter excludes it
/// (this file adds ZERO hermetic tests); placed in the <c>Observability</c> collection so its env-var-in-ctor host
/// overrides are serialized with the other observability E2E fixtures.
/// </para>
/// </remarks>
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]
[Collection("Observability")]
public sealed class KeeperRecoveryE2ETests
{
    // The processor-side content-addressed-write log line (EntryStepDispatchConsumer step 3) — the downstream
    // EFFECT marker. One per distinct dispatch identity; the deduped duplicate adds none (PROBE-06).
    private const string DownstreamEffectMessage = "step output written content-addressed";

    // The dispatch payload the recovered step carries; SampleProcessor echoes it as the single output blob.
    private const string DispatchTripPayload = "keeper-recover-dispatch-trip";

    // The result-path payload; the a-priori resultH chain (HashBlob -> manifest -> HashManifest -> ComputeH)
    // is computed from this so the orchestrator-result re-inject can be addressed before the round-trip runs.
    private const string ResultTripPayload = "keeper-recover-result-trip";

    // The live processor-sample container resolves identity + binds + MarkHealthy after the DB row is seeded
    // (compose start_period + identity-resolve latency); allow a generous budget.
    private const int LivenessPollTimeoutMs = 90_000;

    // The deployed Keeper container must consume the Fault<T>, probe (recover within the loop window), and the
    // re-injected dispatch must round-trip + write output via otel -> ES; tolerate flush + ingest latency.
    private const int OutputPollTimeoutMs = 120_000;

    // otel/log export is async; tolerate flush + ingest latency on the downstream-effect ES proof.
    private const int EsPollTimeoutMs = 120_000;

    // GIVE-UP: with the deployed default Probe (DelaySeconds=5 × MaxAttempts=12 = 60s), the loop exhausts ~60s
    // after the Fault<T> is consumed; allow the full window + Immediate(N) exhaustion + park latency + slack.
    // (Operators MAY set a small Probe__MaxAttempts on the keeper container to shorten this — see the runbook.)
    private const int DlqParkPollTimeoutMs = 180_000;

    // PHASE-39 (TEST-01/02): the keeper_* Prometheus scrape budget. Prometheus is pull-based (15s scrape) and the
    // OTel SDK exports on a 60s cadence, so a series can take up to ~75s to appear after the live recover/give-up
    // flow fires; allow the full SDK-export + scrape budget (mirror MetricsRoundTripE2ETests.cs PromPollTimeoutMs).
    private const int PromPollTimeoutMs = 120_000;

    // Capture bag for the in-test probe bound to queue:orchestrator-result — the re-injected verbatim
    // ExecutionResult the running Keeper container forwards on Recovered (PROBE-03, second type).
    private readonly ConcurrentBag<(string h, Guid corr, Guid step)> _reinjectedResults = new();

    // Capture bag for the in-test probe bound to queue:keeper-dlq — the parked ORIGINAL Fault<T> envelope the
    // running Keeper container forwards on GaveUp (PROBE-04). Drained (acked) on capture → net-zero terminal queue.
    private readonly ConcurrentBag<string> _parkedDlqHashes = new();

    // ====================================================================================================
    // FACT 1 — RECOVER-BOTH-PATHS (PROBE-03 live). Trip a live Fault<EntryStepDispatch>, let the deployed
    // Keeper container probe-recover + re-inject verbatim to the processor with EXACTLY-ONCE effect; then prove
    // the second type (Fault<ExecutionResult>) re-injects verbatim to queue:orchestrator-result.
    // ====================================================================================================
    [Fact]
    public async Task KeeperRecovery_RecoversBothPaths()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var factory = new RealStackWebAppFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        // Read the GENUINE embedded SourceHash off the BUILT Processor.Sample assembly — the same way
        // AssemblyMetadataSourceHashProvider does at runtime. NOT synthetic, NOT recomputed.
        // NOTE (operator gate): rebuild keeper + processor-sample + orchestrator + baseapi-service before the run
        // — the keeper SourceHash must match this phase's code AND the Plan-03 BaseConsole.Core error-transport
        // change is embedded in all three console images (a stale keeper runs the Phase-34 placeholder, Pitfall 5).
        var hash = typeof(global::Processor.Sample.SampleProcessor).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "SourceHash").Value!;

        var procId = await SeedProcessorAsync(client, hash, ct);
        var stepId = await SeedStepAsync(client, procId, name: "KeeperRecoverStep", nextStepIds: null, ct);
        var wfId = await SeedWorkflowAsync(client, new List<Guid> { stepId }, cron: "* * * * *", ct);

        // Pitfall 3 / D-07: only proceed once the REAL processor-sample container's skp:{procId:D} Healthy
        // heartbeat is fresh (the live container resolved identity + bound + MarkHealthy).
        await PollForHealthyLivenessAsync(procId, ct);

        factory.ParentIndexMembersToSrem.Add(wfId.ToString("D"));
        factory.L2KeysToCleanup.Add($"skp:{wfId}");
        factory.L2KeysToCleanup.Add($"skp:{wfId}:{stepId}");

        var dataKeysBefore = ScanKeys("data:*");
        var flagKeysBefore = ScanKeys("flag:*");
        // The new Phase-36 scratch-key family snapshot — assert net-zero (the 30s TTL self-cleans).
        var probeKeysBefore = ScanKeys("keeper:probe:*");

        // ---- Build the entry-step dispatch (mirrors what WorkflowFireJob sends for the entry step). ----
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

        // Poison the inbound dedup-gate flag[dispatch.H] (the processor's FIRST L2 op, a StringGetAsync/GET) as a
        // LIST -> WRONGTYPE on every delivery -> Immediate(N) exhausts -> Fault<EntryStepDispatch> published ->
        // the RUNNING Keeper container consumes it off keeper-fault-recovery and starts its probe loop.
        //
        // PROBE-03 RECOVER timing: the Keeper probe loop READS skp:data:{entryId} + WRITES/deletes
        // skp:keeper:probe:{h} — NEITHER of which is the poisoned flag[dispatch.H]. So the deployed probe's FIRST
        // iteration completes cleanly => Recovered (almost immediately) and the container re-injects the verbatim
        // dispatch to queue:{procId:D}. We CLEAR the poison right after the trip (simulate L2 return) so the
        // re-injected delivery's dedup-gate GET succeeds and produces its real downstream effect — the receiver's
        // surviving Phase-31 flag[H] gate then collapses any redelivery to EXACTLY-ONCE (PROBE-06).
        var dispatchPoisonKey = L2ProjectionKeys.Flag(dispatchH);
        await ArmWrongTypePoisonAsync(dispatchPoisonKey, ct);
        factory.L2KeysToCleanup.Add(dispatchPoisonKey);

        // ---- A-priori resultH for the second-type proof. SampleProcessor echoes its payload, so the full
        //      result-path hash chain is computable up front: blob -> manifest JSON -> manifest entryId -> resultH.
        var rCorr = NewId.NextGuid();
        var rBlobHash = MessageIdentity.HashBlob(ResultTripPayload);
        var rManifestJson = JsonSerializer.Serialize(new[] { rBlobHash });   // ["<64hex>"]
        var rManifestEntryId = MessageIdentity.HashManifest(rManifestJson);
        var resultH = MessageIdentity.ComputeH(rCorr, wfId, stepId, procId, rManifestEntryId);

        // The short-lived in-test IBusControl (D-02) connected to live sk-rabbitmq SENDS the trip dispatch and
        // PUBLISHES the synthetic Fault<ExecutionResult>; it also binds a probe on queue:orchestrator-result to
        // CATCH the verbatim result the running Keeper container re-injects on Recovered. IBusControl is NOT
        // IAsyncDisposable, so Start/Stop are bracketed explicitly in try/finally.
        var bus = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            cfg.Host("localhost", 5673, "/", h => { h.Username("guest"); h.Password("guest"); });
            // Phase-39: observe the keeper's recovered-branch ResumeWorkflow (PUBLISHED → fanout) on a TEMPORARY
            // auto-delete endpoint — NOT the Send'd ExecutionResult on the shared queue:orchestrator-result. The
            // orchestrator container competes for orchestrator-result (the keeper Sends point-to-point there), so
            // racing it for the re-injected result is flaky. ResumeWorkflow is published in the SAME recovered
            // branch (right after the verbatim re-inject Send) carrying the inner's H + CorrelationId, so a fanout
            // copy on our own temp queue deterministically proves the verbatim result recovery. The temp queue is
            // auto-deleted on bus stop (net-zero — no leaked queue in the close-gate rmq SHA).
            cfg.ReceiveEndpoint(e =>
                e.Consumer(() => new ReinjectedResultProbe(_reinjectedResults)));
        });
        await bus.StartAsync(ct);
        try
        {
            // ---- DISPATCH trip: Send to queue:{procId:D}; the dedup-gate GET hits the WRONGTYPE poison on every
            //      delivery -> Immediate(N) exhausts -> Fault<EntryStepDispatch> -> the Keeper container probes
            //      (recovers on first clean iteration) + re-injects verbatim to queue:{procId:D}. ----
            var dispatchEndpoint = await bus.GetSendEndpoint(new Uri($"queue:{procId:D}"));
            await dispatchEndpoint.Send(trippedDispatch, ct);

            // Simulate L2 RETURN: clear the dedup-gate poison so the container's re-injected delivery's GET
            // succeeds and produces its real effect (the collapse rides the receiver flag[H] gate, not the fault).
            // The deployed probe recovers on its first clean iteration well within the 60s loop window, and the
            // re-injected delivery then races the cleared poison — give the trip a brief head-start so the fault
            // fans out + the container picks it up before we clear (the container's loop is the deciding latency).
            await Task.Delay(2_000, ct);
            await ClearPoisonAsync(dispatchPoisonKey, ct);

            // PROBE-06 / exactly-once: the receiver's surviving Phase-31 flag[H] gate collapses any duplicate
            // re-inject. Assert EXACTLY ONE downstream effect for the re-injected dispatch identity (the live
            // inverse of the historical StepB4 x2 over-execution bug). Scope to (corr, stepId); 8s+ ingest settle.
            using var es = new ElasticsearchTestClient();
            var dispatchEffectQuery = BuildEffectQuery(dCorr, stepId);
            var firstEffect = await es.PollEsForLog(dispatchEffectQuery, timeoutMs: EsPollTimeoutMs, ct: ct);
            Assert.NotNull(firstEffect);
            Assert.Contains(DownstreamEffectMessage, firstEffect!.Value.GetRawText());
            Assert.Equal(1, await CountEsHitsAsync(dispatchEffectQuery, ct));

            // ---- RESULT re-inject (second-type proof, PROBE-03). The live orchestrator result-hop trip is NOT
            //      reachable here (no orchestration started — the spike documents the Pitfall-1 window is fragile),
            //      so publish a synthetic Fault<ExecutionResult> (real inner ExecutionResult, H=resultH) with a
            //      CLEAN entryId so the Keeper container's probe RECOVERS on its first clean iteration and
            //      re-injects the VERBATIM result to queue:orchestrator-result, where our in-test probe catches it.
            await PublishSyntheticResultFaultAsync(bus, procId, stepId, wfId, rCorr, resultH, rManifestEntryId, ct);

            // Register the synthetic result's data + probe scratch keys for net-zero (clean — never poisoned).
            factory.L2KeysToCleanup.Add(L2ProjectionKeys.ExecutionData(rManifestEntryId));
            factory.L2KeysToCleanup.Add(L2ProjectionKeys.KeeperProbe(resultH));

            var resultCap = await PollForResultReinjectAsync(resultH, ct);
            Assert.Equal(resultH, resultCap.h);
            Assert.Equal(rCorr, resultCap.corr);   // verbatim inner — same CorrelationId round-tripped
        }
        finally
        {
            await bus.StopAsync(ct);
        }

        // ---- Net-zero teardown. Stop the workflow so its self-rescheduling cron fire ceases, register every
        //      run-minted skp:data:*/skp:flag:* key for deletion, and ASSERT the new skp:keeper:probe:* family is
        //      net-zero (its 30s TTL self-cleans; the deployed probe write-then-deletes it inside the loop). ----
        try { await client.PostAsJsonAsync("/api/v1/orchestration/stop", new List<Guid> { wfId }, ct); }
        catch { /* best-effort net-zero teardown */ }

        foreach (var key in ScanKeys("data:*"))
            if (!dataKeysBefore.Contains(key))
                factory.L2KeysToCleanup.Add(key);
        foreach (var key in ScanKeys("flag:*"))
            if (!flagKeysBefore.Contains(key))
                factory.L2KeysToCleanup.Add(key);

        // PROBE scratch family net-zero: the deployed probe write-then-deletes skp:keeper:probe:{h} inside the
        // loop and the 30s TTL self-cleans any crash-orphan, so the family must be back to its BEFORE snapshot.
        // Register any straggler (a probe key still inside its 30s TTL window) for deletion so the close-gate scan holds.
        foreach (var key in ScanKeys("keeper:probe:*"))
            if (!probeKeysBefore.Contains(key))
                factory.L2KeysToCleanup.Add(key);

        // ── PHASE-39 TEST-01 — keeper_* Prometheus scrape assertions (query the SERVER :9090, NOT the collector ─
        //    exporter — Pitfall 5). The deployed Keeper container's recover flow (Plan 02 instrumentation) emits
        //    the recover-path keeper_* series for THIS procId; assert each appears in Prometheus with the expected
        //    fault_type/outcome/ProcessorId labels, a non-empty service_instance_id, and NO workflowId.
        //
        //    PROM-SUFFIX (RESEARCH Pitfall 1, the #1 flake risk): Plan 01 created the histogram with unit "s", so
        //    its Prom name is keeper_recovery_duration_seconds_{count,sum,bucket}; Counters gain _total;
        //    keeper_in_flight is a bare gauge (UpDownCounter) and TRANSIENT (→ 0 after the loop), so it is asserted
        //    PRESENCE-best-effort only, never a value (RESEARCH OQ-1). The live stack carried no keeper_* series at
        //    authoring time (the keeper container predates the new Keeper meter), so the _seconds form is written per
        //    Plan 01's unit decision and confirmed on the first GREEN gate run in Plan 04 — see 39-03-SUMMARY.
        using var prom = new PrometheusTestClient();

        // keeper_fault_consumed_total{fault_type=dispatch} — the intake counter for THIS procId.
        var faultConsumed = await prom.PollPromForQuery(
            $"keeper_fault_consumed_total{{fault_type=\"dispatch\",ProcessorId=\"{procId:D}\"}}",
            PrometheusTestClient.VectorNonEmpty, PromPollTimeoutMs, ct);
        Assert.NotNull(faultConsumed);
        AssertKeeperLabels(faultConsumed!.Value);

        // keeper_recovered_total{fault_type=dispatch} — the recover-branch counter.
        var recovered = await prom.PollPromForQuery(
            $"keeper_recovered_total{{fault_type=\"dispatch\",ProcessorId=\"{procId:D}\"}}",
            PrometheusTestClient.VectorNonEmpty, PromPollTimeoutMs, ct);
        Assert.NotNull(recovered);
        AssertKeeperLabels(recovered!.Value);

        // keeper_workflow_paused_total — workflow-scoped (no fault_type tag), ProcessorId-filtered.
        var paused = await prom.PollPromForQuery(
            $"keeper_workflow_paused_total{{ProcessorId=\"{procId:D}\"}}",
            PrometheusTestClient.VectorNonEmpty, PromPollTimeoutMs, ct);
        Assert.NotNull(paused);
        AssertKeeperLabels(paused!.Value);

        // keeper_workflow_resumed_total — emitted in the recover branch alongside resume.
        var resumed = await prom.PollPromForQuery(
            $"keeper_workflow_resumed_total{{ProcessorId=\"{procId:D}\"}}",
            PrometheusTestClient.VectorNonEmpty, PromPollTimeoutMs, ct);
        Assert.NotNull(resumed);
        AssertKeeperLabels(resumed!.Value);

        // keeper_recovery_duration_seconds_count{outcome=recovered} — histogram (unit "s" → _seconds suffix).
        var recoveryDuration = await prom.PollPromForQuery(
            $"keeper_recovery_duration_seconds_count{{outcome=\"recovered\",ProcessorId=\"{procId:D}\"}}",
            PrometheusTestClient.VectorNonEmpty, PromPollTimeoutMs, ct);
        Assert.NotNull(recoveryDuration);
        AssertKeeperLabels(recoveryDuration!.Value);
    }

    // ====================================================================================================
    // FACT 2 — GIVE-UP (PROBE-04 live). Force the deployed Keeper probe loop to EXHAUST by poisoning the key the
    // PROBE ITSELF reads (skp:data:{entryId}, a GET) as a LIST -> WRONGTYPE on EVERY probe iteration -> after
    // MaxAttempts the container parks the ORIGINAL Fault<T> envelope to keeper-dlq. An in-test probe bound to
    // queue:keeper-dlq catches the parked envelope (proving the park), then ACK-drains it -> net-zero terminal queue.
    // ====================================================================================================
    [Fact]
    public async Task KeeperRecovery_GivesUp_ParksToDlq()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var factory = new RealStackWebAppFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        var hash = typeof(global::Processor.Sample.SampleProcessor).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "SourceHash").Value!;
        var procId = await SeedProcessorAsync(client, hash, ct);
        var stepId = await SeedStepAsync(client, procId, name: "KeeperGiveUpStep", nextStepIds: null, ct);
        var wfId = await SeedWorkflowAsync(client, new List<Guid> { stepId }, cron: "* * * * *", ct);
        await PollForHealthyLivenessAsync(procId, ct);

        factory.ParentIndexMembersToSrem.Add(wfId.ToString("D"));
        factory.L2KeysToCleanup.Add($"skp:{wfId}");
        factory.L2KeysToCleanup.Add($"skp:{wfId}:{stepId}");

        var probeKeysBefore = ScanKeys("keeper:probe:*");

        // Build a synthetic Fault<ExecutionResult> whose inner carries a KNOWN entryId. The deployed Keeper probe
        // loop reads skp:data:{entryId} as its FIRST op each iteration — poison THAT key as a LIST so the probe's
        // StringGetAsync throws WRONGTYPE on EVERY attempt -> the loop exhausts MaxAttempts -> GaveUp -> the
        // container parks the ORIGINAL Fault<ExecutionResult> envelope to keeper-dlq (D-09/D-10).
        var gCorr = NewId.NextGuid();
        var gBlobHash = MessageIdentity.HashBlob("keeper-giveup-result-trip");
        var gManifestJson = JsonSerializer.Serialize(new[] { gBlobHash });
        var gEntryId = MessageIdentity.HashManifest(gManifestJson);
        var giveUpH = MessageIdentity.ComputeH(gCorr, wfId, stepId, procId, gEntryId);

        var probeDataPoisonKey = L2ProjectionKeys.ExecutionData(gEntryId);   // skp:data:{entryId} — the probe's READ
        await ArmWrongTypePoisonAsync(probeDataPoisonKey, ct);
        factory.L2KeysToCleanup.Add(probeDataPoisonKey);

        // The short-lived in-test IBusControl PUBLISHES the synthetic Fault<ExecutionResult> and binds a probe on
        // queue:keeper-dlq to CATCH the parked envelope, then ACK-drains it (net-zero terminal queue for Phase 39).
        var bus = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            cfg.Host("localhost", 5673, "/", h => { h.Username("guest"); h.Password("guest"); });
            cfg.ReceiveEndpoint(KeeperQueues.DeadLetter, e =>
                e.Consumer(() => new KeeperDlqProbe(_parkedDlqHashes)));
        });
        await bus.StartAsync(ct);
        try
        {
            await PublishSyntheticResultFaultAsync(bus, procId, stepId, wfId, gCorr, giveUpH, gEntryId, ct);

            // Poll the keeper-dlq probe until the parked envelope's inner H is seen. With the deployed default
            // Probe (5s × 12 = 60s) the loop exhausts ~60s after intake; allow the full window + park latency.
            var parked = await PollForDlqParkAsync(giveUpH, ct);
            Assert.Equal(giveUpH, parked);

            // 2-replica drain (Phase-39): the synthetic Fault<ExecutionResult> is PUBLISHED, so BOTH keeper
            // replicas independently consume + give up + park to keeper-dlq (~simultaneously, ~60s after intake).
            // PollForDlqParkAsync returns on the FIRST park; keep the in-test probe alive briefly so it also
            // ACK-drains the SECOND replica's park before the bus stops — otherwise keeper-dlq is left at depth 1
            // and the close gate's keeper-dlq==0 invariant fails.
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
        finally
        {
            await bus.StopAsync(ct);
        }

        // Phase-39 net-zero: deterministically drain keeper-dlq. The synthetic Fault<ExecutionResult> is
        // PUBLISHED, so BOTH keeper replicas can independently give up + park to keeper-dlq, at DIFFERENT times
        // (>10s apart) — a timing-based in-test-probe drain misses the late one and leaves keeper-dlq at depth 1,
        // failing the close gate's keeper-dlq==0 invariant. The park was already PROVEN above via the probe;
        // purge the terminal queue here so the invariant holds regardless of replica/park timing.
        await PurgeKeeperDlqAsync(ct);

        // Net-zero teardown: stop the workflow; the keeper-dlq parked message was ACK-drained by the in-test probe
        // (the terminal queue is purged so the Phase-39 close-gate snapshot stays net-zero); the poisoned
        // skp:data:{entryId} key is registered for deletion; assert the skp:keeper:probe:* family is net-zero.
        try { await client.PostAsJsonAsync("/api/v1/orchestration/stop", new List<Guid> { wfId }, ct); }
        catch { /* best-effort net-zero teardown */ }

        foreach (var key in ScanKeys("keeper:probe:*"))
            if (!probeKeysBefore.Contains(key))
                factory.L2KeysToCleanup.Add(key);

        // ── PHASE-39 TEST-02 — keeper_* Prometheus scrape assertions for the GIVE-UP flow. The synthetic carrier ─
        //    is a Fault<ExecutionResult> (fault_type=result), and the loop EXHAUSTS MaxAttempts → the deployed
        //    container parks to keeper-dlq (reason=probe_exhausted) and records recovery_duration{outcome=gave_up},
        //    while each WRONGTYPE probe iteration increments keeper_l2_probe_failed for this procId. Assert each
        //    appears in Prometheus with the expected labels, a non-empty service_instance_id, and NO workflowId.
        //    Prom-suffix per Plan 01's unit "s" decision (_seconds for the histogram); confirmed on the Plan-04 gate.
        using var prom = new PrometheusTestClient();

        // keeper_dlq_pushed_total{reason=probe_exhausted,fault_type=result} — the give-up park counter.
        var dlqPushed = await prom.PollPromForQuery(
            $"keeper_dlq_pushed_total{{reason=\"probe_exhausted\",fault_type=\"result\",ProcessorId=\"{procId:D}\"}}",
            PrometheusTestClient.VectorNonEmpty, PromPollTimeoutMs, ct);
        Assert.NotNull(dlqPushed);
        AssertKeeperLabels(dlqPushed!.Value);

        // keeper_recovery_duration_seconds_count{outcome=gave_up} — histogram (unit "s" → _seconds suffix).
        var recoveryDuration = await prom.PollPromForQuery(
            $"keeper_recovery_duration_seconds_count{{outcome=\"gave_up\",ProcessorId=\"{procId:D}\"}}",
            PrometheusTestClient.VectorNonEmpty, PromPollTimeoutMs, ct);
        Assert.NotNull(recoveryDuration);
        AssertKeeperLabels(recoveryDuration!.Value);

        // keeper_l2_probe_failed_total — incremented per WRONGTYPE probe iteration (carries ProcessorId, Plan 02).
        var probeFailed = await prom.PollPromForQuery(
            $"keeper_l2_probe_failed_total{{ProcessorId=\"{procId:D}\"}}",
            PrometheusTestClient.VectorNonEmpty, PromPollTimeoutMs, ct);
        Assert.NotNull(probeFailed);
        AssertKeeperLabels(probeFailed!.Value);
    }

    // ── PHASE-39 label-shape helper (TEST-01/02): every keeper_* series must carry a non-empty ──────────
    //    service_instance_id (ambient from the Keeper console's resource) and NO workflowId label (the
    //    cardinality ban — T-39-03/D-08; mirrors MetricsRoundTripE2ETests.AssertBusinessLabels:243-248). ─
    private static void AssertKeeperLabels(JsonElement data)
    {
        var metric = FirstMetricObject(data);

        Assert.True(
            TryGetNonEmpty(metric, "service_instance_id", out _),
            "keeper_* series must carry a non-empty service_instance_id label (ambient from the Keeper resource).");

        // Cardinality constraint (T-39-03): NO workflowId / WorkflowId label on any keeper_* series.
        foreach (var prop in metric.EnumerateObject())
        {
            Assert.False(
                string.Equals(prop.Name, "workflowId", StringComparison.OrdinalIgnoreCase),
                "keeper_* series must NOT carry a workflowId label (cardinality DoS mitigation).");
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

    // ====================================================================================================
    // In-test capture probes (the running Keeper container is the producer; these are the observers).
    // ====================================================================================================

    /// <summary>
    /// Binds <c>queue:orchestrator-result</c> to CATCH the verbatim <see cref="ExecutionResult"/> the running
    /// Keeper container re-injects on <see cref="ProbeOutcome.Recovered"/> (PROBE-03, second type). Records the
    /// inner H + CorrelationId + StepId so the test asserts the re-inject reached the correct origin endpoint by
    /// type, verbatim (same H). No re-deserialize — the bare inner instance is delivered.
    /// </summary>
    // Phase-39: observes the keeper's recovered-branch ResumeWorkflow (fanout) — proves the verbatim result
    // recovery via the carried H + CorrelationId without racing the orchestrator for queue:orchestrator-result
    // (the keeper Sends the verbatim ExecutionResult there point-to-point, then publishes this ResumeWorkflow).
    private sealed class ReinjectedResultProbe(ConcurrentBag<(string h, Guid corr, Guid step)> captured)
        : IConsumer<ResumeWorkflow>
    {
        public Task Consume(ConsumeContext<ResumeWorkflow> context)
        {
            var m = context.Message;
            captured.Add((m.H, m.CorrelationId, Guid.Empty));   // ResumeWorkflow carries no StepId — unused by the assertion
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Binds <c>queue:keeper-dlq</c> to CATCH the ORIGINAL <c>Fault&lt;ExecutionResult&gt;</c> envelope the running
    /// Keeper container parks on <see cref="ProbeOutcome.GaveUp"/> (PROBE-04). The double-<c>.Message</c> unwrap
    /// reads the inner H (proving the envelope — not the bare inner — was parked: <c>context.Message</c> is a
    /// <c>Fault&lt;T&gt;</c> carrying <c>Exceptions[]</c>). Capturing == acking, so this DRAINS the terminal queue
    /// (net-zero for the Phase-39 close gate — keeper-dlq must be empty in both snapshots).
    /// </summary>
    // Phase-39: deterministically drain keeper-dlq via the rabbitmq management API (host port 15673). Used by
    // the give-up test's net-zero teardown to remove any 2-replica duplicate park the in-test probe missed —
    // the park is already proven; this guarantees the close gate's keeper-dlq==0 snapshot regardless of timing.
    private static async Task PurgeKeeperDlqAsync(CancellationToken ct)
    {
        using var http = new System.Net.Http.HttpClient { BaseAddress = new Uri("http://localhost:15673") };
        var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes("guest:guest"));
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", auth);
        try { await http.DeleteAsync("/api/queues/%2F/keeper-dlq/contents", ct); }
        catch { /* best-effort terminal-queue drain */ }
    }

    // KHARD-02 (D-A5): read the live keeper-dlq depth via the rabbitmq mgmt API (host port 15673), the same
    // HttpClient + Basic-auth(guest:guest) shape as PurgeKeeperDlqAsync. GET /api/queues/%2F/keeper-dlq returns
    // a JSON object with an integer `messages` property. On ANY exception or a missing property we return
    // int.MaxValue — "can't read" is treated as "not empty" so the drain loop keeps trying rather than
    // false-passing on a transient mgmt-API hiccup.
    private static async Task<int> GetKeeperDlqDepthAsync(CancellationToken ct)
    {
        using var http = new System.Net.Http.HttpClient { BaseAddress = new Uri("http://localhost:15673") };
        var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes("guest:guest"));
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", auth);
        try
        {
            var json = await http.GetStringAsync("/api/queues/%2F/keeper-dlq", ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("messages").GetInt32();
        }
        catch
        {
            // ANY failure — network/auth error, malformed body, or a MISSING `messages` property
            // (GetProperty throws KeyNotFoundException) — is treated as "not empty" (int.MaxValue) so the
            // drain loop keeps trying rather than false-passing on a transient mgmt-API hiccup.
            return int.MaxValue;
        }
    }

    // KHARD-02 (D-A5): poll-until-stably-empty. keeper-dlq is terminal (no prod consumer), so each
    // late 2-replica park must be actively re-purged; polling alone never reaches 0. The window must
    // exceed the >10s inter-replica give-up gap. Fails loudly on timeout so a teardown regression
    // surfaces here, NOT silently (Pitfall 2: the gate stays snapshot-only — no gate-side purge).
    private static async Task DrainKeeperDlqUntilStablyEmptyAsync(CancellationToken ct)
    {
        var pollInterval   = TimeSpan.FromSeconds(2);
        var stabilityWindow= TimeSpan.FromSeconds(15);   // > the ">10s apart" inter-replica gap
        var maxTimeout     = TimeSpan.FromSeconds(90);
        var deadline       = DateTime.UtcNow + maxTimeout;
        DateTime? emptySince = null;

        while (DateTime.UtcNow < deadline)
        {
            await PurgeKeeperDlqAsync(ct);                       // re-purge any late park
            await Task.Delay(pollInterval, ct);
            var depth = await GetKeeperDlqDepthAsync(ct);
            if (depth == 0)
            {
                emptySince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - emptySince.Value >= stabilityWindow)
                    return;                                      // stably empty for the full window
            }
            else
            {
                emptySince = null;                              // a late park reset the window
            }
        }
        Assert.Fail($"keeper-dlq did not stay empty for {stabilityWindow.TotalSeconds:F0}s within the {maxTimeout.TotalSeconds:F0}s drain budget (late give-up park likely raced the snapshot).");
    }

    private sealed class KeeperDlqProbe(ConcurrentBag<string> capturedHashes)
        : IConsumer<Fault<ExecutionResult>>
    {
        public Task Consume(ConsumeContext<Fault<ExecutionResult>> context)
        {
            capturedHashes.Add(context.Message.Message.H);   // double .Message — the parked envelope's verbatim inner
            return Task.CompletedTask;
        }
    }

    // ---- WRONGTYPE poison arm (git a6c6825 circuit-breaker E2E recipe, verbatim — same as the spike) ----

    // A LIST key makes any subsequent String op (StringSetAsync / StringGetAsync) throw WRONGTYPE on EVERY
    // attempt — a genuine, deterministic infra fault. Poison ONLY the named INFRA op (Pitfall 3): the dispatch
    // dedup-gate flag[dispatch.H] read (recover fact) or the probe's skp:data:{entryId} read (give-up fact).
    private static async Task ArmWrongTypePoisonAsync(string key, CancellationToken ct)
    {
        await using var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
        var db = mux.GetDatabase();
        await db.KeyDeleteAsync(key);                 // start clean (a leftover string would not throw)
        await db.ListRightPushAsync(key, "poison");   // LIST type -> a subsequent String op throws WRONGTYPE
    }

    // Clear an armed WRONGTYPE LIST poison so the receiver's real String op can succeed (the duplicate-collapse
    // proof rides the receiver flag[H] gate, NOT the lingering infra fault) — simulates L2 return.
    private static async Task ClearPoisonAsync(string key, CancellationToken ct)
    {
        await using var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
        await mux.GetDatabase().KeyDeleteAsync(key);
    }

    // ---- Synthetic Fault<ExecutionResult> publish (the second-type carrier; the live orchestrator result-hop
    //      trip is not reachable without orchestration started — the spike documents this and uses the same
    //      synthetic). Publishing the BARE inner would route to the real orchestrator (business-ack, no fault). ----
    private static async Task PublishSyntheticResultFaultAsync(
        IBusControl bus, Guid procId, Guid stepId, Guid wfId, Guid corr, string h, string entryId, CancellationToken ct)
    {
        var synthetic = new ExecutionResult(wfId, stepId, procId, StepOutcome.Completed)
        {
            CorrelationId = corr,
            ExecutionId = NewId.NextGuid(),
            EntryId = entryId,
            H = h,
        };
        // Fault<T> is a MassTransit framework interface — publish it via a message INITIALIZER (anonymous object);
        // MassTransit's dynamic-proxy initializer fills FaultId/Timestamp/Exceptions with defaults and binds the
        // inner Message verbatim. The running Keeper container's FaultExecutionResultConsumer consumes it (D-09).
        await bus.Publish<Fault<ExecutionResult>>(new { Message = synthetic }, ct);
    }

    // ---- Poll the in-test ResumeWorkflow probe until the keeper's recovered-branch resume for this H is seen. ----
    private async Task<(string h, Guid corr, Guid step)> PollForResultReinjectAsync(string expectedH, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(OutputPollTimeoutMs);
        var delay = 500;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var cap in _reinjectedResults)
                if (cap.h == expectedH)
                    return cap;   // the Keeper container probe-recovered + re-injected the verbatim result.

            await Task.Delay(Math.Min(delay, 2_000), ct);
            delay = Math.Min(delay * 2, 2_000);
        }

        Assert.Fail(
            $"No keeper ResumeWorkflow carrying inner H={expectedH} was observed within {OutputPollTimeoutMs}ms — " +
            $"the deployed Keeper container did not probe-recover + re-inject the verbatim result (Send to " +
            $"queue:{OrchestratorQueues.Result}) + publish ResumeWorkflow. Confirm the full compose stack incl. a " +
            "REBUILT keeper is up healthy.");
        throw new InvalidOperationException("unreachable"); // Assert.Fail throws — keeps the compiler happy.
    }

    // ---- Poll the in-test keeper-dlq probe until the parked envelope's inner H is seen (GaveUp). ----
    private async Task<string> PollForDlqParkAsync(string expectedH, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(DlqParkPollTimeoutMs);
        var delay = 1_000;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var h in _parkedDlqHashes)
                if (h == expectedH)
                    return h;   // the Keeper container gave up + parked the ORIGINAL Fault<T> envelope to keeper-dlq.

            await Task.Delay(Math.Min(delay, 3_000), ct);
            delay = Math.Min(delay * 2, 3_000);
        }

        Assert.Fail(
            $"No parked Fault<T> with inner H={expectedH} arrived on queue:{KeeperQueues.DeadLetter} within " +
            $"{DlqParkPollTimeoutMs}ms — the deployed Keeper probe loop did not exhaust MaxAttempts + park. " +
            "Confirm the keeper container is rebuilt + healthy (operators may set a small Probe__MaxAttempts to " +
            "shorten the give-up window — see the 36-04-SUMMARY runbook).");
        throw new InvalidOperationException("unreachable"); // Assert.Fail throws — keeps the compiler happy.
    }

    // ---- ES downstream-effect query + hit count (the zero-duplicate / exactly-once assertion) ----

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
    /// Counts the downstream-effect hits for the query (hits.total.value). Polls briefly past the first hit so a
    /// (hypothetical) duplicate effect — if the dedup gate ever failed — would also be ingested before the count
    /// is read, keeping the exactly-once assertion honest rather than racy.
    /// </summary>
    private static async Task<int> CountEsHitsAsync(string query, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri("http://localhost:9200/") };

        await Task.Delay(8_000, ct);   // settle window so a leaked duplicate would have been ingested.

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
    /// <see cref="L2ProjectionKeys.ExecutionData(string)"/>; <c>flag:*</c> = <see cref="L2ProjectionKeys.Flag"/>;
    /// <c>keeper:probe:*</c> = <see cref="L2ProjectionKeys.KeeperProbe(string)"/>). Content addresses are
    /// server-derived, so the keys cannot be addressed a priori — enumerate the family.
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
            Name: $"keeper-recover-wf-{Guid.NewGuid():N}",
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
    /// <see cref="DisposeAsync"/>. REUSED VERBATIM from
    /// <see cref="global::BaseApi.Tests.Orchestrator.FaultRecoverySpikeE2ETests"/> — the env-var-in-ctor host
    /// overrides + L2KeysToCleanup / ParentIndexMembersToSrem discipline are identical.
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
        /// L2 keys (production "skp:" prefix) the test registers for deletion on teardown — every armed
        /// WRONGTYPE poison key + every run-minted skp:data:{64hex} / skp:flag:{64hex} / skp:keeper:probe:{64hex}
        /// key so the close-gate <c>redis-cli --scan</c> net-zero invariant holds. The steady-state
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
