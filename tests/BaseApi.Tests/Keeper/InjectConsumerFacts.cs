using global::Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Phase 70 / KINJ-01: the Keeper INJECT state is non-destructive — it writes L2[entryId]=Data then sends a
/// reconstructed StepCompleted to queue:orchestrator-result, and deletes NOTHING. The surviving order is
/// write-then-send (the write is the db substitute's only call; the single captured send is the StepCompleted),
/// guarded by a DidNotReceive-delete belt on BOTH KeyDeleteAsync overloads (Pitfall 2).
/// </summary>
public sealed class InjectConsumerFacts
{
    [Fact]
    [Trait("Phase", "70")]
    public async Task Inject_writes_then_sends_completed_and_deletes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = RecoveryTestKit.Db();
        var send = new RecoveryTestKit.CapturingSendProvider();
        var consumer = new InjectConsumer(
            RecoveryTestKit.Mux(db), send,
            RecoveryTestKit.Retry());

        var m = new KeeperInject(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            EntryId = Guid.NewGuid(),
            Data = "{\"out\":1}",
        };
        var ctx = Substitute.For<ConsumeContext<KeeperInject>>();
        ctx.Message.Returns(m);
        ctx.CancellationToken.Returns(ct);

        await consumer.Consume(ctx);

        // The single captured send is a StepCompleted carrying EntryId, targeting queue:orchestrator-result.
        var (uri, msg) = Assert.Single(send.Sent);
        Assert.Equal(new Uri($"queue:{OrchestratorQueues.Result}"), uri);
        var completed = Assert.IsType<StepCompleted>(msg);
        Assert.Equal(m.EntryId, completed.EntryId);
        Assert.Equal(m.CorrelationId, completed.CorrelationId);
        Assert.Equal(m.ExecutionId, completed.ExecutionId);

        // The surviving order is write-then-send. With the source-delete gone the db substitute sees only one
        // call (the write), so a multi-element Received.InOrder no longer guards anything (Pitfall 5). Assert
        // the write directly (SE.Redis 2.13.1 binds the consumer's 2-arg StringSetAsync to the
        // Expiration/ValueCondition overload — match that 5-arg shape) and the single captured StepCompleted
        // above; the write-before-send ordering is implicit in the consumer body.
        await db.Received(1).StringSetAsync(
            (RedisKey)L2ProjectionKeys.ExecutionData(m.EntryId), (RedisValue)m.Data,
            Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>());

        // Belt (KINJ-01): INJECT is non-destructive — it deletes NOTHING. Assert BOTH KeyDeleteAsync overloads
        // (single-key AND multi-key, Pitfall 2) so a reintroduced delete of either shape fails this fact.
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(),   Arg.Any<CommandFlags>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
    }
}
