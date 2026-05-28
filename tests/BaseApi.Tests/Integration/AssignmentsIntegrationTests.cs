using System.Net;
using System.Net.Http.Json;
using BaseApi.Service.Features.Assignment;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Step;
using BaseApi.Tests.Composition;
using Xunit;

namespace BaseApi.Tests.Integration;

/// <summary>
/// Phase 8 Wave B smoke integration tests for the Assignment feature (5 [Fact]s per
/// CONTEXT D-07). Uses <see cref="Phase8WebAppFactory"/> (Wave A 08-01) which
/// encapsulates a per-class throwaway Postgres DB via <c>PostgresFixture</c>.
/// <para>
/// <b>GREEN-state dependency:</b> these tests run against the PRODUCTION
/// <c>AppDbContext</c>. They will only pass AFTER Wave C 08-07 lands (a) DbSets on
/// AppDbContext, (b) the MigrateAsync swap on StartupCompletionService, and (c) the
/// per-entity DI registration (<see cref="AssignmentServiceCollectionExtensions.AddAssignmentFeature"/>).
/// Phase 8 Plan 08-08 verifies the GREEN-state regression after Wave C completes.
/// </para>
/// <para>
/// <b>FK prerequisite chain:</b> AssignmentCreateDto requires a non-Guid.Empty StepId
/// value that EXISTS in the steps table (Postgres FK constraint
/// <c>fk_assignment_step_id</c> rejects non-existent Guids).
/// <see cref="CreatePrereqAsync"/> creates a Processor, then a Step inline via the
/// public HTTP API — mirrors the Plan 08-04 helper pattern (Phase 10 simplification:
/// no Schema POST since AssignmentEntity no longer carries a SchemaId field).
/// </para>
/// </summary>
[Trait("Phase8Wave", "B")]
public sealed class AssignmentsIntegrationTests : IClassFixture<Phase8WebAppFactory>
{
    private readonly Phase8WebAppFactory _factory;

    public AssignmentsIntegrationTests(Phase8WebAppFactory factory) => _factory = factory;

    private static string RandomSha256Hex()
    {
        var bytes = Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray();
        return string.Concat(bytes.Select(b => b.ToString("x2")));
    }

    private static async Task<Guid> CreatePrereqAsync(HttpClient client, CancellationToken ct)
    {
        // 1. Processor — FK target for StepEntity.ProcessorId (transitive prereq for Step).
        var procDto = new ProcessorCreateDto(
            Name: $"prereq-proc-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            SourceHash: RandomSha256Hex(),
            InputSchemaId: null,
            OutputSchemaId: null,
            ConfigSchemaId: null);
        var procResp = await client.PostAsJsonAsync("/api/v1/processors", procDto, ct);
        procResp.EnsureSuccessStatusCode();
        var proc = await procResp.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);

        // 2. Step — FK target for AssignmentEntity.StepId.
        var stepDto = new StepCreateDto(
            Name: $"prereq-step-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            ProcessorId: proc!.Id,
            NextStepIds: null,
            EntryCondition: StepEntryCondition.PreviousCompleted);
        var stepResp = await client.PostAsJsonAsync("/api/v1/steps", stepDto, ct);
        stepResp.EnsureSuccessStatusCode();
        var step = await stepResp.Content.ReadFromJsonAsync<StepReadDto>(cancellationToken: ct);

