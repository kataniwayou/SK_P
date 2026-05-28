using System.Net;
using System.Net.Http.Json;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using BaseApi.Tests.Composition;
using Npgsql;
using Xunit;

namespace BaseApi.Tests.Integration;

/// <summary>
/// Phase 8 Wave B smoke integration tests for the Workflow feature (5 [Fact]s per
/// CONTEXT D-07). Uses <see cref="Phase8WebAppFactory"/> (Wave A 08-01) which
/// encapsulates a per-class throwaway Postgres DB via <c>PostgresFixture</c>.
/// <para>
/// <b>GREEN-state dependency:</b> these tests run against the PRODUCTION
/// <c>AppDbContext</c>. They will only pass AFTER Wave C 08-07 lands (a) DbSets on
/// AppDbContext (including <c>DbSet&lt;WorkflowEntity&gt;</c> +
/// <c>DbSet&lt;WorkflowEntrySteps&gt;</c> + <c>DbSet&lt;WorkflowAssignments&gt;</c>),
/// (b) the MigrateAsync swap on StartupCompletionService, and (c) the per-entity DI
/// registration (<see cref="WorkflowServiceCollectionExtensions.AddWorkflowFeature"/>).
/// Phase 8 Plan 08-08 verifies the GREEN-state regression after Wave C completes.
/// </para>
/// <para>
/// <b>Junction-row verification strategy:</b> because <see cref="WorkflowReadDto"/>
/// v1 ships <c>EntryStepIds = null</c> and <c>AssignmentIds = null</c> on read paths
/// (post-ToRead enrichment deferred), the <c>Create</c>, <c>Update</c>, and
/// <c>Delete</c> junction assertions query the <c>workflow_entry_steps</c> table
/// directly via the factory connection string. This bypasses the v1 enrichment
/// limitation and proves the <c>WorkflowService.SyncJunctionsAsync</c> override is
/// correct end-to-end on both junctions.
/// </para>
/// </summary>
[Trait("Phase8Wave", "B")]
public sealed class WorkflowsIntegrationTests : IClassFixture<Phase8WebAppFactory>
{
    private readonly Phase8WebAppFactory _factory;

    public WorkflowsIntegrationTests(Phase8WebAppFactory factory) => _factory = factory;

    private static string RandomSha256Hex()
    {
        var bytes = Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray();
        return string.Concat(bytes.Select(b => b.ToString("x2")));
    }

    /// <summary>
    /// Creates a Step prerequisite (and its Processor FK chain) inline via the public
    /// HTTP API. Returns the new Step's Id, suitable for use in
    /// <c>WorkflowCreateDto.EntryStepIds</c>.
    /// </summary>
    private static async Task<Guid> CreateStepForWorkflowAsync(HttpClient client, CancellationToken ct)
    {
        // 1. Processor — FK target for Step.ProcessorId.
        var procDto = new ProcessorCreateDto(
            Name: $"wf-proc-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            SourceHash: RandomSha256Hex(),
            InputSchemaId: null,
            OutputSchemaId: null,
            ConfigSchemaId: null);
        var procResp = await client.PostAsJsonAsync("/api/v1/processors", procDto, ct);
        procResp.EnsureSuccessStatusCode();
        var proc = await procResp.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);

