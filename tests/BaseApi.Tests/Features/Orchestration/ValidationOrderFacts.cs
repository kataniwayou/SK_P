using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BaseApi.Service.Features.Assignment;
using BaseApi.Service.Features.Orchestration;
using BaseApi.Service.Features.Orchestration.Loading;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Schema;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using BaseApi.Tests.Composition;
using BaseApi.Tests.TestHelpers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BaseApi.Tests.Features.Orchestration;

/// <summary>
/// Phase 14 SC#5 — proves the LOCKED gate order short-circuits at the FIRST failure and that
/// L1 cleanup runs on the validation-failure path. The within-workflow pipeline order
/// (<see cref="OrchestrationService.StartAsync"/>) is:
/// existence (404 — D-13) → cycle (422) → schema-edge (422) → payload (422).
/// <para>
/// <b>Phase 15 amendment (per-workflow first-failure scope).</b> Plan 15-04 restructured
/// <c>StartAsync</c> into a PER-WORKFLOW loop: for a multi-workflow request <c>[A, B]</c>, the
/// upfront existence gate runs over ALL ids first (404 if any is missing, before any mutation),
/// then workflow A is FULLY processed (pre-clean → load → cycle/schema-edge/payload → L2 project)
/// BEFORE workflow B is even loaded. First-failure semantics therefore changed from "first failing
/// gate across the whole batch" to "first failing gate within the FIRST failing workflow, in
/// iteration order" (15-CONTEXT amendment, line 112). The WITHIN-workflow gate ORDER
/// (cycle → schema-edge → payload) is UNCHANGED — only the cross-workflow SCOPE changed, and
/// partial state across workflows is now ACCEPTED (D-07): a valid earlier workflow stays projected
/// in L2 even though a later workflow fails 422.
/// </para>
/// <para>
/// <b>5 facts:</b>
/// <list type="number">
///   <item><c>ExistenceBeforeCycle_MissingWorkflowId_Returns404_NotCycle</c> — a missing WorkflowId is
///     rejected 404 BEFORE the snapshot is even loaded; existence short-circuits ahead of the 422 gates
///     (D-13 — existence stays 404, NOT 422).</item>
///   <item><c>CycleBeforeSchemaEdge_WorkflowFailingBoth_Returns422Cycle</c> — a (single) workflow that
///     fails BOTH the cycle gate (back-edge) AND the schema-edge gate (mismatched processor schemas)
///     returns <c>errors.gate=="cycle"</c>: the cycle gate (step 3) wins over schema-edge (step 4).
///     WITHIN-workflow gate order — UNCHANGED by the Phase 15 amendment.</item>
///   <item><c>SchemaEdgeBeforePayload_WorkflowFailingBoth_Returns422SchemaEdge</c> — an ACYCLIC (single)
///     workflow that fails BOTH schema-edge (mismatched schemas) AND payload (bad assignment payload)
///     returns <c>errors.gate=="schemaEdge"</c>: schema-edge (step 4) wins over payload (step 5).
///     WITHIN-workflow gate order — UNCHANGED by the Phase 15 amendment.</item>
///   <item><c>PerWorkflowScope_FirstValid_SecondCycle_ProjectsFirst_Fails422</c> — Phase 15 amendment.
///     A request <c>[A, B]</c> where A is a valid single-step graph and B has a back-edge cycle:
///     A is PROJECTED (its L2 root key exists) and the request fails 422 on B's cycle. Proves the
///     per-workflow loop processes A fully before B and that cross-workflow partial state is accepted
///     (D-07), replacing the old "no keys written for ANY workflow when the FIRST/any failed" batch
///     snapshot assumption.</item>
///   <item><c>L1Cleanup_RunsOnValidationFailurePath</c> — a real back-edge cycle forces a 422; a recording
///     loader captures the snapshot and asserts <c>IsDisposed==true</c> + all 5 dicts empty, proving the
///     <c>using var snapshot</c> declaration runs <c>Dispose()</c> on the DOMAIN-VALIDATION failure path
///     (not just the 500 path covered by <c>StartCleanupFacts</c>).</item>
/// </list>
/// </para>
/// </summary>
[Trait("Phase", "14")]
public sealed class ValidationOrderFacts : IClassFixture<HarnessWebAppFactory>
{
    private readonly HarnessWebAppFactory _factory;

