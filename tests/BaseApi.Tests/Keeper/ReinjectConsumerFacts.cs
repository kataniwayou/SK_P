using global::Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Phase 46 / KEEP-05: the Keeper REINJECT state reads L2[entryId]; present → re-injects a reconstructed
/// EntryStepDispatch carrying the D-01 Payload to queue:{ProcessorId}; absent/empty → throws
/// RecoveryDataGoneException (the deliberate data-gone terminal → skp-dlq-1).
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
    [Trait("Phase", "46")]
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
        // IN-04: REINJECT now gates on STRLEN, not a full StringGet. STRLEN > 0 → data present.
        db.StringLengthAsync(L2ProjectionKeys.ExecutionData(m.EntryId), Arg.Any<CommandFlags>())
            .Returns(10L);   // present
        var send = new RecoveryTestKit.CapturingSendProvider();

        var consumer = new ReinjectConsumer(
            RecoveryTestKit.Mux(db), send, RecoveryTestKit.OpenGate(),
            RecoveryTestKit.Retry(), RecoveryTestKit.Recovery(), RecoveryTestKit.Backup());

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
    [Trait("Phase", "46")]
    public async Task Reinject_absent_throws_RecoveryDataGone()
    {
        var ct = TestContext.Current.CancellationToken;
        var m = new KeeperReinject(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            EntryId = Guid.NewGuid(),
            Payload = "{\"cfg\":7}",
        };
        // IN-04: REINJECT now gates on STRLEN. STRLEN == 0 covers BOTH a missing key AND an empty value
        // (the absent-OR-empty data-gone terminal). NSubstitute returns default(long) == 0 unstubbed, so the
        // un-stubbed key here reads as length 0 → data-gone.
        var db = RecoveryTestKit.Db();   // StringLengthAsync defaults to 0 → absent/empty
        var send = new RecoveryTestKit.CapturingSendProvider();

        var consumer = new ReinjectConsumer(
            RecoveryTestKit.Mux(db), send, RecoveryTestKit.OpenGate(),   // gate already open → throw is the data-gone path
            RecoveryTestKit.Retry(), RecoveryTestKit.Recovery(), RecoveryTestKit.Backup());

        await Assert.ThrowsAsync<RecoveryDataGoneException>(() => consumer.Consume(Ctx(m, ct)));
        Assert.Empty(send.Sent);   // nothing re-injected when the data is gone
    }
}
