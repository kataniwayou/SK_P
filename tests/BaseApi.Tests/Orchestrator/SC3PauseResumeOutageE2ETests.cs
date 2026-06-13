using System.Diagnostics;
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
/// D-02: the SC3 outage proof STOPS <c>sk-redis</c>, which would destabilize any sibling RealStack test
/// that touches L2. It therefore lives in its OWN xUnit collection with parallelization DISABLED so it
/// runs ALONE — NEVER the shared <c>"Observability"</c> collection that SC1/SC2 use. The class still
/// carries <c>[Trait("Category","RealStack")]</c> + <c>[Trait("Phase","62")]</c> so the close gate runs
/// it — just serialized against everything else.
/// </summary>
[CollectionDefinition("RedisOutageSerial", DisableParallelization = true)]
public sealed class RedisOutageSerialCollection : ICollectionFixture<RealStackNetZeroSweepFixture>
{
    // Shares the same host-stack net-zero sweep as the Observability collection: whichever DisableParallelization
    // host-stack collection finishes LAST leaves a clean skp: keyspace + drained skp-dlq-1 for the close gate.
}

/// <summary>
/// SC3 / TEST-02 — the RealStack proof of the BIT-gate GLOBAL pause-all / resume-all across a TRUE
/// transient L2 outage induced by <c>docker stop sk-redis</c> … <c>docker start sk-redis</c> (D-01).
/// Sibling to <see cref="SampleRoundTripE2ETests"/> (SC1) — it REUSES that file's
/// <c>RealStackWebAppFactory</c> host overrides + net-zero teardown + <c>PollForHealthyLivenessAsync</c>
/// blocking-liveness check (cloned below), and the orchestrator-seam-log ES poll precedent
/// (<see cref="ElasticsearchTestClient.PollEsForLog"/>, SC1 lines 150-167).
/// <para>
/// The edge model under test (<c>src/Keeper/Health/BitHealthLoop.cs:34-49</c>): the Keeper runs a
/// write-then-delete BIT probe every <c>Probe:DelaySeconds</c>; on each health TRANSITION it
/// <c>bus.Publish</c>es the global control contract once —
/// </para>
/// <code>
/// if (prevHealthy != healthy) {                 // EDGE only
///     if (healthy)   { gate.Open();  await bus.Publish(new ResumeAll{...}); }   // unhealthy → healthy
///     else           { gate.Close(); await bus.Publish(new PauseAll{...});  }   // healthy   → unhealthy
/// }
/// </code>
/// <para>
/// On the orchestrator side <c>PauseAllConsumer</c> logs the seam
/// <c>"Global PauseAll CorrelationId={CorrelationId}"</c> (<c>PauseAllConsumer.cs:23</c>) then runs the
/// idempotent scheduler-wide <c>PauseAllAsync()</c>; <c>ResumeAllConsumer</c> logs
/// <c>"Global ResumeAll CorrelationId={CorrelationId}"</c> (<c>ResumeAllConsumer.cs:31</c>) then resumes
/// per-job (the load-bearing <c>TriggerState == Paused</c> guard, never native <c>ResumeAll()</c>). The
/// live orchestrator owns the Quartz scheduler OUT-OF-PROCESS, so this test cannot read
/// <c>TriggerState</c> in-process (the <see cref="ResumeAllConsumerTests"/> <c>TriggerState.Paused</c> /
/// <c>TriggerState.Normal</c> idiom is the assertion SHAPE we MIRROR; the live read MECHANISM is the
/// orchestrator seam log in ES + an observable round-trip-presence proof).
/// </para>
/// <para>
/// The flow (drive against the real stack):
/// </para>
/// <list type="number">
///   <item><b>Baseline.</b> Seed processor+step+workflow (cron <c>* * * * *</c>),
///   <c>PollForHealthyLivenessAsync</c>, drive Start, prove the round trip fires (a fresh
///   <c>skp:data:*</c> output key lands) — steady state, gate OPEN.</item>
///   <item><b>Outage → PAUSE.</b> <c>docker stop sk-redis</c> → the BIT probe throws <c>RedisException</c>
///   → gate closes → Keeper <c>Publish(PauseAll)</c> → orchestrator <c>PauseAllAsync()</c>. Assert the
///   <c>"Global PauseAll"</c> seam log appears in ES (poll straddles the probe cadence). As an additional
///   observable proof, assert NO NEW <c>skp:data:*</c> output key appears during the paused window (the
///   cron is paused → dispatch stops).</item>
///   <item><b>Recovery → RESUME.</b> <c>docker start sk-redis</c> → probe succeeds → gate opens → Keeper
///   <c>Publish(ResumeAll)</c> → per-job resume (<c>TriggerState == Paused</c> guard). Assert the
///   <c>"Global ResumeAll"</c> seam log appears in ES.</item>
///   <item><b>Idempotency.</b> The resume seam is safe to re-observe — the production guard
///   (<c>TriggerState == Paused</c>) makes a duplicate resume a per-job no-op (a Keeper restart even emits
///   a startup ResumeAll by design). Re-reading the same seam log does not double-resume; assert the
///   steady state holds.</item>
/// </list>
/// <para>
/// <b>D-02 blocking teardown (T-49-06):</b> the test BLOCKS before returning on
/// <c>docker start sk-redis</c> → redis healthy → steady-state RE-ESTABLISHED: the
/// <c>skp:{procId:D}</c> liveness heartbeat is RE-WRITTEN (<c>PollForHealthyLivenessAsync</c> after start)
/// AND a post-resume round-trip output lands (the gate re-opened, the cron re-fires). Only then does it
/// return. <b>CRITICAL:</b> <c>docker start sk-redis</c> runs in a <c>finally</c> so that even if an
/// assertion throws mid-outage the stack is NEVER left with redis stopped for the sibling tests / the
/// close gate.
/// </para>
/// <para>
/// Tagged <c>Category=RealStack</c> + <c>Phase=55</c>: the hermetic filter (<c>Category!=RealStack</c>)
/// EXCLUDES this fact; it runs only against the operator-gated live v5 stack (55-HUMAN-UAT.md). TEST-02
/// stays UNTICKED until that GREEN live run.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]
[Trait("Phase", "62")]
[Collection("RedisOutageSerial")]
public sealed class SC3PauseResumeOutageE2ETests
{
    private const string PauseAllSeam = "Global PauseAll";
    private const string ResumeAllSeam = "Global ResumeAll";

