using BaseApi.Tests.Composition;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Liveness;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Only-when-Healthy write-behavior facts for <see cref="ProcessorLivenessHeartbeat"/>
/// (LIVE-01/02/04/06 / LOOP-02 / D-14) against the real localhost:6380 Redis (<see cref="RedisFixture"/>).
/// The heartbeat now routes through the shared <see cref="ProcessorLivenessWriter"/> (Plan 02), so a beat
/// lands on the NEW per-instance key (<c>skp:proc:{id}:{instanceId}</c>) carrying a frozen-healthy
/// <see cref="ProcessorLivenessEntry"/> (all-SUCCESS => <see cref="LivenessStatus.Healthy"/>, interval 10),
/// plus the index SADD and the L1 Update. A <see cref="FakeTimeProvider"/> drives the beat loop without
/// real-time sleeping; both the per-instance key AND its (per-test-unique) index SET key are tracked for
/// net-zero teardown (D-23) — deleting the whole index SET removes the SADD'd member.
/// <list type="bullet">
///   <item><b>Case A (LIVE-04 / T-60-08):</b> a not-yet-Healthy context writes NOTHING (the PerInstance key
///     never exists) so the gate reader sees it as <c>absent</c>.</item>
///   <item><b>Case B (LIVE-01/02/06 / D-14):</b> a Healthy context writes the per-instance frozen-healthy
///     entry with the derived TTL band (25,30], registers the instanceId in the index SET, mirrors L1, and
///     each subsequent beat refreshes the timestamp.</item>
/// </list>
/// </summary>
[Trait("Phase", "60")]
public sealed class LivenessHeartbeatFacts : IClassFixture<RedisFixture>
{
    private const string InstanceId = "pod-hb";

    private readonly RedisFixture _redis;

    public LivenessHeartbeatFacts(RedisFixture redis) => _redis = redis;

    private static IOptions<ProcessorLivenessOptions> Options(int interval, int ttl) =>
        Microsoft.Extensions.Options.Options.Create(new ProcessorLivenessOptions
        {
            IntervalSeconds = interval,
            StartupIntervalSeconds = 30,
            TtlSeconds = ttl,
            RequestTimeoutSeconds = 8,
            BackoffCapSeconds = 30,
        });

    private ProcessorLivenessHeartbeat NewHeartbeat(
        FakeProcessorContext context,
        IOptions<ProcessorLivenessOptions> options,
        FakeTimeProvider clock,
        out ProcessorLivenessState l1)
    {
        l1 = new ProcessorLivenessState();
        var writer = new ProcessorLivenessWriter(
            _redis.Multiplexer, l1, options, NullLogger<ProcessorLivenessWriter>.Instance);
        return new ProcessorLivenessHeartbeat(
            writer, context, options, clock, InstanceId, NullLogger<ProcessorLivenessHeartbeat>.Instance);
    }

    [Fact]
    public async Task NotHealthy_Writes_No_PerInstance_Key()
    {
        var ct = TestContext.Current.CancellationToken;
        var testProcessorId = Guid.NewGuid();
        var key = L2ProjectionKeys.PerInstance(testProcessorId, InstanceId);
        _redis.Track(key);                                              // net-zero teardown (D-23)
        _redis.Track(L2ProjectionKeys.InstanceIndex(testProcessorId)); // index SET (member-via-key cleanup)
        var db = _redis.Multiplexer.GetDatabase();

        // Not-yet-Healthy replica: IsHealthy false, no Id. The beat must no-op (LIVE-04 / T-60-08).
        var context = new FakeProcessorContext { IsHealthy = false, Id = null };
        var clock = new FakeTimeProvider();

        var heartbeat = NewHeartbeat(context, Options(interval: 5, ttl: 30), clock, out var l1);

        await heartbeat.StartAsync(ct);
        // Advance past one+ interval so the loop runs its no-op tick.
        clock.Advance(TimeSpan.FromSeconds(6));
        await Task.Delay(50, ct);
        await heartbeat.StopAsync(ct);

        // A non-Healthy replica wrote nothing — the PerInstance key is absent to the gate reader.
        Assert.False(await db.KeyExistsAsync(key));
        // The index SET never got a member, and L1 was never updated.
        Assert.False(await db.KeyExistsAsync(L2ProjectionKeys.InstanceIndex(testProcessorId)));
        Assert.Null(l1.Current);
    }