    public ValidationOrderFacts(HarnessWebAppFactory factory) => _factory = factory;

    /// <summary>A constraining draft-2020-12 schema: object requiring a STRING property "foo".</summary>
    private const string ConstrainingSchema =
        "{\"type\":\"object\",\"required\":[\"foo\"],\"properties\":{\"foo\":{\"type\":\"string\"}}}";

    /// <summary>Minimal valid draft-2020-12 schema body — type:object accepts any object payload.</summary>
    private const string MinimalSchema = "{\"type\":\"object\"}";

    // ----- Seeding helpers (mirrored from SchemaEdgeFacts / PayloadConfigSchemaFacts / CycleDetectionFacts) -----

    /// <summary>POSTs a Schema with the given Definition and returns its new Id.</summary>
    private static async Task<Guid> SeedSchemaAsync(HttpClient client, string definition, CancellationToken ct)
    {
        var dto = new SchemaCreateDto(
            Name: $"vof-schema-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            Definition: definition);
        var resp = await client.PostAsJsonAsync("/api/v1/schemas", dto, ct);
        resp.EnsureSuccessStatusCode();
        var read = await resp.Content.ReadFromJsonAsync<SchemaReadDto>(cancellationToken: ct);
        return read!.Id;
    }

    /// <summary>POSTs a Processor with the given schema FKs and returns its new Id.</summary>
    private static async Task<Guid> SeedProcessorAsync(
        HttpClient client, Guid? inputSchemaId, Guid? outputSchemaId, Guid? configSchemaId, CancellationToken ct)
    {
        var dto = new ProcessorCreateDto(
            Name: $"vof-proc-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            SourceHash: HashHelpers.RandomSha256Hex(),
            InputSchemaId: inputSchemaId,
            OutputSchemaId: outputSchemaId,
            ConfigSchemaId: configSchemaId);
        var resp = await client.PostAsJsonAsync("/api/v1/processors", dto, ct);
        resp.EnsureSuccessStatusCode();
        var read = await resp.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);
        return read!.Id;
    }

    /// <summary>POSTs a Step wiring the given ProcessorId + NextStepIds and returns its new Id.</summary>
    private static async Task<Guid> SeedStepAsync(
        HttpClient client, Guid processorId, List<Guid>? nextStepIds, CancellationToken ct)
    {
        var dto = new StepCreateDto(
            Name: $"vof-step-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            ProcessorId: processorId,
            NextStepIds: nextStepIds,
            EntryCondition: StepEntryCondition.PreviousCompleted);
        var resp = await client.PostAsJsonAsync("/api/v1/steps", dto, ct);
        resp.EnsureSuccessStatusCode();
        var read = await resp.Content.ReadFromJsonAsync<StepReadDto>(cancellationToken: ct);
        return read!.Id;
    }

