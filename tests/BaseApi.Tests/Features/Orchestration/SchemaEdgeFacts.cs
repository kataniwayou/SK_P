using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BaseApi.Service.Features.Orchestration.Projection;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Schema;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using BaseApi.Tests.Composition;
using BaseApi.Tests.TestHelpers;
using Messaging.Contracts.Projections;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Features.Orchestration;

/// <summary>
/// Phase 14 SC#3 / L1-VALIDATE-05 integration tests for the schema-edge compatibility gate
/// (<see cref="BaseApi.Service.Features.Orchestration.Validation.SchemaEdgeValidator"/>) reached via
/// <c>POST /api/v1/orchestration/start</c>. Uses <see cref="Phase8WebAppFactory"/> per CONTEXT D-20.
/// <para>
/// <b>2 facts:</b>
/// <list type="number">
///   <item><c>SchemaEdgeMismatch_Returns422_WithParentAndChild</c> — parent.OutputSchemaId=X,
///     child.InputSchemaId=Y (distinct) → 422 + <c>errors.gate=="schemaEdge"</c> +
///     <c>errors.offending.{parentStepId,childStepId}</c>.</item>
///   <item><c>SchemaEdgeNullSide_Passes</c> — parent.OutputSchemaId=null (source processor),
///     child.InputSchemaId=Y → 204 (null on either side passes).</item>
/// </list>
/// </para>
/// </summary>
[Trait("Phase", "14")]
[Collection("ParentIndex")]
public sealed class SchemaEdgeFacts : IClassFixture<HarnessWebAppFactory>
{
    private readonly HarnessWebAppFactory _factory;

    public SchemaEdgeFacts(HarnessWebAppFactory factory) => _factory = factory;

    /// <summary>
    /// Phase 22 (PROC-LIVE-01): seeds each participating processor's self-registered L2 entry
    /// (<c>skp:{procId}</c>) live so the new processor-liveness gate — which runs AFTER schema-edge and
    /// BEFORE UpsertAsync — does not reject the previously-204 path. interval is SECONDS (now + 300*2 &gt; now).
    /// Tracks the key for known-key cleanup (D-23).
    /// </summary>
    private async Task SeedLiveAsync(IDatabase db, Guid procId)
    {
        var projection = new ProcessorProjection(null, null, new LivenessProjection(DateTime.UtcNow, 300, "Live"));
        await db.StringSetAsync(L2ProjectionKeys.Processor(procId), JsonSerializer.Serialize(projection));
        _factory.TrackRedisKey(L2ProjectionKeys.Processor(procId));
    }

    /// <summary>Minimal valid draft-2020-12 schema body — type:object accepts any object payload.</summary>
    private const string MinimalSchema = "{\"type\":\"object\"}";

    /// <summary>POSTs a Schema and returns its new Id.</summary>
    private static async Task<Guid> SeedSchemaAsync(HttpClient client, CancellationToken ct)
    {
        var dto = new SchemaCreateDto(
            Name: $"edge-schema-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            Definition: MinimalSchema);
        var resp = await client.PostAsJsonAsync("/api/v1/schemas", dto, ct);
        resp.EnsureSuccessStatusCode();
        var read = await resp.Content.ReadFromJsonAsync<SchemaReadDto>(cancellationToken: ct);
        return read!.Id;
    }

    /// <summary>
    /// POSTs a Processor with the given Input/Output schema ids (ConfigSchemaId=null so the
    /// payload-config gate never interferes) and returns its new Id.
    /// </summary>
    private static async Task<Guid> SeedProcessorAsync(
        HttpClient client, Guid? inputSchemaId, Guid? outputSchemaId, CancellationToken ct)
    {
        var dto = new ProcessorCreateDto(
            Name: $"edge-proc-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            SourceHash: HashHelpers.RandomSha256Hex(),
            InputSchemaId: inputSchemaId,
            OutputSchemaId: outputSchemaId,
            ConfigSchemaId: null);
        var resp = await client.PostAsJsonAsync("/api/v1/processors", dto, ct);
        resp.EnsureSuccessStatusCode();
        var read = await resp.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);
        return read!.Id;
    }

