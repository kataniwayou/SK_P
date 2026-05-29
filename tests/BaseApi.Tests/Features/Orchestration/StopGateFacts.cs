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
public sealed class StopGateFacts : IClassFixture<Phase8WebAppFactory>
{
    private readonly Phase8WebAppFactory _factory;

    public StopGateFacts(Phase8WebAppFactory factory) => _factory = factory;

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

    /// <summary>Seeds a workflow (with one terminal step + its processor) and Starts it (204).</summary>
    private async Task<(Guid wfId, Guid stepId, Guid procId)> SeedAndStartAsync(HttpClient client, CancellationToken ct)
    {
        var procId = await SeedProcessorAsync(client, ct);
        var stepId = await SeedStepAsync(client, ct, procId);
        var wfId = await SeedWorkflowAsync(client, ct, new List<Guid> { stepId });
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
}