    [Fact]
    public async Task Healthy_Writes_FrozenHealthy_PerInstance_With_Index_And_L1()
    {
        var ct = TestContext.Current.CancellationToken;
        var testProcessorId = Guid.NewGuid();
        var key = L2ProjectionKeys.PerInstance(testProcessorId, InstanceId);
        _redis.Track(key);                                              // net-zero teardown (D-23)
        _redis.Track(L2ProjectionKeys.InstanceIndex(testProcessorId)); // deleting the SET key removes the SADD'd member
        var db = _redis.Multiplexer.GetDatabase();

        var context = new FakeProcessorContext
        {
            IsHealthy = true,
            Id = testProcessorId,
            InputDefinition = "in-def",
            OutputDefinition = "out-def",
        };
        var clock = new FakeTimeProvider();
        // Heartbeat interval 10 => active interval baked into the entry; derived TTL = max(20, 30) = 30 (D-13).
        var heartbeat = NewHeartbeat(context, Options(interval: 10, ttl: 30), clock, out var l1);

        await heartbeat.StartAsync(ct);
        // Drive one beat: the first iteration writes immediately (before the first Task.Delay).
        await Task.Delay(50, ct);

        // Key exists (LIVE-01) — a single whole-value SET (LIVE-06).
        Assert.True(await db.KeyExistsAsync(key));

        // Frozen-healthy ProcessorLivenessEntry: Status Healthy, active interval 10 (D-14 / LOOP-02).
        var raw = await db.StringGetAsync(key);
        var entry = System.Text.Json.JsonSerializer.Deserialize<ProcessorLivenessEntry>(raw!);
        Assert.NotNull(entry);
        Assert.Equal(LivenessStatus.Healthy, entry!.Status);
        Assert.Equal(10, entry.Interval);

        // Derived TTL band (25,30] (D-13 / T-60-09 — max(10*2, 30) = 30 floor wins).
        var remaining = await db.KeyTimeToLiveAsync(key);
        Assert.NotNull(remaining);
        Assert.InRange(remaining!.Value.TotalSeconds, 25, 30);

        // Index SADD (D-15): SMEMBERS InstanceIndex contains the instanceId.
        var members = await db.SetMembersAsync(L2ProjectionKeys.InstanceIndex(testProcessorId));
        Assert.Contains(InstanceId, members.Select(m => (string)m!));

        // L1 mirrors L2 (L1-01 / D-09): the holder's Current IS the written entry.
        Assert.NotNull(l1.Current);
        Assert.Equal(LivenessStatus.Healthy, l1.Current!.Status);
        Assert.Equal(entry.Timestamp, l1.Current.Timestamp);

        // Each subsequent beat refreshes the timestamp (monotonic via FakeTimeProvider) — frozen Healthy.
        // L1 updates synchronously inside WriteAsync (before the network SET), so it advances the instant
        // the next beat runs — a race-free proof the beat fired with a fresher clock read. We advance the
        // fake clock repeatedly (re-arming the loop's Task.Delay even if it had not yet parked when the
        // first Advance ran) until the L1 timestamp moves past the first beat's.
        var firstTimestamp = entry.Timestamp;
        ProcessorLivenessEntry? beat2 = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            clock.Advance(TimeSpan.FromSeconds(10)); // release the Task.Delay -> next beat reads a fresher now
            await Task.Delay(20, ct);
            var current = l1.Current;
            if (current is not null && current.Timestamp > firstTimestamp)
            {
                beat2 = current;
                break;
            }
        }
        await heartbeat.StopAsync(ct);

        Assert.NotNull(beat2);
        Assert.Equal(LivenessStatus.Healthy, beat2!.Status);
        Assert.True(beat2.Timestamp > firstTimestamp, "each healthy beat advances the timestamp");
    }
}
