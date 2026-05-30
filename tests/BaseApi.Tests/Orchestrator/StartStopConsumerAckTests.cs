using System.Collections.Concurrent;
using System.Text.Json;
using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orchestrator.Consumers;
using Orchestrator.Messaging;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// In-memory harness proof (Phase 19 is harness-only — real-broker two-bus fan-out is Phase 20)
/// for the orchestrator consumers' business-ack / infra-throw split + no-Redis-writes + seam log.
/// <list type="bullet">
///   <item>MSG-ACK-01: a WorkflowId absent from L2 → CONSUMED (acked), NOT faulted.</item>
///   <item>MSG-ACK-02: an infra fault (Redis throws) → the consume FAULTS (propagates), not swallowed.</item>
///   <item>ORCH-CON-04: the present-in-L2 case logs the scheduler-job-start seam and writes NOTHING.</item>
/// </list>
/// </summary>
public sealed class StartStopConsumerAckTests
{
    private const string Prefix = "skp:";

    // ----- log capture --------------------------------------------------------------------------

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);
        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => sink.Enqueue(formatter(state, exception));
        }
    }

    // ----- redis stubs --------------------------------------------------------------------------

    /// <summary>A multiplexer whose database returns <see cref="RedisValue.Null"/> for any key (absent-from-L2).</summary>
    private static IConnectionMultiplexer AbsentL2(out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    /// <summary>A multiplexer whose database returns a valid serialized <see cref="WorkflowRootProjection"/>.</summary>
    private static IConnectionMultiplexer PresentL2(out IDatabase db)
    {
        var json = JsonSerializer.Serialize(new WorkflowRootProjection(
            EntryStepIds: [Guid.NewGuid()],
            Cron: null,
            JobId: Guid.NewGuid(),
            Liveness: new LivenessProjection(DateTime.UtcNow, Interval: 30, Status: "active"),
            CorrelationId: Guid.NewGuid().ToString()));
        db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns((RedisValue)json);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    /// <summary>A multiplexer whose StringGetAsync throws a Redis infra fault.</summary>
    private static IConnectionMultiplexer InfraFaultL2()
    {
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Throws(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "boom"));
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    // ----- harness ------------------------------------------------------------------------------

    private static ServiceProvider BuildStartHarness(
        IConnectionMultiplexer mux, CapturingLoggerProvider? logs = null)
        => new ServiceCollection()
            .AddSingleton(mux)
            .AddSingleton(new OrchestratorRedisOptions(Prefix))
            .AddLogging(b => { if (logs is not null) b.AddProvider(logs); })
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<StartOrchestrationConsumer, StartOrchestrationConsumerDefinition>();
                x.UsingInMemory((ctx, cfg) => cfg.ConfigureEndpoints(ctx));
            })
            .BuildServiceProvider(true);

    private static ServiceProvider BuildStopHarness(
        IConnectionMultiplexer mux, CapturingLoggerProvider? logs = null)
        => new ServiceCollection()
            .AddSingleton(mux)
            .AddSingleton(new OrchestratorRedisOptions(Prefix))
            .AddLogging(b => { if (logs is not null) b.AddProvider(logs); })
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<StopOrchestrationConsumer, StopOrchestrationConsumerDefinition>();
                x.UsingInMemory((ctx, cfg) => cfg.ConfigureEndpoints(ctx));
            })
            .BuildServiceProvider(true);

    // ----- Start: MSG-ACK-01 (absent → acked, not faulted) --------------------------------------

    [Fact]
    public async Task Start_Absent_From_L2_Is_Consumed_And_Acked_Not_Faulted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildStartHarness(AbsentL2(out _));
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new StartOrchestration([Guid.NewGuid()]), ct);

            Assert.True(await harness.Consumed.Any<StartOrchestration>(ct));   // consumed → acked
            // No fault: the consume that observed an absent key did NOT throw.
            Assert.False(await harness.Consumed.Any<StartOrchestration>(m => m.Exception != null, ct));
        }
        finally { await harness.Stop(ct); }
    }

    // ----- Start: ORCH-CON-04 (present → seam log, zero writes) ----------------------------------

    [Fact]
    public async Task Start_Present_In_L2_Logs_Seam_And_Writes_Nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var logs = new CapturingLoggerProvider();
        var mux = PresentL2(out var db);
        await using var provider = BuildStartHarness(mux, logs);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new StartOrchestration([Guid.NewGuid()]), ct);

            Assert.True(await harness.Consumed.Any<StartOrchestration>(ct));
            Assert.False(await harness.Consumed.Any<StartOrchestration>(m => m.Exception != null, ct));

            // ORCH-CON-04: seam logged, NO Redis write of any kind.
            Assert.Contains(logs.Messages, m => m.Contains("Scheduler job start"));
            await db.DidNotReceive().StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
                Arg.Any<When>(), Arg.Any<CommandFlags>());
        }
        finally { await harness.Stop(ct); }
    }

    // ----- Start: MSG-ACK-02 (infra fault propagates) -------------------------------------------

    [Fact]
    public async Task Start_Infra_Fault_Faults_Not_Acked_Clean()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildStartHarness(InfraFaultL2());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new StartOrchestration([Guid.NewGuid()]), ct);

            // The infra fault propagated: the consume recorded an exception (NOT a clean ack).
            Assert.True(await harness.Consumed.Any<StartOrchestration>(m => m.Exception != null, ct));
        }
        finally { await harness.Stop(ct); }
    }

    // ----- Stop: mirror of the three cases ------------------------------------------------------

    [Fact]
    public async Task Stop_Absent_From_L2_Is_Consumed_And_Acked_Not_Faulted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildStopHarness(AbsentL2(out _));
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new StopOrchestration([Guid.NewGuid()]), ct);

            Assert.True(await harness.Consumed.Any<StopOrchestration>(ct));
            Assert.False(await harness.Consumed.Any<StopOrchestration>(m => m.Exception != null, ct));
        }
        finally { await harness.Stop(ct); }
    }

    [Fact]
    public async Task Stop_Present_In_L2_Logs_Seam_And_Writes_Nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var logs = new CapturingLoggerProvider();
        var mux = PresentL2(out var db);
        await using var provider = BuildStopHarness(mux, logs);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new StopOrchestration([Guid.NewGuid()]), ct);

            Assert.True(await harness.Consumed.Any<StopOrchestration>(ct));
            Assert.False(await harness.Consumed.Any<StopOrchestration>(m => m.Exception != null, ct));
            Assert.Contains(logs.Messages, m => m.Contains("Scheduler job start"));
            await db.DidNotReceive().StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
                Arg.Any<When>(), Arg.Any<CommandFlags>());
        }
        finally { await harness.Stop(ct); }
    }

    [Fact]
    public async Task Stop_Infra_Fault_Faults_Not_Acked_Clean()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildStopHarness(InfraFaultL2());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new StopOrchestration([Guid.NewGuid()]), ct);

            Assert.True(await harness.Consumed.Any<StopOrchestration>(m => m.Exception != null, ct));
        }
        finally { await harness.Stop(ct); }
    }
}
