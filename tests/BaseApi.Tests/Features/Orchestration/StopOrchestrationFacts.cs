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
/// <b>v3.4.0 semantics change (Phase 24 Plan 02, WEBAPI-SUPPRESS-01):</b> Stop is now
/// delete-if-present (first-win symmetric). Per workflow the root is <c>KeyDeleteAsync</c>-deleted:
/// a PRESENT root is deleted (+ its per-step keys; never processor keys) → 204; an ABSENT root is a
/// tolerant NO-OP (NOT 422 — this DELIBERATELY supersedes the Phase 15 422-on-missing-root EXISTS
/// gate). A second Stop of an already-cleaned workflow is therefore a clean 204 no-op (idempotent).
/// </para>
/// </summary>
[Trait("Phase", "15")]
[Collection("ParentIndex")]
public sealed class StopOrchestrationFacts : IClassFixture<HarnessWebAppFactory>
{
    private readonly HarnessWebAppFactory _factory;

    public StopOrchestrationFacts(HarnessWebAppFactory factory) => _factory = factory;

    private async Task<Guid> SeedWorkflowAsync(HttpClient client, CancellationToken ct)
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
        // PROC-LIVE-01: seed the processor live so the subsequent Start (Stop gates on a Started wf)
        // passes the liveness gate; the writer no longer creates this key (PROC-NOCREATE-01).
        await _factory.SeedLiveProcessorAsync(proc!.Id, ct);

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
    public async Task Stop_Returns204_AndEmptyBody_WhenRootPresent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var wfId = await SeedWorkflowAsync(client, ct);

        // Start writes the root key; the present-root Stop deletes it → 204.
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

    /// <summary>
    /// WEBAPI-SUPPRESS-01 — a Stop of a never-Started (absent-root) workflow is a tolerant NO-OP:
    /// 204, NOT 422. This supersedes the Phase 15 422-on-missing-root EXISTS gate.
    /// </summary>
    [Fact]
    public async Task Stop_Returns204_NoOp_WhenWorkflowRootAbsent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // A well-formed but never-Started id has no L2 root key → delete-if-present no-op → 204.
        var absentId = Guid.NewGuid();
        var resp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/stop",
            new List<Guid> { absentId },
            ct);

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(ct);
        Assert.Equal(string.Empty, body);
    }

    /// <summary>
    /// WEBAPI-SUPPRESS-01 — a repeated Stop of an already-cleaned workflow is an idempotent 204
    /// no-op (the second Stop's root is absent → deletes nothing → 204, NOT 422).
    /// </summary>
    [Fact]
    public async Task Stop_Repeated_Is_Idempotent_204_NoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var wfId = await SeedWorkflowAsync(client, ct);

        var start = await client.PostAsJsonAsync("/api/v1/orchestration/start", new List<Guid> { wfId }, ct);
        Assert.Equal(HttpStatusCode.NoContent, start.StatusCode);

        var first = await client.PostAsJsonAsync("/api/v1/orchestration/stop", new List<Guid> { wfId }, ct);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        // Second Stop — root already deleted → first-win no-op (idempotent 204, NOT 422).
        var second = await client.PostAsJsonAsync("/api/v1/orchestration/stop", new List<Guid> { wfId }, ct);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        await _factory.SremParentIndexAsync(wfId);
    }
}
