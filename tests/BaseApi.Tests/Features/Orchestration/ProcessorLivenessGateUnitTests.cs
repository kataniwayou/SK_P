using System.Text.Json;
using BaseApi.Service.Features.Orchestration;
using BaseApi.Service.Features.Orchestration.Validation;
using BaseApi.Service.Features.Processor;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Features.Orchestration;

/// <summary>
/// Pure-unit gate facts (GATE-01/02/03, Phase 61) for the per-replica
/// <see cref="ProcessorLivenessValidator"/> — deterministic, NO real Redis. Uses a fresh
/// <c>Substitute.For&lt;IConnectionMultiplexer&gt;()</c>/<c>IDatabase</c> (NOT the Keeper FakeRedis): the
/// gate logic is BaseApi-side and reached via the existing <c>InternalsVisibleTo("BaseApi.Tests")</c>.
/// <para>
/// <b>Pitfall 3 (mandatory stubs):</b> an unstubbed <c>SetMembersAsync</c> returns NSubstitute's default
/// (a null <c>RedisValue[]</c>) which would NRE the gate loop — so EVERY case stubs <c>SetMembersAsync</c>
/// to the seeded index, stubs each <c>StringGetAsync(PerInstance(...))</c> to the serialized entry (or
/// <see cref="RedisValue.Null"/> for absent), and stubs <c>SetRemoveAsync(...,FireAndForget)</c> even when
/// unasserted (else the fire-and-forget call NREs on an unconfigured <see cref="Task"/>).
/// </para>
/// <para>Cases: (1) >=1 healthy+fresh replica admits even with stale/unhealthy/absent siblings; (2) no
/// qualifier throws <see cref="OrchestrationValidationException"/> gate=="processorLiveness" + aggregate
/// "no healthy replica" reason; (3) an absent (GET null) member fires an absent-only lazy SREM exactly once.</para>
/// </summary>
[Trait("Phase", "61")]
public sealed class ProcessorLivenessGateUnitTests
{
    private static readonly Guid Proc = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    // --- harness ---------------------------------------------------------------------------------

