using System.Text.Json;
using BaseApi.Service.Features.Assignment;
using BaseApi.Service.Features.Orchestration.Loading;
using BaseApi.Service.Features.Orchestration.Projection;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Schema;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using WriterStepProjection = BaseApi.Service.Features.Orchestration.Projection.StepProjection;
using BaseApi.Tests.Composition;
using BaseApi.Tests.TestHelpers;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using System.Net.Http.Json;
using Xunit;

namespace BaseApi.Tests.Features.Orchestration;

/// <summary>
/// Phase 15 Plan 02 integration facts for <see cref="IRedisProjectionWriter"/> against a
/// real compose Redis (via <see cref="Phase8WebAppFactory"/> + its encapsulated
/// <c>RedisFixture</c>). Each fact seeds a workflow graph through the public HTTP API so
/// the junction rows exist exactly as production writes them, resolves the INTERNAL loader
/// to build the same per-workflow <c>WorkflowGraphSnapshot</c> the Start loop will hand the
/// writer (Plan 04), drives <c>UpsertAsync</c>, then reads the L2 keyspaces back.
/// <para>
/// Phase 22 contract (L2IDX-01 / PROC-NOCREATE-01): the writer SADDs <c>wf.Id:D</c> into the
/// shared <c>skp:</c> parent-index SET and creates ZERO processor keys (processor entries are
/// owned solely by external self-registration). These facts assert SMEMBERS(ParentIndex) CONTAINS
/// the wf id and that no <c>skp:{procId}</c> key exists after Upsert. Each fact tracks the root/step
/// keys it created and SREMs its own wf id from the parent index in cleanup so the shared keyspace
/// returns to its BEFORE state (D-23 known-key cleanup; this class joins the non-parallel
/// <c>ParentIndex</c> collection — Task 4).
/// </para>
/// </summary>
[Trait("Phase", "15")]
[Collection("ParentIndex")]
public sealed class RedisProjectionWriterFacts : IClassFixture<Phase8WebAppFactory>
{
    private readonly Phase8WebAppFactory _factory;

    public RedisProjectionWriterFacts(Phase8WebAppFactory factory) => _factory = factory;

    // ---- HTTP seeding helpers (Schema → Processor → Step → Assignment → Workflow) ----

