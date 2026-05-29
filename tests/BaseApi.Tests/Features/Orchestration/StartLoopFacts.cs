using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BaseApi.Service.Features.Orchestration;
using BaseApi.Service.Features.Orchestration.Projection;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using BaseApi.Tests.Composition;
using BaseApi.Tests.TestHelpers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Features.Orchestration;

/// <summary>
/// Phase 15 Plan 04 integration facts for the D-07 per-workflow Start loop
/// (<c>POST /api/v1/orchestration/start</c>) against real Postgres + real compose Redis
/// (via <see cref="Phase8WebAppFactory"/>). Seeds the L3 graph via the public entity HTTP
/// endpoints so a Start builds a valid L1 snapshot, then reads L2 back via
/// <see cref="Phase8WebAppFactory.RedisMultiplexer"/> + <see cref="Phase8WebAppFactory.RedisKeyPrefix"/>.
/// <para>
/// Maps to ORCH-START-02 (204 happy path), ORCH-START-07 (root correlationId == sent
/// <c>X-Correlation-Id</c>), ORCH-START-05 (re-Start of a shrunk graph leaves no orphan
/// per-step key — delete-then-write), and ORCH-START-04 / OBSERV-REDIS-03 (Redis-down →
/// 500 + RFC 7807 + correlationId + <c>redisOp</c> == "UpsertAsync", no connection string).
/// The per-class <c>RedisFixture</c> SCAN+DEL teardown sweeps residue — no FLUSHDB.
/// </para>
/// </summary>
[Trait("Phase", "15")]
public sealed class StartLoopFacts : IClassFixture<Phase8WebAppFactory>
{
    private readonly Phase8WebAppFactory _factory;

    public StartLoopFacts(Phase8WebAppFactory factory) => _factory = factory;

    // ---- HTTP seeding helpers (Processor → Step → Workflow) ----