    /// <summary>POSTs a Step wiring the given ProcessorId + NextStepIds and returns its new Id.</summary>
    private static async Task<Guid> SeedStepAsync(
        HttpClient client, Guid processorId, List<Guid>? nextStepIds, CancellationToken ct)
    {
        var dto = new StepCreateDto(
            Name: $"edge-step-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            ProcessorId: processorId,
            NextStepIds: nextStepIds,
            EntryCondition: StepEntryCondition.PreviousCompleted);
        var resp = await client.PostAsJsonAsync("/api/v1/steps", dto, ct);
        resp.EnsureSuccessStatusCode();
        var read = await resp.Content.ReadFromJsonAsync<StepReadDto>(cancellationToken: ct);
        return read!.Id;
    }

    /// <summary>POSTs a Workflow with the given EntryStepIds and returns its new Id.</summary>
    private static async Task<Guid> SeedWorkflowAsync(HttpClient client, List<Guid> entryStepIds, CancellationToken ct)
    {
        var dto = new WorkflowCreateDto(
            Name: $"edge-wf-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            EntryStepIds: entryStepIds,
            AssignmentIds: null,
            CronExpression: null);
        var resp = await client.PostAsJsonAsync("/api/v1/workflows", dto, ct);
        resp.EnsureSuccessStatusCode();
        var read = await resp.Content.ReadFromJsonAsync<WorkflowReadDto>(cancellationToken: ct);
        return read!.Id;
    }

    [Fact]
    public async Task SchemaEdgeMismatch_Returns422_WithParentAndChild()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // Distinct schemas: parent outputs X, child expects Y → mismatch.
        var schemaX = await SeedSchemaAsync(client, ct);
        var schemaY = await SeedSchemaAsync(client, ct);

        var parentProc = await SeedProcessorAsync(client, inputSchemaId: null, outputSchemaId: schemaX, ct);
        var childProc = await SeedProcessorAsync(client, inputSchemaId: schemaY, outputSchemaId: null, ct);

        // Child first (no NextStepIds → terminal), then parent wiring NextStepIds=[child].
        var childStepId = await SeedStepAsync(client, childProc, nextStepIds: null, ct);
        var parentStepId = await SeedStepAsync(client, parentProc, nextStepIds: new List<Guid> { childStepId }, ct);

        var wfId = await SeedWorkflowAsync(client, new List<Guid> { parentStepId }, ct);

        var resp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start",
            new List<Guid> { wfId },
            ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var errors = doc.RootElement.GetProperty("errors");
        Assert.Equal("schemaEdge", errors.GetProperty("gate").GetString());

        var offending = errors.GetProperty("offending");
        Assert.Equal(parentStepId.ToString(), offending.GetProperty("parentStepId").GetString());
        Assert.Equal(childStepId.ToString(), offending.GetProperty("childStepId").GetString());
    }

    [Fact]
    public async Task SchemaEdgeNullSide_Passes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // Parent is a source processor (OutputSchemaId=null) → edge passes regardless of child input.
        var schemaY = await SeedSchemaAsync(client, ct);

        var parentProc = await SeedProcessorAsync(client, inputSchemaId: null, outputSchemaId: null, ct);
        var childProc = await SeedProcessorAsync(client, inputSchemaId: schemaY, outputSchemaId: null, ct);

        // Distinct steps, no back-edge (no cycle); ConfigSchemaId=null on all (no payload gate).
        var childStepId = await SeedStepAsync(client, childProc, nextStepIds: null, ct);
        var parentStepId = await SeedStepAsync(client, parentProc, nextStepIds: new List<Guid> { childStepId }, ct);

        var wfId = await SeedWorkflowAsync(client, new List<Guid> { parentStepId }, ct);

        // PROC-LIVE-01: seed both participating processors live so the liveness gate (post-schemaEdge,
        // pre-Upsert) passes; otherwise the previously-204 path now returns 422 "absent".
        var db = _factory.RedisMultiplexer.GetDatabase();
        await SeedLiveAsync(db, parentProc);
        await SeedLiveAsync(db, childProc);

        // A successful Start writes root/step keys + SADDs the parent index — track + SREM for cleanup.
        _factory.TrackRedisKey(L2ProjectionKeys.Root(wfId));
        _factory.TrackRedisKey(L2ProjectionKeys.Step(wfId, parentStepId));
        _factory.TrackRedisKey(L2ProjectionKeys.Step(wfId, childStepId));

        try
        {
            var resp = await client.PostAsJsonAsync(
                "/api/v1/orchestration/start",
                new List<Guid> { wfId },
                ct);

            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        }
        finally
        {
            await db.SetRemoveAsync(RedisProjectionKeys.ParentIndex(), wfId.ToString("D"));
        }
    }
}
