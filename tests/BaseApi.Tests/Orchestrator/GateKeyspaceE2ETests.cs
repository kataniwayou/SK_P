using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BaseApi.Tests.Observability.Helpers;
using Messaging.Contracts.Projections;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Deterministic fabricated-key gate-verdict proof (Phase 62, TEST-01 / TEST-02 — the xUnit RealStack
/// half of D-04). Drives the already-shipped per-replica liveness gate (<c>ProcessorLivenessValidator</c>,
/// READ-ONLY here — this phase PROVES it, never modifies it) through the in-process WebAPI by FABRICATING
/// crafted per-instance liveness keys directly in host Redis (Phase-61 WR-01/WR-02 craft-redis-state
/// style). No container lifecycle, no heartbeat poll, zero timing race — the verdict is a pure function of
/// the keyspace this test writes.
/// </summary>
/// <remarks>
/// <para>
/// Reuses the <see cref="SampleRoundTripE2ETests"/> harness WHOLESALE — the same
/// <c>RealStackWebAppFactory</c> (host overrides + net-zero teardown), the same
/// <c>SeedConfigSchemaAsync</c> / <c>SeedProcessorAsync</c> / <c>SeedStepAsync</c> /
/// <c>SeedWorkflowAsync</c> helpers, the new <c>SeedFabricatedLivenessAsync</c> helper (Plan 02 Task 1),
/// and the same <c>[Collection("Observability")]</c> (DisableParallelization +
/// <c>ICollectionFixture&lt;RealStackNetZeroSweepFixture&gt;</c>). No new harness is authored.
/// </para>
/// <para>
/// Each test uses a DISTINCT throwaway <c>procId</c> (a fresh DB Processor row seeded against a UNIQUE
/// per-test SourceHash — NOT the genuine embedded Sample hash, which GET-or-create resolves to the live
/// processor-sample's own procId) so its fabricated <c>skp:proc:{procId}</c> instance index never collides
/// with a live replica's index (RESEARCH Pitfall 6 / T-62-04). The fixture does NOT sweep
/// <c>skp:proc:*</c>, so every fabricated per-instance key + index member is registered for teardown.
/// </para>
/// <list type="bullet">
///   <item><b>Test A (≥1-healthy admits)</b> — a healthy replica seeded ALONGSIDE an unhealthy and a stale
///   sibling still admits (204): the gate short-circuits on the first Healthy+fresh replica
///   (<c>ProcessorLivenessValidator.cs:69</c>).</item>
///   <item><b>Test B (422 when none qualify)</b> — only an unhealthy + a stale key (NO healthy) →
///   <c>ProcessorNotLive</c> 422 (RFC 7807). The problem body carries COUNTS only (<c>:74-77</c>) — asserted
///   to NOT leak any fabricated instanceId (V7 no-info-leak / T-62-06).</item>
///   <item><b>Test C (malformed → 422, never 500)</b> — a malformed per-instance value is treated as
///   malformed (<c>:63-64</c>) → 422, never an unhandled 500 (V5 input validation / T-62-05).</item>
/// </list>
/// Tagged <c>Category=RealStack</c> so the hermetic filter (<c>Category!=RealStack</c>) excludes it; the
/// build gate still COMPILES it (D-14).
/// </remarks>
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]   // hermetic filter (Category=RealStack) excludes it; the build gate still COMPILES it
[Trait("Phase", "62")]
[Collection("Observability")]       // DisableParallelization + ICollectionFixture<RealStackNetZeroSweepFixture>
public sealed class GateKeyspaceE2ETests
{
    // Host Redis (the real container's keyspace) — mirrors SampleRoundTripE2ETests' host override; used
    // ONLY by Test C's deliberate malformed write (the helper requires a valid entry).
    private const string HostRedis = "localhost:6380,abortConnect=false,connectTimeout=5000";

