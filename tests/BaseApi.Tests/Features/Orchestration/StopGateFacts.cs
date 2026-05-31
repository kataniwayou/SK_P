using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using BaseApi.Tests.Composition;
using BaseApi.Tests.TestHelpers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Features.Orchestration;

/// <summary>
/// Phase 15 Plan 04 integration facts for the D-04/D-06 Redis-based Stop gate + cleanup
/// (<c>POST /api/v1/orchestration/stop</c>) against real Postgres + real compose Redis
/// (via <see cref="Phase8WebAppFactory"/>). A workflow is Started (which projects the 3
/// keyspaces) and then Stopped; L2 is read back via the factory multiplexer.
/// <para>
/// Maps to ORCH-STOP-02 (all exist → 204; root+step gone, processor retained — ORCH-STOP-04
/// rev), ORCH-STOP-03 (any missing → 422 listing the FULL missing set, NO deletion),
/// ORCH-STOP-06 rev (repeated Stop → 422; non-idempotent), and ORCH-STOP-07 / OBSERV-REDIS-03
/// (Redis-down → 500 + RFC 7807 + correlationId + <c>redisOp</c> == "KeyExistsAsync", no
/// connection string). The per-class <c>RedisFixture</c> SCAN+DEL teardown sweeps residue.
/// </para>
/// </summary>
[Trait("Phase", "15")]
[Collection("ParentIndex")]
public sealed class StopGateFacts : IClassFixture<HarnessWebAppFactory>
{
    private readonly HarnessWebAppFactory _factory;

    public StopGateFacts(HarnessWebAppFactory factory) => _factory = factory;

    // ---- HTTP seeding helpers (Processor → Step → Workflow) ----

