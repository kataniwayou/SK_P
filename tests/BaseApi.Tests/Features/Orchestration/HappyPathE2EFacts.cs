using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BaseApi.Service.Features.Orchestration;
using BaseApi.Service.Features.Orchestration.Projection;
using WriterStepProjection = BaseApi.Service.Features.Orchestration.Projection.StepProjection;
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
/// Phase 16 Plan 02 — TEST-REDIS-06 (D-03): a dedicated full-HTTP happy-path fact that drives the
/// real <c>POST /api/v1/orchestration/start</c> → <c>OrchestrationService</c> → Redis path and
/// asserts the root + per-step L2 keyspaces via System.Text.Json round-trip deserialization against
/// the locked projection records.
/// <para>
/// Unlike <c>RedisProjectionWriterFacts</c> (which calls <c>UpsertAsync</c> in isolation), this fact
/// POSTs through the public API so the per-workflow Start loop, the <c>X-Correlation-Id</c> plumbing,
/// and the full HTTP path are exercised end-to-end. It also serves as the POSITIVE SCAN control
/// referenced by Plan 03 (the same keys ARE findable in "D"-form, proving a no-write SCAN isn't
/// always-empty). Keys are read via <c>_factory.RedisKeyPrefix</c> in default "D" GUID form — never
/// the <c>:N</c> form (T-16-02-01 mitigation; a wrong key GETs null and fails loudly).
/// </para>
/// <para>
/// <b>Phase 22 (PROC-NOCREATE-01 + PROC-LIVE-01):</b> the writer no longer creates the
/// <c>skp:{procId}</c> processor key (Plan 03) — it is now owned exclusively by external
/// self-registration — and the Start path now runs a processor-liveness gate (Plan 04) that requires
/// a live entry. So this fact (a) seeds the processor's liveness entry EXTERNALLY via
/// <see cref="Phase8WebAppFactory.SeedLiveProcessorAsync"/> before Start, (b) asserts the processor key
/// holds the EXTERNALLY-SEEDED shape (a source/sink processor → null input/output definitions), NOT a
/// writer-created one, and (c) joins the non-parallel <c>ParentIndex</c> collection and SREMs its wf id
/// (a passing Start SADDs the shared parent index).
/// </para>
/// </summary>
[Trait("Phase", "16")]
[Collection("ParentIndex")]
public sealed class HappyPathE2EFacts : IClassFixture<HarnessWebAppFactory>
{
    private readonly HarnessWebAppFactory _factory;

    public HappyPathE2EFacts(HarnessWebAppFactory factory) => _factory = factory;

    // ---- HTTP seeding helpers (Schema → Processor(in/out) → Step → Workflow) ----

