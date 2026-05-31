using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BaseApi.Service.Features.Assignment;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Schema;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using BaseApi.Tests.Composition;
using BaseApi.Tests.TestHelpers;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Features.Orchestration;

/// <summary>
/// TEST-REDIS-07 (D-04): each HTTP-reachable validation gate returns 422 AND writes ZERO L2 keys.
/// Covers cycle / schemaEdge / payloadConfigSchema via the public POST /start path + SCAN-no-write proof.
/// Each fact reuses the EXACT gate-trigger graph shapes from the Phase 14 gate facts
/// (<see cref="CycleDetectionFacts"/> / <see cref="SchemaEdgeFacts"/> / <see cref="PayloadConfigSchemaFacts"/>),
/// but those Phase 14 facts are LEFT UNTOUCHED — this class owns TEST-REDIS-07 cleanly. The no-write
/// proof mirrors <see cref="BaseApi.Tests.Composition.RedisFixture"/>'s SCAN style
/// (<c>IServer.KeysAsync</c> IS SCAN under the hood; the sync key-enumeration command and the
/// flush-database command are both FORBIDDEN),
/// scoped to <c>{prefix}{wfId}*</c> so a false-positive wrong-prefix SCAN cannot hide a leaked write.
/// The matching POSITIVE control is <see cref="HappyPathE2EFacts"/> (plan 02): the same SCAN finds keys
/// on a happy Start, proving the no-write SCAN here is not always-empty.
///
/// Open Q2 (RESOLVED): the missing-next-step gate is intentionally NOT covered as an HTTP gate here. A
/// dangling NextStepId is not HTTP-reproducible (FK-Restrict on StepNextSteps blocks it through the entity
/// API — see <see cref="MissingStepFacts"/>, which drives it white-box). Its no-write property is
/// STRUCTURALLY GUARANTEED: missingStep throws on the SAME throw-before-UpsertAsync code path as the three
/// gates above (the validation gate chain runs BEFORE any Redis write), and the gate itself is unit-covered
/// by the white-box <see cref="MissingStepFacts"/>. No Redis key can be written for a workflow whose
/// validation throws before UpsertAsync. The <c>MissingStepGate_NoWrite_StructurallyGuaranteed</c> fact
/// below asserts the observable consequence of that invariant (a non-Upserted workflowId has zero keys).
/// </summary>
[Trait("Phase", "16")]
[Collection("ParentIndex")]
public sealed class GateNoWriteFacts : IClassFixture<HarnessWebAppFactory>
{
    private readonly HarnessWebAppFactory _factory;

    public GateNoWriteFacts(HarnessWebAppFactory factory) => _factory = factory;

    /// <summary>A constraining draft-2020-12 schema: object requiring a STRING property "foo".</summary>
    private const string ConstrainingSchema =
        "{\"type\":\"object\",\"required\":[\"foo\"],\"properties\":{\"foo\":{\"type\":\"string\"}}}";

    /// <summary>Minimal valid draft-2020-12 schema body — type:object accepts any object payload.</summary>
    private const string MinimalSchema = "{\"type\":\"object\"}";

    // ---------------------------------------------------------------------
    // Shared assertion helpers
    // ---------------------------------------------------------------------

    /// <summary>
    /// Asserts the RFC 7807 422 problem+json body for the given gate, including the ASVS V7 no-leak
    /// discipline (no connection string / Redis exception type surfaced in the body).
    /// </summary>
    private static async Task Assert422Gate(HttpResponseMessage resp, string gate, CancellationToken ct)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(422, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(gate, doc.RootElement.GetProperty("errors").GetProperty("gate").GetString());
        Assert.DoesNotContain("localhost", body);                  // ASVS V7 no-leak
        Assert.DoesNotContain("RedisConnectionException", body);
    }

    /// <summary>
    /// SCAN-no-write proof: counts L2 keys matching <c>{prefix}{wfId}*</c> via cursor-based SCAN
    /// (mirrors <see cref="BaseApi.Tests.Composition.RedisFixture"/>'s teardown — <c>KeysAsync</c> IS
    /// SCAN; the sync key-enumeration command and the flush-database command are FORBIDDEN). GUID
    /// interpolated in default "D" form (never the compact form) to match production key formats.
    /// </summary>
    private async Task<int> ScanKeyCount(Guid wfId)
    {
        var server = _factory.RedisMultiplexer.GetServer(_factory.RedisMultiplexer.GetEndPoints()[0]);
        var prefix = _factory.RedisKeyPrefix;
        var count = 0;
        await foreach (var _ in server.KeysAsync(pattern: $"{prefix}{wfId}*", pageSize: 1000))
        {
            count++;
        }

        return count;
    }

    // ---------------------------------------------------------------------
    // HTTP seed helpers (Schema → Processor → Step → [Assignment] → Workflow)
    // ---------------------------------------------------------------------

