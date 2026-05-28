using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using BaseApi.Tests.Composition;
using Xunit;

namespace BaseApi.Tests.Features.Orchestration;

/// <summary>
/// Phase 9 REQ-3 + REQ-5 + REQ-6 integration tests for
/// <c>POST /api/v1/orchestration/start</c>. Uses
/// <see cref="Phase8WebAppFactory"/> per CONTEXT D-20.
/// <para>
/// <b>5 facts (REQ-3 happy + REQ-5 validation x 3 + REQ-6 existence):</b>
/// <list type="number">
///   <item><c>Start_Returns204_AndEmptyBody_WhenWorkflowIdsValid</c> — seed a
///     Workflow, POST <c>[wf.Id]</c>, assert 204 + Content-Length 0.</item>
///   <item><c>Start_Returns400_WhenWorkflowIdsContainDuplicate</c> — POST
///     <c>[wf.Id, wf.Id]</c>, assert 400 ValidationProblemDetails.</item>
///   <item><c>Start_Returns400_WhenWorkflowIdsEmpty</c> — POST <c>[]</c>, assert
///     400 ValidationProblemDetails.</item>
///   <item><c>Start_Returns400_WhenWorkflowIdsContainsGuidEmpty</c> — POST
///     <c>[wf.Id, Guid.Empty]</c>, assert 400 ValidationProblemDetails.</item>
///   <item><c>Start_Returns404_WhenAnyWorkflowIdMissing</c> — POST
///     <c>[Guid.NewGuid()]</c>, assert 404 ProblemDetails with
///     <c>resourceType="WorkflowEntity"</c> and missing-id in <c>resourceId</c>.</item>
/// </list>
/// </para>
/// </summary>
[Trait("Phase", "9")]
public sealed class StartOrchestrationFacts : IClassFixture<Phase8WebAppFactory>
{
    private readonly Phase8WebAppFactory _factory;

    public StartOrchestrationFacts(Phase8WebAppFactory factory) => _factory = factory;

    // Copied verbatim from tests/BaseApi.Tests/Integration/WorkflowsIntegrationTests.cs.
    private static string RandomSha256Hex()
    {
        var bytes = Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray();
        return string.Concat(bytes.Select(b => b.ToString("x2")));
    }

    /// <summary>
    /// Seeds a Workflow via the public HTTP API (Processor → Step → Workflow chain).
    /// Returns the new Workflow's Id. Mirrors
    /// <c>WorkflowsIntegrationTests.CreateStepForWorkflowAsync</c> lines 53-79 +
    /// adds a Workflow POST at the end.
    /// </summary>
    private static async Task<Guid> SeedWorkflowAsync(HttpClient client, CancellationToken ct)
    {
        // 1. Processor — FK target for Step.ProcessorId.
        var procDto = new ProcessorCreateDto(
            Name: $"orch-proc-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            SourceHash: RandomSha256Hex(),
            InputSchemaId: null,
            OutputSchemaId: null);
        var procResp = await client.PostAsJsonAsync("/api/v1/processors", procDto, ct);
        procResp.EnsureSuccessStatusCode();
        var proc = await procResp.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);

        // 2. Step — FK target for WorkflowEntrySteps.StepId.
        var stepDto = new StepCreateDto(
            Name: $"orch-step-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            ProcessorId: proc!.Id,
            NextStepIds: null,
            EntryCondition: StepEntryCondition.PreviousCompleted);
        var stepResp = await client.PostAsJsonAsync("/api/v1/steps", stepDto, ct);
        stepResp.EnsureSuccessStatusCode();
        var step = await stepResp.Content.ReadFromJsonAsync<StepReadDto>(cancellationToken: ct);

        // 3. Workflow — the entity OrchestrationService validates by id.
        var wfDto = new WorkflowCreateDto(
            Name: $"orch-wf-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            EntryStepIds: new List<Guid> { step!.Id },
            AssignmentIds: null,
            CronExpression: null);
        var wfResp = await client.PostAsJsonAsync("/api/v1/workflows", wfDto, ct);
        wfResp.EnsureSuccessStatusCode();
        var wf = await wfResp.Content.ReadFromJsonAsync<WorkflowReadDto>(cancellationToken: ct);
        return wf!.Id;
    }

    [Fact]
    public async Task Start_Returns204_AndEmptyBody_WhenWorkflowIdsValid()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var wfId = await SeedWorkflowAsync(client, ct);

        var resp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start",
            new List<Guid> { wfId },
            ct);

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        // 204 carries no body — Content-Length is either 0 or absent.
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.Equal(string.Empty, body);
    }

    [Fact]
    public async Task Start_Returns400_WhenWorkflowIdsContainDuplicate()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var wfId = await SeedWorkflowAsync(client, ct);

        var resp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start",
            new List<Guid> { wfId, wfId },
            ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Start_Returns400_WhenWorkflowIdsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start",
            new List<Guid>(),
            ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Start_Returns400_WhenWorkflowIdsContainsGuidEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var wfId = await SeedWorkflowAsync(client, ct);

        var resp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start",
            new List<Guid> { wfId, Guid.Empty },
            ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Start_Returns404_WhenAnyWorkflowIdMissing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // No seeding — every id below is guaranteed unseeded.
        var missingId = Guid.NewGuid();
        var resp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start",
            new List<Guid> { missingId },
            ct);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(404, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("WorkflowEntity", doc.RootElement.GetProperty("resourceType").GetString());
        // resourceId is the comma-joined missing-id string (a single id here).
        var resourceId = doc.RootElement.GetProperty("resourceId").GetString();
        Assert.Contains(missingId.ToString(), resourceId);
    }
}
