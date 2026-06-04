using System.Diagnostics.Metrics;
using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Observability;
using BaseProcessor.Core.Processing;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;
using BaseApi.Tests.Orchestrator;
using ExecutionResult = Messaging.Contracts.ExecutionResult;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Phase 32 Plan 04 Task 2 (req-1, req-7-trip; D-01/D-02/D-11). Hermetic facts for the final-attempt
/// breaker catch in <see cref="EntryStepDispatchConsumer"/>. The breaker fires ONLY when an INFRA op
/// throws on the exhausting delivery (<c>GetRetryAttempt() == Limit</c>); the attempt is controlled via
/// <c>DispatchTestKit.RetryContext</c> (stubs the <c>ConsumeRetryContext</c> payload that
/// <c>GetRetryAttempt()</c> reads — see <c>RetryAttemptNumberingFacts</c> for the real-bus boundary pin).
/// <list type="bullet">
///   <item><b>InfraThrow_At_Limit_Trips</b> — infra Send-throw at attempt==Limit sets the no-TTL marker,
///   increments <c>workflow_cancelled</c> once, emits a WARN log, and RE-THROWS.</item>
///   <item><b>InfraThrow_Below_Limit_DoesNotTrip</b> — infra throw at attempt&lt;Limit sets NO marker,
///   NO <c>workflow_cancelled</c>, and re-throws (lets the retry continue).</item>
///   <item><b>ProcessAsyncThrow_StaysFailed_TripsNothing</b> — a business <c>ProcessAsync</c> throw is
///   caught by the existing business catch -> Failed ExecutionResult sent + acked, NO marker, NO
///   <c>workflow_cancelled</c>, NO re-throw (Pitfall 2).</item>
/// </list>
/// </summary>
public sealed class BreakerTriggerFacts
{
    private const int Limit = 3;
    private const string Output = "{\"v\":1}";

    private static ProcessorMetrics ObservableMetrics(out ServiceProvider provider)
    {
        provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        return new ProcessorMetrics(provider.GetRequiredService<IMeterFactory>());
    }

    /// <summary>A send provider whose every Send throws an infra fault (broker unreachable).</summary>
    private sealed class ThrowingSendProvider : ISendEndpointProvider
    {
        public Task<ISendEndpoint> GetSendEndpoint(Uri address)
        {
            var endpoint = Substitute.For<ISendEndpoint>();
            endpoint.Send(Arg.Any<ExecutionResult>(), Arg.Any<CancellationToken>())
                .Returns<Task>(_ => throw new InvalidOperationException("stub: broker Send unreachable (infra)"));
            return Task.FromResult(endpoint);
        }
        public ConnectHandle ConnectSendObserver(ISendObserver observer) => throw new NotSupportedException();
    }

    /// <summary>A capturing logger that records each WARN entry's rendered message + state values.</summary>
    private sealed class CapturingLogger : ILogger<EntryStepDispatchConsumer>
    {
        public List<(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> State)> Entries { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter)
        {
            var kvps = state as IReadOnlyList<KeyValuePair<string, object?>> ?? Array.Empty<KeyValuePair<string, object?>>();
            Entries.Add((level, formatter(state, ex), kvps));
        }
        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }

    private static (IConnectionMultiplexer Mux, IDatabase Db) PassThroughRedis()
    {
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);
        // All StringSetAsync overloads succeed (Task<bool> true) — the marker SET must succeed in the catch.
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(),
            Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>()).Returns(true);
#pragma warning disable CS0618
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(),
            Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