    private static async Task<Guid> SeedProcessorAsync(HttpClient client, CancellationToken ct)
    {
        var dto = new ProcessorCreateDto(
            Name: $"slf-proc-{Guid.NewGuid():N}",
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

    private static async Task<Guid> SeedStepAsync(
        HttpClient client, CancellationToken ct, Guid processorId, List<Guid>? nextStepIds = null)
    {
        var dto = new StepCreateDto(
            Name: $"slf-step-{Guid.NewGuid():N}",
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

    private static async Task UpdateStepNextAsync(
        HttpClient client, CancellationToken ct, Guid stepId, Guid processorId, List<Guid>? nextStepIds)
    {
        var dto = new StepUpdateDto(
            Name: $"slf-step-upd-{Guid.NewGuid():N}",
            Version: "1.0.1",
            Description: null,
            ProcessorId: processorId,
            NextStepIds: nextStepIds,
            EntryCondition: StepEntryCondition.Always);
        var resp = await client.PutAsJsonAsync($"/api/v1/steps/{stepId}", dto, ct);
        resp.EnsureSuccessStatusCode();
    }

    private static async Task<Guid> SeedWorkflowAsync(
        HttpClient client, CancellationToken ct, List<Guid> entryStepIds)
    {
        var dto = new WorkflowCreateDto(
            Name: $"slf-wf-{Guid.NewGuid():N}",
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

    // ----------------------------- ORCH-START-02 + ORCH-START-07 -----------------------------

    [Fact]
    public async Task Start_Returns204()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var procId = await SeedProcessorAsync(client, ct);
        var stepId = await SeedStepAsync(client, ct, procId);
        var wfId = await SeedWorkflowAsync(client, ct, new List<Guid> { stepId });

        var correlationId = $"slf-corr-{Guid.NewGuid():N}";
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orchestration/start")
        {
            Content = JsonContent.Create(new List<Guid> { wfId }),
        };
        req.Headers.Add("X-Correlation-Id", correlationId);

        var resp = await client.SendAsync(req, ct);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        // ORCH-START-07 — the projected root key carries the sent correlationId (resolved
        // ONCE from HttpContext.Items and passed explicitly to UpsertAsync, D-01).
        var prefix = _factory.RedisKeyPrefix;
        var db = _factory.RedisMultiplexer.GetDatabase();
        var rootValue = await db.StringGetAsync($"{prefix}{wfId}");
        Assert.True(rootValue.HasValue, "root key should be set after a 204 Start");
        var root = JsonSerializer.Deserialize<TestRoot>(rootValue.ToString());
        Assert.NotNull(root);
        Assert.Equal(correlationId, root!.correlationId);
    }

    // ----------------------------- ORCH-START-05 (delete-then-write GC) -----------------------------

    [Fact]
    public async Task ReStart_Removes_Orphan_Step()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // Graph A -> B (A.NextStepIds = [B]); workflow entry = [A]. First Start projects
        // per-step keys for BOTH A and B.
        var procId = await SeedProcessorAsync(client, ct);
        var stepB = await SeedStepAsync(client, ct, procId);
        var stepA = await SeedStepAsync(client, ct, procId, nextStepIds: new List<Guid> { stepB });
        var wfId = await SeedWorkflowAsync(client, ct, new List<Guid> { stepA });

        var first = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start", new List<Guid> { wfId }, ct);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var prefix = _factory.RedisKeyPrefix;
        var db = _factory.RedisMultiplexer.GetDatabase();
        Assert.True(await db.KeyExistsAsync($"{prefix}{wfId}:{stepA}"), "step A key projected on first Start");
        Assert.True(await db.KeyExistsAsync($"{prefix}{wfId}:{stepB}"), "step B key projected on first Start");

        // Shrink the graph: remove the A -> B edge so B is no longer reachable.
        await UpdateStepNextAsync(client, ct, stepA, procId, nextStepIds: null);

        var second = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start", new List<Guid> { wfId }, ct);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        // ORCH-START-05 — the now-orphaned per-step key for B is GONE (tolerant pre-clean
        // deleted the whole prior reachable set before the writer re-projected the shrunk graph).
        Assert.True(await db.KeyExistsAsync($"{prefix}{wfId}:{stepA}"), "step A key still projected after re-Start");
        Assert.False(await db.KeyExistsAsync($"{prefix}{wfId}:{stepB}"), "orphaned step B key must be removed (delete-then-write)");
    }

    // ----------------------------- ORCH-START-04 / OBSERV-REDIS-03 -----------------------------

    [Fact]
    public async Task Start_RedisDown_500()
    {
        var ct = TestContext.Current.CancellationToken;

        // Deterministic Redis fault at the writer seam (the LAST Redis op on the Start path).
        // Using a throwing IRedisProjectionWriter double + a no-op IRedisL2Cleanup (so the
        // tolerant pre-clean succeeds and UpsertAsync is the offending op) exercises the exact
        // OBSERV-REDIS-03 catch-and-tag path without depending on flaky connection/backlog
        // timing of a dead TCP endpoint. RedisConnectionException : RedisException, so the
        // service's `catch (RedisException)` tags Data["redisOp"]="UpsertAsync".
        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped<IRedisL2Cleanup, NoOpRedisL2Cleanup>();
                services.AddScoped<IRedisProjectionWriter, RedisDownProjectionWriter>();
            }));
        using var client = factory.CreateClient();

        var procId = await SeedProcessorAsync(client, ct);
        var stepId = await SeedStepAsync(client, ct, procId);
        var wfId = await SeedWorkflowAsync(client, ct, new List<Guid> { stepId });

        var correlationId = $"slf-down-{Guid.NewGuid():N}";
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orchestration/start")
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
        // OBSERV-REDIS-03 — op name surfaced; correlationId present; NO connection string / detail leak.
        Assert.Equal("UpsertAsync", doc.RootElement.GetProperty("redisOp").GetString());
        Assert.Equal(correlationId, doc.RootElement.GetProperty("correlationId").GetString());
        Assert.DoesNotContain("localhost", body);
        Assert.DoesNotContain("RedisConnectionException", body);
    }

    /// <summary>No-op cleanup so the Start pre-clean step succeeds and the throwing writer is the
    /// offending Redis op (UpsertAsync).</summary>
    private sealed class NoOpRedisL2Cleanup : IRedisL2Cleanup
    {
        public Task StopCleanupAsync(Guid workflowId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Throwing projection writer — simulates a Redis fault on the Start write path.</summary>
    private sealed class RedisDownProjectionWriter : IRedisProjectionWriter
    {
        public Task UpsertAsync(WorkflowGraphSnapshot snapshot, string correlationId, CancellationToken ct)
            => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "simulated Redis down");
    }

    /// <summary>Minimal projection of the root value used for the correlationId assertion.</summary>
    private sealed record TestRoot(string correlationId);
}
