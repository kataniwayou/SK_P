using global::Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Phase 46 / KEEP-08: the Keeper CLEANUP state deletes the redundant L2 composite-backup copy on the
/// happy path (net-zero composite invariant).
/// </summary>
public sealed class CleanupConsumerFacts
{
    [Fact]
    [Trait("Phase", "46")]
    public async Task Cleanup_deletes_composite_backup()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = RecoveryTestKit.Db();
        var send = new RecoveryTestKit.CapturingSendProvider();

        var consumer = new CleanupConsumer(
            RecoveryTestKit.Mux(db), send, RecoveryTestKit.OpenGate(),
            RecoveryTestKit.Retry(), RecoveryTestKit.Recovery(), RecoveryTestKit.Backup());

        var m = new KeeperCleanup(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
        };
        var ctx = Substitute.For<ConsumeContext<KeeperCleanup>>();
        ctx.Message.Returns(m);
        ctx.CancellationToken.Returns(ct);

        await consumer.Consume(ctx);

        var key = L2ProjectionKeys.CompositeBackup(m.CorrelationId, m.WorkflowId, m.ProcessorId, m.ExecutionId);
        await db.Received(1).KeyDeleteAsync((RedisKey)key, Arg.Any<CommandFlags>());
    }
}
