using BaseApi.Tests.Composition;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Liveness;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Only-when-Healthy write-behavior facts for <see cref="ProcessorLivenessHeartbeat"/>
/// (LIVE-01/02/04/06) against the real localhost:6380 Redis (<see cref="RedisFixture"/>). A
/// <see cref="FakeTimeProvider"/> drives the beat loop without real-time sleeping; the
/// <c>skp:{testProcessorId}</c> key is tracked for net-zero teardown (triple-SHA close-gate
/// discipline — D-23).
/// <list type="bullet">
///   <item><b>Case A (LIVE-04):</b> a not-yet-Healthy context writes NOTHING (the key never exists)
///     so the orchestrator sees it as <c>absent</c>.</item>
///   <item><b>Case B (LIVE-01/02/06):</b> a Healthy context writes a single whole-value key with the
///     configured sliding TTL applied.</item>
/// </list>
/// </summary>
public sealed class LivenessHeartbeatFacts : IClassFixture<RedisFixture>
{
    private readonly RedisFixture _redis;

    public LivenessHeartbeatFacts(RedisFixture redis) => _redis = redis;

    private static IOptions<ProcessorLivenessOptions> Options(int interval, int ttl) =>
        Microsoft.Extensions.Options.Options.Create(new ProcessorLivenessOptions
        {
            IntervalSeconds = interval,
            TtlSeconds = ttl,
            RequestTimeoutSeconds = 8,
            BackoffCapSeconds = 30,
        });

    [Fact]
    public async Task NotHealthy_Writes_No_Key()
    {
        var ct = TestContext.Current.CancellationToken;
        var testProcessorId = Guid.NewGuid();
        var key = L2ProjectionKeys.Processor(testProcessorId);
        _redis.Track(L2ProjectionKeys.Processor(testProcessorId)); // net-zero teardown (D-23)
        var db = _redis.Multiplexer.GetDatabase();

        // Not-yet-Healthy replica: IsHealthy false, no Id. The beat must no-op (LIVE-04).
        var context = new FakeProcessorContext { IsHealthy = false, Id = null };
        var clock = new FakeTimeProvider();

        var heartbeat = new ProcessorLivenessHeartbeat(
            _redis.Multiplexer, context, Options(interval: 5, ttl: 30), clock,
            NullLogger<ProcessorLivenessHeartbeat>.Instance);

        await heartbeat.StartAsync(ct);
        // Advance past one+ interval so the loop runs its no-op tick.
        clock.Advance(TimeSpan.FromSeconds(6));
        await Task.Delay(50, ct);
        await heartbeat.StopAsync(ct);

        // A non-Healthy replica wrote nothing — absent to the reader.
        Assert.False(await db.KeyExistsAsync(key));
    }

    [Fact]
    public async Task Healthy_Writes_WholeValue_With_Sliding_Ttl()
    {
        var ct = TestContext.Current.CancellationToken;
        var testProcessorId = Guid.NewGuid();
        var key = L2ProjectionKeys.Processor(testProcessorId);
        _redis.Track(L2ProjectionKeys.Processor(testProcessorId)); // net-zero teardown (D-23)
        var db = _redis.Multiplexer.GetDatabase();

        var context = new FakeProcessorContext
        {
            IsHealthy = true,
            Id = testProcessorId,
            InputDefinition = "in-def",
            OutputDefinition = "out-def",
        };
        var clock = new FakeTimeProvider();
        const int ttl = 30;

        var heartbeat = new ProcessorLivenessHeartbeat(
            _redis.Multiplexer, context, Options(interval: 5, ttl: ttl), clock,
            NullLogger<ProcessorLivenessHeartbeat>.Instance);

        await heartbeat.StartAsync(ct);
        // Drive one beat: the first iteration writes immediately (before the first Task.Delay).
        await Task.Delay(50, ct);
        await heartbeat.StopAsync(ct);

        // Key exists (LIVE-01) — a single whole-value SET (LIVE-06).
        Assert.True(await db.KeyExistsAsync(key));

        // Sliding TTL applied (LIVE-02) — approximately TtlSeconds.
        var remaining = await db.KeyTimeToLiveAsync(key);
        Assert.NotNull(remaining);
        Assert.InRange(remaining!.Value.TotalSeconds, ttl - 5, ttl);

        // The value is valid JSON deserializing to the frozen record (LIVE-06 — blind whole-value SET).
        var raw = await db.StringGetAsync(key);
        var projection = System.Text.Json.JsonSerializer.Deserialize<ProcessorProjection>(raw!);
        Assert.NotNull(projection);
        Assert.Equal(LivenessStatus.Healthy, projection!.Liveness.Status);
    }
}
