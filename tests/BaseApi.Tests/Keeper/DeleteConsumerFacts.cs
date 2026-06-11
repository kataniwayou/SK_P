using global::Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Phase 46 / KEEP-07: the Keeper DELETE state deletes the L2 execution-data key (GC only).
/// </summary>
public sealed class DeleteConsumerFacts
{
    [Fact]
    [Trait("Phase", "46")]
    public async Task Delete_deletes_execution_data_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = RecoveryTestKit.Db();
        var send = new RecoveryTestKit.CapturingSendProvider();

        var consumer = new DeleteConsumer(
            RecoveryTestKit.Mux(db), send, RecoveryTestKit.OpenGate(),
            RecoveryTestKit.Retry(), RecoveryTestKit.Recovery());

        var m = new KeeperDelete(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            EntryId = Guid.NewGuid(),
        };
        var ctx = Substitute.For<ConsumeContext<KeeperDelete>>();
        ctx.Message.Returns(m);
        ctx.CancellationToken.Returns(ct);

        await consumer.Consume(ctx);

        await db.Received(1).KeyDeleteAsync(
            (RedisKey)L2ProjectionKeys.ExecutionData(m.EntryId), Arg.Any<CommandFlags>());
    }
}
