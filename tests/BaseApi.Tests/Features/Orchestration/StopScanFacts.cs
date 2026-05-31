using System.Net;
using System.Net.Http.Json;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using BaseApi.Tests.Composition;
using BaseApi.Tests.TestHelpers;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Features.Orchestration;

/// <summary>
/// Phase 16 Plan 04 thin confirmatory fact for TEST-REDIS-09 — the INVERTED post-Stop key
/// state of <c>POST /api/v1/orchestration/stop</c> against real Postgres + real compose Redis
/// (via <see cref="Phase8WebAppFactory"/>). The behavior itself is already covered by the
/// Phase-15 <c>StopGateFacts</c> / <c>StopOrchestrationFacts</c> / <c>StopCleanupFacts</c>;
/// this is a NEW additive class (those are left untouched) that mirrors the rewritten SC5
/// phrasing with a single EXISTS-shaped post-Stop assertion.
/// <para>
/// Contract (15-CONTEXT D-06): after a Stop, the root key + per-step keys are GONE
/// (GET-and-follow cleanup) while the per-processor key REMAINS (never deleted by cleanup;
/// TTL'd per D-08). The per-class <c>RedisFixture</c> SCAN+DEL teardown sweeps residue.
/// </para>
/// </summary>
[Trait("Phase", "16")]
[Collection("ParentIndex")]
public sealed class StopScanFacts : IClassFixture<HarnessWebAppFactory>
{
    private readonly HarnessWebAppFactory _factory;

    public StopScanFacts(HarnessWebAppFactory factory) => _factory = factory;

    // ---- HTTP seeding helpers (Processor → Step → Workflow) ----

    private static async Task<Guid> SeedProcessorAsync(HttpClient client, CancellationToken ct)
    {
        var dto = new ProcessorCreateDto(
            Name: $"ssf-proc-{Guid.NewGuid():N}",
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
            Name: $"ssf-step-{Guid.NewGuid():N}",
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
            Name: $"ssf-wf-{Guid.NewGuid():N}",
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

    // ----------------------------- TEST-REDIS-09 (inverted post-Stop key state) -----------------------------

    [Fact]
    public async Task Stop_AfterStart_RemovesRootAndStep_KeepsProcessor()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var procId = await SeedProcessorAsync(client, ct);
        var stepId = await SeedStepAsync(client, ct, procId);
        var wfId = await SeedWorkflowAsync(client, ct, new List<Guid> { stepId });

        var prefix = _factory.RedisKeyPrefix;
        var db = _factory.RedisMultiplexer.GetDatabase();

        // PROC-NOCREATE-01 + PROC-LIVE-01: the writer no longer creates the processor key; seed it
        // EXTERNALLY (self-registration) so the Start liveness gate passes AND the post-Stop "processor
        // RETAINED" assertion holds (cleanup never deletes processor keys).
        await _factory.SeedLiveProcessorAsync(procId, ct);

        try
        {
            // Start projects root + per-step keyspaces (the processor key is the externally-seeded one).
            var start = await client.PostAsJsonAsync(
                "/api/v1/orchestration/start", new List<Guid> { wfId }, ct);
            Assert.Equal(HttpStatusCode.NoContent, start.StatusCode);
            Assert.True(await db.KeyExistsAsync($"{prefix}{wfId}"), "root present after Start");
            Assert.True(await db.KeyExistsAsync($"{prefix}{wfId}:{stepId}"), "per-step present after Start");
            Assert.True(await db.KeyExistsAsync($"{prefix}{procId}"), "processor present (externally self-registered)");

            // Stop runs the EXISTS gate (root present → passes) then the tolerant GET-and-follow
            // cleanup: root + per-step deleted; processor NEVER deleted (TTL'd); SREMs the parent index.
            var stop = await client.PostAsJsonAsync(
                "/api/v1/orchestration/stop", new List<Guid> { wfId }, ct);
            Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);

            // Inverted contract (D-06).
            Assert.False(await db.KeyExistsAsync($"{prefix}{wfId}"), "root deleted post-Stop");
            Assert.False(await db.KeyExistsAsync($"{prefix}{wfId}:{stepId}"), "per-step deleted post-Stop");
            Assert.True(await db.KeyExistsAsync($"{prefix}{procId}"), "processor RETAINED post-Stop (TTL)");
        }
        finally
        {
            // Stop's cleanup already SREMs the wf id; this is defensive/idempotent.
            await _factory.SremParentIndexAsync(wfId);
        }
    }
}
