using System.Net;
using System.Net.Http.Json;
using BaseApi.Service.Features.Assignment;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Schema;
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
/// <b>FK prerequisite chain:</b> AssignmentCreateDto requires non-Guid.Empty StepId
/// and SchemaId values that EXIST in their respective Postgres tables (Postgres FK
/// constraint <c>fk_assignment_step_id</c> + <c>fk_assignment_schema_id</c> reject
/// non-existent Guids). <see cref="CreatePrereqAsync"/> creates a Schema, then a
/// Processor, then a Step inline via the public HTTP API — mirrors the Plan 08-04
/// helper pattern but extended with a Schema POST.
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

    private static async Task<(Guid stepId, Guid schemaId)> CreatePrereqAsync(HttpClient client, CancellationToken ct)
    {
        // 1. Schema — FK target for AssignmentEntity.SchemaId.
        var schemaDto = new SchemaCreateDto(
            Name: $"prereq-schema-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            Definition: "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\"}");
        var schemaResp = await client.PostAsJsonAsync("/api/v1/schemas", schemaDto, ct);
        schemaResp.EnsureSuccessStatusCode();
        var schema = await schemaResp.Content.ReadFromJsonAsync<SchemaReadDto>(cancellationToken: ct);

        // 2. Processor — FK target for StepEntity.ProcessorId (transitive prereq for Step).
        var procDto = new ProcessorCreateDto(
            Name: $"prereq-proc-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            SourceHash: RandomSha256Hex(),
            InputSchemaId: null,
            OutputSchemaId: null);
        var procResp = await client.PostAsJsonAsync("/api/v1/processors", procDto, ct);
        procResp.EnsureSuccessStatusCode();
        var proc = await procResp.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);

        // 3. Step — FK target for AssignmentEntity.StepId.
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

        return (step!.Id, schema!.Id);
    }

    private static AssignmentCreateDto NewValidCreateDto(Guid stepId, Guid schemaId, string suffix = "") => new(
        Name: $"my-assignment{suffix}",
        Version: "1.0.0",
        Description: "Integration test assignment",
        StepId: stepId,
        SchemaId: schemaId,
        Payload: "{\"key\":\"value\"}");

    [Fact]
    public async Task List_ReturnsEmptyArray_OnEmptyDb()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/assignments", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Equal("[]", body);
    }

    [Fact]
    public async Task Create_Returns201_AndLocationHeader_WhenValid()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var (stepId, schemaId) = await CreatePrereqAsync(client, ct);

        var resp = await client.PostAsJsonAsync("/api/v1/assignments",
            NewValidCreateDto(stepId, schemaId), ct);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.NotNull(resp.Headers.Location);
        Assert.Matches(@"^/api/v1/assignments/[a-f0-9\-]{36}$", resp.Headers.Location!.ToString());

        var read = await resp.Content.ReadFromJsonAsync<AssignmentReadDto>(cancellationToken: ct);
        Assert.NotNull(read);
        Assert.NotEqual(Guid.Empty, read!.Id);
        Assert.Equal(stepId, read.StepId);
        Assert.Equal(schemaId, read.SchemaId);
        Assert.Equal("{\"key\":\"value\"}", read.Payload);
        Assert.NotEqual(default, read.CreatedAt);            // HTTP-07: audit populated
        Assert.NotEqual(default, read.UpdatedAt);
    }

    [Fact]
    public async Task GetById_Returns200_WhenExisting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var (stepId, schemaId) = await CreatePrereqAsync(client, ct);

        var createResp = await client.PostAsJsonAsync("/api/v1/assignments",
            NewValidCreateDto(stepId, schemaId, "-getbyid"), ct);
        var created = await createResp.Content.ReadFromJsonAsync<AssignmentReadDto>(cancellationToken: ct);

        var resp = await client.GetAsync($"/api/v1/assignments/{created!.Id}", ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var read = await resp.Content.ReadFromJsonAsync<AssignmentReadDto>(cancellationToken: ct);
        Assert.Equal(created.Id, read!.Id);
        Assert.Equal(created.Payload, read.Payload);
    }

    [Fact]
    public async Task Update_Returns200_AndChangedFields_WhenExisting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var (stepId, schemaId) = await CreatePrereqAsync(client, ct);

        var createResp = await client.PostAsJsonAsync("/api/v1/assignments",
            NewValidCreateDto(stepId, schemaId, "-update"), ct);
        var created = await createResp.Content.ReadFromJsonAsync<AssignmentReadDto>(cancellationToken: ct);

        var update = new AssignmentUpdateDto(
            Name: created!.Name,
            Version: "1.0.1",
            Description: "Updated",
            StepId: stepId,
            SchemaId: schemaId,
            Payload: "{\"updated\":true}");
        var putResp = await client.PutAsJsonAsync($"/api/v1/assignments/{created.Id}", update, ct);
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        var getResp = await client.GetAsync($"/api/v1/assignments/{created.Id}", ct);
        var read = await getResp.Content.ReadFromJsonAsync<AssignmentReadDto>(cancellationToken: ct);
        Assert.Equal("1.0.1", read!.Version);
        Assert.Equal("{\"updated\":true}", read.Payload);
    }

    [Fact]
    public async Task Delete_Returns204_WhenExisting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var (stepId, schemaId) = await CreatePrereqAsync(client, ct);

        var createResp = await client.PostAsJsonAsync("/api/v1/assignments",
            NewValidCreateDto(stepId, schemaId, "-delete"), ct);
        var created = await createResp.Content.ReadFromJsonAsync<AssignmentReadDto>(cancellationToken: ct);

        var deleteResp = await client.DeleteAsync($"/api/v1/assignments/{created!.Id}", ct);
        Assert.Equal(HttpStatusCode.NoContent, deleteResp.StatusCode);

        var getResp = await client.GetAsync($"/api/v1/assignments/{created.Id}", ct);
        Assert.Equal(HttpStatusCode.NotFound, getResp.StatusCode);
    }
}
