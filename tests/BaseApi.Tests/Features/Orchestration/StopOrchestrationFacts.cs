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
/// Stop endpoint contract for <c>POST /api/v1/orchestration/stop</c>.
/// <para>
/// <b>v3.3.0 semantics change (Phase 15 Plan 04):</b> Stop is no longer a Postgres
/// existence check (the original Phase 9 D-12 shared-method 404 behavior). It is now a
/// Redis (L2) EXISTS gate (D-04/D-06): all requested workflow root keys must already
/// exist in L2 (i.e. the workflow was Started) → per-workflow cleanup → 204; ANY missing
/// root → 422 listing the full missing set, NO deletion. These facts were updated from
/// the obsolete 404 assertions to the new gate semantics — the full Redis-gate coverage
/// (processor retention, repeat-422, Redis-down 500) lives in <see cref="StopGateFacts"/>.
/// </para>
/// </summary>
[Trait("Phase", "15")]
public sealed class StopOrchestrationFacts : IClassFixture<Phase8WebAppFactory>
{
    private readonly Phase8WebAppFactory _factory;

    public StopOrchestrationFacts(Phase8WebAppFactory factory) => _factory = factory;

    private static async Task<Guid> SeedWorkflowAsync(HttpClient client, CancellationToken ct)
    {
        var procDto = new ProcessorCreateDto(
            Name: $"orch-stop-proc-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            SourceHash: HashHelpers.RandomSha256Hex(),
            InputSchemaId: null,
            OutputSchemaId: null,
            ConfigSchemaId: null);
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
    public async Task Stop_Returns204_AndEmptyBody_WhenAllRootsExist()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var wfId = await SeedWorkflowAsync(client, ct);

        // v3.3.0 Stop gates on L2 existence — the workflow must be Started (root key written)
        // before a Stop can succeed. Start it first, then Stop → 204.
        var start = await client.PostAsJsonAsync("/api/v1/orchestration/start", new List<Guid> { wfId }, ct);
        Assert.Equal(HttpStatusCode.NoContent, start.StatusCode);

        var resp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/stop",
            new List<Guid> { wfId },
            ct);

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.Equal(string.Empty, body);
    }

    [Fact]
    public async Task Stop_Returns422_WhenAnyWorkflowRootMissing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // A well-formed but never-Started id has no L2 root key → the EXISTS gate fails → 422.
        var missingId = Guid.NewGuid();
        var resp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/stop",
            new List<Guid> { missingId },
            ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(422, doc.RootElement.GetProperty("status").GetInt32());
        // The 422 detail lists the missing id (MissingRoots gate, string.Join(", ", missing)).
        Assert.Contains(missingId.ToString(), body);
    }

    /// <summary>
    /// Multi-id gate — proves a 422 lists the FULL missing set (not just the first) when
    /// every requested workflow root is absent from L2.
    /// </summary>
    [Fact]
    public async Task Stop_Returns422_WithFullMissingList_WhenMultipleWorkflowRootsMissing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var missingId1 = Guid.NewGuid();
        var missingId2 = Guid.NewGuid();
        var resp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/stop",
            new List<Guid> { missingId1, missingId2 },
            ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(422, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Contains(missingId1.ToString(), body);
        Assert.Contains(missingId2.ToString(), body);
    }
}