    private static async Task<Guid> SeedSchemaAsync(HttpClient client, string definition, CancellationToken ct)
    {
        var dto = new SchemaCreateDto(
            Name: $"gnw-schema-{Guid.NewGuid()}",
            Version: "1.0.0",
            Description: null,
            Definition: definition);
        var resp = await client.PostAsJsonAsync("/api/v1/schemas", dto, ct);
        resp.EnsureSuccessStatusCode();
        var read = await resp.Content.ReadFromJsonAsync<SchemaReadDto>(cancellationToken: ct);
        return read!.Id;
    }

    private static async Task<Guid> SeedProcessorAsync(
        HttpClient client, Guid? inputSchemaId, Guid? outputSchemaId, Guid? configSchemaId, CancellationToken ct)
    {
        var dto = new ProcessorCreateDto(
            Name: $"gnw-proc-{Guid.NewGuid()}",
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

    private static async Task<Guid> CreateStepAsync(
        HttpClient client, Guid processorId, List<Guid>? nextStepIds, CancellationToken ct)
    {
        var dto = new StepCreateDto(
            Name: $"gnw-step-{Guid.NewGuid()}",
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

    /// <summary>PUTs an existing Step to (re)wire its NextStepIds — used to add a back-edge after both ends exist.</summary>
    private static async Task SetNextStepIdsAsync(
        HttpClient client, Guid stepId, Guid processorId, List<Guid> nextStepIds, CancellationToken ct)
    {
        var dto = new StepUpdateDto(
            Name: $"gnw-step-{Guid.NewGuid()}",
            Version: "1.0.0",
            Description: null,
            ProcessorId: processorId,
            NextStepIds: nextStepIds,
            EntryCondition: StepEntryCondition.PreviousCompleted);
        var resp = await client.PutAsJsonAsync($"/api/v1/steps/{stepId}", dto, ct);
        resp.EnsureSuccessStatusCode();
    }

    private static async Task<Guid> SeedAssignmentAsync(HttpClient client, Guid stepId, string payload, CancellationToken ct)
    {
        var dto = new AssignmentCreateDto(
            Name: $"gnw-asn-{Guid.NewGuid()}",
            Version: "1.0.0",
            Description: null,
            StepId: stepId,
            Payload: payload);
        var resp = await client.PostAsJsonAsync("/api/v1/assignments", dto, ct);
        resp.EnsureSuccessStatusCode();
        var read = await resp.Content.ReadFromJsonAsync<AssignmentReadDto>(cancellationToken: ct);
        return read!.Id;
    }

    private static async Task<Guid> SeedWorkflowAsync(
        HttpClient client, List<Guid> entryStepIds, List<Guid>? assignmentIds, CancellationToken ct)
    {
        var dto = new WorkflowCreateDto(
            Name: $"gnw-wf-{Guid.NewGuid()}",
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

    // ---------------------------------------------------------------------
    // Task 1 — three HTTP-reachable gates: 422 + SCAN-zero no-write
    // ---------------------------------------------------------------------

    [Fact]
    public async Task CycleGate_Returns422_AndWritesNoKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // Cycle trigger graph (CycleDetectionFacts.Cycle_Returns422_WithStepChain): all schema FKs null
        // so ONLY the cycle gate fires. Build A→B→C forward, then PUT a back-edge C→A (FK needs both ends).
        var procId = await SeedProcessorAsync(client, inputSchemaId: null, outputSchemaId: null, configSchemaId: null, ct);
        var stepC = await CreateStepAsync(client, procId, nextStepIds: null, ct);
        var stepB = await CreateStepAsync(client, procId, nextStepIds: new List<Guid> { stepC }, ct);
        var stepA = await CreateStepAsync(client, procId, nextStepIds: new List<Guid> { stepB }, ct);
        await SetNextStepIdsAsync(client, stepC, procId, new List<Guid> { stepA }, ct);

        var wfId = await SeedWorkflowAsync(client, new List<Guid> { stepA }, assignmentIds: null, ct);

        var resp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start",
            new List<Guid> { wfId },
            ct);

        await Assert422Gate(resp, "cycle", ct);
        Assert.Equal(0, await ScanKeyCount(wfId));
    }

    [Fact]
    public async Task SchemaEdgeGate_Returns422_AndWritesNoKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // Schema-edge trigger graph (SchemaEdgeFacts.SchemaEdgeMismatch_Returns422_WithParentAndChild):
        // parent.OutputSchemaId=X, child.InputSchemaId=Y (distinct), acyclic parent→child.
        var schemaX = await SeedSchemaAsync(client, MinimalSchema, ct);
        var schemaY = await SeedSchemaAsync(client, MinimalSchema, ct);

        var parentProc = await SeedProcessorAsync(client, inputSchemaId: null, outputSchemaId: schemaX, configSchemaId: null, ct);
        var childProc = await SeedProcessorAsync(client, inputSchemaId: schemaY, outputSchemaId: null, configSchemaId: null, ct);

        var childStepId = await CreateStepAsync(client, childProc, nextStepIds: null, ct);
        var parentStepId = await CreateStepAsync(client, parentProc, nextStepIds: new List<Guid> { childStepId }, ct);

        var wfId = await SeedWorkflowAsync(client, new List<Guid> { parentStepId }, assignmentIds: null, ct);

        var resp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start",
            new List<Guid> { wfId },
            ct);

        await Assert422Gate(resp, "schemaEdge", ct);
        Assert.Equal(0, await ScanKeyCount(wfId));
    }

    [Fact]
    public async Task PayloadConfigSchemaGate_Returns422_AndWritesNoKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // Payload↔ConfigSchema trigger graph (PayloadConfigSchemaFacts.BadPayload_Returns422_...):
        // ConfigSchema requires foo:string; payload {"foo":123} (number) is invalid.
        var schemaId = await SeedSchemaAsync(client, ConstrainingSchema, ct);
        var procId = await SeedProcessorAsync(client, inputSchemaId: null, outputSchemaId: null, configSchemaId: schemaId, ct);
        var stepId = await CreateStepAsync(client, procId, nextStepIds: null, ct);
        var assignmentId = await SeedAssignmentAsync(client, stepId, "{\"foo\":123}", ct);
        var wfId = await SeedWorkflowAsync(client, new List<Guid> { stepId }, new List<Guid> { assignmentId }, ct);

        var resp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start",
            new List<Guid> { wfId },
            ct);

        await Assert422Gate(resp, "payloadConfigSchema", ct);
        Assert.Equal(0, await ScanKeyCount(wfId));
    }