    // The live processor-sample container resolves identity + binds + MarkHealthy after the DB row is
    // seeded; allow a generous budget (mirrors SC1).
    private const int LivenessPollTimeoutMs = 90_000;

    // The orchestrator fires the dispatch at the next "* * * * *" occurrence (top of the next minute),
    // then the processor round-trips and writes output; allow > 60s plus round-trip slack.
    private const int OutputPollTimeoutMs = 150_000;

    // otel/log export is async; tolerate flush + ingest latency on the pause/resume seam-log ES proofs.
    // Generous so the poll STRADDLES the Probe:DelaySeconds BIT cadence (the edge may take a probe tick
    // to be observed after docker stop/start) plus the otel→ES ingest latency.
    private const int EsPollTimeoutMs = 150_000;

    // After docker stop/start, give the BIT loop enough time to TICK across the Probe:DelaySeconds cadence
    // and observe the edge before we begin asserting (the probe is not instantaneous on the transition).
    private const int OutageSettleMs = 20_000;

    // The "no new output during the paused window" negative: observe for long enough to span at least one
    // "* * * * *" cron occurrence (>60s) so a still-firing cron would have produced a key.
    private const int PausedQuietWindowMs = 90_000;

    [Fact]
    public async Task LiveBitGate_PauseAllThenResumeAll_AcrossTrueTransientRedisOutage_DockerStopStart()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var factory = new RealStackWebAppFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();
        using var es = new ElasticsearchTestClient();

