using System.Diagnostics.Metrics;
using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Observability;
using BaseProcessor.Core.Processing;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;
using BaseApi.Tests.Orchestrator;
using ExecutionResult = Messaging.Contracts.ExecutionResult;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Phase 32.1 (req-2 dedup). Hermetic fact for the retained Phase-31 <c>flag[H]</c> dedup gate at the
/// TOP of <see cref="EntryStepDispatchConsumer"/>.Consume (NSubstitute <see cref="IDatabase"/>, no live
/// Redis — default Category, no <c>RealStack</c> trait):
/// <list type="bullet">
///   <item><b>FlagAck_Increments_DispatchDeduped_Once_And_Discards</b> — a duplicate whose
///   <c>flag[H]=="Ack"</c> increments <c>processor_dispatch_deduped</c> exactly once (tagged
///   <c>ProcessorId</c>) then ack-and-discards: no transform, no write, no Send.</item>
/// </list>
/// </summary>
public sealed class CheckAndDropFacts
{
    private const string Output = "{\"v\":1}";

    /// <summary>A real <see cref="ProcessorMetrics"/> the test can OBSERVE via a MeterListener (unlike the
    /// internal <c>DispatchTestKit.Metrics()</c> the default Build uses).</summary>
    private static ProcessorMetrics ObservableMetrics(out ServiceProvider provider)
    {
        provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        return new ProcessorMetrics(provider.GetRequiredService<IMeterFactory>());
    }

    private static EntryStepDispatchConsumer BuildWith(
        IConnectionMultiplexer redis, IProcessorContext context,
        DispatchTestKit.FakeProcessor processor, ISendEndpointProvider send, ProcessorMetrics metrics) =>
        new(redis, context, processor, DispatchTestKit.Options(300), send, metrics,
            NullLogger<EntryStepDispatchConsumer>.Instance);

    [Fact]
    public async Task FlagAck_Increments_DispatchDeduped_Once_And_Discards()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatch = new EntryStepDispatch(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "{\"cfg\":1}")
        {
            CorrelationId = Guid.NewGuid(),
            EntryId = "",
            H = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef",
        };
        var db = Substitute.For<IDatabase>();
        // flag[H] == "Ack" -> drop (the cancelled key is never read on this path).
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci => ((RedisKey)ci[0]).ToString() == L2ProjectionKeys.Flag(dispatch.H)
                ? (RedisValue)"Ack" : RedisValue.Null);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results(Output));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();

        var dedupCount = 0;
        var metrics = ObservableMetrics(out var provider);
        using (provider)
        {
            using var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == ProcessorMetrics.MeterName
                        && instrument.Name == "processor_dispatch_deduped")
                        l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, m, _, _) => dedupCount += (int)m);
            listener.Start();

            var consumer = BuildWith(mux, context, processor, send, metrics);
            await consumer.Consume(OrchestratorTestStubs.Context(dispatch, ct));
        }

        Assert.Equal(1, dedupCount);          // dedup counter +1 exactly once
        Assert.False(processor.Invoked);      // ack-and-discard: no transform
        Assert.Empty(send.Sent);              // no Send
        // No L2 writes at all on the flag-Ack drop.
        await db.DidNotReceive().StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(),
            Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>());
    }
}