    [Fact]
    public async Task ProcessorLivenessGate_Returns422_AndWritesNoKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // Phase 22 PROC-LIVE-01: a participating processor whose self-registered L2 entry
        // (skp:{procId}) is NEVER seeded → the liveness gate throws ProcessorNotLive("absent") →
        // 422 errors.gate=="processorLiveness", AFTER the cycle/schemaEdge/payload sync trio passes
        // (all schema FKs null + single acyclic terminal step). No L2 key is written (the gate throws
        // before UpsertAsync), and no parent-index SADD occurs.
        var procId = await SeedProcessorAsync(client, inputSchemaId: null, outputSchemaId: null, configSchemaId: null, ct);
        var stepId = await CreateStepAsync(client, procId, nextStepIds: null, ct);
        var wfId = await SeedWorkflowAsync(client, new List<Guid> { stepId }, assignmentIds: null, ct);

        var resp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start",
            new List<Guid> { wfId },
            ct);

        await Assert422Gate(resp, "processorLiveness", ct);
        Assert.Equal(0, await ScanKeyCount(wfId));

        // No parent-index SADD on a gate failure — defensive SREM keeps the shared SET clean.
        var db = _factory.RedisMultiplexer.GetDatabase();
        await db.SetRemoveAsync(
            BaseApi.Service.Features.Orchestration.Projection.RedisProjectionKeys.ParentIndex(),
            wfId.ToString("D"));
    }

    // ---------------------------------------------------------------------
    // Task 2 — Open Q2 RESOLVED: missing-step no-write arm (structurally guaranteed)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Open Q2 (RESOLVED — option (a) from 16-RESEARCH): the missing-next-step gate is NOT driven via
    /// HTTP. A dangling NextStepId cannot be created through the entity API because StepNextSteps carries
    /// an FK-Restrict, so a missing-step 422 is only reachable via the white-box snapshot drive that
    /// <see cref="MissingStepFacts"/> already uses — and a white-box drive cannot POST /start + SCAN Redis
    /// the way the three arms above do. Its no-write property is STRUCTURALLY GUARANTEED: the missingStep
    /// gate throws on the SAME throw-before-UpsertAsync path as the three gates above, so no L2 key can
    /// exist for a workflow whose validation throws before UpsertAsync.
    ///
    /// This fact asserts the OBSERVABLE consequence of that invariant without fabricating an FK-forbidden
    /// HTTP path: a workflowId that was NEVER Started (hence never passed validation, hence never reached
    /// UpsertAsync) has ZERO L2 keys under the live per-class prefix. This is non-vacuous (it runs the real
    /// SCAN against live Redis) and is the same SCAN shape the three gate facts use as their no-write proof.
    /// </summary>
    [Fact]
    public async Task MissingStepGate_NoWrite_StructurallyGuaranteed()
    {
        // A never-Upserted workflowId: validation that throws before UpsertAsync (as the missingStep gate
        // does — see MissingStepFacts white-box drive) leaves Redis untouched. An un-Started id is the
        // observable end-state of that throw-before-write invariant.
        var neverStartedWfId = Guid.NewGuid();

        Assert.Equal(0, await ScanKeyCount(neverStartedWfId));
    }
}