    private static async Task<Guid> SeedSchemaAsync(HttpClient client, CancellationToken ct)
    {
        var dto = new SchemaCreateDto(
            Name: $"rpw-schema-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            Definition: """{ "type": "object" }""");
        var resp = await client.PostAsJsonAsync("/api/v1/schemas", dto, ct);
        resp.EnsureSuccessStatusCode();
        var schema = await resp.Content.ReadFromJsonAsync<SchemaReadDto>(cancellationToken: ct);
        return schema!.Id;
    }

    private static async Task<Guid> SeedProcessorAsync(
        HttpClient client, CancellationToken ct, Guid? inputSchemaId = null, Guid? outputSchemaId = null)
    {
        var dto = new ProcessorCreateDto(
            Name: $"rpw-proc-{Guid.NewGuid():N}",
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
        HttpClient client, CancellationToken ct, Guid processorId, List<Guid>? nextStepIds = null)
    {
        var dto = new StepCreateDto(
            Name: $"rpw-step-{Guid.NewGuid():N}",
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

    private static async Task<Guid> SeedAssignmentAsync(
        HttpClient client, CancellationToken ct, Guid stepId, string payload)
    {
        var dto = new AssignmentCreateDto(
            Name: $"rpw-asg-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            StepId: stepId,
            Payload: payload);
        var resp = await client.PostAsJsonAsync("/api/v1/assignments", dto, ct);
        resp.EnsureSuccessStatusCode();
        var asg = await resp.Content.ReadFromJsonAsync<AssignmentReadDto>(cancellationToken: ct);
        return asg!.Id;
    }

    private static async Task<Guid> SeedWorkflowAsync(
        HttpClient client, CancellationToken ct, List<Guid> entryStepIds, List<Guid> assignmentIds, string? cron = null)
    {
        var dto = new WorkflowCreateDto(
            Name: $"rpw-wf-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            EntryStepIds: entryStepIds,
            AssignmentIds: assignmentIds,
            CronExpression: cron);
        var resp = await client.PostAsJsonAsync("/api/v1/workflows", dto, ct);
        resp.EnsureSuccessStatusCode();
        var wf = await resp.Content.ReadFromJsonAsync<WorkflowReadDto>(cancellationToken: ct);
        return wf!.Id;
    }

    /// <summary>
    /// SREMs the wf id from the shared parent index and deletes the tracked root/step keys created by
    /// this Upsert, so the shared <c>skp:</c> keyspace returns to its BEFORE state (D-23 / T-22-15).
    /// </summary>
    private async Task CleanupAsync(IDatabase db, Guid wfId, IEnumerable<Guid> stepIds, CancellationToken ct)
    {
        await db.SetRemoveAsync(RedisProjectionKeys.ParentIndex(), wfId.ToString("D"));
        var keys = new List<RedisKey> { L2ProjectionKeys.Root(wfId) };
        keys.AddRange(stepIds.Select(s => (RedisKey)L2ProjectionKeys.Step(wfId, s)));
        await db.KeyDeleteAsync(keys.ToArray());
    }

    // ----------------------------- L2-PROJECT-01/03/04/05 + L2IDX-01 + PROC-NOCREATE-01 -----------------------------

    [Fact]
    public async Task Upsert_Writes_Root_Step_ParentIndex_And_No_Processor_Key()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        const string cron = "*/5 * * * *";
        const string payload = """{ "k": "v" }""";

        // A processor with an Input + Output schema (the processor key is NOT written by the writer
        // any more — PROC-NOCREATE-01 — but the step still references the processor id).
        var inputSchemaId = await SeedSchemaAsync(client, ct);
        var outputSchemaId = await SeedSchemaAsync(client, ct);
        var procId = await SeedProcessorAsync(client, ct, inputSchemaId, outputSchemaId);
        var stepId = await SeedStepAsync(client, ct, procId);            // terminal step → NextStepIds == []
        var assignmentId = await SeedAssignmentAsync(client, ct, stepId, payload);
        var wfId = await SeedWorkflowAsync(
            client, ct, entryStepIds: new List<Guid> { stepId },
            assignmentIds: new List<Guid> { assignmentId }, cron: cron);

        var correlationId = $"corr-{Guid.NewGuid():N}";

        // Build the per-workflow snapshot via the real loader, then drive the writer.
        using (var scope = _factory.Services.CreateScope())
        {
            var loader = scope.ServiceProvider.GetRequiredService<IWorkflowGraphLoader>();
            var writer = scope.ServiceProvider.GetRequiredService<IRedisProjectionWriter>();
            using var snapshot = await loader.LoadL1Async(new[] { wfId }, ct);
            await writer.UpsertAsync(snapshot, correlationId, ct);
        }

        var db = _factory.RedisMultiplexer.GetDatabase();
        // Track keys for known-key cleanup (defense-in-depth alongside the explicit CleanupAsync).
        _factory.TrackRedisKey(L2ProjectionKeys.Root(wfId));
        _factory.TrackRedisKey(L2ProjectionKeys.Step(wfId, stepId));

        try
        {
            // --- Root keyspace ---
            var rootValue = await db.StringGetAsync(L2ProjectionKeys.Root(wfId));
            Assert.True(rootValue.HasValue, "root key should be set");
            var root = JsonSerializer.Deserialize<WorkflowRootProjection>(rootValue.ToString());
            Assert.NotNull(root);
            Assert.Contains(stepId, root!.EntryStepIds);
            Assert.Equal(cron, root.Cron);
            Assert.NotEqual(Guid.Empty, root.JobId);
            Assert.Equal(correlationId, root.CorrelationId);
            Assert.Equal("Pending", root.Liveness.Status);
            Assert.Equal(0, root.Liveness.Interval);

            // --- Step keyspace ---
            var stepValue = await db.StringGetAsync(L2ProjectionKeys.Step(wfId, stepId));
            Assert.True(stepValue.HasValue, "step key should be set");
            var stepJson = stepValue.ToString();
            // entryCondition serializes as an int (no string-enum converter): Always == 4.
            using (var doc = JsonDocument.Parse(stepJson))
            {
                Assert.Equal(JsonValueKind.Number, doc.RootElement.GetProperty("entryCondition").ValueKind);
                Assert.Equal((int)StepEntryCondition.Always, doc.RootElement.GetProperty("entryCondition").GetInt32());
            }
            var step = JsonSerializer.Deserialize<WriterStepProjection>(stepJson);
            Assert.NotNull(step);
            Assert.Equal(StepEntryCondition.Always, step!.EntryCondition);
            Assert.Equal(procId, step.ProcessorId);
            // Payload round-trips through the Assignment-create pipeline which re-serializes the
            // JSON (whitespace canonicalized), so compare on JSON value-equality, not the raw literal.
            Assert.Equal(CanonicalJson(payload), CanonicalJson(step.Payload));
            Assert.Empty(step.NextStepIds);   // terminal step → []

            // --- L2IDX-01: parent index SET contains wf.Id (D-format) ---
            var members = await db.SetMembersAsync(RedisProjectionKeys.ParentIndex());
            Assert.Contains(wfId.ToString("D"), members.Select(m => m.ToString()));

            // --- PROC-NOCREATE-01: the writer creates ZERO processor keys ---
            Assert.False(
                await db.KeyExistsAsync(L2ProjectionKeys.Processor(procId)),
                "writer must not create a processor key (PROC-NOCREATE-01 — external self-registration only)");
        }
        finally
        {
            await CleanupAsync(db, wfId, new[] { stepId }, ct);
        }
    }

    // ----------------------------- Rule 1: step with no assignment -----------------------------

    [Fact]
    public async Task Upsert_StepWithoutAssignment_ProjectsEmptyPayload()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // Workflow with a step but NO assignment (AssignmentIds == null) — a valid shape
        // (ENTITY-08). The writer must not crash; the step's payload projects as "".
        var procId = await SeedProcessorAsync(client, ct);
        var stepId = await SeedStepAsync(client, ct, procId);
        var wfId = await SeedWorkflowAsync(
            client, ct, entryStepIds: new List<Guid> { stepId },
            assignmentIds: new List<Guid>());

        using (var scope = _factory.Services.CreateScope())
        {
            var loader = scope.ServiceProvider.GetRequiredService<IWorkflowGraphLoader>();
            var writer = scope.ServiceProvider.GetRequiredService<IRedisProjectionWriter>();
            using var snapshot = await loader.LoadL1Async(new[] { wfId }, ct);
            await writer.UpsertAsync(snapshot, $"corr-{Guid.NewGuid():N}", ct);
        }

        var db = _factory.RedisMultiplexer.GetDatabase();
        _factory.TrackRedisKey(L2ProjectionKeys.Root(wfId));
        _factory.TrackRedisKey(L2ProjectionKeys.Step(wfId, stepId));

        try
        {
            var stepValue = await db.StringGetAsync(L2ProjectionKeys.Step(wfId, stepId));
            Assert.True(stepValue.HasValue, "step key should be set even without an assignment");
            var step = JsonSerializer.Deserialize<WriterStepProjection>(stepValue.ToString());
            Assert.NotNull(step);
            Assert.Equal(string.Empty, step!.Payload);
        }
        finally
        {
            await CleanupAsync(db, wfId, new[] { stepId }, ct);
        }
    }

    /// <summary>Normalizes a JSON string for whitespace-insensitive value comparison.</summary>
    private static string CanonicalJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement);
    }
}