#pragma warning restore CS0618
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(),
            Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return (mux, db);
    }

    private static EntryStepDispatchConsumer BuildWith(
        IConnectionMultiplexer redis, IProcessorContext context,
        DispatchTestKit.FakeProcessor processor, ISendEndpointProvider send,
        ProcessorMetrics metrics, ILogger<EntryStepDispatchConsumer> logger) =>
        new(redis, context, processor, DispatchTestKit.Options(300), send, metrics,
            DispatchTestKit.Retry(Limit), logger);

    private static EntryStepDispatch NewDispatch() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "{\"cfg\":1}")
        {
            CorrelationId = Guid.NewGuid(),
            EntryId = "",
            H = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef",
        };

    [Fact]
    public async Task InfraThrow_At_Limit_Trips_SetsMarker_IncrementsCounter_WarnLog_Rethrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatch = NewDispatch();
        var (mux, db) = PassThroughRedis();
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results(Output));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new ThrowingSendProvider();   // infra Send fault
        var logger = new CapturingLogger();

        var tripCount = 0;
        var metrics = ObservableMetrics(out var provider);
        using (provider)
        {
            using var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == ProcessorMetrics.MeterName && instrument.Name == "workflow_cancelled")
                        l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, m, _, _) => tripCount += (int)m);
            listener.Start();

            var consumer = BuildWith(mux, context, processor, send, metrics, logger);

            // attempt == Limit -> the exhausting delivery -> the breaker MUST trip and re-throw.
            await Assert.ThrowsAnyAsync<Exception>(() =>
                consumer.Consume(DispatchTestKit.RetryContext(dispatch, Limit, ct)));
        }

        // Marker SET with the no-TTL (Expiration) overload, value = CancelledMarkerValue.
        await db.Received(1).StringSetAsync(
            Arg.Is<RedisKey>(k => k.ToString() == L2ProjectionKeys.Cancelled(dispatch.WorkflowId)),
            Arg.Is<RedisValue>(v => v == L2ProjectionKeys.CancelledMarkerValue),
            Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>());

        Assert.Equal(1, tripCount);   // workflow_cancelled +1 exactly once

        // A WARN log carrying the four server-minted ids as structured template fields.
        var warn = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        var keys = warn.State.Select(kv => kv.Key).ToArray();
        Assert.Contains("WorkflowId", keys);
        Assert.Contains("StepId", keys);
        Assert.Contains("ProcessorId", keys);
        Assert.Contains("H", keys);
    }

    [Fact]
    public async Task InfraThrow_Below_Limit_DoesNotTrip_NoMarker_NoCounter_Rethrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatch = NewDispatch();
        var (mux, db) = PassThroughRedis();
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results(Output));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new ThrowingSendProvider();
        var logger = new CapturingLogger();

        var tripCount = 0;
        var metrics = ObservableMetrics(out var provider);
        using (provider)
        {
            using var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == ProcessorMetrics.MeterName && instrument.Name == "workflow_cancelled")
                        l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, m, _, _) => tripCount += (int)m);
            listener.Start();

            var consumer = BuildWith(mux, context, processor, send, metrics, logger);

            // attempt < Limit -> a NON-final retry -> the infra fault re-throws (retry continues) but
            // the breaker does NOT trip.
            await Assert.ThrowsAnyAsync<Exception>(() =>
                consumer.Consume(DispatchTestKit.RetryContext(dispatch, Limit - 1, ct)));
        }

        Assert.Equal(0, tripCount);
        await db.DidNotReceive().StringSetAsync(
            Arg.Is<RedisKey>(k => k.ToString() == L2ProjectionKeys.Cancelled(dispatch.WorkflowId)),
            Arg.Any<RedisValue>(), Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>());
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task ProcessAsyncThrow_StaysFailed_TripsNothing_NoRethrow()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatch = NewDispatch();
        var (mux, db) = PassThroughRedis();
        // ProcessAsync throws a BUSINESS exception (caught at the existing :129 business catch).
        var processor = new DispatchTestKit.FakeProcessor(new InvalidOperationException("business boom"));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();   // Send succeeds (the Failed result is sent)
        var logger = new CapturingLogger();

        var tripCount = 0;
        var metrics = ObservableMetrics(out var provider);
        using (provider)
        {
            using var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == ProcessorMetrics.MeterName && instrument.Name == "workflow_cancelled")
                        l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, m, _, _) => tripCount += (int)m);
            listener.Start();

            var consumer = BuildWith(mux, context, processor, send, metrics, logger);

            // Even at attempt == Limit, a business throw must NOT trip the breaker and must NOT re-throw.
            await consumer.Consume(DispatchTestKit.RetryContext(dispatch, Limit, ct));
        }

        // The business catch produced a Failed ExecutionResult + acked (no re-throw — the call returned).
        var sent = Assert.Single(send.Sent);
        Assert.Equal(StepOutcome.Failed, sent.Outcome);
        Assert.Equal(0, tripCount);
        await db.DidNotReceive().StringSetAsync(
            Arg.Is<RedisKey>(k => k.ToString() == L2ProjectionKeys.Cancelled(dispatch.WorkflowId)),
            Arg.Any<RedisValue>(), Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>());
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }
}
