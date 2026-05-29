using BaseApi.Service.Features.Assignment;
using BaseApi.Service.Features.Orchestration.Loading;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Schema;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using BaseApi.Tests.Composition;
using BaseApi.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using Xunit;

namespace BaseApi.Tests.Features.Orchestration;

/// <summary>
/// Phase 13 SC3 + SC5 white-box facts for the L1 loader
/// (<see cref="IWorkflowGraphLoader"/>). Resolves the INTERNAL loader directly out
/// of a DI scope (reachable via <c>InternalsVisibleTo("BaseApi.Tests")</c> added in
/// Plan 13-01) and asserts the contents of the transient
/// <c>WorkflowGraphSnapshot</c> built by Plan 13-02's real
/// <c>LoadL1Async</c>. Uses the real Postgres + Redis
/// <see cref="Phase8WebAppFactory"/> fixture; graphs are seeded through the public
/// HTTP API so the junction rows exist exactly as production writes them.
/// <para>
/// <b>3 facts:</b>
/// <list type="number">
///   <item><c>LoadL1Async_PopulatesAllFiveDictionaries_ForMultiWorkflowGraph</c> —
///     SC3: seeds 2 workflows (one with a Config schema + an Assignment), asserts
///     all 5 snapshot dictionaries contain the expected ids and the enriched
///     <c>EntryStepIds</c>/<c>AssignmentIds</c>/<c>NextStepIds</c> are correct.</item>
///   <item><c>LoadL1Async_IncludesAllChildren_ForMultiChildFanOut</c> — SC5
///     fan-out: a parent step P with NextStepIds=[childA, childB]; asserts BOTH
///     children appear in the snapshot AND in <c>Steps[P].NextStepIds</c>.</item>
///   <item><c>LoadL1Async_Terminates_ForCyclicGraph</c> — SC5 termination: a cycle
///     A→B→A; asserts the load COMPLETES (Task.WhenAny timeout guard) and the
///     snapshot contains both steps (T-13-05 / T-13-10 DoS-mitigation verification).</item>
/// </list>
/// </para>
/// </summary>
[Trait("Phase", "13")]
public sealed class WorkflowGraphLoaderFacts : IClassFixture<Phase8WebAppFactory>
{
    private readonly Phase8WebAppFactory _factory;

    public WorkflowGraphLoaderFacts(Phase8WebAppFactory factory) => _factory = factory;

    // ---- HTTP seeding helpers (Processor → Step → Workflow chain, extended) ----

