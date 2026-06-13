using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using BaseApi.Tests.Composition;
using BaseApi.Tests.TestHelpers;
using Messaging.Contracts.Projections;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Features.Orchestration;

/// <summary>
/// Phase 24 Plan 02 (WEBAPI-SUPPRESS-01) integration facts for the delete-if-present Stop
/// (<c>POST /api/v1/orchestration/stop</c>) against real Postgres + real compose Redis
/// (via <see cref="Phase8WebAppFactory"/>). A workflow is Started (which projects the keyspaces)
/// and then Stopped; L2 is read back via the factory multiplexer.
/// <para>
/// Reconciled from the superseded Phase 15 422 EXISTS-gate. Coverage: present root → 204 + root/step
/// deleted, processor retained; a repeated Stop is an idempotent 204 no-op (NOT 422); a mixed batch
/// (present + absent) is per-id delete-if-present (present deleted, absent a no-op, overall 204);
/// Redis-down → 500 + RFC 7807 + correlationId + <c>redisOp</c> == "KeyDeleteAsync" (no connection
/// string). The per-class <c>RedisFixture</c> SCAN+DEL teardown sweeps residue.
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
        // ORCH-STOP-04 rev — processor liveness keys are NEVER deleted by cleanup. Phase 61 (D-06/11):
        // the externally self-registered liveness now lives in the per-instance index (skp:proc:{procId}).
        Assert.True(await db.KeyExistsAsync(L2ProjectionKeys.InstanceIndex(procId)), "processor liveness must be retained");
    }

    // ----------------------------- WEBAPI-SUPPRESS-01: mixed batch is per-id delete-if-present -----------------------------

    [Fact]
    public async Task Stop_MixedBatch_DeletesPresent_NoOpAbsent_204()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var (startedWf, startedStep, _) = await SeedAndStartAsync(client, ct);
        var absentId = Guid.NewGuid();   // no L2 root key for this id

        // Stop [present, absent] → 204; the present root is deleted (per-id delete-if-present), the
        // absent id is a tolerant no-op. This supersedes the Phase 15 all-or-nothing 422 gate.
        var stop = await client.PostAsJsonAsync(
            "/api/v1/orchestration/stop", new List<Guid> { startedWf, absentId }, ct);

        Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);

        var prefix = _factory.RedisKeyPrefix;
        var db = _factory.RedisMultiplexer.GetDatabase();
        // The present id's keys ARE deleted (per-id, not blocked by the absent id).
        Assert.False(await db.KeyExistsAsync($"{prefix}{startedWf}"), "present root deleted");
        Assert.False(await db.KeyExistsAsync($"{prefix}{startedWf}:{startedStep}"), "present per-step deleted");
        // The absent id never had a root — nothing to delete (no 422, no error).
        Assert.False(await db.KeyExistsAsync($"{prefix}{absentId}"), "absent root stays absent");

        // Stop's per-id cleanup SREMs the present wf; this is defensive/idempotent.
        await _factory.SremParentIndexAsync(startedWf);
    }

    // ----------------------------- WEBAPI-SUPPRESS-01: repeated Stop is an idempotent 204 no-op -----------------------------

    [Fact]
    public async Task Stop_Repeat_Is_Idempotent_204()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var (wfId, _, _) = await SeedAndStartAsync(client, ct);

        var first = await client.PostAsJsonAsync("/api/v1/orchestration/stop", new List<Guid> { wfId }, ct);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        // Second Stop — the root key is gone → delete-if-present deletes nothing → 204 no-op (NOT 422).
        var second = await client.PostAsJsonAsync("/api/v1/orchestration/stop", new List<Guid> { wfId }, ct);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
    }

    // ----------------------------- ORCH-STOP-07 / OBSERV-REDIS-03 -----------------------------

    [Fact]
    public async Task Stop_RedisDown_500()
    {
        var ct = TestContext.Current.CancellationToken;

        // Deterministic Redis fault on the Stop EXISTS gate. Substitute IConnectionMultiplexer
        // so GetDatabase().KeyExistsAsync throws RedisConnectionException (: RedisException) —
        // 24.1 R1/R3 (probe-not-delete): the FIRST Redis touch on the Stop path is now the per-id
        // KeyExistsAsync PROBE (the lead op changed from KeyDeleteAsync to KeyExistsAsync). The service
        // catches it and tags Data["redisOp"]="KeyDeleteAsync" — the STABLE Stop-path op name is kept
        // identical to the prior contract (OBSERV-REDIS-03) so the 500 body is unchanged for callers.
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

        // A well-formed non-empty id list passes the rule validation; the FIRST Redis touch on the
        // Stop path is now the per-id KeyExistsAsync probe → RedisException → 500 redisOp=KeyDeleteAsync.
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
        Assert.Equal("KeyDeleteAsync", doc.RootElement.GetProperty("redisOp").GetString());
        Assert.Equal(correlationId, doc.RootElement.GetProperty("correlationId").GetString());
        Assert.DoesNotContain("localhost", body);
        Assert.DoesNotContain("RedisConnectionException", body);
    }

    [Fact]
    public async Task Stop_RedisDown_OnPostDeleteCleanup_500_KeyDeleteAsync()
    {
        var ct = TestContext.Current.CancellationToken;

        // OBSERV-REDIS-03: a Redis fault during the POST-delete cleanup (the per-id KeyExistsAsync
        // probe returned true → StopCleanupAsync reads the root, BFS-collects step keys, and deletes
        // root+steps in one batch) must surface the SAME stable Stop op name as the delete itself
        // ("KeyDeleteAsync"). The cleanup try/catch tags it; without it the fault would reach the
        // 500 handler with no redisOp.

        // Seed + Start through the REAL factory (real cleanup, real mux) so the root key genuinely
        // exists — the per-id KeyDeleteAsync must delete a present root (returns true) to REACH cleanup.
        using var seedClient = _factory.CreateClient();
        var (wfId, _, _) = await SeedAndStartAsync(seedClient, ct);

        // Stop through a variant whose IRedisL2Cleanup throws on the post-delete cleanup. The mux is
        // NOT substituted, so the real KeyDeleteAsync deletes the seeded root (returns true) and the
        // fault originates in the cleanup → service tags redisOp="KeyDeleteAsync" → 500.
        // IRedisL2Cleanup is internal (Castle/NSubstitute can't proxy it), so use a hand-rolled stub.
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
        Assert.Equal("KeyDeleteAsync", doc.RootElement.GetProperty("redisOp").GetString());
        Assert.Equal(correlationId, doc.RootElement.GetProperty("correlationId").GetString());
        Assert.DoesNotContain("localhost", body);
        Assert.DoesNotContain("RedisConnectionException", body);

        // The cleanup threw, so the started wf's parent-index member persists — remove it (the root
        // was already deleted by the real KeyDeleteAsync) so the close-gate scan SHA returns to BEFORE.
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