    private static async Task<Guid> SeedProcessorAsync(
        HttpClient client, CancellationToken ct, Guid? inputSchemaId, Guid? outputSchemaId)
    {
        var dto = new ProcessorCreateDto(
            Name: $"hp-proc-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            SourceHash: HashHelpers.RandomSha256Hex(),
            InputSchemaId: inputSchemaId,
            OutputSchemaId: outputSchemaId,
            ConfigSchemaId: null);
        var resp = await client.PostAsJsonAsync("/api/v1/processors", dto, ct);
        resp.EnsureSuccessStatusCode();
        var proc = await resp.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);
        return proc!.Id;
    }

    private static async Task<Guid> SeedStepAsync(
        HttpClient client, CancellationToken ct, Guid processorId)
    {
        // Terminal step — NextStepIds null → projects as [] (not null).
        var dto = new StepCreateDto(
            Name: $"hp-step-{Guid.NewGuid():N}",
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
        HttpClient client, CancellationToken ct, List<Guid> entryStepIds)
    {
        var dto = new WorkflowCreateDto(
            Name: $"hp-wf-{Guid.NewGuid():N}",
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

    // ----------------------------- TEST-REDIS-06 -----------------------------

    [Fact]
    public async Task Start_HappyPath_WritesRootAndStepKeyspaces()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // Seed a valid graph: processor (source/sink, no schema defs) → terminal step → workflow.
        var procId = await SeedProcessorAsync(client, ct, inputSchemaId: null, outputSchemaId: null);
        var stepId = await SeedStepAsync(client, ct, procId);
        var wfId = await SeedWorkflowAsync(client, ct, new List<Guid> { stepId });

        // PROC-LIVE-01: the processor-liveness gate (Plan 04) now runs before UpsertAsync and requires
        // a live self-registered skp:{procId} entry. PROC-NOCREATE-01: the writer no longer creates it,
        // so we seed it EXTERNALLY (centralized helper) — this is the production ownership boundary.
        await _factory.SeedLiveProcessorAsync(procId, ct);

        // A passing Start writes root + per-step keys + SADDs the parent index — track + SREM for cleanup.
        var prefix = _factory.RedisKeyPrefix;
        _factory.TrackRedisKey($"{prefix}{wfId}");
        _factory.TrackRedisKey($"{prefix}{wfId}:{stepId}");

        try
        {
            // Full HTTP Start with an explicit X-Correlation-Id header (load-bearing — round-trips
            // into the root projection per ORCH-START-07).
            var correlationId = $"e2e-corr-{Guid.NewGuid():N}";
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orchestration/start")
            {
                Content = JsonContent.Create(new List<Guid> { wfId }),
            };
            req.Headers.Add("X-Correlation-Id", correlationId);

            var resp = await client.SendAsync(req, ct);
            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

            var db = _factory.RedisMultiplexer.GetDatabase();

            // --- Root keyspace --- key $"{prefix}{wfId}" (DEFAULT "D" form — NEVER :N)
            var rootVal = await db.StringGetAsync($"{prefix}{wfId}");
            Assert.True(rootVal.HasValue, "root key set after 204 Start");
            var root = JsonSerializer.Deserialize<WorkflowRootProjection>(rootVal.ToString());
            Assert.NotNull(root);
            Assert.Contains(stepId, root!.EntryStepIds);
            Assert.NotEqual(Guid.Empty, root.JobId);
            Assert.Equal(correlationId, root.CorrelationId);
            Assert.Equal("Pending", root.Liveness.Status);

            // --- Per-step keyspace --- key $"{prefix}{wfId}:{stepId}"
            var stepVal = await db.StringGetAsync($"{prefix}{wfId}:{stepId}");
            Assert.True(stepVal.HasValue, "per-step key set after 204 Start");
            var step = JsonSerializer.Deserialize<WriterStepProjection>(stepVal.ToString());
            Assert.NotNull(step);
            Assert.Equal(procId, step!.ProcessorId);
            Assert.Empty(step.NextStepIds);                                  // terminal → [] not null
            Assert.Equal(StepEntryCondition.Always, step.EntryCondition);    // enum compare (int-backed, Always==4); NOT a "Always" string

            // --- Per-processor keyspace --- PROC-NOCREATE-01: the writer creates ZERO processor keys.
            // The skp:{procId} entry present is the EXTERNALLY-SEEDED self-registration (null defs, "Live"),
            // NOT a writer-created one — the writer no longer touches this keyspace.
            var procVal = await db.StringGetAsync($"{prefix}{procId}");
            Assert.True(procVal.HasValue, "processor key is the externally self-registered entry");
            var proc = JsonSerializer.Deserialize<ProcessorProjection>(procVal.ToString());
            Assert.NotNull(proc);
            Assert.Null(proc!.InputDefinition);   // externally seeded (source/sink) — NOT writer-populated from schema defs
            Assert.Null(proc.OutputDefinition);
            Assert.Equal("Live", proc.Liveness.Status);  // self-registration liveness, NOT the writer's "Pending"
        }
        finally
        {
            await _factory.SremParentIndexAsync(wfId);
        }
    }
}