    private static async Task<Guid> SeedProcessorAsync(
        HttpClient client, CancellationToken ct, Guid? configSchemaId = null)
    {
        var dto = new ProcessorCreateDto(
            Name: $"glf-proc-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            SourceHash: HashHelpers.RandomSha256Hex(),
            InputSchemaId: null,
            OutputSchemaId: null,
            ConfigSchemaId: configSchemaId);
        var resp = await client.PostAsJsonAsync("/api/v1/processors", dto, ct);
        resp.EnsureSuccessStatusCode();
        var proc = await resp.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);
        return proc!.Id;
    }

    private static async Task<Guid> SeedSchemaAsync(HttpClient client, CancellationToken ct)
    {
        var dto = new SchemaCreateDto(
            Name: $"glf-schema-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            Definition: """{ "type": "object" }""");
        var resp = await client.PostAsJsonAsync("/api/v1/schemas", dto, ct);
        resp.EnsureSuccessStatusCode();
        var schema = await resp.Content.ReadFromJsonAsync<SchemaReadDto>(cancellationToken: ct);
        return schema!.Id;
    }

    private static async Task<Guid> SeedStepAsync(
        HttpClient client, CancellationToken ct, Guid processorId, List<Guid>? nextStepIds = null)
    {
        var dto = new StepCreateDto(
            Name: $"glf-step-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            ProcessorId: processorId,
            NextStepIds: nextStepIds,
            EntryCondition: StepEntryCondition.PreviousCompleted);
        var resp = await client.PostAsJsonAsync("/api/v1/steps", dto, ct);
        resp.EnsureSuccessStatusCode();
        var step = await resp.Content.ReadFromJsonAsync<StepReadDto>(cancellationToken: ct);
        return step!.Id;
    }

    private static async Task UpdateStepNextStepsAsync(
        HttpClient client, CancellationToken ct, Guid stepId, Guid processorId, List<Guid> nextStepIds)
    {
        // Closes a cycle through the public API so the StepNextSteps junction rows
        // exist exactly as production writes them. PUT /api/v1/steps/{id} (inherited
        // BaseController.Update) takes a StepUpdateDto with the same NextStepIds field.
        var dto = new StepUpdateDto(
            Name: $"glf-step-upd-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            ProcessorId: processorId,
            NextStepIds: nextStepIds,
            EntryCondition: StepEntryCondition.PreviousCompleted);
        var resp = await client.PutAsJsonAsync($"/api/v1/steps/{stepId}", dto, ct);
        resp.EnsureSuccessStatusCode();
    }

    private static async Task<Guid> SeedAssignmentAsync(
        HttpClient client, CancellationToken ct, Guid stepId)
    {
        var dto = new AssignmentCreateDto(
            Name: $"glf-asg-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            StepId: stepId,
            Payload: """{ "k": "v" }""");
        var resp = await client.PostAsJsonAsync("/api/v1/assignments", dto, ct);
        resp.EnsureSuccessStatusCode();
        var asg = await resp.Content.ReadFromJsonAsync<AssignmentReadDto>(cancellationToken: ct);
        return asg!.Id;
    }

    private static async Task<Guid> SeedWorkflowAsync(
        HttpClient client, CancellationToken ct, List<Guid> entryStepIds, List<Guid>? assignmentIds = null)
    {
        var dto = new WorkflowCreateDto(
            Name: $"glf-wf-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            EntryStepIds: entryStepIds,
            AssignmentIds: assignmentIds,
            CronExpression: null);
        var resp = await client.PostAsJsonAsync("/api/v1/workflows", dto, ct);
        resp.EnsureSuccessStatusCode();
        var wf = await resp.Content.ReadFromJsonAsync<WorkflowReadDto>(cancellationToken: ct);
        return wf!.Id;
    }

    // ----------------------------- SC3 -----------------------------

    [Fact]
    public async Task LoadL1Async_PopulatesAllFiveDictionaries_ForMultiWorkflowGraph()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // Workflow 1 — Processor (with Config schema) → Step → Assignment → Workflow.
        var configSchemaId = await SeedSchemaAsync(client, ct);
        var proc1 = await SeedProcessorAsync(client, ct, configSchemaId: configSchemaId);
        var step1 = await SeedStepAsync(client, ct, proc1);
        var assignmentId = await SeedAssignmentAsync(client, ct, step1);
        var wf1 = await SeedWorkflowAsync(
            client, ct, entryStepIds: new List<Guid> { step1 },
            assignmentIds: new List<Guid> { assignmentId });

        // Workflow 2 — a second independent Processor → Step → Workflow chain.
        var proc2 = await SeedProcessorAsync(client, ct);
        var step2 = await SeedStepAsync(client, ct, proc2);
        var wf2 = await SeedWorkflowAsync(client, ct, entryStepIds: new List<Guid> { step2 });

        // White-box: resolve the INTERNAL loader from a DI scope and build the snapshot.
        using var scope = _factory.Services.CreateScope();
        var loader = scope.ServiceProvider.GetRequiredService<IWorkflowGraphLoader>();
        using var snapshot = await loader.LoadL1Async(new[] { wf1, wf2 }, ct);

        // Workflows dictionary — both ids present.
        Assert.True(snapshot.Workflows.ContainsKey(wf1));
        Assert.True(snapshot.Workflows.ContainsKey(wf2));

        // Steps dictionary — both entry steps present.
        Assert.Contains(step1, snapshot.Steps.Keys);
        Assert.Contains(step2, snapshot.Steps.Keys);

        // Processors dictionary — both referenced processors present.
        Assert.True(snapshot.Processors.ContainsKey(proc1));
        Assert.True(snapshot.Processors.ContainsKey(proc2));

        // Schemas dictionary — the Config schema referenced by proc1 present.
        Assert.True(snapshot.Schemas.ContainsKey(configSchemaId));

        // Assignments dictionary — the assignment referenced by wf1 present.
        Assert.True(snapshot.Assignments.ContainsKey(assignmentId));

        // Enrichment ran: EntryStepIds + AssignmentIds rebuilt from the junctions.
        Assert.NotNull(snapshot.Workflows[wf1].EntryStepIds);
        Assert.Contains(step1, snapshot.Workflows[wf1].EntryStepIds!);
        Assert.NotNull(snapshot.Workflows[wf1].AssignmentIds);
        Assert.Contains(assignmentId, snapshot.Workflows[wf1].AssignmentIds!);
        Assert.NotNull(snapshot.Workflows[wf2].EntryStepIds);
        Assert.Contains(step2, snapshot.Workflows[wf2].EntryStepIds!);

        // Step NextStepIds enrichment is non-null (empty list, not null) for a leaf step.
        Assert.NotNull(snapshot.Steps[step1].NextStepIds);
    }

    // ----------------------------- SC5 fan-out -----------------------------

    [Fact]
    public async Task LoadL1Async_IncludesAllChildren_ForMultiChildFanOut()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // Children FIRST (NextStepIds reference must point at existing steps).
        var proc = await SeedProcessorAsync(client, ct);
        var childA = await SeedStepAsync(client, ct, proc);
        var childB = await SeedStepAsync(client, ct, proc);

        // Parent P fans out to BOTH children.
        var parentP = await SeedStepAsync(
            client, ct, proc, nextStepIds: new List<Guid> { childA, childB });

        var wfId = await SeedWorkflowAsync(client, ct, entryStepIds: new List<Guid> { parentP });

        using var scope = _factory.Services.CreateScope();
        var loader = scope.ServiceProvider.GetRequiredService<IWorkflowGraphLoader>();
        using var snapshot = await loader.LoadL1Async(new[] { wfId }, ct);

        // All three steps reachable.
        Assert.True(snapshot.Steps.ContainsKey(parentP));
        Assert.True(snapshot.Steps.ContainsKey(childA));
        Assert.True(snapshot.Steps.ContainsKey(childB));

        // Multi-child fan-out: BOTH children present in P's NextStepIds (not just first).
        var pNext = snapshot.Steps[parentP].NextStepIds;
        Assert.NotNull(pNext);
        Assert.Contains(childA, pNext!);
        Assert.Contains(childB, pNext!);
    }

    // ----------------------------- SC5 cycle termination -----------------------------

    [Fact]
    public async Task LoadL1Async_Terminates_ForCyclicGraph()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var proc = await SeedProcessorAsync(client, ct);

        // Create A first, then B with NextStepIds=[A], then PUT A with NextStepIds=[B]
        // to close the cycle A→B→A through the public API (junction rows exist).
        var stepA = await SeedStepAsync(client, ct, proc);
        var stepB = await SeedStepAsync(client, ct, proc, nextStepIds: new List<Guid> { stepA });
        await UpdateStepNextStepsAsync(client, ct, stepA, proc, nextStepIds: new List<Guid> { stepB });

        var wfId = await SeedWorkflowAsync(client, ct, entryStepIds: new List<Guid> { stepA });

        using var scope = _factory.Services.CreateScope();
        var loader = scope.ServiceProvider.GetRequiredService<IWorkflowGraphLoader>();

        // Completion guard: a hang fails the test instead of blocking the whole suite
        // (T-13-10 — this fact IS the DoS-mitigation verification for the visited guard).
        var loadTask = loader.LoadL1Async(new[] { wfId }, ct);
        var completed = await Task.WhenAny(loadTask, Task.Delay(TimeSpan.FromSeconds(10), ct));
        Assert.Same(loadTask, completed);   // proves the BFS terminated on the cycle

        using var snapshot = await loadTask;
        Assert.True(snapshot.Steps.ContainsKey(stepA));
        Assert.True(snapshot.Steps.ContainsKey(stepB));
    }
}