    private static async Task<Guid> SeedProcessorAsync(HttpClient client, CancellationToken ct)
    {
        var dto = new ProcessorCreateDto(
            Name: $"sgf-proc-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            SourceHash: HashHelpers.RandomSha256Hex(),
            InputSchemaId: null,
            OutputSchemaId: null,
            ConfigSchemaId: null);
        var resp = await client.PostAsJsonAsync("/api/v1/processors", dto, ct);
        resp.EnsureSuccessStatusCode();
        var proc = await resp.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);
        return proc!.Id;
    }

    private static async Task<Guid> SeedStepAsync(HttpClient client, CancellationToken ct, Guid processorId)
    {
        var dto = new StepCreateDto(
            Name: $"sgf-step-{Guid.NewGuid():N}",
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

    private static async Task<Guid> SeedWorkflowAsync(HttpClient client, CancellationToken ct, List<Guid> entryStepIds)
    {
        var dto = new WorkflowCreateDto(
            Name: $"sgf-wf-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            EntryStepIds: entryStepIds,
            AssignmentIds: null,
            CronExpression: null);
        var resp = await client.PostAsJsonAsync("/api/v1/workflows", dto, ct);
        resp.EnsureSuccessStatusCode();
        var wf = await resp.Content.ReadFromJsonAsync<WorkflowReadDto>(cancellationToken: ct);
        return wf!.Id;
    }

    /// <summary>
    /// Seeds a workflow (with one terminal step + its processor) and Starts it (204). PROC-LIVE-01:
    /// seeds the processor's self-registered skp:{procId} liveness entry LIVE first (the writer no
    /// longer creates it — PROC-NOCREATE-01 — and the Start path gates on it). Tracks the root + step
    /// keys for known-key cleanup so a Stop-failure path (422/500, no SREM-delete) leaves no residue.
    /// </summary>
    private async Task<(Guid wfId, Guid stepId, Guid procId)> SeedAndStartAsync(HttpClient client, CancellationToken ct)
    {
        var procId = await SeedProcessorAsync(client, ct);
        var stepId = await SeedStepAsync(client, ct, procId);
        var wfId = await SeedWorkflowAsync(client, ct, new List<Guid> { stepId });
        await _factory.SeedLiveProcessorAsync(procId, ct);
        var prefix = _factory.RedisKeyPrefix;
        _factory.TrackRedisKey($"{prefix}{wfId}");
        _factory.TrackRedisKey($"{prefix}{wfId}:{stepId}");
        var start = await client.PostAsJsonAsync("/api/v1/orchestration/start", new List<Guid> { wfId }, ct);
        Assert.Equal(HttpStatusCode.NoContent, start.StatusCode);
        return (wfId, stepId, procId);
    }

    // ----------------------------- ORCH-STOP-02 + ORCH-STOP-04 (rev) -----------------------------

    [Fact]
    public async Task Stop_AllExist_204()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var (wfId, stepId, procId) = await SeedAndStartAsync(client, ct);

        var stop = await client.PostAsJsonAsync("/api/v1/orchestration/stop", new List<Guid> { wfId }, ct);
        Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);

        var prefix = _factory.RedisKeyPrefix;
        var db = _factory.RedisMultiplexer.GetDatabase();
        Assert.False(await db.KeyExistsAsync($"{prefix}{wfId}"), "root key must be deleted");
        Assert.False(await db.KeyExistsAsync($"{prefix}{wfId}:{stepId}"), "step key must be deleted");
        // ORCH-STOP-04 rev — processor keys are NEVER deleted by cleanup.
        Assert.True(await db.KeyExistsAsync($"{prefix}{procId}"), "processor key must be retained");
    }

    // ----------------------------- ORCH-STOP-03 (422 full missing list, NO delete) -----------------------------

    [Fact]
    public async Task Stop_Missing_422_NoDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var (startedWf, startedStep, _) = await SeedAndStartAsync(client, ct);
        var neverStarted = Guid.NewGuid();   // no L2 root key for this id

        var stop = await client.PostAsJsonAsync(
            "/api/v1/orchestration/stop", new List<Guid> { startedWf, neverStarted }, ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, stop.StatusCode);
        Assert.Equal("application/problem+json", stop.Content.Headers.ContentType?.MediaType);

        var body = await stop.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(422, doc.RootElement.GetProperty("status").GetInt32());
        // The detail lists the FULL missing set (here: the one never-Started id).
        Assert.Contains(neverStarted.ToString(), body);
        // The started id's keys must NOT have been deleted (gate fails BEFORE any cleanup).
        var prefix = _factory.RedisKeyPrefix;
        var db = _factory.RedisMultiplexer.GetDatabase();
        Assert.True(await db.KeyExistsAsync($"{prefix}{startedWf}"), "started root key must survive a failed gate");
        Assert.True(await db.KeyExistsAsync($"{prefix}{startedWf}:{startedStep}"), "started step key must survive a failed gate");

        // The 422 gate fails before cleanup, so the started wf's parent-index SADD was NOT SREMed —
        // remove it here (root/step keys are tracked by SeedAndStartAsync) so the close-gate scan SHA holds.
        await _factory.SremParentIndexAsync(startedWf);
    }

    // ----------------------------- ORCH-STOP-06 (rev): repeated Stop → 422 -----------------------------

    [Fact]
    public async Task Stop_Repeat_422()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var (wfId, _, _) = await SeedAndStartAsync(client, ct);

        var first = await client.PostAsJsonAsync("/api/v1/orchestration/stop", new List<Guid> { wfId }, ct);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        // Second Stop — the root key is gone, so the EXISTS gate fails → 422 (non-idempotent).
        var second = await client.PostAsJsonAsync("/api/v1/orchestration/stop", new List<Guid> { wfId }, ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);

        var body = await second.Content.ReadAsStringAsync(ct);
        Assert.Contains(wfId.ToString(), body);
    }

    // ----------------------------- ORCH-STOP-07 / OBSERV-REDIS-03 -----------------------------

    [Fact]
    public async Task Stop_RedisDown_500()
    {
        var ct = TestContext.Current.CancellationToken;

        // Deterministic Redis fault on the Stop EXISTS gate. Substitute IConnectionMultiplexer
        // so GetDatabase().KeyExistsAsync throws RedisConnectionException (: RedisException) —
        // the service catches it and tags Data["redisOp"]="KeyExistsAsync" (OBSERV-REDIS-03).
        // This is deterministic vs. a flaky dead-TCP endpoint whose backlog may swallow the op.
        var db = Substitute.For<IDatabase>();
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns<Task<bool>>(_ => throw new RedisConnectionException(
                ConnectionFailureType.UnableToConnect, "simulated Redis down"));
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IConnectionMultiplexer>(mux);
            }));
        using var client = factory.CreateClient();

        // A well-formed non-empty id list passes the rule validation; the FIRST Redis touch
        // on the Stop path is the EXISTS batch → RedisException → 500 redisOp=KeyExistsAsync.
        var correlationId = $"sgf-down-{Guid.NewGuid():N}";
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orchestration/stop")
        {
            Content = JsonContent.Create(new List<Guid> { Guid.NewGuid() }),
        };
        req.Headers.Add("X-Correlation-Id", correlationId);

        var resp = await client.SendAsync(req, ct);
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(500, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("KeyExistsAsync", doc.RootElement.GetProperty("redisOp").GetString());
        Assert.Equal(correlationId, doc.RootElement.GetProperty("correlationId").GetString());
        Assert.DoesNotContain("localhost", body);
        Assert.DoesNotContain("RedisConnectionException", body);
    }

    [Fact]
    public async Task Stop_RedisDown_OnPostGateCleanup_500_KeyExistsAsync()
    {
        var ct = TestContext.Current.CancellationToken;

        // WR-01 (15-REVIEW): a Redis fault during the POST-gate cleanup deletes (all roots
        // exist → EXISTS gate passes → tolerant traverse-and-delete) must surface the SAME
        // stable Stop op name as the EXISTS gate itself. Without the cleanup-loop try/catch
        // this fault reached the 500 handler with NO redisOp, breaking OBSERV-REDIS-03 for the
        // post-gate sub-path.

        // Seed + Start through the REAL factory (real cleanup, real mux) so the root key
        // genuinely exists — the EXISTS gate must read a present root to REACH the cleanup loop.
        using var seedClient = _factory.CreateClient();
        var (wfId, _, _) = await SeedAndStartAsync(seedClient, ct);

        // Stop through a variant whose IRedisL2Cleanup throws on the post-gate delete. The mux
        // is NOT substituted, so the EXISTS gate uses the real, seeded root (gate passes) and the
        // fault originates in the cleanup loop → service tags redisOp="KeyExistsAsync" → 500.
        // IRedisL2Cleanup is internal (Castle/NSubstitute can't proxy it), so use a hand-rolled
        // throwing stub — same pattern as StartLoopFacts' NoOpRedisL2Cleanup/RedisDownProjectionWriter.
        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped<Service.Features.Orchestration.Projection.IRedisL2Cleanup, ThrowingRedisL2Cleanup>();
            }));
        using var client = factory.CreateClient();

        var correlationId = $"sgf-clean-down-{Guid.NewGuid():N}";
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orchestration/stop")
        {
            Content = JsonContent.Create(new List<Guid> { wfId }),
        };
        req.Headers.Add("X-Correlation-Id", correlationId);

        var resp = await client.SendAsync(req, ct);
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(500, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("KeyExistsAsync", doc.RootElement.GetProperty("redisOp").GetString());
        Assert.Equal(correlationId, doc.RootElement.GetProperty("correlationId").GetString());
        Assert.DoesNotContain("localhost", body);
        Assert.DoesNotContain("RedisConnectionException", body);

        // The cleanup threw before SREM, so the started wf's parent-index member persists — remove it
        // (root/step keys tracked by SeedAndStartAsync) so the close-gate scan SHA returns to BEFORE.
        await _factory.SremParentIndexAsync(wfId);
    }

    /// <summary>Throwing cleanup — simulates a Redis fault on the POST-gate Stop delete loop (WR-01).</summary>
    private sealed class ThrowingRedisL2Cleanup : Service.Features.Orchestration.Projection.IRedisL2Cleanup
    {
        public Task StopCleanupAsync(Guid workflowId, CancellationToken ct)
            => throw new RedisConnectionException(
                ConnectionFailureType.UnableToConnect, "simulated Redis down on cleanup");
    }
}
