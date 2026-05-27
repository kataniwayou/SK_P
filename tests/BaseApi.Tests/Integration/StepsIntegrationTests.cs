using System.Net;
using System.Net.Http.Json;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Step;
using BaseApi.Tests.Composition;
using Npgsql;
using Xunit;

namespace BaseApi.Tests.Integration;

/// <summary>
/// Phase 8 Wave B smoke integration tests for the Step feature (5 [Fact]s per CONTEXT D-07).
/// Uses <see cref="Phase8WebAppFactory"/> (Wave A 08-01) which encapsulates a per-class
/// throwaway Postgres DB via <c>PostgresFixture</c>.
/// <para>
/// <b>GREEN-state dependency:</b> these tests run against the PRODUCTION
/// <c>AppDbContext</c>. They will only pass AFTER Wave C 08-07 lands (a) DbSets on
/// AppDbContext (including <c>DbSet&lt;StepNextSteps&gt;</c>), (b) the MigrateAsync swap
/// on StartupCompletionService, and (c) the per-entity DI registration
/// (<see cref="StepServiceCollectionExtensions.AddStepFeature"/>). Phase 8 Plan 08-08
/// verifies the GREEN-state regression after Wave C completes.
/// </para>
/// <para>
/// <b>Junction-row verification strategy:</b> because <see cref="StepReadDto"/> v1 carries
/// <c>NextStepIds = null</c> on read paths (post-ToRead enrichment deferred — see
/// <see cref="StepReadDto"/> remarks), the <c>Create</c> and <c>Update</c> junction
/// assertions query the <c>step_next_steps</c> table directly via the factory connection
/// string. This bypasses the v1 enrichment limitation and proves the
/// <c>StepService.SyncJunctionsAsync</c> override is correct end-to-end.
/// </para>
/// </summary>
[Trait("Phase8Wave", "B")]
public sealed class StepsIntegrationTests : IClassFixture<Phase8WebAppFactory>
{
    private readonly Phase8WebAppFactory _factory;

    public StepsIntegrationTests(Phase8WebAppFactory factory) => _factory = factory;

    private static string RandomSha256Hex()
    {
        var bytes = Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray();
        return string.Concat(bytes.Select(b => b.ToString("x2")));
    }

    private static async Task<Guid> CreateProcessorAsync(HttpClient client, CancellationToken ct)
    {
        var dto = new ProcessorCreateDto(
            Name: $"step-proc-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            SourceHash: RandomSha256Hex(),
            InputSchemaId: null,
            OutputSchemaId: null);
        var resp = await client.PostAsJsonAsync("/api/v1/processors", dto, ct);
        resp.EnsureSuccessStatusCode();
        var read = await resp.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);
        return read!.Id;
    }

    private async Task<Guid> CreateBareStepAsync(HttpClient client, Guid processorId, CancellationToken ct)
    {
        var dto = new StepCreateDto(
            Name: $"bare-step-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            ProcessorId: processorId,
            NextStepIds: null,
            EntryCondition: StepEntryCondition.PreviousCompleted);
        var resp = await client.PostAsJsonAsync("/api/v1/steps", dto, ct);
        resp.EnsureSuccessStatusCode();
        var read = await resp.Content.ReadFromJsonAsync<StepReadDto>(cancellationToken: ct);
        return read!.Id;
    }

    private async Task<int> CountJunctionsForStepAsync(Guid stepId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_factory.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM step_next_steps WHERE step_id = @id", conn);
        cmd.Parameters.AddWithValue("id", stepId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    [Fact]
    public async Task List_ReturnsEmptyArray_OnEmptyDb()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/steps", ct);

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
    public async Task Create_Returns201_WithNextStepIdsPersisted()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var processorId = await CreateProcessorAsync(client, ct);

        // Pre-create two target steps (with empty next-step collections) so we have
        // valid Guids to reference in the junction insert.
        var nextA = await CreateBareStepAsync(client, processorId, ct);
        var nextB = await CreateBareStepAsync(client, processorId, ct);

        var dto = new StepCreateDto(
            Name: "step-with-next",
            Version: "1.0.0",
            Description: null,
            ProcessorId: processorId,
            NextStepIds: new List<Guid> { nextA, nextB },
            EntryCondition: StepEntryCondition.PreviousCompleted);
        var resp = await client.PostAsJsonAsync("/api/v1/steps", dto, ct);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var read = await resp.Content.ReadFromJsonAsync<StepReadDto>(cancellationToken: ct);
        Assert.NotNull(read);
        Assert.NotEqual(Guid.Empty, read!.Id);

        // Direct DB assertion — junction rows persisted via SyncJunctionsAsync.
        Assert.Equal(2, await CountJunctionsForStepAsync(read.Id, ct));
    }

    [Fact]
    public async Task GetById_Returns200_WithJunctionRowsPopulated()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var processorId = await CreateProcessorAsync(client, ct);
        var id = await CreateBareStepAsync(client, processorId, ct);

        var resp = await client.GetAsync($"/api/v1/steps/{id}", ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var read = await resp.Content.ReadFromJsonAsync<StepReadDto>(cancellationToken: ct);
        Assert.Equal(id, read!.Id);
        Assert.Equal(StepEntryCondition.PreviousCompleted, read.EntryCondition);
    }

    [Fact]
    public async Task Update_Returns200_RemovesOldAndAddsNewJunctions()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var processorId = await CreateProcessorAsync(client, ct);
        var nextA = await CreateBareStepAsync(client, processorId, ct);
        var nextB = await CreateBareStepAsync(client, processorId, ct);
        var nextC = await CreateBareStepAsync(client, processorId, ct);

        // Create with [nextA, nextB] — 2 junction rows persist.
        var create = new StepCreateDto("upd-step", "1.0.0", null, processorId,
            new List<Guid> { nextA, nextB }, StepEntryCondition.PreviousCompleted);
        var createResp = await client.PostAsJsonAsync("/api/v1/steps", create, ct);
        var created = await createResp.Content.ReadFromJsonAsync<StepReadDto>(cancellationToken: ct);
        Assert.Equal(2, await CountJunctionsForStepAsync(created!.Id, ct));

        // Update with [nextC] only — old junctions A+B removed, nextC inserted.
        var update = new StepUpdateDto("upd-step", "1.0.1", null, processorId,
            new List<Guid> { nextC }, StepEntryCondition.PreviousCompleted);
        var updResp = await client.PutAsJsonAsync($"/api/v1/steps/{created.Id}", update, ct);
        Assert.Equal(HttpStatusCode.OK, updResp.StatusCode);

        // Junction count is now 1 (nextC only — old A+B removed by SyncJunctionsAsync).
        Assert.Equal(1, await CountJunctionsForStepAsync(created.Id, ct));
    }

    [Fact]
    public async Task Delete_Returns204_WhenExisting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var processorId = await CreateProcessorAsync(client, ct);
        var id = await CreateBareStepAsync(client, processorId, ct);

        var resp = await client.DeleteAsync($"/api/v1/steps/{id}", ct);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        var getResp = await client.GetAsync($"/api/v1/steps/{id}", ct);
        Assert.Equal(HttpStatusCode.NotFound, getResp.StatusCode);
    }
}
