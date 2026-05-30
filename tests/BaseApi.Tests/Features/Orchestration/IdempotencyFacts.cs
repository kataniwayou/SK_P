using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BaseApi.Service.Features.Orchestration.Projection;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using BaseApi.Tests.Composition;
using BaseApi.Tests.TestHelpers;
using Messaging.Contracts.Projections;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Features.Orchestration;

/// <summary>
/// Phase 16 Plan 04 integration facts for the idempotency + concurrency contract of
/// <c>POST /api/v1/orchestration/start</c> against real Postgres + real compose Redis
/// (via <see cref="Phase8WebAppFactory"/>). Start has NO Redis lock — second Start replaces
/// the L2 keys (PUT-like, D-02) and concurrent Starts are last-write-wins (D-01).
/// <para>
/// Maps to TEST-REDIS-08. The sequential fact proves the SECOND write is reflected by
/// asserting the fresh <c>jobId</c> (a <c>Guid.NewGuid()</c> per Start, 15-CONTEXT D-05)
/// CHANGED between Starts — a positive overwrite assertion (Aliasing Risk D-02), not merely
/// <c>!= Guid.Empty</c> which a no-op second Start would still satisfy. The concurrent fact is
/// observational only: two parallel POSTs both 204 and the final root round-trips, with NO
/// deterministic-winner assertion (per-workflow Start does delete-then-write, so a genuine
/// interleave can transiently wipe the other writer — D-01 tolerates this to stay non-flaky
/// across the 3-GREEN gate). The per-class <c>RedisFixture</c> SCAN+DEL teardown sweeps residue.
/// </para>
/// </summary>
[Trait("Phase", "16")]
public sealed class IdempotencyFacts : IClassFixture<HarnessWebAppFactory>
{
    private readonly HarnessWebAppFactory _factory;

    public IdempotencyFacts(HarnessWebAppFactory factory) => _factory = factory;

    // ---- HTTP seeding helpers (Processor → Step → Workflow) ----

    private static async Task<Guid> SeedProcessorAsync(HttpClient client, CancellationToken ct)
    {
        var dto = new ProcessorCreateDto(
            Name: $"idf-proc-{Guid.NewGuid():N}",
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
            Name: $"idf-step-{Guid.NewGuid():N}",
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
            Name: $"idf-step-upd-{Guid.NewGuid():N}",
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
            Name: $"idf-wf-{Guid.NewGuid():N}",
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

    // ----------------------------- TEST-REDIS-08 (D-02): sequential second-write-reflected -----------------------------

    [Fact]
    public async Task ReStart_SameWorkflow_ReflectsSecondWrite()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // Graph A -> B (A.NextStepIds = [B]); workflow entry = [A]. First Start projects per-step
        // keys for BOTH A and B; the shrink later proves the orphan is GC'd by delete-then-write.
        var procId = await SeedProcessorAsync(client, ct);
        var stepB = await SeedStepAsync(client, ct, procId);
        var stepA = await SeedStepAsync(client, ct, procId, nextStepIds: new List<Guid> { stepB });
        var wfId = await SeedWorkflowAsync(client, ct, new List<Guid> { stepA });

        var prefix = _factory.RedisKeyPrefix;
        var db = _factory.RedisMultiplexer.GetDatabase();

        // First Start — capture the root jobId.
        var first = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start", new List<Guid> { wfId }, ct);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        var firstRoot = JsonSerializer.Deserialize<WorkflowRootProjection>(
            (await db.StringGetAsync($"{prefix}{wfId}")).ToString());
        var firstJobId = firstRoot!.JobId;
        Assert.True(await db.KeyExistsAsync($"{prefix}{wfId}:{stepB}"), "step B key projected on first Start");

        // Shrink the graph (remove A -> B) so B becomes an orphan on the second Start.
        await UpdateStepNextAsync(client, ct, stepA, procId, nextStepIds: null);

        // Second Start (SAME workflowIds) — overwrite, not no-op.
        var second = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start", new List<Guid> { wfId }, ct);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        var secondRoot = JsonSerializer.Deserialize<WorkflowRootProjection>(
            (await db.StringGetAsync($"{prefix}{wfId}")).ToString());

        // D-02 (Aliasing Risk) — jobId = Guid.NewGuid() per Start, so a reflected second write
        // CHANGES it. Assert the CHANGE, not merely != Guid.Empty (a no-op would pass that).
        Assert.NotEqual(firstJobId, secondRoot!.JobId);

        // Delete-then-write GC — the now-orphaned per-step key for B is gone; A survives.
        Assert.True(await db.KeyExistsAsync($"{prefix}{wfId}:{stepA}"), "step A key still projected after re-Start");
        Assert.False(await db.KeyExistsAsync($"{prefix}{wfId}:{stepB}"), "orphaned step B key must be removed (delete-then-write)");
    }

    // ----------------------------- TEST-REDIS-08 (D-01): concurrent observational, no winner -----------------------------

    [Fact]
    public async Task ConcurrentStart_SameWorkflow_BothSucceed_FinalStructurallyValid()
    {
        var ct = TestContext.Current.CancellationToken;

        // A1 caution: HttpClient is thread-safe, but a single instance MAY serialize the two
        // SendAsync calls internally. Use TWO clients so the POSTs genuinely race. Swapping back
        // to one client is a one-liner if ever needed.
        using var c1 = _factory.CreateClient();
        using var c2 = _factory.CreateClient();

        var procId = await SeedProcessorAsync(c1, ct);
        var stepId = await SeedStepAsync(c1, ct, procId);
        var wfId = await SeedWorkflowAsync(c1, ct, new List<Guid> { stepId });

        var prefix = _factory.RedisKeyPrefix;
        var db = _factory.RedisMultiplexer.GetDatabase();

        // Two parallel POST /start for the SAME wfId. Last-write-wins, no Redis lock (PROJECT.md).
        var t1 = c1.PostAsJsonAsync("/api/v1/orchestration/start", new List<Guid> { wfId }, ct);
        var t2 = c2.PostAsJsonAsync("/api/v1/orchestration/start", new List<Guid> { wfId }, ct);
        var responses = await Task.WhenAll(t1, t2);

        // Both succeed, no crash.
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));

        // Final root is structurally valid (round-trips) — NO assertion about WHICH jobId won.
        var root = JsonSerializer.Deserialize<WorkflowRootProjection>(
            (await db.StringGetAsync($"{prefix}{wfId}")).ToString());
        Assert.NotNull(root);
    }
}