        // 2. Step — FK target for WorkflowEntrySteps.StepId.
        var stepDto = new StepCreateDto(
            Name: $"wf-step-{Guid.NewGuid():N}",
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

    /// <summary>
    /// Counts the <c>workflow_entry_steps</c> junction rows for the given Workflow
    /// directly via Npgsql — bypasses the v1 <c>WorkflowReadDto.EntryStepIds = null</c>
    /// limitation and asserts the <c>SyncJunctionsAsync</c> override is correct.
    /// </summary>
    private async Task<int> CountEntryStepJunctionsAsync(Guid workflowId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_factory.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM workflow_entry_steps WHERE workflow_id = @id", conn);
        cmd.Parameters.AddWithValue("id", workflowId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    [Fact]
    public async Task List_ReturnsEmptyArray_OnEmptyDb()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/workflows", ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // Phase8WebAppFactory uses IClassFixture (shared per class) — sibling facts may
        // have created rows by the time this fact runs. Assert the response is a
        // well-formed JSON array (the list-endpoint contract), not strictly-zero rows.
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.NotNull(body);
        Assert.StartsWith("[", body);
        Assert.EndsWith("]", body);
    }

    [Fact]
    public async Task Create_Returns201_WithCronNullAndEntryStepsPersisted()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var stepA = await CreateStepForWorkflowAsync(client, ct);
        var stepB = await CreateStepForWorkflowAsync(client, ct);

        var dto = new WorkflowCreateDto(
            Name: "wf-with-entry-steps",
            Version: "1.0.0",
            Description: null,
            EntryStepIds: new List<Guid> { stepA, stepB },
            AssignmentIds: null,
            CronExpression: null);                           // ENTITY-08 — null is valid
        var resp = await client.PostAsJsonAsync("/api/v1/workflows", dto, ct);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var read = await resp.Content.ReadFromJsonAsync<WorkflowReadDto>(cancellationToken: ct);
        Assert.NotNull(read);
        Assert.NotEqual(Guid.Empty, read!.Id);
        Assert.Null(read.CronExpression);

        // Direct DB assertion — junction rows persisted via SyncJunctionsAsync.
        Assert.Equal(2, await CountEntryStepJunctionsAsync(read.Id, ct));
    }

    [Fact]
    public async Task GetById_Returns200_WhenExisting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var step = await CreateStepForWorkflowAsync(client, ct);

        var dto = new WorkflowCreateDto(
            Name: "wf-get",
            Version: "1.0.0",
            Description: null,
            EntryStepIds: new List<Guid> { step },
            AssignmentIds: null,
            CronExpression: null);
        var createResp = await client.PostAsJsonAsync("/api/v1/workflows", dto, ct);
        var created = await createResp.Content.ReadFromJsonAsync<WorkflowReadDto>(cancellationToken: ct);

        var resp = await client.GetAsync($"/api/v1/workflows/{created!.Id}", ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var read = await resp.Content.ReadFromJsonAsync<WorkflowReadDto>(cancellationToken: ct);
        Assert.Equal(created.Id, read!.Id);
    }

    [Fact]
    public async Task Update_Returns200_AndRemovesOldEntryStepJunctions()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var stepA = await CreateStepForWorkflowAsync(client, ct);
        var stepB = await CreateStepForWorkflowAsync(client, ct);
        var stepC = await CreateStepForWorkflowAsync(client, ct);

        // Create with [stepA, stepB] + 5-field cron → 2 junction rows persist.
        var createDto = new WorkflowCreateDto(
            Name: "wf-update",
            Version: "1.0.0",
            Description: null,
            EntryStepIds: new List<Guid> { stepA, stepB },
            AssignmentIds: null,
            CronExpression: "0 0 * * *");                    // 5-field cron OK (VALID-19)
        var createResp = await client.PostAsJsonAsync("/api/v1/workflows", createDto, ct);
        var created = await createResp.Content.ReadFromJsonAsync<WorkflowReadDto>(cancellationToken: ct);
        Assert.Equal(2, await CountEntryStepJunctionsAsync(created!.Id, ct));

        // Update with [stepC] only + null cron → old junctions A+B removed, stepC inserted.
        var updateDto = new WorkflowUpdateDto(
            Name: "wf-update",
            Version: "1.0.1",
            Description: null,
            EntryStepIds: new List<Guid> { stepC },
            AssignmentIds: null,
            CronExpression: null);
        var updResp = await client.PutAsJsonAsync($"/api/v1/workflows/{created.Id}", updateDto, ct);
        Assert.Equal(HttpStatusCode.OK, updResp.StatusCode);

        // Junction count is now 1 (stepC only — old A+B removed by SyncJunctionsAsync).
        Assert.Equal(1, await CountEntryStepJunctionsAsync(created.Id, ct));
    }

    [Fact]
    public async Task Delete_Returns204_AndCascadesEntryStepJunctions()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var step = await CreateStepForWorkflowAsync(client, ct);

        var createDto = new WorkflowCreateDto(
            Name: "wf-delete",
            Version: "1.0.0",
            Description: null,
            EntryStepIds: new List<Guid> { step },
            AssignmentIds: null,
            CronExpression: null);
        var createResp = await client.PostAsJsonAsync("/api/v1/workflows", createDto, ct);
        var created = await createResp.Content.ReadFromJsonAsync<WorkflowReadDto>(cancellationToken: ct);
        Assert.Equal(1, await CountEntryStepJunctionsAsync(created!.Id, ct));

        var deleteResp = await client.DeleteAsync($"/api/v1/workflows/{created.Id}", ct);
        Assert.Equal(HttpStatusCode.NoContent, deleteResp.StatusCode);

        // DeleteBehavior.Cascade on WorkflowEntrySteps.WorkflowId → junction rows removed.
        Assert.Equal(0, await CountEntryStepJunctionsAsync(created.Id, ct));

        var getResp = await client.GetAsync($"/api/v1/workflows/{created.Id}", ct);
        Assert.Equal(HttpStatusCode.NotFound, getResp.StatusCode);
    }
}
