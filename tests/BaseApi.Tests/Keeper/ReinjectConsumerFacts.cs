using System.Diagnostics.Metrics;
using global::Keeper.Observability;
using global::Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Phase 52 / KEEP-01: the Keeper REINJECT state reads L2[entryId]; present → re-injects a reconstructed
/// EntryStepDispatch carrying the D-01 Payload to queue:{ProcessorId}; absent/empty (STRLEN==0, no Redis
/// exception) → BY-DESIGN silent drop (no throw, no send) + keeper_reinject_dropped counter (D-06/D-07).
/// </summary>
public sealed class ReinjectConsumerFacts
{
    private static ConsumeContext<KeeperReinject> Ctx(KeeperReinject m, CancellationToken ct)
    {
        var ctx = Substitute.For<ConsumeContext<KeeperReinject>>();
        ctx.Message.Returns(m);
        ctx.CancellationToken.Returns(ct);
        return ctx;
    }

    [Fact]
    [Trait("Phase", "52")]
    public async Task Reinject_present_sends_EntryStepDispatch_with_Payload()
    {
        var ct = TestContext.Current.CancellationToken;
        var m = new KeeperReinject(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            EntryId = Guid.NewGuid(),
            Payload = "{\"cfg\":7}",
        };
        var db = RecoveryTestKit.Db();
        // IN-04: REINJECT gates on STRLEN, not a full StringGet. STRLEN > 0 → data present.
        db.StringLengthAsync(L2ProjectionKeys.ExecutionData(m.EntryId), Arg.Any<CommandFlags>())
            .Returns(10L);   // present
        var send = new RecoveryTestKit.CapturingSendProvider();

        var consumer = new ReinjectConsumer(
            RecoveryTestKit.Mux(db), send,
            RecoveryTestKit.Retry(),
            RecoveryTestKit.Metrics(), NullLogger<ReinjectConsumer>.Instance);

        await consumer.Consume(Ctx(m, ct));

        var (uri, msg) = Assert.Single(send.Sent);
        Assert.Equal(new Uri($"queue:{m.ProcessorId:D}"), uri);
        var dispatch = Assert.IsType<EntryStepDispatch>(msg);
        Assert.Equal(m.Payload, dispatch.Payload);
        Assert.Equal(m.EntryId, dispatch.EntryId);
        Assert.Equal(m.CorrelationId, dispatch.CorrelationId);
        Assert.Equal(m.ExecutionId, dispatch.ExecutionId);
        Assert.Equal(m.WorkflowId, dispatch.WorkflowId);
        Assert.Equal(m.StepId, dispatch.StepId);
        Assert.Equal(m.ProcessorId, dispatch.ProcessorId);
    }

    [Fact]
    [Trait("Phase", "52")]
    public async Task Reinject_absent_drops_no_throw_no_send_and_increments_counter()
    {
        var ct = TestContext.Current.CancellationToken;
        var m = new KeeperReinject(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            EntryId = Guid.NewGuid(),
            Payload = "{\"cfg\":7}",
        };
        // IN-04: STRLEN==0 covers BOTH a missing key AND an empty value (the absent-OR-empty drop case).
        // NSubstitute returns default(long) == 0 unstubbed, so the un-stubbed key reads as length 0 → drop.
        var db = RecoveryTestKit.Db();
        var send = new RecoveryTestKit.CapturingSendProvider();

        // Build a real KeeperMetrics over a real IMeterFactory and observe the counter via a MeterListener.
        var meterFactory = new ServiceCollection().AddMetrics().BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();
        var metrics = new KeeperMetrics(meterFactory);

        long dropped = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == KeeperMetrics.MeterName && instrument.Name == "keeper_reinject_dropped")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => Interlocked.Add(ref dropped, measurement));
        listener.Start();

        var consumer = new ReinjectConsumer(
            RecoveryTestKit.Mux(db), send,
            RecoveryTestKit.Retry(),
            metrics, NullLogger<ReinjectConsumer>.Instance);

        await consumer.Consume(Ctx(m, ct));   // D-06: no throw

        Assert.Empty(send.Sent);              // nothing re-injected when the data is gone
        Assert.Equal(1, Interlocked.Read(ref dropped));   // D-07: keeper_reinject_dropped incremented by 1
    }
}
