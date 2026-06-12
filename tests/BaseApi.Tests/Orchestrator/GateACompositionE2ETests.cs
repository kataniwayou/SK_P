using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using BaseApi.Tests.Observability.Helpers;
using Messaging.Contracts.Projections;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Gate-A COMPOSITION proof (CFG-08 incompatible → 422; CFG-09 compatible → 204) — the RealStack
/// composition harness that the operator's live close run (Plan 05) executes. This file is excluded
/// from the hermetic suite by <c>[Trait("Category","RealStack")]</c> but MUST COMPILE 0-warning; the
/// autonomous deliverable (Plan 03) is the compiling proof, not its live execution.
/// </summary>
/// <remarks>
/// <para>
/// Reuses the <see cref="SampleRoundTripE2ETests"/> harness WHOLESALE — the same
/// <c>RealStackWebAppFactory</c> (host overrides + net-zero teardown), the same
/// <c>SeedProcessorAsync</c> / <c>SeedConfigSchemaAsync</c> / <c>SeedStepAsync</c> /
/// <c>SeedWorkflowAsync</c> / <c>PollForHealthyLivenessAsync</c> helpers (promoted to <c>internal</c>),
/// and the same <c>[Collection("Observability")]</c> (DisableParallelization +
/// <c>ICollectionFixture&lt;RealStackNetZeroSweepFixture&gt;</c>). No new harness is authored.
/// </para>
/// <para>
/// <b>CFG-08 (incompatible → 422)</b> proves Gate A is the CAUSE of the 422 via three signals (D-06):
/// </para>
/// <list type="number">
///   <item><b>(a) ES clash log, polled FIRST</b> — the Phase-57 D-10 <c>LogError("Gate A
///   incompatibility …")</c> shipped from <c>ProcessorStartupOrchestrator.cs:187</c>, scoped to
///   <c>service.name == "processor-badconfig"</c>. This proves the container BOOTED and RAN Gate A,
///   so "absent liveness" is provably CAUSATION (Gate A withheld health) — not the observationally
///   identical "container is simply down" (RESEARCH Pitfall 1).</item>
///   <item><b>(b) <c>skp:{badId}</c> stably absent</b> — the INVERSE of
///   <c>PollForHealthyLivenessAsync</c>: read across ~3 windows spanning &gt; one 10s heartbeat
///   interval, failing if the liveness key EVER appears (the mechanism — Gate A withheld
///   <c>MarkHealthy</c>, so the heartbeat no-ops).</item>
///   <item><b>(c) Start → 422</b> — <c>POST /api/v1/orchestration/start</c> for a workflow whose graph
///   includes the badconfig processor returns 422 (<c>ProcessorLivenessValidator.cs:33-35</c>:
///   absent liveness → <c>ProcessorNotLive(id,"absent")</c> → UnprocessableEntity) — the OUTCOME.</item>
/// </list>
/// <para>
/// <b>CFG-09 (compatible → 204)</b> proves Gate A is not a false-positive blocker: a
/// <c>Processor.Sample</c> seeded with a COMPATIBLE non-null <c>ConfigSchemaId</c> (Gate A RUNS AND
/// PASSES — not skipped) goes Healthy, writes liveness, and Start returns 204 — the positive
/// <see cref="SampleRoundTripE2ETests"/> flow with the compatible-schema seed.
/// </para>
/// </remarks>
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]   // hermetic filter (Category=RealStack) excludes it; the build gate still COMPILES it
[Trait("Phase", "58")]
[Collection("Observability")]       // DisableParallelization + ICollectionFixture<RealStackNetZeroSweepFixture>
public sealed class GateACompositionE2ETests
{
    // The CFG-08 clash schema sentinel Name — MUST match Plan 04's close-script seed so the live N=3 run
    // is idempotent (schemas have NO uniqueness constraint; the fixed Name is the GET-or-create key).
    private const string ClashSchemaName = "gateA-badconfig-clash";

