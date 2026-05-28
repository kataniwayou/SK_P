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
/// Phase 9 REQ-4 integration tests for <c>POST /api/v1/orchestration/stop</c>.
/// Stop and Start are behaviorally identical in v1 (CONTEXT D-12 — both endpoints
/// delegate to the same <c>OrchestrationService.ValidateWorkflowIdsAsync</c>
/// method). Detailed validation coverage (duplicate / empty / Guid.Empty) is
/// asserted in <see cref="StartOrchestrationFacts"/>; here we only prove the
/// <c>/stop</c> URL is correctly wired to the same service method.
/// <para>
/// <b>2 facts:</b>
/// <list type="number">
///   <item><c>Stop_Returns204_AndEmptyBody_WhenWorkflowIdsValid</c> — happy path
///     mirror of Start.</item>
///   <item><c>Stop_Returns404_WhenAnyWorkflowIdMissing</c> — proves the existence
///     check applies to the /stop URL too (not just /start).</item>
/// </list>
/// </para>
/// </summary>
[Trait("Phase", "9")]
public sealed class StopOrchestrationFacts : IClassFixture<Phase8WebAppFactory>
{
    private readonly Phase8WebAppFactory _factory;

    public StopOrchestrationFacts(Phase8WebAppFactory factory) => _factory = factory;

    private static string RandomSha256Hex()
    {
        var bytes = Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray();
        return string.Concat(bytes.Select(b => b.ToString("x2")));
    }

    private static async Task<Guid> SeedWorkflowAsync(HttpClient client, CancellationToken ct)
    {
        var procDto = new ProcessorCreateDto(
            Name: $"orch-stop-proc-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            SourceHash: RandomSha256Hex(),
            InputSchemaId: null,
            OutputSchemaId: null);
        var procResp = await client.PostAsJsonAsync("/api/v1/processors", procDto, ct);
        procResp.EnsureSuccessStatusCode();
        var proc = await procResp.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);

        var stepDto = new StepCreateDto(
            Name: $"orch-stop-step-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            ProcessorId: proc!.Id,
            NextStepIds: null,
            EntryCondition: StepEntryCondition.PreviousCompleted);
        var stepResp = await client.PostAsJsonAsync("/api/v1/steps", stepDto, ct);
        stepResp.EnsureSuccessStatusCode();
        var step = await stepResp.Content.ReadFromJsonAsync<StepReadDto>(cancellationToken: ct);

        var wfDto = new WorkflowCreateDto(
            Name: $"orch-stop-wf-{Guid.NewGuid():N}",
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
    public async Task Stop_Returns204_AndEmptyBody_WhenWorkflowIdsValid()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var wfId = await SeedWorkflowAsync(client, ct);

        var resp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/stop",
            new List<Guid> { wfId },
            ct);

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.Equal(string.Empty, body);
    }

    [Fact]
    public async Task Stop_Returns404_WhenAnyWorkflowIdMissing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var missingId = Guid.NewGuid();
        var resp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/stop",
            new List<Guid> { missingId },
            ct);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("WorkflowEntity", doc.RootElement.GetProperty("resourceType").GetString());
        var resourceId = doc.RootElement.GetProperty("resourceId").GetString();
        Assert.Contains(missingId.ToString(), resourceId);
    }
}
