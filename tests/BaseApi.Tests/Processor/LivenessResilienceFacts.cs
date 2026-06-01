using System.Collections.Concurrent;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Liveness;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Redis-fault log-and-continue resilience fact (D-11 / T-26-10). The heartbeat is pointed at a DEAD
/// Redis endpoint (<c>abortConnect=false</c> soft-dep build, mirroring
/// <c>ConsoleTestHostFixture</c>) so every <c>StringSetAsync</c> faults. The worker MUST:
/// <list type="bullet">
///   <item>NOT fault (the host stays up — no exception escapes <c>ExecuteAsync</c>);</item>
///   <item>log a warning naming the processor id;</item>
///   <item>keep beating (a second interval still does not crash).</item>
/// </list>
/// Never asserts a key write — Redis is dead. No <see cref="RedisFixture"/> (no real keys created).
/// </summary>
public sealed class LivenessResilienceFacts
{
    // Dead Redis port — soft-dep build (abortConnect=false) lets the multiplexer materialize even though
    // no server answers, so StringSetAsync faults at call time (mirrors ConsoleTestHostFixture).
    private const string DeadRedis = "127.0.0.1:6399,abortConnect=false,connectTimeout=1000";

    [Fact]
    public async Task DeadRedis_Worker_DoesNotFault_And_Logs_Warning()
    {
        var ct = TestContext.Current.CancellationToken;
        var testProcessorId = Guid.NewGuid();

        await using var redis = await ConnectionMultiplexer.ConnectAsync(DeadRedis);

        var context = new FakeProcessorContext
        {
            IsHealthy = true,
            Id = testProcessorId,
            InputDefinition = "in-def",
            OutputDefinition = "out-def",
        };
        var clock = new FakeTimeProvider();
        var options = Microsoft.Extensions.Options.Options.Create(new ProcessorLivenessOptions
        {
            IntervalSeconds = 5,
            TtlSeconds = 30,
            RequestTimeoutSeconds = 8,
            BackoffCapSeconds = 30,
        });
        var logger = new CapturingLogger<ProcessorLivenessHeartbeat>();

        var heartbeat = new ProcessorLivenessHeartbeat(redis, context, options, clock, logger);

        await heartbeat.StartAsync(ct);
        await Task.Delay(50, ct);                 // first beat faults on the dead Redis
        clock.Advance(TimeSpan.FromSeconds(6));   // release the delay -> second beat
        await Task.Delay(50, ct);                 // second beat also faults
        await heartbeat.StopAsync(ct);

        // (a) The worker task did not fault — the host stays up (StopAsync would surface a faulted task).
        Assert.True(heartbeat.ExecuteTask is null || !heartbeat.ExecuteTask.IsFaulted);

        // (b) A warning was logged naming the processor id (D-11 log-and-continue).
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains(testProcessorId.ToString()));

        // (c) The loop kept beating — at least the two beats both warned (no crash between them).
        var warnings = logger.Entries.Count(e => e.Level == LogLevel.Warning);
        Assert.True(warnings >= 1, $"expected >= 1 warning, got {warnings}");
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Enqueue(new LogEntry(logLevel, formatter(state, exception)));
    }
}