    // The clash schema definition: types "quantity" as a STRING, while BadConfig(int Quantity) is CLR int.
    // ConfigSchemaCoverageCheck classifies schema-string-vs-CLR-int as a CONFIRMED clash → Gate A withholds
    // MarkHealthy. Carries the draft 2020-12 $schema key (server-side meta-schema validated on write).
    private const string ClashSchemaDefinition =
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"quantity":{"type":"string"}}}""";

    // Host Redis (the real container's keyspace) — mirrors SampleRoundTripE2ETests' host override.
    private const string HostRedis = "localhost:6380,abortConnect=false,connectTimeout=5000";

    // otel/log export is async; tolerate flush + ingest latency on the ES clash-log proof. Generous to
    // cover container boot + identity-resolve + Gate-A-run + otel→ES ingest.
    private const int EsPollTimeoutMs = 120_000;

    [Fact]
    public async Task BadConfig_GateAIncompatible_ClashLogged_LivenessAbsent_Start422()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var factory = new SampleRoundTripE2ETests.RealStackWebAppFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        // Read the GENUINE embedded SourceHash off the BUILT Processor.BadConfig assembly — the same way
        // AssemblyMetadataSourceHashProvider does at runtime (NOT synthetic, NOT recomputed). This is the
        // identity the live processor-badconfig container resolves via GetProcessorBySourceHash(hash).
        var badHash = typeof(global::Processor.BadConfig.BadConfigProcessor).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "SourceHash").Value!;

        // Seed the CLASH config schema (GET-or-create-by-Name; never PUT — frozen-once-referenced 409s),
        // then seed the badconfig Processor row WITH that ConfigSchemaId so Gate A RUNS and CLASHES. The
        // workflow graph includes the badconfig step so the liveness gate evaluates it at Start.
        var clashSchemaId = await SampleRoundTripE2ETests.SeedConfigSchemaAsync(
            client, ClashSchemaName, ClashSchemaDefinition, ct);
        var badId = await SampleRoundTripE2ETests.SeedProcessorAsync(
            client, badHash, ct, configSchemaId: clashSchemaId);
        var badStepId = await SampleRoundTripE2ETests.SeedStepAsync(client, badId, ct);
        // A null-cron workflow never fires a dispatch (HydrateAndScheduleAsync business-skip) — but the
        // Start liveness gate evaluates every participant regardless of cron, so null is correct here:
        // we want the gate to BLOCK at Start, not to drive a round-trip.
        var badWorkflowId = await SampleRoundTripE2ETests.SeedWorkflowAsync(
            client, new List<Guid> { badStepId }, cron: "* * * * *", ct);

        // ---- Signal (a): ES CLASH LOG, polled FIRST (D-06 causation linchpin) ----
        // The badconfig container booted, resolved identity, fetched the clash schema, ran Gate A's
        // ConfigSchemaCoverageCheck, FAILED it, and shipped one Error log via otel → ES. Asserting THIS
        // first upgrades "absent liveness" below from coincidence ("maybe it's just down") to causation
        // ("Gate A withheld health"). Scope to the badconfig service.name + match the shipped message text.
        using var es = new ElasticsearchTestClient();
        // otel maps the rendered message under the nested "body.text" object, which is NOT
        // phrase-searchable (the proven CorrelationPropagationE2ETests / OrchestrationLogsE2ETests
        // precedent — never `match` on "body"). Pin the STRUCTURED log via `term`s: the badconfig
        // service.name + this run's ProcessorId (per-test-scoped, robust to prior ES history) + the
        // shipped {OriginalFormat} template (ProcessorStartupOrchestrator.cs:188). The rendered text is
        // then asserted in C# via GetRawText() (which includes body.text + {OriginalFormat}).
        var clashLogQuery = $$"""
          {
            "size": 5,
            "sort": [ { "@timestamp": { "order": "desc" } } ],
            "query": {
              "bool": {
                "must": [
                  { "term": { "resource.attributes.service.name": "processor-badconfig" } },
                  { "term": { "attributes.ProcessorId": "{{badId}}" } },
                  { "term": { "attributes.{OriginalFormat}": "Gate A incompatibility for processor {ProcessorId} config schema {ConfigSchemaId}: {Clash}" } }
                ]
              }
            }
          }
          """;
        var clash = await es.PollEsForLog(clashLogQuery, timeoutMs: EsPollTimeoutMs, ct: ct);
        Assert.NotNull(clash);
        Assert.Contains("Gate A incompatibility", clash!.Value.GetRawText());

        // ---- Signal (b): skp:{badId} STABLY ABSENT (the mechanism) ----
        // The INVERSE of PollForHealthyLivenessAsync: read the liveness key across 3 windows spanning >
        // one 10s heartbeat interval. Gate A withheld MarkHealthy, so the heartbeat never writes — the
        // key must stay absent the WHOLE window. Fail if it EVER appears.
        await using var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
        var db = mux.GetDatabase();
        var livenessKey = L2ProjectionKeys.Processor(badId);
        for (var i = 0; i < 3; i++)
        {
            ct.ThrowIfCancellationRequested();
            var raw = await db.StringGetAsync(livenessKey);
            Assert.True(
                raw.IsNullOrEmpty,
                $"skp:{badId} unexpectedly present — Gate A did NOT withhold MarkHealthy (the badconfig " +
                $"processor went Healthy despite the config-schema clash, which would false-pass CFG-08).");
            await Task.Delay(5_000, ct);
        }

        // ---- Signal (c): Start → 422 (the outcome) ----
        // The workflow's only participant (badconfig) has no liveness key → ProcessorLivenessValidator
        // throws ProcessorNotLive(absent) → 422 UnprocessableEntity. This is the operator-observable
        // block: a config-incompatible processor can never be scheduled.
        var startResp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start", new List<Guid> { badWorkflowId }, ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, startResp.StatusCode);

        // Net-zero teardown: CFG-08 binds no queue and writes no data key (Gate A withheld the bind), so
        // the only residue is the L2 root the Start ATTEMPT may have touched — register defensively. The
        // badconfig skp:{badId} liveness key never exists (nothing to clean). The clash Schema + badconfig
        // Processor rows are idempotent GET-or-create state (shared with the close script — left in place).
        factory.ParentIndexMembersToSrem.Add(badWorkflowId.ToString("D"));
        factory.L2KeysToCleanup.Add($"skp:{badWorkflowId}");
        factory.L2KeysToCleanup.Add($"skp:{badWorkflowId}:{badStepId}");
    }

    [Fact]
    public async Task SampleCompatible_GateAPasses_Healthy_Start204()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var factory = new SampleRoundTripE2ETests.RealStackWebAppFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        // Read the GENUINE embedded SourceHash off the BUILT Processor.Sample assembly (same identity loop
        // as SampleRoundTripE2ETests — the live processor-sample container resolves THIS id).
        var sampleHash = typeof(global::Processor.Sample.SampleProcessor).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "SourceHash").Value!;

        // Seed the COMPATIBLE config schema (Processor.Sample's SampleConfig(string? Value) COVERS it),
        // then seed the Sample Processor row WITH that ConfigSchemaId so Gate A RUNS AND PASSES — NOT
        // Gate-A-skipped (D-03). This proves Gate A is not a false-positive blocker on a compatible schema.
        var compatibleSchemaId = await SampleRoundTripE2ETests.SeedConfigSchemaAsync(
            client,
            SampleRoundTripE2ETests.SampleCompatibleSchemaName,
            SampleRoundTripE2ETests.SampleCompatibleSchemaDefinition,
            ct);
        var sampleId = await SampleRoundTripE2ETests.SeedProcessorAsync(
            client, sampleHash, ct, configSchemaId: compatibleSchemaId);
        var sampleStepId = await SampleRoundTripE2ETests.SeedStepAsync(client, sampleId, ct);
        var sampleWorkflowId = await SampleRoundTripE2ETests.SeedWorkflowAsync(
            client, new List<Guid> { sampleStepId }, cron: "* * * * *", ct);

        // Gate A passed → the live container MarkHealthy'd and writes skp:{sampleId}. Poll for that REAL
        // heartbeat (the same truthful gate as SampleRoundTrip — no synthetic seed). Only Start once fresh.
        await SampleRoundTripE2ETests.PollForHealthyLivenessAsync(sampleId, ct);

        // Start → 204 NoContent: the L2 root was projected, the body Guid minted + published, and the
        // liveness gate PASSED (truthfully — the compatible-schema processor is genuinely Healthy).
        var startResp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start", new List<Guid> { sampleWorkflowId }, ct);
        Assert.Equal(HttpStatusCode.NoContent, startResp.StatusCode);

        // Net-zero teardown (cron round-trip — full sweep like SampleRoundTrip): register the L2 root/step
        // keys + parent-index member the Start created. The steady-state skp:{sampleId} liveness key is
        // LEFT (the live container keeps refreshing it — present in both close-gate snapshots).
        factory.ParentIndexMembersToSrem.Add(sampleWorkflowId.ToString("D"));
        factory.L2KeysToCleanup.Add($"skp:{sampleWorkflowId}");
        factory.L2KeysToCleanup.Add($"skp:{sampleWorkflowId}:{sampleStepId}");

        // Stop the workflow so its self-rescheduling cron fire ceases (NET-ZERO-31 — left running it churns
        // the close-gate redis --scan name-set). Best-effort: a stop hiccup must not fail a green assertion.
        try { await client.PostAsJsonAsync("/api/v1/orchestration/stop", new List<Guid> { sampleWorkflowId }, ct); }
        catch { /* best-effort net-zero teardown */ }
    }
}
