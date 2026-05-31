using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BaseApi.Service.Features.Orchestration.Projection;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using BaseApi.Tests.Composition;
using BaseApi.Tests.TestHelpers;
using Messaging.Contracts.Projections;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Features.Orchestration;

/// <summary>
/// Phase 22 PROC-LIVE-01 acceptance facts for the processor-liveness gate
/// (<see cref="BaseApi.Service.Features.Orchestration.Validation.ProcessorLivenessValidator"/>) reached
/// via <c>POST /api/v1/orchestration/start</c>. Mirrors <see cref="SchemaEdgeFacts"/> (HTTP-seed
/// Schema→Processor→Step→Workflow on the <see cref="HarnessWebAppFactory"/>) combined with the
/// direct-L2-seed pattern from <see cref="StopCleanupFacts"/>: each participating processor's
/// self-registered entry (<c>skp:{procId}</c>) is seeded directly with a strongly-typed
/// <see cref="ProcessorProjection"/> to simulate external self-registration.
/// <para>
/// <b>3 facts:</b>
/// <list type="number">
///   <item><c>AllProcessorsLive_Returns204</c> — every processor seeded with
///     <c>LivenessProjection(now, 300, "Live")</c> (interval SECONDS; now + 300*2 &gt; now) → 204.</item>
///   <item><c>AbsentProcessor_Returns422</c> — one processor's key NOT seeded → 422
///     <c>errors.gate=="processorLiveness"</c>, <c>errors.offending.reason=="absent"</c>,
///     <c>offending.procId</c> == the unseeded id.</item>
///   <item><c>StaleProcessor_Returns422</c> — processor seeded with
///     <c>LivenessProjection(now-1d, 0, "Live")</c> (deadline &lt;= now) → 422 reason=="stale".</item>
/// </list>
/// Liveness unit is SECONDS (LOCKED Plan 04 / D-16): deadline = timestamp + interval*2. Each fact
/// tracks the skp:{procId} keys it seeds for known-key cleanup (D-23); a passing /start (the 204 fact)
/// SADDs the parent index, so this class joins the non-parallel <c>ParentIndex</c> collection and SREMs
/// its own wf id (T-22-15).
/// </para>
/// </summary>
[Trait("Phase", "22")]
[Collection("ParentIndex")]
public sealed class ProcessorLivenessFacts : IClassFixture<HarnessWebAppFactory>
{
    private readonly HarnessWebAppFactory _factory;

    public ProcessorLivenessFacts(HarnessWebAppFactory factory) => _factory = factory;

    // ---- HTTP seed helpers (no schemas → cycle/schemaEdge/payload gates all pass) ----