    // Stable per-test throwaway SourceHashes (64 lowercase hex, ^[a-f0-9]{64}$). FIXED (not random) so
    // SeedProcessorAsync's GET-or-create reuses the SAME procId every run: its per-procId dispatch queue is
    // then steady-state (present in BOTH the close gate's BEFORE and AFTER rabbitmq snapshots) — a fresh
    // random procId instead churns a NEW durable dispatch queue each run, violating the gate's rabbitmq
    // list_queues SHA (observed live in the Phase-62 gate). Distinct per test so each fabricated
    // skp:proc index is isolated from the others AND from the live processor-sample replicas (which
    // heartbeat under the GENUINE embedded hash's procId — RESEARCH Pitfall 6 / T-62-04). NONE equals the
    // genuine Sample hash, so no live container ever self-registers under these procIds.
    private static readonly string TestAHash = new('a', 64);
    private static readonly string TestBHash = new('b', 64);
    private static readonly string TestCHash = new('c', 64);

    /// <summary>
    /// Test A: a single Healthy+fresh replica ADMITS (204) even alongside a fabricated unhealthy and a
    /// fabricated stale sibling — the gate's first-qualifier-wins short-circuit
    /// (<c>ProcessorLivenessValidator.cs:69</c>).
    /// </summary>
    [Fact]
    public async Task FabricatedKeys_OneHealthyAmongUnhealthyAndStale_Admits204()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var factory = new SampleRoundTripE2ETests.RealStackWebAppFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        var (procId, stepId, wfId) = await SeedWorkflowGraphAsync(factory, client, TestAHash, ct);

        // Fabricate THREE per-instance keys on this throwaway procId: one healthy (admits), one unhealthy
        // (any Fail => Unhealthy), one stale ((now-25)+20 = now-5 <= now => stale). Build via the Create
        // factory + consts — NEVER hand-author the JSON. The helper writes + SADDs + registers teardown.
        var now = DateTime.UtcNow;
        await SampleRoundTripE2ETests.SeedFabricatedLivenessAsync(factory, procId, "fab-healthy",
            ProcessorLivenessEntry.Create(null, null, null, now, interval: 10), ct);
        await SampleRoundTripE2ETests.SeedFabricatedLivenessAsync(factory, procId, "fab-unhealthy",
            ProcessorLivenessEntry.Create(SchemaOutcome.Fail, null, null, now, interval: 10), ct);
        await SampleRoundTripE2ETests.SeedFabricatedLivenessAsync(factory, procId, "fab-stale",
            ProcessorLivenessEntry.Create(null, null, null, now.AddSeconds(-25), interval: 10), ct);

        var startResp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start", new List<Guid> { wfId }, ct);
        Assert.Equal(HttpStatusCode.NoContent, startResp.StatusCode);