    private static (ProcessorLivenessValidator validator, IDatabase db) BuildGate(DateTime now)
    {
        var db = Substitute.For<IDatabase>();
        // Default-stub SetRemoveAsync(FireAndForget) so the absent-path fire-and-forget never NREs (Pitfall 3).
        db.SetRemoveAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));

        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        var clock = new FakeClock(now);
        return (new ProcessorLivenessValidator(mux, clock), db);
    }

    private static void StubIndex(IDatabase db, params string[] members)
        => db.SetMembersAsync(L2ProjectionKeys.InstanceIndex(Proc), Arg.Any<CommandFlags>())
             .Returns(Task.FromResult(members.Select(m => (RedisValue)m).ToArray()));

    private static void StubMember(IDatabase db, string instanceId, RedisValue value)
        => db.StringGetAsync(L2ProjectionKeys.PerInstance(Proc, instanceId), Arg.Any<CommandFlags>())
             .Returns(Task.FromResult(value));

    private static RedisValue Entry(string status, DateTime ts, int interval)
    {
        // Build a wire entry with the given status WITHOUT the Create(...) derivation (which would force
        // status from the summary). The positional ctor is public for exactly this (deserialize/seed) path.
        var summary = new LivenessSummary(SchemaOutcome.Success, SchemaOutcome.Success, SchemaOutcome.Success);
        return JsonSerializer.Serialize(new ProcessorLivenessEntry(ts, interval, status, summary));
    }

    private static WorkflowGraphSnapshot OneProcessor()
    {
        var snapshot = new WorkflowGraphSnapshot(NullLogger<WorkflowGraphSnapshot>.Instance)
        {
            Processors = new Dictionary<Guid, ProcessorReadDto>
            {
                [Proc] = new ProcessorReadDto(
                    Id: Proc, Name: "p", Version: "1.0.0", Description: null,
                    SourceHash: new string('a', 64), InputSchemaId: null, OutputSchemaId: null,
                    ConfigSchemaId: null, CreatedAt: default, UpdatedAt: default, CreatedBy: null, UpdatedBy: null),
            },
        };
        return snapshot;
    }

    // --- GATE-02: >=1 healthy+fresh admits -------------------------------------------------------

    [Fact]
    public async Task OneHealthyFresh_Admits()
    {
        var now = new DateTime(2026, 6, 13, 12, 0, 0, DateTimeKind.Utc);
        var (validator, db) = BuildGate(now);
        StubIndex(db, "inst-a");
        StubMember(db, "inst-a", Entry(LivenessStatus.Healthy, now, 300));

        // No throw == admit.
        await validator.ValidateAsync(OneProcessor(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OneStale_Plus_OneHealthyFresh_Admits_FirstQualifierWins()
    {
        var now = new DateTime(2026, 6, 13, 12, 0, 0, DateTimeKind.Utc);
        var (validator, db) = BuildGate(now);
        StubIndex(db, "inst-stale", "inst-fresh");
        StubMember(db, "inst-stale", Entry(LivenessStatus.Healthy, now.AddDays(-1), 0)); // deadline <= now => stale
        StubMember(db, "inst-fresh", Entry(LivenessStatus.Healthy, now, 300));           // healthy + fresh

        await validator.ValidateAsync(OneProcessor(), TestContext.Current.CancellationToken);
    }

    // --- GATE-03: no qualifier -> 422 aggregate reason -------------------------------------------

    [Fact]
    public async Task AllUnhealthyOrStale_Throws_AggregateReason()
    {
        var now = new DateTime(2026, 6, 13, 12, 0, 0, DateTimeKind.Utc);
        var (validator, db) = BuildGate(now);
        StubIndex(db, "inst-sick", "inst-stale");
        StubMember(db, "inst-sick", Entry(LivenessStatus.Unhealthy, now, 300));        // unhealthy
        StubMember(db, "inst-stale", Entry(LivenessStatus.Healthy, now.AddDays(-1), 0)); // stale

        var ex = await Assert.ThrowsAsync<OrchestrationValidationException>(
            () => validator.ValidateAsync(OneProcessor(), TestContext.Current.CancellationToken));

        Assert.Equal("processorLiveness", ex.Gate);
        var json = JsonSerializer.Serialize(ex.ErrorsExtension);
        using var doc = JsonDocument.Parse(json);
        var offending = doc.RootElement.GetProperty("offending");
        var reason = offending.GetProperty("reason").GetString();
        Assert.StartsWith("no healthy replica", reason);
        Assert.Contains("1 unhealthy", reason);
        Assert.Contains("1 stale", reason);
        Assert.Equal(Proc.ToString(), offending.GetProperty("procId").GetString());
    }

    [Fact]
    public async Task EmptyIndex_ZeroReplicas_Throws()
    {
        var now = new DateTime(2026, 6, 13, 12, 0, 0, DateTimeKind.Utc);
        var (validator, db) = BuildGate(now);
        StubIndex(db); // empty index => zero replicas

        var ex = await Assert.ThrowsAsync<OrchestrationValidationException>(
            () => validator.ValidateAsync(OneProcessor(), TestContext.Current.CancellationToken));
        Assert.Equal("processorLiveness", ex.Gate);
    }

    [Fact]
    public async Task MalformedValue_FailsThatReplica_NeverThrowsJsonException()
    {
        var now = new DateTime(2026, 6, 13, 12, 0, 0, DateTimeKind.Utc);
        var (validator, db) = BuildGate(now);
        StubIndex(db, "inst-bad");
        StubMember(db, "inst-bad", "not-json-at-all"); // JsonException internally => counted malformed

        var ex = await Assert.ThrowsAsync<OrchestrationValidationException>(
            () => validator.ValidateAsync(OneProcessor(), TestContext.Current.CancellationToken));
        Assert.Equal("processorLiveness", ex.Gate);
        var json = JsonSerializer.Serialize(ex.ErrorsExtension);
        using var doc = JsonDocument.Parse(json);
        Assert.Contains("1 malformed", doc.RootElement.GetProperty("offending").GetProperty("reason").GetString());
    }

    // --- GATE-02 (WR-01): exact freshness boundary ----------------------------------------------

    [Fact]
    public async Task ExactBoundary_DeadlineEqualsNow_CountsStale()
    {
        // WR-01: fresh iff deadline > now. At the EXACT boundary (deadline == now) the replica is STALE
        // (deadline <= now). One replica at the boundary => no qualifier => 422 "1 stale" — this pins the
        // gate side of the boundary that the self-watchdog (now >= deadline) must agree with.
        var now = new DateTime(2026, 6, 13, 12, 0, 0, DateTimeKind.Utc);
        var (validator, db) = BuildGate(now);
        StubIndex(db, "inst-edge");
        // Timestamp + Interval*2 == now exactly (300*2 = 600s before now).
        StubMember(db, "inst-edge", Entry(LivenessStatus.Healthy, now.AddSeconds(-600), 300));

        var ex = await Assert.ThrowsAsync<OrchestrationValidationException>(
            () => validator.ValidateAsync(OneProcessor(), TestContext.Current.CancellationToken));
        var json = JsonSerializer.Serialize(ex.ErrorsExtension);
        using var doc = JsonDocument.Parse(json);
        Assert.Contains("1 stale", doc.RootElement.GetProperty("offending").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task OneTickBeforeBoundary_StrictlyFresh_Admits()
    {
        // WR-01: a single tick before the boundary (deadline = now + 1 tick > now) is strictly fresh => admit.
        var now = new DateTime(2026, 6, 13, 12, 0, 0, DateTimeKind.Utc);
        var (validator, db) = BuildGate(now);
        StubIndex(db, "inst-edge");
        StubMember(db, "inst-edge", Entry(LivenessStatus.Healthy, now.AddSeconds(-600).AddTicks(1), 300));

        // No throw == admit (deadline is one tick in the future).
        await validator.ValidateAsync(OneProcessor(), TestContext.Current.CancellationToken);
    }

    // --- GATE-03 (WR-02): transport fault keeps the 422-vs-500 split ------------------------------

    [Fact]
    public async Task RedisFault_On_Get_Propagates_NotSwallowed_As_422()
    {
        // WR-02 guard: the broadened deserialize catch is `when (ex is JsonException or NotSupportedException)`
        // ONLY — a genuine transport RedisException originates on StringGetAsync (OUTSIDE the try) and MUST
        // propagate untouched to the caller's redisOp catch (=> 500), never collapse into the 422 gate.
        var now = new DateTime(2026, 6, 13, 12, 0, 0, DateTimeKind.Utc);
        var (validator, db) = BuildGate(now);
        StubIndex(db, "inst-a");
        db.StringGetAsync(L2ProjectionKeys.PerInstance(Proc, "inst-a"), Arg.Any<CommandFlags>())
          .Returns<Task<RedisValue>>(_ => throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "transport down"));

        // It surfaces as RedisException (→ 500 at the caller), NOT OrchestrationValidationException (422).
        await Assert.ThrowsAsync<RedisConnectionException>(
            () => validator.ValidateAsync(OneProcessor(), TestContext.Current.CancellationToken));
    }

    // --- GATE-03: absent-only lazy SREM ----------------------------------------------------------

    [Fact]
    public async Task OneAbsent_Plus_OneHealthy_Admits_And_SREMs_Absent_Once()
    {
        var now = new DateTime(2026, 6, 13, 12, 0, 0, DateTimeKind.Utc);
        var (validator, db) = BuildGate(now);
        StubIndex(db, "inst-gone", "inst-fresh");
        StubMember(db, "inst-gone", RedisValue.Null);                        // absent (TTL-expired)
        StubMember(db, "inst-fresh", Entry(LivenessStatus.Healthy, now, 300)); // healthy + fresh

        await validator.ValidateAsync(OneProcessor(), TestContext.Current.CancellationToken);

        // D-09: the absent member is lazily SREM'd exactly once, fire-and-forget, from the index.
        await db.Received(1).SetRemoveAsync(
            L2ProjectionKeys.InstanceIndex(Proc), (RedisValue)"inst-gone", CommandFlags.FireAndForget);
        // The present (healthy) member is NEVER pruned.
        await db.DidNotReceive().SetRemoveAsync(
            L2ProjectionKeys.InstanceIndex(Proc), (RedisValue)"inst-fresh", Arg.Any<CommandFlags>());
    }

    /// <summary>A deterministic <see cref="TimeProvider"/> pinned to a fixed UTC instant.</summary>
    private sealed class FakeClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
    }
}