    private static async Task<Guid> SeedProcessorAsync(HttpClient client, CancellationToken ct)
    {
        var dto = new ProcessorCreateDto(
            Name: $"live-proc-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            SourceHash: HashHelpers.RandomSha256Hex(),
            InputSchemaId: null,
            OutputSchemaId: null,
            ConfigSchemaId: null);
        var resp = await client.PostAsJsonAsync("/api/v1/processors", dto, ct);
        resp.EnsureSuccessStatusCode();
        var read = await resp.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);
        return read!.Id;
    }

    private static async Task<Guid> SeedStepAsync(HttpClient client, Guid processorId, List<Guid>? nextStepIds, CancellationToken ct)
    {
        var dto = new StepCreateDto(
            Name: $"live-step-{Guid.NewGuid():N}",
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

    private static async Task<Guid> SeedWorkflowAsync(HttpClient client, List<Guid> entryStepIds, CancellationToken ct)
    {
        var dto = new WorkflowCreateDto(
            Name: $"live-wf-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            EntryStepIds: entryStepIds,
            AssignmentIds: null,
            CronExpression: null);
        var resp = await client.PostAsJsonAsync("/api/v1/workflows", dto, ct);
        resp.EnsureSuccessStatusCode();
        var read = await resp.Content.ReadFromJsonAsync<WorkflowReadDto>(cancellationToken: ct);
        return read!.Id;
    }

    /// <summary>
    /// Seeds a processor's self-registered L2 entry directly (skp:{procId}) by serializing the RECORD
    /// (NOT hand-written JSON) so camelCase member names stay correct. Tracks the key for cleanup.
    /// </summary>
    private async Task SeedLivenessAsync(IDatabase db, Guid procId, LivenessProjection liveness, CancellationToken ct)
    {
        var projection = new ProcessorProjection(null, null, liveness);
        await db.StringSetAsync(L2ProjectionKeys.Processor(procId), JsonSerializer.Serialize(projection));
        _factory.TrackRedisKey(L2ProjectionKeys.Processor(procId));
    }

    /// <summary>SREMs the wf id from the shared parent index (a passing /start SADDs it).</summary>
    private async Task SremWorkflowAsync(IDatabase db, Guid wfId)
        => await db.SetRemoveAsync(RedisProjectionKeys.ParentIndex(), wfId.ToString("D"));

    // ----------------------------- 204: all participating processors live -----------------------------

    [Fact]
    public async Task AllProcessorsLive_Returns204()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var db = _factory.RedisMultiplexer.GetDatabase();

        // Two processors → two steps (parent → child terminal), single workflow.
        var procA = await SeedProcessorAsync(client, ct);
        var procB = await SeedProcessorAsync(client, ct);
        var childStep = await SeedStepAsync(client, procB, nextStepIds: null, ct);
        var parentStep = await SeedStepAsync(client, procA, nextStepIds: new List<Guid> { childStep }, ct);
        var wfId = await SeedWorkflowAsync(client, new List<Guid> { parentStep }, ct);

        // Seed BOTH processors live: interval SECONDS, now + 300*2 > now.
        await SeedLivenessAsync(db, procA, new LivenessProjection(DateTime.UtcNow, 300, "Live"), ct);
        await SeedLivenessAsync(db, procB, new LivenessProjection(DateTime.UtcNow, 300, "Live"), ct);

        // The root/step keys a successful Start writes also need cleaning up.
        _factory.TrackRedisKey(L2ProjectionKeys.Root(wfId));
        _factory.TrackRedisKey(L2ProjectionKeys.Step(wfId, parentStep));
        _factory.TrackRedisKey(L2ProjectionKeys.Step(wfId, childStep));

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
            await SremWorkflowAsync(db, wfId);
        }
    }

    // ----------------------------- 422: one processor absent -----------------------------

    [Fact]
    public async Task AbsentProcessor_Returns422()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var db = _factory.RedisMultiplexer.GetDatabase();

        var liveProc = await SeedProcessorAsync(client, ct);
        var absentProc = await SeedProcessorAsync(client, ct);
        var childStep = await SeedStepAsync(client, absentProc, nextStepIds: null, ct);
        var parentStep = await SeedStepAsync(client, liveProc, nextStepIds: new List<Guid> { childStep }, ct);
        var wfId = await SeedWorkflowAsync(client, new List<Guid> { parentStep }, ct);

        // Seed ONLY the live processor; absentProc's skp:{id} key is never written → gate fires "absent".
        await SeedLivenessAsync(db, liveProc, new LivenessProjection(DateTime.UtcNow, 300, "Live"), ct);

        try
        {
            var resp = await client.PostAsJsonAsync(
                "/api/v1/orchestration/start",
                new List<Guid> { wfId },
                ct);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var errors = doc.RootElement.GetProperty("errors");
            Assert.Equal("processorLiveness", errors.GetProperty("gate").GetString());
            var offending = errors.GetProperty("offending");
            Assert.Equal("absent", offending.GetProperty("reason").GetString());
            Assert.Equal(absentProc.ToString(), offending.GetProperty("procId").GetString());
        }
        finally
        {
            // Defensive — a 422 throws before UpsertAsync (no SADD), but SREM is idempotent.
            await SremWorkflowAsync(db, wfId);
        }
    }

    // ----------------------------- 422: malformed external registration (WR-01) -----------------------------

    /// <summary>
    /// WR-01 regression: an EXTERNAL self-registrant may write a well-formed JSON object whose
    /// <c>liveness</c> member is null (<c>{"liveness":null}</c>), or non-JSON garbage. System.Text.Json
    /// does NOT enforce the non-nullable <see cref="ProcessorProjection.Liveness"/> annotation at runtime,
    /// so before the fix <c>liveness.Timestamp</c> NRE'd (and bad JSON JsonException'd) — neither a
    /// <c>RedisException</c>, so both escaped the gate as a 500. The validator now maps both malformed
    /// shapes to the same 422 processor-liveness gate with <c>reason=="malformed"</c>. This fact seeds the
    /// raw bytes directly (NOT the record) so the malformed shape reaches the validator verbatim.
    /// </summary>
    [Theory]
    [InlineData("{\"inputDefinition\":null,\"outputDefinition\":null,\"liveness\":null}")]
    [InlineData("not-json-at-all")]
    public async Task MalformedProcessorRegistration_Returns422(string rawEntry)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var db = _factory.RedisMultiplexer.GetDatabase();

        var procId = await SeedProcessorAsync(client, ct);
        var stepId = await SeedStepAsync(client, procId, nextStepIds: null, ct);
        var wfId = await SeedWorkflowAsync(client, new List<Guid> { stepId }, ct);

        // Seed the malformed entry directly (raw bytes — bypasses the record serializer) and track it.
        await db.StringSetAsync(L2ProjectionKeys.Processor(procId), rawEntry);
        _factory.TrackRedisKey(L2ProjectionKeys.Processor(procId));

        try
        {
            var resp = await client.PostAsJsonAsync(
                "/api/v1/orchestration/start",
                new List<Guid> { wfId },
                ct);

            // MUST be 422 (processorLiveness gate), NOT 500 (fallback handler).
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var errors = doc.RootElement.GetProperty("errors");
            Assert.Equal("processorLiveness", errors.GetProperty("gate").GetString());
            var offending = errors.GetProperty("offending");
            Assert.Equal("malformed", offending.GetProperty("reason").GetString());
            Assert.Equal(procId.ToString(), offending.GetProperty("procId").GetString());
        }
        finally
        {
            // Defensive — a 422 throws before UpsertAsync (no SADD), but SREM is idempotent.
            await SremWorkflowAsync(db, wfId);
        }
    }

    // ----------------------------- 422: processor stale -----------------------------

    [Fact]
    public async Task StaleProcessor_Returns422()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var db = _factory.RedisMultiplexer.GetDatabase();

        var procId = await SeedProcessorAsync(client, ct);
        var stepId = await SeedStepAsync(client, procId, nextStepIds: null, ct);
        var wfId = await SeedWorkflowAsync(client, new List<Guid> { stepId }, ct);

        // Stale: timestamp far in the past + interval=0 → deadline = (now-1d) + 0 <= now → "stale".
        await SeedLivenessAsync(db, procId, new LivenessProjection(DateTime.UtcNow.AddDays(-1), 0, "Live"), ct);

        try
        {
            var resp = await client.PostAsJsonAsync(
                "/api/v1/orchestration/start",
                new List<Guid> { wfId },
                ct);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var errors = doc.RootElement.GetProperty("errors");
            Assert.Equal("processorLiveness", errors.GetProperty("gate").GetString());
            Assert.Equal("stale", errors.GetProperty("offending").GetProperty("reason").GetString());
        }
        finally
        {
            await SremWorkflowAsync(db, wfId);
        }
    }
}
