using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Schema;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using BaseApi.Tests.Composition;
using Xunit;

namespace BaseApi.Tests.Integration;

/// <summary>
/// 4 cross-entity error-mapping facts proving Phase 4 PostgresExceptionMapper +
/// Phase 8 entity FK/unique constraints + Schema validator close end-to-end.
/// Plan 08-08 TEST-06 floor exactly.
/// </summary>
public sealed class ErrorMappingFacts : IClassFixture<Phase8WebAppFactory>
{
    private readonly Phase8WebAppFactory _factory;

    public ErrorMappingFacts(Phase8WebAppFactory factory) => _factory = factory;

    private static string RandomSha256Hex()
    {
        var bytes = Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray();
        return string.Concat(bytes.Select(b => b.ToString("x2")));
    }

    // SC#2 — Postgres unique violation (23505) → Phase 4 mapper → 409 + field name in detail.
    [Fact]
    public async Task Create_Duplicate_SourceHash_Returns409()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var hash = RandomSha256Hex();

        var dto1 = new ProcessorCreateDto(
            Name: $"dup-1-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            SourceHash: hash,
            InputSchemaId: null,
            OutputSchemaId: null);
        var first = await client.PostAsJsonAsync("/api/v1/processors", dto1, ct);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var dto2 = dto1 with { Name = $"dup-2-{Guid.NewGuid():N}" };  // same hash
        var second = await client.PostAsJsonAsync("/api/v1/processors", dto2, ct);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);

        var body = await second.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(409, doc.RootElement.GetProperty("status").GetInt32());
        var detail = doc.RootElement.GetProperty("detail").GetString()!;
        // Phase 4 PostgresExceptionMapper Option A regex extracts "source_hash" from
        // uq_processor_source_hash. Accept either the field name or the constraint name in detail.
        Assert.True(
            detail.Contains("source_hash", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("uq_processor_source_hash", StringComparison.OrdinalIgnoreCase),
            $"Expected detail to mention source_hash or uq_processor_source_hash; got: {detail}");
        // T-04-LEAK regression: no Npgsql or stack frames in body.
        Assert.DoesNotContain("Npgsql.PostgresException", body);
        Assert.DoesNotContain("at BaseApi.", body);
        Assert.True(doc.RootElement.TryGetProperty("correlationId", out _));
    }

    // SC#5 (FK violation half) — POST Workflow with non-existent entryStepIds → 23503 → 422.
    [Fact]
    public async Task Create_Workflow_Non_Existent_EntryStepId_Returns422()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var dto = new WorkflowCreateDto(
            Name: $"wf-bad-fk-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            EntryStepIds: new List<Guid> { Guid.NewGuid() },  // random, definitely non-existent
            AssignmentIds: null,
            CronExpression: null);
        var resp = await client.PostAsJsonAsync("/api/v1/workflows", dto, ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(422, doc.RootElement.GetProperty("status").GetInt32());
        var detail = doc.RootElement.GetProperty("detail").GetString()!;
        // Phase 4 mapper extracts the FK column. Accept any of: step_id, entry_step_id, or the full constraint name.
        Assert.True(
            detail.Contains("step_id", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("fk_workflow_entry_steps_step_id", StringComparison.OrdinalIgnoreCase),
            $"Expected detail to mention step_id or fk_workflow_entry_steps_step_id; got: {detail}");
        Assert.True(doc.RootElement.TryGetProperty("correlationId", out _));
    }

    // SC#5 (DELETE Restrict half) — DELETE Step that a Workflow references → 23503 → 422 (FK Restrict).
    [Fact]
    public async Task Delete_Step_Referenced_By_Workflow_Returns422()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // 1. Create a Processor (FK target for Step)
        var procDto = new ProcessorCreateDto(
            Name: $"sc5-proc-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            SourceHash: RandomSha256Hex(),
            InputSchemaId: null,
            OutputSchemaId: null);
        var procResp = await client.PostAsJsonAsync("/api/v1/processors", procDto, ct);
        procResp.EnsureSuccessStatusCode();
        var proc = await procResp.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);

        // 2. Create a Step
        var stepDto = new StepCreateDto(
            Name: $"sc5-step-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            ProcessorId: proc!.Id,
            NextStepIds: null,
            EntryCondition: StepEntryCondition.PreviousCompleted);
        var stepResp = await client.PostAsJsonAsync("/api/v1/steps", stepDto, ct);
        stepResp.EnsureSuccessStatusCode();
        var step = await stepResp.Content.ReadFromJsonAsync<StepReadDto>(cancellationToken: ct);

        // 3. Create a Workflow referencing the Step via EntryStepIds
        var wfDto = new WorkflowCreateDto(
            Name: $"sc5-wf-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            EntryStepIds: new List<Guid> { step!.Id },
            AssignmentIds: null,
            CronExpression: null);
        var wfResp = await client.PostAsJsonAsync("/api/v1/workflows", wfDto, ct);
        wfResp.EnsureSuccessStatusCode();

        // 4. Attempt DELETE Step — should be 422 (FK Restrict on workflow_entry_steps.step_id)
        var deleteResp = await client.DeleteAsync($"/api/v1/steps/{step.Id}", ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, deleteResp.StatusCode);

        var body = await deleteResp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(422, doc.RootElement.GetProperty("status").GetInt32());
        Assert.True(doc.RootElement.TryGetProperty("correlationId", out _));
    }

    // SC#3 — Invalid JSON Schema definition → 400 + SSRF $ref blocked (no outbound HTTP).
    [Fact]
    public async Task Create_Schema_Invalid_JsonSchema_Returns400_NoOutboundCall()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // Payload combines TWO probes in one definition:
        //   1. INVALID meta-schema content: `"type": "not-a-real-type"` violates Draft 2020-12
        //      (the `type` keyword's value must be one of the 7 JSON Schema primitive types or
        //      an array thereof — see meta-schema rule, JsonSchema.Net 9.2.1 verified). This
        //      guarantees the validator's `MetaSchemas.Draft202012.Evaluate(...).IsValid` returns
        //      `false` and the validator surfaces a field-level "Definition" error (HTTP 400).
        //   2. SSRF probe: `"$ref": "https://attacker.example/schema.json"` is the same external
        //      reference the plan body specified — if `SchemaRegistry.Global.Fetch` were not
        //      no-op (VALID-09 defense-in-depth), the meta-schema evaluator would attempt to
        //      resolve the $ref during evaluation and incur a TCP connect to attacker.example
        //      (which takes seconds to time out). The <500ms timing-bound assertion below
        //      proves no outbound HTTP happened.
        // Note: the bare `{"$ref":"https://attacker.example/schema.json"}` payload from the
        // plan body IS itself a structurally-valid Draft 2020-12 schema — Rule 1 fix-forward
        // (plan internal inconsistency: assumed-invalid payload is in fact valid against the
        // meta-schema; combined-invalid payload below satisfies BOTH the 400 and SSRF intents).
        var dto = new SchemaCreateDto(
            Name: $"ssrf-probe-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            Definition: "{\"type\":\"not-a-real-type\",\"$ref\":\"https://attacker.example/schema.json\"}");

        var sw = Stopwatch.StartNew();
        var resp = await client.PostAsJsonAsync("/api/v1/schemas", dto, ct);
        sw.Stop();

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(400, doc.RootElement.GetProperty("status").GetInt32());

        // VALID-08 + VALID-09 — the schema is "valid JSON" but not a valid Draft 2020-12 schema
        // (the `type` keyword has an illegal value AND the $ref points to an unresolvable remote
        // URL because Fetch is no-op). The validator reports a field-level error. The errors
        // map MUST mention the Definition field.
        Assert.True(doc.RootElement.TryGetProperty("errors", out var errors));
        Assert.True(
            errors.EnumerateObject().Any(p =>
                string.Equals(p.Name, "Definition", StringComparison.OrdinalIgnoreCase)),
            "Expected field-level error on 'Definition'.");

        // SSRF assertion (RESEARCH §SSRF test seam line 251): test completes in <500ms — a real
        // outbound HTTP would take seconds to time out against attacker.example.
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"Schema validation took {sw.ElapsedMilliseconds}ms — expected <500ms. Likely a network call leaked through.");

        Assert.True(doc.RootElement.TryGetProperty("correlationId", out _));
    }
}
