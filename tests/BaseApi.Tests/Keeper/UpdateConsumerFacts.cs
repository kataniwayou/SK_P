using global::Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Phase 46 / KEEP-04: the Keeper UPDATE state writes the validated data to the L2 composite-backup key
/// WITH the BackupOptions TTL (crash-backstop), only once the gate is open.
/// </summary>
public sealed class UpdateConsumerFacts
{
    [Fact]
    [Trait("Phase", "46")]
    public async Task Update_writes_composite_with_TTL_only_when_gate_open()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = RecoveryTestKit.Db();
        var send = new RecoveryTestKit.CapturingSendProvider();
        const int ttlDays = 2;

        var consumer = new UpdateConsumer(
            RecoveryTestKit.Mux(db), send, RecoveryTestKit.OpenGate(),
            RecoveryTestKit.Retry(), RecoveryTestKit.Recovery(), RecoveryTestKit.Backup(ttlDays));

        var m = new KeeperUpdate(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            ValidatedData = "{\"v\":42}",
        };
        var ctx = Substitute.For<ConsumeContext<KeeperUpdate>>();
        ctx.Message.Returns(m);
        ctx.CancellationToken.Returns(ct);

        await consumer.Consume(ctx);

        var key = L2ProjectionKeys.CompositeBackup(m.CorrelationId, m.WorkflowId, m.ProcessorId, m.ExecutionId);
        // The `expiry:`-named call binds to SE.Redis 2.13's Expiration/ValueCondition overload (TimeSpan
        // implicitly converts to Expiration) — assert the TTL there.
        await db.Received(1).StringSetAsync(
            (RedisKey)key, (RedisValue)m.ValidatedData, (Expiration)TimeSpan.FromDays(ttlDays),
            Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>());
    }
}
