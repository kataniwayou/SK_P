using System.Text.Json;
using BaseApi.Service.Features.Assignment;
using BaseApi.Service.Features.Orchestration.Loading;
using BaseApi.Service.Features.Orchestration.Projection;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Schema;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using BaseApi.Tests.Composition;
using BaseApi.Tests.TestHelpers;
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
/// writer (Plan 04), drives <c>UpsertAsync</c>, then reads the three L2 keyspaces back.
/// <para>
/// Maps to L2-PROJECT-01 (3 keyspaces in one batch), L2-PROJECT-03/04/05 (locked camelCase
/// shapes) and D-08 (processor-only TTL). The per-class <c>RedisFixture</c> SCAN+DEL teardown
/// already sweeps the TTL'd processor keys — no fixture change, no FLUSHDB.
/// </para>
/// </summary>
[Trait("Phase", "15")]
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

    // ----------------------------- L2-PROJECT-01/03/04/05 -----------------------------

    [Fact]
    public async Task Upsert_Writes_Three_Keyspaces()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        const string cron = "*/5 * * * *";
        const string payload = """{ "k": "v" }""";

        // A processor with an Input + Output schema so inputDefinition/outputDefinition are non-null.
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

        var prefix = _factory.RedisKeyPrefix;
        var db = _factory.RedisMultiplexer.GetDatabase();

        // --- Root keyspace ---
        var rootValue = await db.StringGetAsync($"{prefix}{wfId}");
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
        var stepValue = await db.StringGetAsync($"{prefix}{wfId}:{stepId}");
        Assert.True(stepValue.HasValue, "step key should be set");
        var stepJson = stepValue.ToString();
        // entryCondition serializes as an int (no string-enum converter): Always == 4.
        using (var doc = JsonDocument.Parse(stepJson))
        {
            Assert.Equal(JsonValueKind.Number, doc.RootElement.GetProperty("entryCondition").ValueKind);
            Assert.Equal((int)StepEntryCondition.Always, doc.RootElement.GetProperty("entryCondition").GetInt32());
        }
        var step = JsonSerializer.Deserialize<StepProjection>(stepJson);
        Assert.NotNull(step);
        Assert.Equal(StepEntryCondition.Always, step!.EntryCondition);
        Assert.Equal(procId, step.ProcessorId);
        // Payload round-trips through the Assignment-create pipeline which re-serializes the
        // JSON (whitespace canonicalized), so compare on JSON value-equality, not the raw literal.
        Assert.Equal(CanonicalJson(payload), CanonicalJson(step.Payload));
        Assert.Empty(step.NextStepIds);   // terminal step → []

        // --- Processor keyspace ---
        var procValue = await db.StringGetAsync($"{prefix}{procId}");
        Assert.True(procValue.HasValue, "processor key should be set");
        var procJson = procValue.ToString();
        var proc = JsonSerializer.Deserialize<ProcessorProjection>(procJson);
        Assert.NotNull(proc);
        Assert.NotNull(proc!.InputDefinition);
        Assert.NotNull(proc.OutputDefinition);
        Assert.Equal("Pending", proc.Liveness.Status);

        // Field-name shape: assert the locked camelCase member names exist verbatim.
        using (var procDoc = JsonDocument.Parse(procJson))
        {
            Assert.True(procDoc.RootElement.TryGetProperty("inputDefinition", out _));
            Assert.True(procDoc.RootElement.TryGetProperty("outputDefinition", out _));
        }
    }

    // ----------------------------- D-08 (processor-only TTL) -----------------------------

    [Fact]
    public async Task ProcessorProjection_Ttl()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var procId = await SeedProcessorAsync(client, ct);
        var stepId = await SeedStepAsync(client, ct, procId);
        var assignmentId = await SeedAssignmentAsync(client, ct, stepId, """{ "k": "v" }""");
        var wfId = await SeedWorkflowAsync(
            client, ct, entryStepIds: new List<Guid> { stepId },
            assignmentIds: new List<Guid> { assignmentId });

        using (var scope = _factory.Services.CreateScope())
        {
            var loader = scope.ServiceProvider.GetRequiredService<IWorkflowGraphLoader>();
            var writer = scope.ServiceProvider.GetRequiredService<IRedisProjectionWriter>();
            using var snapshot = await loader.LoadL1Async(new[] { wfId }, ct);
            await writer.UpsertAsync(snapshot, $"corr-{Guid.NewGuid():N}", ct);
        }

        var prefix = _factory.RedisKeyPrefix;
        var db = _factory.RedisMultiplexer.GetDatabase();

        // Processor key carries a positive TTL (default ProcessorKeyTtlDays = 100).
        var procTtl = await db.KeyTimeToLiveAsync($"{prefix}{procId}");
        Assert.NotNull(procTtl);
        Assert.True(procTtl!.Value > TimeSpan.Zero, "processor key must have a positive TTL");

        // Root + step keys carry NO TTL (Pitfall 2).
        var rootTtl = await db.KeyTimeToLiveAsync($"{prefix}{wfId}");
        Assert.Null(rootTtl);
        var stepTtl = await db.KeyTimeToLiveAsync($"{prefix}{wfId}:{stepId}");
        Assert.Null(stepTtl);
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

        var prefix = _factory.RedisKeyPrefix;
        var db = _factory.RedisMultiplexer.GetDatabase();

        var stepValue = await db.StringGetAsync($"{prefix}{wfId}:{stepId}");
        Assert.True(stepValue.HasValue, "step key should be set even without an assignment");
        var step = JsonSerializer.Deserialize<StepProjection>(stepValue.ToString());
        Assert.NotNull(step);
        Assert.Equal(string.Empty, step!.Payload);
    }

    /// <summary>Normalizes a JSON string for whitespace-insensitive value comparison.</summary>
    private static string CanonicalJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement);
    }
}