    /// <summary>
    /// PUTs an existing Step to (re)wire its NextStepIds — used to add a back-edge cycle after both ends
    /// exist (FK on StepNextSteps requires both ends to be present first).
    /// </summary>
    private static async Task SetNextStepIdsAsync(
        HttpClient client, Guid stepId, Guid processorId, List<Guid> nextStepIds, CancellationToken ct)
    {
        var dto = new StepUpdateDto(
            Name: $"vof-step-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            ProcessorId: processorId,
            NextStepIds: nextStepIds,
            EntryCondition: StepEntryCondition.PreviousCompleted);
        var resp = await client.PutAsJsonAsync($"/api/v1/steps/{stepId}", dto, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>POSTs an Assignment binding the given StepId + Payload and returns its new Id.</summary>
    private static async Task<Guid> SeedAssignmentAsync(HttpClient client, Guid stepId, string payload, CancellationToken ct)
    {
        var dto = new AssignmentCreateDto(
            Name: $"vof-asn-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            StepId: stepId,
            Payload: payload);
        var resp = await client.PostAsJsonAsync("/api/v1/assignments", dto, ct);
        resp.EnsureSuccessStatusCode();
        var read = await resp.Content.ReadFromJsonAsync<AssignmentReadDto>(cancellationToken: ct);
        return read!.Id;
    }

    /// <summary>POSTs a Workflow with the given EntryStepIds + optional AssignmentIds and returns its new Id.</summary>
    private static async Task<Guid> SeedWorkflowAsync(
        HttpClient client, List<Guid> entryStepIds, List<Guid>? assignmentIds, CancellationToken ct)
    {
        var dto = new WorkflowCreateDto(
            Name: $"vof-wf-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            EntryStepIds: entryStepIds,
            AssignmentIds: assignmentIds,
            CronExpression: null);
        var resp = await client.PostAsJsonAsync("/api/v1/workflows", dto, ct);
        resp.EnsureSuccessStatusCode();
        var read = await resp.Content.ReadFromJsonAsync<WorkflowReadDto>(cancellationToken: ct);
        return read!.Id;
    }

    /// <summary>
    /// Recording loader double — wraps the REAL <see cref="WorkflowGraphLoader"/>, invokes the real
    /// <c>LoadL1Async</c>, and stashes the returned snapshot so the test can inspect its disposal state
    /// AFTER the request completes. Copied from <c>StartCleanupFacts</c> (T-13-09 — registered only for
    /// the one fact via <c>ConfigureTestServices</c>; production DI is unaffected).
    /// </summary>
    private sealed class RecordingWorkflowGraphLoader : IWorkflowGraphLoader
    {
        private readonly IWorkflowGraphLoader _inner;
        public WorkflowGraphSnapshot? Captured { get; private set; }

        public RecordingWorkflowGraphLoader(IWorkflowGraphLoader inner) => _inner = inner;

        public async Task<WorkflowGraphSnapshot> LoadL1Async(
            IReadOnlyList<Guid> workflowIds, CancellationToken ct)
        {
            Captured = await _inner.LoadL1Async(workflowIds, ct);
            return Captured;
        }
    }

    // ---------------------------------------- Facts ----------------------------------------

    /// <summary>
    /// Existence (step 1, 404 — D-13) short-circuits ahead of the cycle gate (step 3). A WorkflowId that
    /// does not exist is rejected 404 BEFORE the snapshot is loaded, so no cycle is ever evaluated.
    /// </summary>
    [Fact]
    public async Task ExistenceBeforeCycle_MissingWorkflowId_Returns404_NotCycle()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // A well-formed but non-existent WorkflowId — existence runs first (before snapshot load).
        var missingId = Guid.NewGuid();

        var resp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start",
            new List<Guid> { missingId },
            ct);

        // Existence wins: 404 (D-13), NOT a 422 cycle.
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.NotEqual(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    /// <summary>
    /// Cycle (step 3) short-circuits ahead of schema-edge (step 4). The seeded workflow fails BOTH gates:
    /// a back-edge cycle A→B→A AND mismatched processor schemas (A.Output != B.Input). The FIRST gate
    /// (cycle) fires, so <c>errors.gate == "cycle"</c>.
    /// </summary>
    [Fact]
    public async Task CycleBeforeSchemaEdge_WorkflowFailingBoth_Returns422Cycle()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // Distinct schemas so the edge A→B would ALSO fail schema-edge (A outputs X, B expects Y).
        var schemaX = await SeedSchemaAsync(client, MinimalSchema, ct);
        var schemaY = await SeedSchemaAsync(client, MinimalSchema, ct);

        var procA = await SeedProcessorAsync(client, inputSchemaId: schemaY, outputSchemaId: schemaX, configSchemaId: null, ct);
        var procB = await SeedProcessorAsync(client, inputSchemaId: schemaY, outputSchemaId: schemaX, configSchemaId: null, ct);

        // Build A→B forward, then add the back-edge B→A to close the cycle (FK needs both ends first).
        var stepB = await SeedStepAsync(client, procB, nextStepIds: null, ct);
        var stepA = await SeedStepAsync(client, procA, nextStepIds: new List<Guid> { stepB }, ct);
        await SetNextStepIdsAsync(client, stepB, procB, new List<Guid> { stepA }, ct);

        var wfId = await SeedWorkflowAsync(client, new List<Guid> { stepA }, assignmentIds: null, ct);

        var resp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start",
            new List<Guid> { wfId },
            ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var errors = doc.RootElement.GetProperty("errors");

        // Cycle (step 3) wins over schema-edge (step 4).
        Assert.Equal("cycle", errors.GetProperty("gate").GetString());
    }

    /// <summary>
    /// Schema-edge (step 4) short-circuits ahead of payload (step 5). The seeded workflow is ACYCLIC
    /// (A→B) but fails BOTH: schema-edge (A.Output != B.Input) AND payload (B's Assignment violates B's
    /// ConfigSchema). The FIRST gate (schema-edge) fires, so <c>errors.gate == "schemaEdge"</c>.
    /// </summary>
    [Fact]
    public async Task SchemaEdgeBeforePayload_WorkflowFailingBoth_Returns422SchemaEdge()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var schemaX = await SeedSchemaAsync(client, MinimalSchema, ct);
        var schemaY = await SeedSchemaAsync(client, MinimalSchema, ct);
        var configSchema = await SeedSchemaAsync(client, ConstrainingSchema, ct);

        // A outputs X; B expects Y (schema-edge mismatch) AND B carries a ConfigSchema the payload violates.
        var procA = await SeedProcessorAsync(client, inputSchemaId: null, outputSchemaId: schemaX, configSchemaId: null, ct);
        var procB = await SeedProcessorAsync(client, inputSchemaId: schemaY, outputSchemaId: null, configSchemaId: configSchema, ct);

        // Acyclic A→B (B terminal).
        var stepB = await SeedStepAsync(client, procB, nextStepIds: null, ct);
        var stepA = await SeedStepAsync(client, procA, nextStepIds: new List<Guid> { stepB }, ct);

        // B's Assignment payload violates configSchema (foo must be a string; 123 is a number).
        var asnB = await SeedAssignmentAsync(client, stepB, "{\"foo\":123}", ct);

        var wfId = await SeedWorkflowAsync(client, new List<Guid> { stepA }, new List<Guid> { asnB }, ct);

        var resp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start",
            new List<Guid> { wfId },
            ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var errors = doc.RootElement.GetProperty("errors");

        // Schema-edge (step 4) wins over payload (step 5).
        Assert.Equal("schemaEdge", errors.GetProperty("gate").GetString());
    }