        RegisterWorkflowTeardown(factory, wfId, stepId);
    }

    /// <summary>
    /// Test B: when NO fabricated replica qualifies (only an unhealthy + a stale key, no healthy) the gate
    /// BLOCKS with 422 (RFC 7807). The problem body carries COUNTS only — asserted NOT to leak any
    /// fabricated instanceId string (V7 no-info-leak / T-62-06).
    /// </summary>
    [Fact]
    public async Task FabricatedKeys_NoHealthyReplica_Blocks422_NoInfoLeak()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var factory = new SampleRoundTripE2ETests.RealStackWebAppFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        var (procId, stepId, wfId) = await SeedWorkflowGraphAsync(factory, client, TestBHash, ct);

        // Fabricate ONLY non-qualifying siblings: an unhealthy + a stale key, no healthy => no qualifier.
        var now = DateTime.UtcNow;
        await SampleRoundTripE2ETests.SeedFabricatedLivenessAsync(factory, procId, "fab-unhealthy",
            ProcessorLivenessEntry.Create(SchemaOutcome.Fail, null, null, now, interval: 10), ct);
        await SampleRoundTripE2ETests.SeedFabricatedLivenessAsync(factory, procId, "fab-stale",
            ProcessorLivenessEntry.Create(null, null, null, now.AddSeconds(-25), interval: 10), ct);

        var startResp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start", new List<Guid> { wfId }, ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, startResp.StatusCode);

        // No-info-leak guard (V7 / T-62-06): the counts-only reason (ProcessorLivenessValidator.cs:74-77)
        // must NOT echo any fabricated instanceId. All fabricated members are prefixed "fab-".
        var body = await startResp.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain("fab-", body, StringComparison.Ordinal);

        RegisterWorkflowTeardown(factory, wfId, stepId);
    }

    /// <summary>
    /// Test C: a MALFORMED per-instance value (invalid JSON) is treated as malformed by the reader
    /// (<c>ProcessorLivenessValidator.cs:63-64</c>) → the gate BLOCKS with 422, NEVER an unhandled 500
    /// (V5 input validation / T-62-05). The malformed value is written directly (the helper requires a
    /// valid entry) and both the key + index member are registered for teardown.
    /// </summary>
    [Fact]
    public async Task FabricatedKeys_MalformedPerInstanceValue_Blocks422_Not500()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var factory = new SampleRoundTripE2ETests.RealStackWebAppFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        var (procId, stepId, wfId) = await SeedWorkflowGraphAsync(factory, client, TestCHash, ct);

        // Write a deliberately malformed per-instance value + SADD the member, then register BOTH for
        // teardown (the Task-1 helper only accepts a valid ProcessorLivenessEntry, so craft this one inline).
        await using (var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis))
        {
            var db = mux.GetDatabase();
            await db.StringSetAsync(
                L2ProjectionKeys.PerInstance(procId, "fab-bad"), "{not-an-entry", TimeSpan.FromSeconds(60));
            await db.SetAddAsync(L2ProjectionKeys.InstanceIndex(procId), "fab-bad");
        }
        factory.L2KeysToCleanup.Add(L2ProjectionKeys.PerInstance(procId, "fab-bad"));
        factory.InstanceIndexMembersToSrem.Add((procId, "fab-bad"));

        var startResp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start", new List<Guid> { wfId }, ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, startResp.StatusCode);

        RegisterWorkflowTeardown(factory, wfId, stepId);
    }

    // ---- Shared seed: a DISTINCT throwaway Processor row + step + workflow (the gate participant) ----
    // Identity is driven by the caller's STABLE per-test sourceHash (TestAHash/TestBHash/TestCHash — never
    // the genuine embedded Sample hash): SeedProcessorAsync is GET-or-create on the hash, and the genuine
    // hash resolves to the SAME procId the live processor-sample replicas heartbeat against, whose real
    // Healthy index members would poison the fabricated-only scenarios. A distinct, FIXED hash yields one
    // stable fresh procId per test, so the gate evaluates EXACTLY this test's fabricated index AND the
    // per-procId dispatch queue stays steady-state across runs (the workflow's only participant is THIS procId).
    private static async Task<(Guid ProcId, Guid StepId, Guid WfId)> SeedWorkflowGraphAsync(
        SampleRoundTripE2ETests.RealStackWebAppFactory factory,
        HttpClient client,
        string sourceHash,
        CancellationToken ct)
    {
        var schemaId = await SampleRoundTripE2ETests.SeedConfigSchemaAsync(
            client,
            SampleRoundTripE2ETests.SampleCompatibleSchemaName,
            SampleRoundTripE2ETests.SampleCompatibleSchemaDefinition,
            ct);
        var procId = await SampleRoundTripE2ETests.SeedProcessorAsync(client, sourceHash, ct, configSchemaId: schemaId);
        var stepId = await SampleRoundTripE2ETests.SeedStepAsync(client, procId, ct);
        var wfId = await SampleRoundTripE2ETests.SeedWorkflowAsync(
            client, new List<Guid> { stepId }, cron: "* * * * *", ct);
        return (procId, stepId, wfId);
    }

    // ---- Net-zero teardown for the workflow L2 root/step the Start ATTEMPT may have projected ----
    // Mirrors GateACompositionE2ETests:174-176. The fabricated skp:proc keys + index members are
    // auto-registered by SeedFabricatedLivenessAsync (or explicitly for Test C's malformed write).
    private static void RegisterWorkflowTeardown(
        SampleRoundTripE2ETests.RealStackWebAppFactory factory, Guid wfId, Guid stepId)
    {
        factory.ParentIndexMembersToSrem.Add(wfId.ToString("D"));
        factory.L2KeysToCleanup.Add($"skp:{wfId}");
        factory.L2KeysToCleanup.Add($"skp:{wfId}:{stepId}");
    }
}
