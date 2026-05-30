using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using BaseApi.Tests.Composition;
using BaseApi.Tests.TestHelpers;
using Xunit;

namespace BaseApi.Tests.Features.Orchestration;

/// <summary>
/// Phase 9 REQ-3 + REQ-5 + REQ-6 integration tests for
/// <c>POST /api/v1/orchestration/start</c>. Uses
/// <see cref="HarnessWebAppFactory"/> (D-01 in-memory bus swap) so the happy-path
/// publish completes in-process instead of hanging on the real broker; the validation
/// facts (400/404) short-circuit before publish either way.
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
public sealed class StartOrchestrationFacts : IClassFixture<HarnessWebAppFactory>
{
    private readonly HarnessWebAppFactory _factory;

    public StartOrchestrationFacts(HarnessWebAppFactory factory) => _factory = factory;

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
            SourceHash: HashHelpers.RandomSha256Hex(),
            InputSchemaId: null,
            OutputSchemaId: null,
            ConfigSchemaId: null);
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
        // IN-04 (iteration 2): for a single missing id the resourceId must equal the
        // bare GUID string — `string.Join(", ", missing)` over a one-element list
        // emits the GUID with no separator/wrapping. Strict equality locks in the
        // exact format and catches any future separator/wrapping regression.
        var resourceId = doc.RootElement.GetProperty("resourceId").GetString();
        Assert.Equal(missingId.ToString(), resourceId);
    }

    /// <summary>
    /// IN-04 (iteration 2): locks the multi-id <c>string.Join(", ", missing)</c>
    /// contract — every missing id appears in <c>resourceId</c> and they are
    /// comma-space delimited (not <c>;</c>, <c>\n</c>, <c>[...]</c>, etc.). The
    /// single-id fact above cannot detect a regression in the join formatting.
    /// </summary>
    [Fact]
    public async Task Start_Returns404_WithCommaJoinedIds_WhenMultipleWorkflowIdsMissing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var missingId1 = Guid.NewGuid();
        var missingId2 = Guid.NewGuid();
        var resp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start",
            new List<Guid> { missingId1, missingId2 },
            ct);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(404, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("WorkflowEntity", doc.RootElement.GetProperty("resourceType").GetString());
        var resourceId = doc.RootElement.GetProperty("resourceId").GetString();
        Assert.NotNull(resourceId);
        // Both ids must appear (order is whatever LINQ Except yields — currently
        // input order, but we don't depend on that here).
        Assert.Contains(missingId1.ToString(), resourceId);
        Assert.Contains(missingId2.ToString(), resourceId);
        // The exact ", " separator (comma + single space) is the locked contract
        // from OrchestrationService.ValidateWorkflowIdsAsync (`string.Join(", ", missing)`).
        Assert.Contains(", ", resourceId);
        // Resource id must be exactly "{id1}, {id2}" — no wrapping ([...]), no
        // alternate separators (;, \n, etc.). Try both possible orderings so we
        // do not over-couple to LINQ Except's incidental input-order behavior.
        var expectedInOrder = $"{missingId1}, {missingId2}";
        var expectedReversed = $"{missingId2}, {missingId1}";
        Assert.True(
            resourceId == expectedInOrder || resourceId == expectedReversed,
            $"resourceId '{resourceId}' must equal '{expectedInOrder}' or '{expectedReversed}' (comma-space joined, unwrapped).");
    }
}