        return step!.Id;
    }

    private static AssignmentCreateDto NewValidCreateDto(Guid stepId, string suffix = "") => new(
        Name: $"my-assignment{suffix}",
        Version: "1.0.0",
        Description: "Integration test assignment",
        StepId: stepId,
        Payload: "{\"key\":\"value\"}");

    [Fact]
    public async Task List_ReturnsEmptyArray_OnEmptyDb()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/assignments", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Phase8WebAppFactory uses IClassFixture (shared per class) — sibling facts may
        // have created rows by the time this fact runs. Assert the response is a
        // well-formed JSON array (the list-endpoint contract), not strictly-zero rows.
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.NotNull(body);
        Assert.StartsWith("[", body);
        Assert.EndsWith("]", body);
    }

    [Fact]
    public async Task Create_Returns201_AndLocationHeader_WhenValid()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var stepId = await CreatePrereqAsync(client, ct);

        var resp = await client.PostAsJsonAsync("/api/v1/assignments",
            NewValidCreateDto(stepId), ct);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.NotNull(resp.Headers.Location);
        // Location header is absolute (Kestrel composes it with scheme+host); the
        // [controller] route token preserves the C# class-name casing — "Assignments"
        // not "assignments". Regex tolerates both.
        Assert.Matches(@"(?i)/api/v1/assignments/[a-f0-9\-]{36}$", resp.Headers.Location!.ToString());

        var read = await resp.Content.ReadFromJsonAsync<AssignmentReadDto>(cancellationToken: ct);
        Assert.NotNull(read);
        Assert.NotEqual(Guid.Empty, read!.Id);
        Assert.Equal(stepId, read.StepId);
        // Postgres jsonb normalizes whitespace (adds " " after colons) and may reorder keys.
        // Compare semantically rather than via literal string equality.
        Assert.Equal(NormalizeJson("{\"key\":\"value\"}"), NormalizeJson(read.Payload));
        Assert.NotEqual(default, read.CreatedAt);            // HTTP-07: audit populated
        Assert.NotEqual(default, read.UpdatedAt);
    }

    [Fact]
    public async Task GetById_Returns200_WhenExisting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var stepId = await CreatePrereqAsync(client, ct);

        var createResp = await client.PostAsJsonAsync("/api/v1/assignments",
            NewValidCreateDto(stepId, "-getbyid"), ct);
        var created = await createResp.Content.ReadFromJsonAsync<AssignmentReadDto>(cancellationToken: ct);

        var resp = await client.GetAsync($"/api/v1/assignments/{created!.Id}", ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var read = await resp.Content.ReadFromJsonAsync<AssignmentReadDto>(cancellationToken: ct);
        Assert.Equal(created.Id, read!.Id);
        // jsonb-storage normalizes whitespace + may reorder keys; compare semantically.
        Assert.Equal(NormalizeJson(created.Payload), NormalizeJson(read.Payload));
    }

    [Fact]
    public async Task Update_Returns200_AndChangedFields_WhenExisting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var stepId = await CreatePrereqAsync(client, ct);

        var createResp = await client.PostAsJsonAsync("/api/v1/assignments",
            NewValidCreateDto(stepId, "-update"), ct);
        var created = await createResp.Content.ReadFromJsonAsync<AssignmentReadDto>(cancellationToken: ct);

        var update = new AssignmentUpdateDto(
            Name: created!.Name,
            Version: "1.0.1",
            Description: "Updated",
            StepId: stepId,
            Payload: "{\"updated\":true}");
        var putResp = await client.PutAsJsonAsync($"/api/v1/assignments/{created.Id}", update, ct);
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        var getResp = await client.GetAsync($"/api/v1/assignments/{created.Id}", ct);
        var read = await getResp.Content.ReadFromJsonAsync<AssignmentReadDto>(cancellationToken: ct);
        Assert.Equal("1.0.1", read!.Version);
        // jsonb-storage normalizes whitespace + may reorder keys; compare semantically.
        Assert.Equal(NormalizeJson("{\"updated\":true}"), NormalizeJson(read.Payload));
    }

    /// <summary>
    /// Semantic JSON equality helper: parse, then re-serialize with sorted keys + compact
    /// whitespace. Two semantically equivalent JSON documents always produce the same
    /// normalized string. Used to compare Assignment.Payload across Postgres jsonb storage
    /// (which reorders keys + adds whitespace).
    /// </summary>
    private static string NormalizeJson(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json);
        return SerializeSorted(node);
    }

    private static string SerializeSorted(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is null) return "null";
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            var sb = new System.Text.StringBuilder("{");
            var first = true;
            foreach (var kvp in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!first) sb.Append(',');
                sb.Append(System.Text.Json.JsonSerializer.Serialize(kvp.Key));
                sb.Append(':');
                sb.Append(SerializeSorted(kvp.Value));
                first = false;
            }
            sb.Append('}');
            return sb.ToString();
        }
        if (node is System.Text.Json.Nodes.JsonArray arr)
        {
            var sb = new System.Text.StringBuilder("[");
            var first = true;
            foreach (var item in arr)
            {
                if (!first) sb.Append(',');
                sb.Append(SerializeSorted(item));
                first = false;
            }
            sb.Append(']');
            return sb.ToString();
        }
        return node.ToJsonString();
    }

    [Fact]
    public async Task Delete_Returns204_WhenExisting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var stepId = await CreatePrereqAsync(client, ct);

        var createResp = await client.PostAsJsonAsync("/api/v1/assignments",
            NewValidCreateDto(stepId, "-delete"), ct);
        var created = await createResp.Content.ReadFromJsonAsync<AssignmentReadDto>(cancellationToken: ct);

        var deleteResp = await client.DeleteAsync($"/api/v1/assignments/{created!.Id}", ct);
        Assert.Equal(HttpStatusCode.NoContent, deleteResp.StatusCode);

        var getResp = await client.GetAsync($"/api/v1/assignments/{created.Id}", ct);
        Assert.Equal(HttpStatusCode.NotFound, getResp.StatusCode);
    }
}
