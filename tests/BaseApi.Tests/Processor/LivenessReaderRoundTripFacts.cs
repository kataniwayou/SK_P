using System.Text.Json;
using BaseApi.Service.Features.Orchestration;
using BaseApi.Service.Features.Orchestration.Validation;
using BaseApi.Service.Features.Processor;
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
/// THE success test of Phase 26 (LIVE-05 closed loop + LIVE-03). Drives ONE Healthy heartbeat write to
/// the real localhost:6380 Redis, then proves the written value:
/// <list type="number">
///   <item>deserializes via <c>JsonSerializer.Deserialize&lt;ProcessorProjection&gt;</c> with
///     <c>Liveness.Status == LivenessStatus.Healthy</c> and <c>Liveness.Interval == IntervalSeconds</c>
///     (LIVE-03 — seconds, NOT milliseconds);</item>
///   <item>passes the REAL, UNCHANGED <see cref="ProcessorLivenessValidator"/> as LIVE when
///     <c>now &lt; timestamp + interval*2</c>;</item>
///   <item>ages to STALE (the validator throws <c>OrchestrationValidationException</c> reason "stale")
///     once the clock advances PAST <c>timestamp + interval*2</c> — the writer↔reader interval-seconds
///     math holds (LIVE-05).</item>
/// </list>
/// The <c>skp:{testProcessorId}</c> key is tracked for net-zero teardown (D-23). BaseApi.Tests has
/// InternalsVisibleTo so the internal validator + snapshot are constructable directly.
/// </summary>
public sealed class LivenessReaderRoundTripFacts : IClassFixture<RedisFixture>
{
    private readonly RedisFixture _redis;

    public LivenessReaderRoundTripFacts(RedisFixture redis) => _redis = redis;

    /// <summary>Builds a minimal one-processor snapshot the validator can iterate (it reads only Id).</summary>
    private static WorkflowGraphSnapshot SnapshotWith(Guid processorId)
    {
        var snapshot = new WorkflowGraphSnapshot(NullLogger<WorkflowGraphSnapshot>.Instance);
        snapshot.Processors[processorId] = new ProcessorReadDto(
            Id: processorId,
            Name: "round-trip-proc",
            Version: "1.0.0",
            Description: null,
            SourceHash: new string('a', 64),
            InputSchemaId: null,
            OutputSchemaId: null,
            ConfigSchemaId: null,
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow,
            CreatedBy: null,
            UpdatedBy: null);
        return snapshot;
    }

    [Fact]
    public async Task WrittenValue_Deserializes_And_Reader_Sees_Live_Then_Stale()
    {
        var ct = TestContext.Current.CancellationToken;
        var testProcessorId = Guid.NewGuid();
        var key = L2ProjectionKeys.Processor(testProcessorId);
        _redis.Track(L2ProjectionKeys.Processor(testProcessorId)); // net-zero teardown (D-23)
        var db = _redis.Multiplexer.GetDatabase();

        const int interval = 5;

        // ---- Drive ONE Healthy heartbeat write at a FIXED writer clock instant ----
        var writeInstant = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var writerClock = new FakeTimeProvider(writeInstant);
        var context = new FakeProcessorContext
        {
            IsHealthy = true,
            Id = testProcessorId,
            InputDefinition = "in-def",
            OutputDefinition = "out-def",
        };
        var options = Microsoft.Extensions.Options.Options.Create(new ProcessorLivenessOptions
        {
            IntervalSeconds = interval,
            TtlSeconds = 30,
            RequestTimeoutSeconds = 8,
            BackoffCapSeconds = 30,
        });

        var heartbeat = new ProcessorLivenessHeartbeat(
            _redis.Multiplexer, context, options, writerClock,
            NullLogger<ProcessorLivenessHeartbeat>.Instance);

        await heartbeat.StartAsync(ct);
        await Task.Delay(50, ct); // first iteration writes immediately
        await heartbeat.StopAsync(ct);

        // ---- (1) Deserialize the raw value via the frozen record (LIVE-03) ----
        var raw = await db.StringGetAsync(key);
        Assert.False(raw.IsNullOrEmpty);
        var projection = JsonSerializer.Deserialize<ProcessorProjection>(raw!);
        Assert.NotNull(projection);
        Assert.Equal(LivenessStatus.Healthy, projection!.Liveness.Status);
        Assert.Equal(interval, projection.Liveness.Interval);                 // LIVE-03 — seconds, not ms
        Assert.Equal(writeInstant.UtcDateTime, projection.Liveness.Timestamp); // fresh writer-clock stamp

        var written = projection.Liveness.Timestamp;
        var snapshot = SnapshotWith(testProcessorId);

        // ---- (2) Reader sees LIVE: now < timestamp + interval*2 ----
        var liveClock = new FakeTimeProvider(new DateTimeOffset(written.AddSeconds(interval), TimeSpan.Zero));
        var liveValidator = new ProcessorLivenessValidator(_redis.Multiplexer, liveClock);
        await liveValidator.ValidateAsync(snapshot, ct); // MUST NOT throw

        // ---- (3) Reader sees STALE: advance PAST timestamp + interval*2 ----
        var staleClock = new FakeTimeProvider(
            new DateTimeOffset(written.AddSeconds(interval * 2 + 1), TimeSpan.Zero));
        var staleValidator = new ProcessorLivenessValidator(_redis.Multiplexer, staleClock);
        var ex = await Assert.ThrowsAsync<OrchestrationValidationException>(
            () => staleValidator.ValidateAsync(snapshot, ct));
        Assert.Equal("processorLiveness", ex.Gate);
        Assert.Equal("stale", ((ProcessorLivenessOffending)ex.Offending).reason);
    }
}
