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
/// Phase 14 SC#1 (L1-VALIDATE-03) integration tests for the cycle gate on
/// <c>POST /api/v1/orchestration/start</c>. Drives the FULL HTTP pipeline through the
/// real <c>CycleDetector</c> seam over a real Postgres-backed L1 snapshot.
/// <para>
/// Two facts:
/// <list type="number">
///   <item><c>Cycle_Returns422_WithStepChain</c> — a true cycle A→B→C→A returns 422 +
///     <c>application/problem+json</c> with <c>errors.gate == "cycle"</c> and a non-empty
///     <c>errors.offending.stepChain</c> containing the repeated cycle node.</item>
///   <item><c>DiamondDag_Passes_NoFalsePositiveCycle</c> — a diamond DAG (A→B, A→C, B→D,
///     C→D; D reached via two acyclic paths) returns 204 (D-14 — a diamond is NOT a cycle;
///     guards the single-set false-positive Pitfall 2).</item>
/// </list>
/// </para>
/// </summary>
[Trait("Phase", "14")]
[Collection("ParentIndex")]
public sealed class CycleDetectionFacts : IClassFixture<HarnessWebAppFactory>
{
    private readonly HarnessWebAppFactory _factory;

    public CycleDetectionFacts(HarnessWebAppFactory factory) => _factory = factory;

    /// <summary>
    /// Seeds a Processor (all schema FKs null so the schema-edge gate is a no-pass-through —
    /// this test isolates the cycle gate). Returns the Processor id.
    /// </summary>
    private static async Task<Guid> SeedProcessorAsync(HttpClient client, CancellationToken ct)
    {
        var procDto = new ProcessorCreateDto(
            Name: $"cyc-proc-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            SourceHash: HashHelpers.RandomSha256Hex(),
            InputSchemaId: null,
            OutputSchemaId: null,
            ConfigSchemaId: null);
        var procResp = await client.PostAsJsonAsync("/api/v1/processors", procDto, ct);
        procResp.EnsureSuccessStatusCode();
        var proc = await procResp.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);
        return proc!.Id;
    }

    /// <summary>POSTs a Step with the supplied (possibly null) NextStepIds. Returns the new Step id.</summary>
    private static async Task<Guid> CreateStepAsync(
        HttpClient client, Guid processorId, List<Guid>? nextStepIds, CancellationToken ct)
    {
        var stepDto = new StepCreateDto(
            Name: $"cyc-step-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            ProcessorId: processorId,
            NextStepIds: nextStepIds,
            EntryCondition: StepEntryCondition.PreviousCompleted);
        var stepResp = await client.PostAsJsonAsync("/api/v1/steps", stepDto, ct);
        stepResp.EnsureSuccessStatusCode();
        var step = await stepResp.Content.ReadFromJsonAsync<StepReadDto>(cancellationToken: ct);
        return step!.Id;
    }

    /// <summary>PUTs an existing Step to (re)wire its NextStepIds — used to add a back-edge after both ends exist.</summary>
    private static async Task SetNextStepIdsAsync(
        HttpClient client, Guid stepId, Guid processorId, List<Guid> nextStepIds, CancellationToken ct)
    {
        var updateDto = new StepUpdateDto(
            Name: $"cyc-step-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            ProcessorId: processorId,
            NextStepIds: nextStepIds,
            EntryCondition: StepEntryCondition.PreviousCompleted);
        var resp = await client.PutAsJsonAsync($"/api/v1/steps/{stepId}", updateDto, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>POSTs a Workflow with the supplied entry steps. Returns the new Workflow id.</summary>
    private static async Task<Guid> CreateWorkflowAsync(
        HttpClient client, List<Guid> entryStepIds, CancellationToken ct)
    {
        var wfDto = new WorkflowCreateDto(
            Name: $"cyc-wf-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            EntryStepIds: entryStepIds,
            AssignmentIds: null,
            CronExpression: null);
        var wfResp = await client.PostAsJsonAsync("/api/v1/workflows", wfDto, ct);
        wfResp.EnsureSuccessStatusCode();
        var wf = await wfResp.Content.ReadFromJsonAsync<WorkflowReadDto>(cancellationToken: ct);
        return wf!.Id;
    }

    [Fact]
    public async Task Cycle_Returns422_WithStepChain()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var procId = await SeedProcessorAsync(client, ct);

        // Build A→B→C with forward edges (each child must exist before it can be referenced).
        var stepC = await CreateStepAsync(client, procId, nextStepIds: null, ct);
        var stepB = await CreateStepAsync(client, procId, nextStepIds: new List<Guid> { stepC }, ct);
        var stepA = await CreateStepAsync(client, procId, nextStepIds: new List<Guid> { stepB }, ct);

        // Add the back-edge C→A (the cycle) via PUT now that A exists.
        await SetNextStepIdsAsync(client, stepC, procId, new List<Guid> { stepA }, ct);

        var wfId = await CreateWorkflowAsync(client, new List<Guid> { stepA }, ct);

        var resp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start",
            new List<Guid> { wfId },
            ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(422, doc.RootElement.GetProperty("status").GetInt32());

        var errors = doc.RootElement.GetProperty("errors");
        Assert.Equal("cycle", errors.GetProperty("gate").GetString());

        var stepChain = errors.GetProperty("offending").GetProperty("stepChain");
        Assert.Equal(JsonValueKind.Array, stepChain.ValueKind);
        var chainIds = stepChain.EnumerateArray().Select(e => Guid.Parse(e.GetString()!)).ToList();
        Assert.NotEmpty(chainIds);
        // The repeated cycle node bounds the chain — it must appear (start AND end close the loop).
        Assert.Contains(stepA, chainIds);
    }

    [Fact]
    public async Task DiamondDag_Passes_NoFalsePositiveCycle()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var procId = await SeedProcessorAsync(client, ct);

        // Diamond: A→B, A→C, B→D, C→D. D reached via two acyclic paths (NOT a cycle).
        var stepD = await CreateStepAsync(client, procId, nextStepIds: null, ct);
        var stepB = await CreateStepAsync(client, procId, nextStepIds: new List<Guid> { stepD }, ct);
        var stepC = await CreateStepAsync(client, procId, nextStepIds: new List<Guid> { stepD }, ct);
        var stepA = await CreateStepAsync(client, procId, nextStepIds: new List<Guid> { stepB, stepC }, ct);

        var wfId = await CreateWorkflowAsync(client, new List<Guid> { stepA }, ct);

        // PROC-LIVE-01: all four steps share one processor — seed it live so the liveness gate
        // (post-cycle, pre-Upsert) accepts this acyclic 204 path. Track root + step keys + SREM the
        // parent index so the close-gate scan SHA holds.
        await _factory.SeedLiveProcessorAsync(procId, ct);
        var prefix = _factory.RedisKeyPrefix;
        _factory.TrackRedisKey($"{prefix}{wfId}");
        foreach (var s in new[] { stepA, stepB, stepC, stepD })
        {
            _factory.TrackRedisKey($"{prefix}{wfId}:{s}");
        }

        try
        {
            var resp = await client.PostAsJsonAsync(
                "/api/v1/orchestration/start",
                new List<Guid> { wfId },
                ct);

            // D-14: a fan-in/diamond DAG is acyclic — the two-set algorithm must NOT flag it.
            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        }
        finally
        {
            await _factory.SremParentIndexAsync(wfId);
        }
    }
}