    /// <summary>
    /// Phase 15 amendment — per-workflow first-failure SCOPE. A request <c>[A, B]</c> where A is a valid
    /// single-step graph (no schemas → passes all 3 gates) and B has a back-edge cycle A'→B'→A'. The
    /// existence gate passes for both, then the per-workflow loop processes A FULLY (projecting its L2
    /// root key) BEFORE loading B, then B's cycle gate throws → the request returns 422. The test asserts
    /// BOTH: the 422 (B failed) AND that A's L2 root key EXISTS (A was projected). This is the
    /// cross-workflow partial-state behavior the Phase 15 per-workflow loop introduces (D-07), replacing
    /// the old batch-snapshot assumption that a single failure left NO workflow projected.
    /// </summary>
    [Fact]
    public async Task PerWorkflowScope_FirstValid_SecondCycle_ProjectsFirst_Fails422()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // Workflow A — valid single-step graph, no schemas (passes cycle + schema-edge + payload).
        var procA = await SeedProcessorAsync(client, inputSchemaId: null, outputSchemaId: null, configSchemaId: null, ct);
        var stepA = await SeedStepAsync(client, procA, nextStepIds: null, ct);
        var wfA = await SeedWorkflowAsync(client, new List<Guid> { stepA }, assignmentIds: null, ct);

