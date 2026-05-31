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
using Xunit;

namespace BaseApi.Tests.Features.Orchestration;

/// <summary>
/// Phase 14 SC#4 / L1-VALIDATE-06 + L1-VALIDATE-08 integration tests for the Payload↔ConfigSchema
/// conformance gate (<see cref="BaseApi.Service.Features.Orchestration.Validation.PayloadConfigSchemaValidator"/>)
/// reached via <c>POST /api/v1/orchestration/start</c>. Uses <see cref="Phase8WebAppFactory"/> per CONTEXT D-20.
/// <para>
/// <b>3 facts:</b>
/// <list type="number">
///   <item><c>BadPayload_Returns422_WithAssignmentIdAndErrors</c> — Assignment.Payload violates its
///     resolved ConfigSchema → 422 + <c>errors.gate=="payloadConfigSchema"</c> +
///     <c>errors.offending.{assignmentId, errors[]}</c>.</item>
///   <item><c>NullConfigSchemaId_Passes</c> — Processor.ConfigSchemaId=null → 204 (no schema to validate).</item>
///   <item><c>SameSchema_TwoAssignments_BothValidated_Returns204</c> — ONE Schema referenced by ONE
///     Processor used by TWO Steps, each with its OWN valid-payload Assignment → 204. Exercises the
///     per-Start LOCAL <c>Dictionary&lt;Guid,JsonSchema&gt;</c> cache code path (L1-VALIDATE-08): both
///     assignments validate against the SAME ConfigSchemaId in a single Start. The cache is a local in
///     <c>PayloadConfigSchemaValidator.Validate</c>, so the schema is parsed at most once; per the plan
///     we assert the cache code path behaviorally (both same-schema assignments pass in one Start) rather
///     than add production instrumentation solely for the test.</item>
/// </list>
/// </para>
/// </summary>
[Trait("Phase", "14")]
[Collection("ParentIndex")]
public sealed class PayloadConfigSchemaFacts : IClassFixture<HarnessWebAppFactory>
{
    private readonly HarnessWebAppFactory _factory;

    public PayloadConfigSchemaFacts(HarnessWebAppFactory factory) => _factory = factory;

    /// <summary>A constraining draft-2020-12 schema: object requiring a STRING property "foo".</summary>
    private const string ConstrainingSchema =
        "{\"type\":\"object\",\"required\":[\"foo\"],\"properties\":{\"foo\":{\"type\":\"string\"}}}";

    /// <summary>POSTs a Schema with the given Definition and returns its new Id.</summary>
    private static async Task<Guid> SeedSchemaAsync(HttpClient client, string definition, CancellationToken ct)
    {
        var dto = new SchemaCreateDto(
            Name: $"pcs-schema-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            Definition: definition);
        var resp = await client.PostAsJsonAsync("/api/v1/schemas", dto, ct);
        resp.EnsureSuccessStatusCode();
        var read = await resp.Content.ReadFromJsonAsync<SchemaReadDto>(cancellationToken: ct);
        return read!.Id;
    }

    /// <summary>
    /// POSTs a Processor with the given ConfigSchemaId (Input/Output null so the schema-edge gate is a
    /// no-op) and returns its new Id.
    /// </summary>
    private static async Task<Guid> SeedProcessorAsync(HttpClient client, Guid? configSchemaId, CancellationToken ct)
    {
        var dto = new ProcessorCreateDto(
            Name: $"pcs-proc-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            SourceHash: HashHelpers.RandomSha256Hex(),
            InputSchemaId: null,
            OutputSchemaId: null,
            ConfigSchemaId: configSchemaId);
        var resp = await client.PostAsJsonAsync("/api/v1/processors", dto, ct);
        resp.EnsureSuccessStatusCode();
        var read = await resp.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);
        return read!.Id;
    }

    /// <summary>POSTs a terminal Step (NextStepIds=null) wiring the given ProcessorId and returns its new Id.</summary>
    private static async Task<Guid> SeedStepAsync(HttpClient client, Guid processorId, CancellationToken ct)
    {
        var dto = new StepCreateDto(
            Name: $"pcs-step-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            ProcessorId: processorId,
            NextStepIds: null,
            EntryCondition: StepEntryCondition.PreviousCompleted);
        var resp = await client.PostAsJsonAsync("/api/v1/steps", dto, ct);
        resp.EnsureSuccessStatusCode();
        var read = await resp.Content.ReadFromJsonAsync<StepReadDto>(cancellationToken: ct);
        return read!.Id;
    }

    /// <summary>POSTs an Assignment binding the given StepId + Payload and returns its new Id.</summary>
    private static async Task<Guid> SeedAssignmentAsync(HttpClient client, Guid stepId, string payload, CancellationToken ct)
    {
        var dto = new AssignmentCreateDto(
            Name: $"pcs-asn-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            StepId: stepId,
            Payload: payload);
        var resp = await client.PostAsJsonAsync("/api/v1/assignments", dto, ct);
        resp.EnsureSuccessStatusCode();
        var read = await resp.Content.ReadFromJsonAsync<AssignmentReadDto>(cancellationToken: ct);
        return read!.Id;
    }