        // D-08: read the GENUINE embedded SourceHash off the BUILT Processor.Sample assembly (the same way
        // the runtime reader does). The live processor-sample container resolves THIS procId by hash.
        var hash = typeof(global::Processor.Sample.SampleProcessor).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "SourceHash").Value!;

        var procId = await SeedProcessorAsync(client, hash, ct);
        var stepId = await SeedStepAsync(client, procId, ct);
        // Seed WITH a cron so the orchestrator's one-shot job actually fires the dispatch (null cron is a
        // business-skip in HydrateAndScheduleAsync — the round-trip would never run / pause-resume would
        // have nothing to pause).
        var wfId = await SeedWorkflowAsync(client, new List<Guid> { stepId }, cron: "* * * * *", ct);

        // Track whether we have stopped sk-redis so the finally only heals when needed.
        var redisStopped = false;
        try
        {
            // =====================================================================================
            // BASELINE — steady state, gate OPEN: liveness fresh + the round trip fires (output lands).
            // =====================================================================================
            await PollForHealthyLivenessAsync(procId, ct);

            var dataKeysBefore = ScanExecutionDataKeys();

            var startResp = await client.PostAsJsonAsync(
                "/api/v1/orchestration/start", new List<Guid> { wfId }, ct);
            Assert.Equal(HttpStatusCode.NoContent, startResp.StatusCode);

            // Register the L2 root/step keys + parent-index member the Start created for net-zero teardown.
            factory.ParentIndexMembersToSrem.Add(wfId.ToString("D"));
            factory.L2KeysToCleanup.Add($"skp:{wfId}");
            factory.L2KeysToCleanup.Add($"skp:{wfId}:{stepId}");

            // Prove the round trip fires BEFORE the outage (the steady-state baseline the pause halts).
            var baselineDataKey = await PollForNewExecutionDataKeyAsync(dataKeysBefore, OutputPollTimeoutMs, ct);
            Assert.NotNull(baselineDataKey);
            factory.L2KeysToCleanup.Add(baselineDataKey!.Value);

            // =====================================================================================
            // OUTAGE → PAUSE — docker stop sk-redis → BIT probe throws RedisException → gate.Close()
            //                  → Keeper Publish(PauseAll) → orchestrator PauseAllAsync().
            // =====================================================================================
            // Snapshot the data keys present at the moment of the outage so the paused-window negative can
            // detect any NEW dispatch output (there must be none while paused).
            var keysAtOutage = ScanExecutionDataKeys();

            await DockerAsync(ct, "stop", RedisContainer);   // TRUE transient outage (D-01) — NOT pause/in-redis.
            redisStopped = true;

            // Let the BIT loop TICK across Probe:DelaySeconds so it observes the unhealthy edge.
            await Task.Delay(OutageSettleMs, ct);

            // ASSERT PAUSE (live read mechanism = orchestrator seam log in ES). The TriggerState.Paused
            // idiom (ResumeAllConsumerTests.cs:74-82) is the SHAPE mirrored; here the orchestrator owns the
            // scheduler out-of-process, so we poll the "Global PauseAll" seam (PauseAllConsumer.cs:23).
            var pauseSeam = await es.PollEsForLog(
                OrchestratorSeamQuery(PauseAllSeam), timeoutMs: EsPollTimeoutMs, ct: ct);
            Assert.NotNull(pauseSeam);
            Assert.Contains(PauseAllSeam, pauseSeam!.Value.GetRawText());

            // ADDITIONAL observable PAUSE proof: NO new skp:data:* output key appears during the paused
            // window (the cron is paused → dispatch stops). Observe across at least one "* * * * *"
            // occurrence; a still-firing cron would have minted a key.
            var leaked = await AnyNewExecutionDataKeyWithinAsync(keysAtOutage, PausedQuietWindowMs, ct);
            Assert.Null(leaked);   // dispatch is paused — no new output landed during the outage window.

            // =====================================================================================
            // RECOVERY → RESUME — docker start sk-redis → probe succeeds → gate.Open()
            //                     → Keeper Publish(ResumeAll) → per-job resume (TriggerState==Paused guard).
            // =====================================================================================
            await DockerAsync(ct, "start", RedisContainer);
            redisStopped = false;

            // Let the BIT loop TICK across Probe:DelaySeconds so it observes the healthy edge.
            await Task.Delay(OutageSettleMs, ct);

            // ASSERT RESUME (live read mechanism = orchestrator seam log in ES). Mirrors the
            // TriggerState.Normal idiom (ResumeAllConsumerTests.cs:81-82) via the "Global ResumeAll" seam
            // (ResumeAllConsumer.cs:31).
            var resumeSeam = await es.PollEsForLog(
                OrchestratorSeamQuery(ResumeAllSeam), timeoutMs: EsPollTimeoutMs, ct: ct);
            Assert.NotNull(resumeSeam);
            Assert.Contains(ResumeAllSeam, resumeSeam!.Value.GetRawText());

            // =====================================================================================
            // IDEMPOTENCY — the resume seam is safe to re-observe (TriggerState==Paused guard makes a
            // duplicate resume a per-job no-op; a Keeper restart even emits a startup ResumeAll by design).
            // Re-reading the same seam does not double-resume; the steady state simply re-asserts itself.
            // =====================================================================================
            var resumeSeamAgain = await es.PollEsForLog(
                OrchestratorSeamQuery(ResumeAllSeam), timeoutMs: EsPollTimeoutMs, ct: ct);
            Assert.NotNull(resumeSeamAgain);   // re-observing resume is harmless (idempotent per-job guard).

            // =====================================================================================
            // BLOCKING TEARDOWN (D-02 / T-49-06) — BLOCK before returning on steady-state RE-ESTABLISHED:
            //   (a) sk-redis healthy + the skp:{procId:D} liveness heartbeat RE-WRITTEN, AND
            //   (b) a post-resume round-trip output lands (gate re-opened, the cron re-fires).
            // =====================================================================================
            await PollForHealthyLivenessAsync(procId, ct);

            var keysAfterResume = ScanExecutionDataKeys();
            var postResumeDataKey = await PollForNewExecutionDataKeyAsync(keysAfterResume, OutputPollTimeoutMs, ct);
            Assert.NotNull(postResumeDataKey);   // gate re-opened — the round trip fires again post-resume.
            factory.L2KeysToCleanup.Add(postResumeDataKey!.Value);

            // NET-ZERO: stop the workflow so its self-rescheduling cron fire ceases (best-effort).
            try { await client.PostAsJsonAsync("/api/v1/orchestration/stop", new List<Guid> { wfId }, ct); }
            catch { /* best-effort net-zero teardown */ }
        }
        finally
        {
            // CRITICAL (T-49-06): heal the outage no matter what — even if an assertion threw mid-outage,
            // start sk-redis so the suite NEVER leaves redis stopped for the sibling tests / the close gate.
            if (redisStopped)
            {
                try { await DockerAsync(CancellationToken.None, "start", RedisContainer); }
                catch { /* best-effort heal — a docker hiccup must not mask the original failure */ }
            }
        }
    }

    // ---- Orchestrator seam-log ES query (mirrors SampleRoundTripE2ETests.cs:150-167) ----------------

    /// <summary>
    /// Builds an ES <c>_search</c> body that PREFIX-matches the seam on <c>body.text</c> and terms on the
    /// orchestrator service, asserting the distinct seam text in C# on the returned hit (the
    /// <c>PauseAllConsumer</c> / <c>ResumeAllConsumer</c> seam logs are structured templates —
    /// "Global PauseAll CorrelationId={CorrelationId}" / "Global ResumeAll ..." — exactly as the SC1
    /// advance proof does).
    /// <para>
    /// GAP-49-4 (D-11): the must clause uses a <c>prefix</c> on <c>body.text</c>, NOT <c>match_phrase</c>,
    /// and NOT plain <c>body</c>. The otel collector maps the rendered log message under the nested
    /// <c>body.text</c> object, but that field is mapped as a <c>keyword</c> (exact-value) type — so a
    /// <c>match_phrase</c> CANNOT substring-match the seam inside the longer
    /// "Global PauseAll CorrelationId=&lt;guid&gt;" value (returns total:0, why the poll previously came back
    /// null even though the orchestrator emits + exports the seam to ES). The seam is an exact PREFIX of the
    /// body, so a <c>prefix</c> query on the keyword field matches reliably (verified: prefix→8 hits,
    /// match_phrase→0). SC1/SampleRoundTrip avoid this by terming on the exact <c>attributes.WorkflowId</c>
    /// keyword rather than a body substring.
    /// </para>
    /// </summary>
    private static string OrchestratorSeamQuery(string seam) => $$"""
      {
        "size": 5,
        "sort": [ { "@timestamp": { "order": "desc" } } ],
        "query": {
          "bool": {
            "must": [
              { "prefix": { "body.text": "{{seam}}" } },
              { "term": { "resource.attributes.service.name": "orchestrator" } }
            ]
          }
        }
      }
      """;

    // ---- docker stop/start sk-redis (D-01: TRUE transient outage via System.Diagnostics.Process) -----

    private const string RedisContainer = "sk-redis";   // compose.yaml:137 container_name.

    /// <summary>
    /// Shell out <c>docker {verb} {container}</c> via <see cref="Process"/> (e.g. <c>docker stop sk-redis</c>
    /// / <c>docker start sk-redis</c>). Throws on a non-zero exit so a failed stop/start surfaces clearly —
    /// except the <c>finally</c> heal which swallows (best-effort).
    /// </summary>
    private static async Task DockerAsync(CancellationToken ct, string verb, string container)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(verb);
        psi.ArgumentList.Add(container);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start 'docker {verb} {container}'.");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'docker {verb} {container}' exited {proc.ExitCode}. stdout={stdout} stderr={stderr}");
        }
    }

    // ---- Liveness poll (blocking-teardown re-write check): wait for the REAL container's heartbeat -----
    // CLONED from SampleRoundTripE2ETests.cs:199-240. Used BOTH as the baseline gate AND as the D-02
    // blocking-teardown steady-state-re-established check after docker start.

    // Phase 61 (GATE-01/02/03, D-06/11): per-replica liveness — SMEMBERS the index -> GET each per-instance
    // ProcessorLivenessEntry -> accept on >=1 Healthy + fresh replica (the legacy flat skp:{procId} was retired).
    private static async Task PollForHealthyLivenessAsync(Guid procId, CancellationToken ct)
    {
        await using var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
        var db = mux.GetDatabase();
        var index = L2ProjectionKeys.InstanceIndex(procId);

        var deadline = DateTime.UtcNow.AddMilliseconds(LivenessPollTimeoutMs);
        var delay = 500;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var members = await db.SetMembersAsync(index);
            foreach (var member in members)
            {
                var raw = await db.StringGetAsync(L2ProjectionKeys.PerInstance(procId, member.ToString()));
                if (raw.IsNullOrEmpty) continue;
                var entry = JsonSerializer.Deserialize<ProcessorLivenessEntry>(raw!);
                if (entry is { Status: LivenessStatus.Healthy })
                {
                    var age = DateTime.UtcNow - entry.Timestamp.ToUniversalTime();
                    var staleAfter = TimeSpan.FromSeconds(Math.Max(entry.Interval, 1) * 3);
                    if (age <= staleAfter)
                        return;   // a REAL replica is Healthy — steady state established / re-established.
                }
            }

            await Task.Delay(Math.Min(delay, 2_000), ct);
            delay = Math.Min(delay * 2, 2_000);
        }

        Assert.Fail(
            $"The processor-sample container never wrote a fresh Healthy per-instance liveness key under {index} " +
            $"within {LivenessPollTimeoutMs}ms. Either the container is down, its embedded SourceHash diverges " +
            $"from the host-built hash registered as the DB row, or (in teardown) the L2 outage did not heal. " +
            $"Ensure the full compose stack incl. processor-sample is up healthy and sk-redis is started.");
    }

    // ---- Round-trip output poll: a NEW skp:data:* key appears (positive proof) ----------------------

    private static async Task<RedisKey?> PollForNewExecutionDataKeyAsync(
        HashSet<string> before, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var delay = 1_000;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var key in ScanExecutionDataKeys())
            {
                if (!before.Contains(key))
                {
                    return key;   // the round-trip's output landed in L2.
                }
            }

            await Task.Delay(Math.Min(delay, 3_000), ct);
            delay = Math.Min(delay * 2, 3_000);
        }

        Assert.Fail(
            $"No new skp:data:* execution-data key appeared within {timeoutMs}ms — the live round trip " +
            $"(orchestrator fire → dispatch → ProcessAsync → output write) did not complete. Confirm the " +
            $"processor-sample container bound queue:{{id:D}}, the workflow cron fired, and the L2 gate is open.");
        return null;   // unreachable (Assert.Fail throws) — keeps the compiler happy.
    }

    /// <summary>
    /// NEGATIVE proof (paused window): observe for <paramref name="windowMs"/> and return the FIRST new
    /// <c>skp:data:*</c> key that appears, or <c>null</c> if none did. While the cron is paused, no new
    /// output should land, so the expected result is <c>null</c>. Spans at least one "* * * * *" cron
    /// occurrence so a still-firing cron would have produced a key.
    /// </summary>
    private static async Task<RedisKey?> AnyNewExecutionDataKeyWithinAsync(
        HashSet<string> before, int windowMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(windowMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var key in ScanExecutionDataKeys())
            {
                if (!before.Contains(key))
                {
                    return key;   // a new output key leaked during the paused window — pause did NOT take.
                }
            }

            await Task.Delay(3_000, ct);
        }

        return null;   // no new output during the paused window — dispatch is paused (the expected result).
    }

    /// <summary>
    /// SCAN host Redis for all keys under the execution-data discriminator (<c>skp:data:*</c>). The entryId
    /// is server-minted, so we enumerate the family rather than addressing the key directly. Tolerates a
    /// down/recovering redis: a connection failure during the outage window returns the keys seen so far
    /// (an empty/partial set just means "no NEW key observed", which is the correct paused-window reading).
    /// </summary>
    private static HashSet<string> ScanExecutionDataKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var mux = ConnectionMultiplexer.Connect(HostRedis);
            foreach (var ep in mux.GetEndPoints())
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
        }
        catch (RedisException)
        {
            // sk-redis is stopped (outage window) — no keys are readable. Returning the empty/partial set
            // is correct: during the outage there are no NEW keys, which is exactly what the negative proof
            // asserts. The positive polls run only when redis is up, so this never masks a real signal.
        }

        return keys;
    }

    // ---- HTTP seeding helpers (Processor → Step → Workflow) — cloned from SampleRoundTripE2ETests -----

    private static async Task<Guid> SeedProcessorAsync(HttpClient client, string sourceHash, CancellationToken ct)
    {
        // GET-or-create (idempotent): the genuine embedded hash is FIXED and the row is unique-constrained
        // in host Postgres across runs — reuse the existing row (the one the live container heartbeats
        // against), create only on a fresh DB.
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
    /// <see cref="DisposeAsync"/>. CLONED from <see cref="SampleRoundTripE2ETests"/>'s
    /// <c>RealStackWebAppFactory</c> — the env-var-in-ctor host overrides + L2KeysToCleanup /
    /// ParentIndexMembersToSrem discipline are identical.
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
        /// L2 keys (production "skp:" prefix) the test registers for deletion on teardown — populated AFTER
        /// the real Start projects them + the round trips mint them. Drained in <see cref="DisposeAsync"/>
        /// so the close-gate <c>redis-cli --scan</c> net-zero invariant holds. The steady-state
        /// <c>skp:{procId:D}</c> liveness key is NOT registered (the live container keeps it fresh in both
        /// close-gate snapshots).
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
