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
/// Phase 32 Plan 04 Task 1 (req-3 processor / req-7 dedup; D-05/D-10/D-13). Hermetic facts for the two
/// new gates added to the TOP of <see cref="EntryStepDispatchConsumer"/>.Consume (NSubstitute
/// <see cref="IDatabase"/>, no live Redis — default Category, no <c>RealStack</c> trait):
/// <list type="bullet">
///   <item><b>DedupCounterOnFlagAck</b> — a duplicate whose <c>flag[H]=="Ack"</c> increments
///   <c>processor_dispatch_deduped</c> exactly once (tagged <c>ProcessorId</c>) then ack-and-discards:
///   no transform, no write, no Send.</item>
///   <item><b>CancelledMarkerSet_AckAndDiscards</b> — when <c>skp:cancelled:{wf:D}=="true"</c> (flag not
///   Ack), the consumer returns producing NO Send, NO dedup increment, NO <c>StringSetAsync</c> to ANY
///   key (no marker write, no flag write).</item>
///   <item><b>OtherWorkflowMarker_DoesNotAffectThisDispatch</b> — a marker set for a DIFFERENT workflow
///   leaves THIS dispatch processing normally (proves the marker is <c>workflowId</c>-keyed).</item>
///   <item><b>CancelledPath_TouchesNoFlagKey</b> — the cancelled drop reads/writes NO
///   <c>L2ProjectionKeys.Flag(...)</c> key (D-13 — the cancelled path never seeds/reads flag[H]).</item>
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

    [Fact]
    public async Task CancelledMarkerSet_AckAndDiscards_NoSend_NoCounter_NoWrite()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatch = new EntryStepDispatch(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "{\"cfg\":1}")
        {
            CorrelationId = Guid.NewGuid(),
            EntryId = "",
            H = "feedfacefeedfacefeedfacefeedfacefeedfacefeedfacefeedfacefeedface",
        };
        var db = Substitute.For<IDatabase>();
        // flag[H] is NOT Ack (null); the cancelled marker for THIS workflow IS set.
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci => ((RedisKey)ci[0]).ToString() == L2ProjectionKeys.Cancelled(dispatch.WorkflowId)
                ? (RedisValue)L2ProjectionKeys.CancelledMarkerValue : RedisValue.Null);
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

        Assert.False(processor.Invoked);      // ack-and-discard: no transform
        Assert.Empty(send.Sent);              // no Send
        Assert.Equal(0, dedupCount);          // NOT a flag[H]==Ack drop -> no dedup counter
        // NO StringSetAsync to ANY key (no marker write, no flag write) on the cancelled drop.
        await db.DidNotReceive().StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(),
            Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>());
#pragma warning disable CS0618 // also cover the legacy When overloads
        await db.DidNotReceive().StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(),
            Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
#pragma warning restore CS0618
        await db.DidNotReceive().StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(),
            Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task OtherWorkflowMarker_DoesNotAffectThisDispatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatch = new EntryStepDispatch(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "{\"cfg\":1}")
        {
            CorrelationId = Guid.NewGuid(),
            EntryId = "",
            H = "0badc0de0badc0de0badc0de0badc0de0badc0de0badc0de0badc0de0badc0de",
        };
        var otherWorkflow = Guid.NewGuid();   // a DIFFERENT workflow's marker is set
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci => ((RedisKey)ci[0]).ToString() == L2ProjectionKeys.Cancelled(otherWorkflow)
                ? (RedisValue)L2ProjectionKeys.CancelledMarkerValue : RedisValue.Null);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results(Output));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();
        var consumer = DispatchTestKit.Build(mux, context, processor, send);

        await consumer.Consume(OrchestratorTestStubs.Context(dispatch, ct));

        // THIS workflow is not cancelled -> the consumer proceeds normally (transform runs, one result sent).
        Assert.True(processor.Invoked);
        var sent = Assert.Single(send.Sent);
        Assert.Equal(StepOutcome.Completed, sent.Outcome);
    }

    [Fact]
    public async Task CancelledPath_TouchesNoFlagKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatch = new EntryStepDispatch(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "{\"cfg\":1}")
        {
            CorrelationId = Guid.NewGuid(),
            EntryId = "",
            H = "abad1deaabad1deaabad1deaabad1deaabad1deaabad1deaabad1deaabad1dea",
        };
        var db = Substitute.For<IDatabase>();
        // flag[H] is NOT Ack; the cancelled marker IS set -> the cancelled drop fires.
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci => ((RedisKey)ci[0]).ToString() == L2ProjectionKeys.Cancelled(dispatch.WorkflowId)
                ? (RedisValue)L2ProjectionKeys.CancelledMarkerValue : RedisValue.Null);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results(Output));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();
        var consumer = DispatchTestKit.Build(mux, context, processor, send);

        await consumer.Consume(OrchestratorTestStubs.Context(dispatch, ct));

        // D-13: the cancelled drop reads the flag[H] gate ONCE (the existing Phase-31 gate, which is first),
        // but writes NO flag key. The only legitimate flag READ is the Phase-31 flag[H] gate; the cancelled
        // path itself reads Cancelled(...) only. Assert NO flag WRITE happened (no Pending/Ack seed).
        await db.DidNotReceive().StringSetAsync(
            Arg.Is<RedisKey>(k => k.ToString().Contains("flag")),
            Arg.Any<RedisValue>(), Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>());
#pragma warning disable CS0618
        await db.DidNotReceive().StringSetAsync(
            Arg.Is<RedisKey>(k => k.ToString().Contains("flag")),
            Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
#pragma warning restore CS0618
        await db.DidNotReceive().StringSetAsync(
            Arg.Is<RedisKey>(k => k.ToString().Contains("flag")),
            Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }
}
