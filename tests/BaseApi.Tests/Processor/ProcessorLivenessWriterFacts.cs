using BaseApi.Tests.Composition;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Liveness;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Write-behavior facts for the shared <see cref="ProcessorLivenessWriter"/> (Phase 60, LOOP-03/04)
/// against the real localhost:6380 Redis (<see cref="RedisFixture"/>). Verifies the single write path:
/// L2 SET(perInstance, derived TTL) + idempotent index SADD + L1 Update, plus log-and-continue resilience.
/// <list type="bullet">
///   <item><b>Startup entry (interval 30):</b> per-instance key TTL band (55, 60], <c>Status == Unhealthy</c>.</item>
///   <item><b>Heartbeat entry (interval 10):</b> per-instance key TTL band (25, 30], <c>Status == Healthy</c>.</item>
///   <item><b>SADD idempotency (D-15):</b> a second WriteAsync keeps the index member count at 1.</item>
///   <item><b>L1 == L2 (D-09):</b> the holder's <c>.Current</c> is the SAME entry written to L2.</item>
///   <item><b>Resilience (D-11):</b> a dead/stubbed Redis does NOT throw and still Updates L1.</item>
/// </list>
/// Each test uses a fresh <c>Guid.NewGuid()</c> processorId so its per-instance key AND its index SET key
/// are unique (no shared-SET contention); both keys are tracked for net-zero teardown — deleting the whole
/// index SET key removes the SADD'd member (D-23 known-key cleanup).
/// </summary>
[Trait("Phase", "60")]
public sealed class ProcessorLivenessWriterFacts : IClassFixture<RedisFixture>
{
    private readonly RedisFixture _redis;

    public ProcessorLivenessWriterFacts(RedisFixture redis) => _redis = redis;

    private ProcessorLivenessWriter NewWriter(out ProcessorLivenessState l1) =>
        NewWriter(_redis.Multiplexer, out l1);

    private static ProcessorLivenessWriter NewWriter(IConnectionMultiplexer redis, out ProcessorLivenessState l1)
    {
        l1 = new ProcessorLivenessState();
        return new ProcessorLivenessWriter(
            redis,
            l1,
            Options.Create(new ProcessorLivenessOptions()),
            NullLogger<ProcessorLivenessWriter>.Instance);
    }

    [Fact]
    public async Task Startup_Entry_Writes_Unhealthy_With_60s_Ttl()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        const string instanceId = "pod-x";
        _redis.Track(L2ProjectionKeys.PerInstance(id, instanceId)); // net-zero teardown (D-23)
        _redis.Track(L2ProjectionKeys.InstanceIndex(id));           // deleting the SET key removes the SADD'd member
        var db = _redis.Multiplexer.GetDatabase();

        var writer = NewWriter(out _);

        // Startup: one schema FAIL => Unhealthy; active interval 30 => TTL = max(60, 30) = 60.
        var entry = ProcessorLivenessEntry.Create(
            SchemaOutcome.Fail, SchemaOutcome.Success, SchemaOutcome.Success, DateTime.UtcNow, interval: 30);

        await writer.WriteAsync(id, instanceId, entry);

        var raw = await db.StringGetAsync(L2ProjectionKeys.PerInstance(id, instanceId));
        var stored = System.Text.Json.JsonSerializer.Deserialize<ProcessorLivenessEntry>(raw!);
        Assert.NotNull(stored);
        Assert.Equal(LivenessStatus.Unhealthy, stored!.Status);
        Assert.Equal(30, stored.Interval);