        // Workflow B — real back-edge cycle B1→B2→B1 (all schema FKs null so the cycle gate is reached).
        var procB = await SeedProcessorAsync(client, inputSchemaId: null, outputSchemaId: null, configSchemaId: null, ct);
        var stepB2 = await SeedStepAsync(client, procB, nextStepIds: null, ct);
        var stepB1 = await SeedStepAsync(client, procB, nextStepIds: new List<Guid> { stepB2 }, ct);
        await SetNextStepIdsAsync(client, stepB2, procB, new List<Guid> { stepB1 }, ct);
        var wfB = await SeedWorkflowAsync(client, new List<Guid> { stepB1 }, assignmentIds: null, ct);

        // Request order [A, B] — existence passes for both; A is projected; B's cycle gate throws.
        var resp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start",
            new List<Guid> { wfA, wfB },
            ct);

        // B failed the cycle gate → the request is 422 (per-workflow first-failure: B is the first — and
        // only — failing workflow in iteration order).
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("cycle", doc.RootElement.GetProperty("errors").GetProperty("gate").GetString());

        // Cross-workflow partial state (D-07) — A was FULLY processed before B was loaded, so A's L2
        // root key EXISTS despite B's later 422. (The old batch-snapshot model would have written NOTHING.)
        var prefix = _factory.RedisKeyPrefix;
        var db = _factory.RedisMultiplexer.GetDatabase();
        Assert.True(
            await db.KeyExistsAsync($"{prefix}{wfA}"),
            "workflow A's L2 root key must exist — per-workflow loop projected A before B failed");
    }

    /// <summary>
    /// L1 cleanup runs on the DOMAIN-VALIDATION (422) failure path. A real back-edge cycle forces a 422;
    /// a recording loader captures the snapshot and the test asserts the <c>using var snapshot</c>
    /// declaration ran <c>Dispose()</c> (IsDisposed + all 5 dicts empty). Unlike <c>StartCleanupFacts</c>
    /// (which forces a 500 via a throwing writer), this proves cleanup also runs when a validator throws.
    /// </summary>
    [Fact]
    public async Task L1Cleanup_RunsOnValidationFailurePath()
    {
        var ct = TestContext.Current.CancellationToken;

        RecordingWorkflowGraphLoader? recorder = null;
        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                // Wrap the real loader so the populated snapshot is captured; the cycle gate (step 3)
                // then throws a 422 and the using-declaration's Dispose runs on that failure path.
                services.AddScoped<WorkflowGraphLoader>();
                services.AddScoped<IWorkflowGraphLoader>(sp =>
                {
                    recorder = new RecordingWorkflowGraphLoader(
                        sp.GetRequiredService<WorkflowGraphLoader>());
                    return recorder;
                });
            }));

        using var client = factory.CreateClient();

        // Seed a real back-edge cycle A→B→A (all schema FKs null so the cycle gate is reached cleanly).
        var procId = await SeedProcessorAsync(client, inputSchemaId: null, outputSchemaId: null, configSchemaId: null, ct);
        var stepB = await SeedStepAsync(client, procId, nextStepIds: null, ct);
        var stepA = await SeedStepAsync(client, procId, nextStepIds: new List<Guid> { stepB }, ct);
        await SetNextStepIdsAsync(client, stepB, procId, new List<Guid> { stepA }, ct);

        var wfId = await SeedWorkflowAsync(client, new List<Guid> { stepA }, assignmentIds: null, ct);

        var resp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start",
            new List<Guid> { wfId },
            ct);

        // The cycle gate throws OrchestrationValidationException → 422.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);

        // L1 cleanup ran on the 422 validation-failure path via `using var snapshot`.
        Assert.NotNull(recorder);
        Assert.NotNull(recorder!.Captured);
        Assert.True(recorder.Captured!.IsDisposed);

        // Disposal cleared all 5 dictionaries (L1-VALIDATE-01 / ORCH-START — cleanup on every failure).
        Assert.Empty(recorder.Captured.Workflows);
        Assert.Empty(recorder.Captured.Steps);
        Assert.Empty(recorder.Captured.Processors);
        Assert.Empty(recorder.Captured.Schemas);
        Assert.Empty(recorder.Captured.Assignments);
    }
}
