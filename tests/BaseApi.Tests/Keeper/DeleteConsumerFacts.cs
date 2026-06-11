using global::Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Phase 52 / KEEP-03: the Keeper DELETE state deletes the L2 execution-data key (GC only) and
/// drops-on-absent (KeyDeleteAsync no-ops on a missing key, A18 line 217).
/// </summary>
public sealed class DeleteConsumerFacts
{
    private static ConsumeContext<KeeperDelete> Ctx(KeeperDelete m, CancellationToken ct)
    {
        var ctx = Substitute.For<ConsumeContext<KeeperDelete>>();
        ctx.Message.Returns(m);
        ctx.CancellationToken.Returns(ct);
        return ctx;
    }

    private static KeeperDelete NewDelete() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            EntryId = Guid.NewGuid(),
        };

    [Fact]
    [Trait("Phase", "52")]
    public async Task Delete_deletes_execution_data_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = RecoveryTestKit.Db();
        var send = new RecoveryTestKit.CapturingSendProvider();

        var consumer = new DeleteConsumer(
            RecoveryTestKit.Mux(db), send,
            RecoveryTestKit.Retry(), RecoveryTestKit.Recovery());

        var m = NewDelete();

        await consumer.Consume(Ctx(m, ct));

        await db.Received(1).KeyDeleteAsync(
            (RedisKey)L2ProjectionKeys.ExecutionData(m.EntryId), Arg.Any<CommandFlags>());
    }

    [Fact]
    [Trait("Phase", "52")]
    public async Task Delete_absent_key_no_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = RecoveryTestKit.Db();
        // KeyDeleteAsync returns false for an absent key — drop-on-absent (KEEP-03).
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(false);
        var send = new RecoveryTestKit.CapturingSendProvider();

        var consumer = new DeleteConsumer(
            RecoveryTestKit.Mux(db), send,
            RecoveryTestKit.Retry(), RecoveryTestKit.Recovery());

        var m = NewDelete();

        // No throw on an absent key — the consume completes cleanly.
        await consumer.Consume(Ctx(m, ct));

        await db.Received(1).KeyDeleteAsync(
            (RedisKey)L2ProjectionKeys.ExecutionData(m.EntryId), Arg.Any<CommandFlags>());
    }
}