        var remaining = await db.KeyTimeToLiveAsync(L2ProjectionKeys.PerInstance(id, instanceId));
        Assert.NotNull(remaining);
        Assert.InRange(remaining!.Value.TotalSeconds, 55, 60); // max(30*2, 30) = 60
    }

    [Fact]
    public async Task Heartbeat_Entry_Writes_Healthy_With_30s_Ttl()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        const string instanceId = "pod-x";
        _redis.Track(L2ProjectionKeys.PerInstance(id, instanceId)); // net-zero teardown (D-23)
        _redis.Track(L2ProjectionKeys.InstanceIndex(id));
        var db = _redis.Multiplexer.GetDatabase();

        var writer = NewWriter(out _);

        // Heartbeat: all SUCCESS => Healthy; active interval 10 => TTL = max(20, 30) = 30 (Ttl-floor wins).
        var entry = ProcessorLivenessEntry.Create(
            SchemaOutcome.Success, SchemaOutcome.Success, SchemaOutcome.Success, DateTime.UtcNow, interval: 10);

        await writer.WriteAsync(id, instanceId, entry);

        var raw = await db.StringGetAsync(L2ProjectionKeys.PerInstance(id, instanceId));
        var stored = System.Text.Json.JsonSerializer.Deserialize<ProcessorLivenessEntry>(raw!);
        Assert.NotNull(stored);
        Assert.Equal(LivenessStatus.Healthy, stored!.Status);
        Assert.Equal(10, stored.Interval);

        var remaining = await db.KeyTimeToLiveAsync(L2ProjectionKeys.PerInstance(id, instanceId));
        Assert.NotNull(remaining);
        Assert.InRange(remaining!.Value.TotalSeconds, 25, 30); // max(10*2, 30) = 30 floor
    }

    [Fact]
    public async Task Index_Sadd_Is_Idempotent_Across_Two_Writes()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        const string instanceId = "pod-x";
        _redis.Track(L2ProjectionKeys.PerInstance(id, instanceId));
        _redis.Track(L2ProjectionKeys.InstanceIndex(id)); // net-zero: drop the whole index SET (and its member)
        var db = _redis.Multiplexer.GetDatabase();

        var writer = NewWriter(out _);
        var entry = ProcessorLivenessEntry.Create(
            SchemaOutcome.Success, SchemaOutcome.Success, SchemaOutcome.Success, DateTime.UtcNow, interval: 10);

        await writer.WriteAsync(id, instanceId, entry);

        // First write registers the member in the index SET.
        var members = await db.SetMembersAsync(L2ProjectionKeys.InstanceIndex(id));
        Assert.Contains(instanceId, members.Select(m => (string)m!));

        // A SECOND WriteAsync (D-15 idempotent SADD) keeps the member count at 1.
        await writer.WriteAsync(id, instanceId, entry);
        Assert.Equal(1, await db.SetLengthAsync(L2ProjectionKeys.InstanceIndex(id)));
    }

    [Fact]
    public async Task L1_Holds_Same_Entry_Written_To_L2()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        const string instanceId = "pod-x";
        _redis.Track(L2ProjectionKeys.PerInstance(id, instanceId));
        _redis.Track(L2ProjectionKeys.InstanceIndex(id));

        var writer = NewWriter(out var l1);
        var entry = ProcessorLivenessEntry.Create(
            SchemaOutcome.Success, SchemaOutcome.Success, SchemaOutcome.Success, DateTime.UtcNow, interval: 10);

        await writer.WriteAsync(id, instanceId, entry);

        // D-09: the L1 record IS the SAME immutable reference written to L2 this iteration.
        Assert.Same(entry, l1.Current);
    }

    [Fact]
    public async Task Dead_Redis_Does_Not_Throw_And_Still_Updates_L1()
    {
        // Stubbed multiplexer whose GetDatabase() throws — simulates a dead/unreachable Redis.
        var deadRedis = Substitute.For<IConnectionMultiplexer>();
        deadRedis.GetDatabase(Arg.Any<int>(), Arg.Any<object>())
            .Returns(_ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "dead"));

        var writer = NewWriter(deadRedis, out var l1);
        var entry = ProcessorLivenessEntry.Create(
            SchemaOutcome.Fail, SchemaOutcome.Success, SchemaOutcome.Success, DateTime.UtcNow, interval: 30);

        // Resilience (D-11): WriteAsync logs-and-continues — never throws.
        await writer.WriteAsync(Guid.NewGuid(), "pod-x", entry);

        // L1 is updated UNCONDITIONALLY (Open Q3) — independent of Redis reachability.
        Assert.Same(entry, l1.Current);
    }
}
