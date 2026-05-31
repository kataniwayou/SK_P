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
/// Idempotency + concurrency facts, reconciled to the Phase 24 first-win semantics
/// (WEBAPI-SUPPRESS-01), against real Postgres + real compose Redis (via <see cref="Phase8WebAppFactory"/>).
/// <para>
/// Under first-win a re-Start of an already-present workflow is a NO-OP: the root is NOT rewritten
/// (its <c>jobId</c> is UNCHANGED) and the keyspace is NOT overwritten — this DELIBERATELY supersedes
/// the Phase 16 last-write-wins TEST-REDIS-06 contract (which asserted the jobId CHANGED). The
/// concurrent fact is observational only: two parallel POSTs both 204 (the loser's existence probe
/// sees the winner's root and skips, or a tie does an idempotent SET) and the final root round-trips,
/// with NO deterministic-winner assertion. The per-class <c>RedisFixture</c> SCAN+DEL teardown sweeps residue.
/// </para>
/// </summary>
[Trait("Phase", "16")]
[Collection("ParentIndex")]
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

    // ----------------------------- WEBAPI-SUPPRESS-01 (supersedes TEST-REDIS-06): re-Start is first-win no-op -----------------------------

    [Fact]
    public async Task ReStart_SameWorkflow_IsFirstWin_NoOverwrite()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // Graph A -> B (A.NextStepIds = [B]); workflow entry = [A]. First Start projects per-step
        // keys for BOTH A and B; the shrink + re-Start proves first-win does NOT re-project.
        var procId = await SeedProcessorAsync(client, ct);
        var stepB = await SeedStepAsync(client, ct, procId);
        var stepA = await SeedStepAsync(client, ct, procId, nextStepIds: new List<Guid> { stepB });
        var wfId = await SeedWorkflowAsync(client, ct, new List<Guid> { stepA });

        var prefix = _factory.RedisKeyPrefix;
        var db = _factory.RedisMultiplexer.GetDatabase();

        // PROC-LIVE-01: seed the single participating processor live so the liveness gate accepts the Start.
        await _factory.SeedLiveProcessorAsync(procId, ct);

        try
        {
            // First Start — capture the root jobId.
            var first = await client.PostAsJsonAsync(
                "/api/v1/orchestration/start", new List<Guid> { wfId }, ct);
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
            var firstRoot = JsonSerializer.Deserialize<WorkflowRootProjection>(
                (await db.StringGetAsync($"{prefix}{wfId}")).ToString());
            var firstJobId = firstRoot!.JobId;
            Assert.True(await db.KeyExistsAsync($"{prefix}{wfId}:{stepB}"), "step B key projected on first Start");

            // Shrink the graph (remove A -> B). Under the SUPERSEDED last-write-wins contract a
            // re-Start would GC B; under first-win the re-Start skips entirely, so B SURVIVES.
            await UpdateStepNextAsync(client, ct, stepA, procId, nextStepIds: null);

            // Second Start (SAME workflowIds) — first-win: the root exists → whole write path skipped.
            var second = await client.PostAsJsonAsync(
                "/api/v1/orchestration/start", new List<Guid> { wfId }, ct);
            Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
            var secondRoot = JsonSerializer.Deserialize<WorkflowRootProjection>(
                (await db.StringGetAsync($"{prefix}{wfId}")).ToString());

            // First-win — the root was NOT rewritten, so the jobId is UNCHANGED (no fresh Guid.NewGuid()).
            Assert.Equal(firstJobId, secondRoot!.JobId);

            // No overwrite/GC — BOTH the original step keys survive (B was NOT GC'd, A was NOT re-projected away).
            Assert.True(await db.KeyExistsAsync($"{prefix}{wfId}:{stepA}"), "step A key still present after first-win re-Start");
            Assert.True(
                await db.KeyExistsAsync($"{prefix}{wfId}:{stepB}"),
                "first-win re-Start must NOT GC the orphan (no delete-then-write overwrite)");

            // Track the keys the first Start wrote for known-key cleanup (both A and B survive).
            _factory.TrackRedisKey($"{prefix}{wfId}");
            _factory.TrackRedisKey($"{prefix}{wfId}:{stepA}");
            _factory.TrackRedisKey($"{prefix}{wfId}:{stepB}");
        }
        finally
        {
            await _factory.SremParentIndexAsync(wfId);
        }
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

        // PROC-LIVE-01: seed the participating processor live so both concurrent Starts pass the gate.
        await _factory.SeedLiveProcessorAsync(procId, ct);

        try
        {
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

            _factory.TrackRedisKey($"{prefix}{wfId}");
            _factory.TrackRedisKey($"{prefix}{wfId}:{stepId}");
        }
        finally
        {
            await _factory.SremParentIndexAsync(wfId);
        }
    }
}
