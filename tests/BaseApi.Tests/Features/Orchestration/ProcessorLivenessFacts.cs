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
/// Processor-liveness gate acceptance facts (GATE-01/02/03, Phase 61, re-pointed from Phase 22) for the
/// per-replica gate (<see cref="BaseApi.Service.Features.Orchestration.Validation.ProcessorLivenessValidator"/>)
/// reached via <c>POST /api/v1/orchestration/start</c>. Mirrors <see cref="SchemaEdgeFacts"/> (HTTP-seed
/// Schema→Processor→Step→Workflow on the <see cref="HarnessWebAppFactory"/>): each participating processor's
/// self-registered liveness is seeded directly into the per-replica keyspace — SADD the instance index
/// (<c>skp:proc:{procId}</c>) + SET each per-instance <see cref="ProcessorLivenessEntry"/>
/// (<c>skp:proc:{procId}:{instanceId}</c>) — to simulate external self-registration. The legacy flat
/// <c>skp:{procId}</c>/<c>ProcessorProjection</c> seed was retired with the contract (D-11).
/// <para>
/// <b>Facts:</b>
/// <list type="number">
///   <item><c>AllProcessorsLive_Returns204</c> — every processor seeded with ONE healthy+fresh replica
///     (interval SECONDS; now + 300*2 &gt; now) → 204.</item>
///   <item><c>AbsentProcessor_Returns422</c> — one processor has an EMPTY index (zero replicas) → 422
///     <c>errors.gate=="processorLiveness"</c>, <c>errors.offending.reason</c> starts "no healthy replica",
///     <c>offending.procId</c> == the absent id.</item>
///   <item><c>MalformedProcessorRegistration_Returns422</c> — a present-but-malformed per-instance value
///     fails that replica → 422 reason contains "malformed" (WR-01: never a 500).</item>
///   <item><c>StaleProcessor_Returns422</c> — processor seeded with a stale replica
///     (timestamp now-1d, interval 0; deadline &lt;= now) → 422 reason contains "stale".</item>
/// </list>
/// Freshness math is SECONDS: deadline = timestamp + interval*2. Each fact tracks the per-instance + index
/// keys it seeds for known-key cleanup (D-23); a passing /start (the 204 fact) SADDs the parent index, so
/// this class joins the non-parallel <c>ParentIndex</c> collection and SREMs its own wf id (T-22-15).
/// </para>
/// </summary>
[Trait("Phase", "22")]
[Trait("Phase", "61")]
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
    /// Seeds ONE per-replica liveness entry directly into the per-instance keyspace (GATE-01/02/03, D-06):
    /// SADD the instance index (<c>skp:proc:{procId}</c>) + SET the per-instance <see cref="ProcessorLivenessEntry"/>
    /// (<c>skp:proc:{procId}:{instanceId}</c>), serializing via the sanctioned <c>Create(...)</c> factory.
    /// Tracks both keys for known-key cleanup (D-23). <paramref name="ts"/>/<paramref name="interval"/> drive
    /// the freshness window (deadline = ts + interval*2): fresh => Healthy admit; stale => 422.
    /// </summary>
    private async Task SeedLivenessAsync(IDatabase db, Guid procId, string instanceId, DateTime ts, int interval, CancellationToken ct)
    {
        await db.SetAddAsync(L2ProjectionKeys.InstanceIndex(procId), instanceId);
        await db.StringSetAsync(
            L2ProjectionKeys.PerInstance(procId, instanceId),
            JsonSerializer.Serialize(ProcessorLivenessEntry.Create(null, null, null, ts, interval)));
        _factory.TrackRedisKey(L2ProjectionKeys.InstanceIndex(procId));
        _factory.TrackRedisKey(L2ProjectionKeys.PerInstance(procId, instanceId));
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

        // Seed BOTH processors with ONE healthy+fresh replica: interval SECONDS, now + 300*2 > now.
        await SeedLivenessAsync(db, procA, "pod-a-1", DateTime.UtcNow, 300, ct);
        await SeedLivenessAsync(db, procB, "pod-b-1", DateTime.UtcNow, 300, ct);

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

        // Seed ONLY the live processor; absentProc's index is never written → empty index → zero replicas
        // → gate fires the aggregate "no healthy replica" reason for absentProc.
        await SeedLivenessAsync(db, liveProc, "pod-live-1", DateTime.UtcNow, 300, ct);

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
            Assert.StartsWith("no healthy replica", offending.GetProperty("reason").GetString());
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
    /// WR-01 regression (T-61-01): an EXTERNAL self-registrant may write a present-but-malformed per-instance
    /// value — a well-formed JSON object with a null <c>summary</c> (<c>{"summary":null}</c>), or non-JSON
    /// garbage. System.Text.Json does NOT enforce the non-nullable <see cref="ProcessorLivenessEntry.Summary"/>
    /// annotation at runtime, so an unguarded gate would NRE (null Summary) or JsonException (bad JSON) — neither
    /// a <c>RedisException</c>, so both would escape as a 500. The per-replica gate counts both as <c>malformed</c>
    /// (fails that replica) → 422, NEVER a 500. This fact SADDs the index + SETs the raw bytes directly so the
    /// malformed shape reaches the validator verbatim.
    /// </summary>
    [Theory]
    [InlineData("{\"timestamp\":\"2026-01-01T00:00:00Z\",\"interval\":300,\"status\":\"Healthy\",\"summary\":null}")]
    [InlineData("not-json-at-all")]
    public async Task MalformedProcessorRegistration_Returns422(string rawEntry)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();
        var db = _factory.RedisMultiplexer.GetDatabase();

        var procId = await SeedProcessorAsync(client, ct);
        var stepId = await SeedStepAsync(client, procId, nextStepIds: null, ct);
        var wfId = await SeedWorkflowAsync(client, new List<Guid> { stepId }, ct);

        // Seed the index + the malformed per-instance value directly (raw bytes — bypasses the record
        // serializer) so the gate discovers the replica, GETs it, and counts it malformed. Track both keys.
        const string instanceId = "pod-bad-1";
        await db.SetAddAsync(L2ProjectionKeys.InstanceIndex(procId), instanceId);
        await db.StringSetAsync(L2ProjectionKeys.PerInstance(procId, instanceId), rawEntry);
        _factory.TrackRedisKey(L2ProjectionKeys.InstanceIndex(procId));
        _factory.TrackRedisKey(L2ProjectionKeys.PerInstance(procId, instanceId));

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
            Assert.Contains("malformed", offending.GetProperty("reason").GetString());
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

        // Stale: timestamp far in the past + interval=0 → deadline = (now-1d) + 0 <= now → the one discovered
        // replica fails "stale", no replica qualifies → 422 aggregate reason counts it stale.
        await SeedLivenessAsync(db, procId, "pod-stale-1", DateTime.UtcNow.AddDays(-1), 0, ct);

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
            var reason = errors.GetProperty("offending").GetProperty("reason").GetString();
            Assert.StartsWith("no healthy replica", reason);
            Assert.Contains("1 stale", reason);
        }
        finally
        {
            await SremWorkflowAsync(db, wfId);
        }
    }
}