    /// <summary>POSTs a Workflow with the given EntryStepIds + AssignmentIds and returns its new Id.</summary>
    private static async Task<Guid> SeedWorkflowAsync(
        HttpClient client, List<Guid> entryStepIds, List<Guid> assignmentIds, CancellationToken ct)
    {
        var dto = new WorkflowCreateDto(
            Name: $"pcs-wf-{Guid.NewGuid():N}",
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

    [Fact]
    public async Task BadPayload_Returns422_WithAssignmentIdAndErrors()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var schemaId = await SeedSchemaAsync(client, ConstrainingSchema, ct);
        var procId = await SeedProcessorAsync(client, configSchemaId: schemaId, ct);
        var stepId = await SeedStepAsync(client, procId, ct);
        // foo must be a string; 123 is a number → invalid against the ConfigSchema.
        var assignmentId = await SeedAssignmentAsync(client, stepId, "{\"foo\":123}", ct);
        var wfId = await SeedWorkflowAsync(client, new List<Guid> { stepId }, new List<Guid> { assignmentId }, ct);

        var resp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start",
            new List<Guid> { wfId },
            ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var errors = doc.RootElement.GetProperty("errors");
        Assert.Equal("payloadConfigSchema", errors.GetProperty("gate").GetString());

        var offending = errors.GetProperty("offending");
        Assert.Equal(assignmentId.ToString(), offending.GetProperty("assignmentId").GetString());

        var errArray = offending.GetProperty("errors");
        Assert.Equal(JsonValueKind.Array, errArray.ValueKind);
        Assert.True(errArray.GetArrayLength() > 0, "offending.errors must be a non-empty array.");
    }

    [Fact]
    public async Task NullConfigSchemaId_Passes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // No ConfigSchema → the payload-config gate has nothing to validate against → passes.
        var procId = await SeedProcessorAsync(client, configSchemaId: null, ct);
        var stepId = await SeedStepAsync(client, procId, ct);
        var assignmentId = await SeedAssignmentAsync(client, stepId, "{}", ct);
        var wfId = await SeedWorkflowAsync(client, new List<Guid> { stepId }, new List<Guid> { assignmentId }, ct);

        // PROC-LIVE-01: seed the processor live so the liveness gate accepts this 204 path.
        await _factory.SeedLiveProcessorAsync(procId, ct);
        var prefix = _factory.RedisKeyPrefix;
        _factory.TrackRedisKey($"{prefix}{wfId}");
        _factory.TrackRedisKey($"{prefix}{wfId}:{stepId}");

        try
        {
            var resp = await client.PostAsJsonAsync(
                "/api/v1/orchestration/start",
                new List<Guid> { wfId },
                ct);

            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        }
        finally
        {
            await _factory.SremParentIndexAsync(wfId);
        }
    }

    [Fact]
    public async Task SameSchema_TwoAssignments_BothValidated_Returns204()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // ONE Schema, ONE Processor (carrying that ConfigSchemaId), TWO Steps each with its OWN
        // valid-payload Assignment. Both assignments resolve to the SAME ConfigSchemaId, so they exercise
        // the per-Start LOCAL cache code path: the schema is parsed once and reused for the second
        // assignment. Both valid → 204 (cache hit path covered).
        var schemaId = await SeedSchemaAsync(client, ConstrainingSchema, ct);
        var procId = await SeedProcessorAsync(client, configSchemaId: schemaId, ct);

        var step1 = await SeedStepAsync(client, procId, ct);
        var step2 = await SeedStepAsync(client, procId, ct);
        var asn1 = await SeedAssignmentAsync(client, step1, "{\"foo\":\"bar\"}", ct);
        var asn2 = await SeedAssignmentAsync(client, step2, "{\"foo\":\"baz\"}", ct);

        var wfId = await SeedWorkflowAsync(
            client,
            new List<Guid> { step1, step2 },
            new List<Guid> { asn1, asn2 },
            ct);

        // PROC-LIVE-01: both steps share one processor — seed it live so the liveness gate accepts 204.
        await _factory.SeedLiveProcessorAsync(procId, ct);
        var prefix = _factory.RedisKeyPrefix;
        _factory.TrackRedisKey($"{prefix}{wfId}");
        _factory.TrackRedisKey($"{prefix}{wfId}:{step1}");
        _factory.TrackRedisKey($"{prefix}{wfId}:{step2}");

        try
        {
            var resp = await client.PostAsJsonAsync(
                "/api/v1/orchestration/start",
                new List<Guid> { wfId },
                ct);

            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        }
        finally
        {
            await _factory.SremParentIndexAsync(wfId);
        }
    }
}
