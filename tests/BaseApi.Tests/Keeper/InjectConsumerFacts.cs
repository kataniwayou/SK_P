using global::Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Phase 52 / KEEP-02: the Keeper INJECT state is forward-only — it writes L2[entryId]=Data, sends a
/// reconstructed StepCompleted to queue:orchestrator-result, then deletes L2[deleteEntryId], STRICTLY in
/// that order (Received.InOrder locks the A18 op order, Pitfall 5).
/// </summary>
public sealed class InjectConsumerFacts
{
    [Fact]
    [Trait("Phase", "52")]
    public async Task Inject_writes_sends_completed_deletes_source_in_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = RecoveryTestKit.Db();
        var send = new RecoveryTestKit.CapturingSendProvider();
        var consumer = new InjectConsumer(
            RecoveryTestKit.Mux(db), send,
            RecoveryTestKit.Retry(), RecoveryTestKit.Recovery());

        var m = new KeeperInject(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            EntryId = Guid.NewGuid(),
            Data = "{\"out\":1}",
            DeleteEntryId = Guid.NewGuid(),
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

        // Strict A18 order: write L2[entryId]=Data → send StepCompleted → delete L2[deleteEntryId] (Pitfall 5).
        // The send is the most critical safety step — deleting the source before the send lands would silently
        // lose the result. NSubstitute's Received.InOrder only covers the Redis substitute's calls directly, so
        // it locks write < delete; the send between them is captured by CapturingSendProvider. SE.Redis 2.13.1
        // binds the consumer's 2-arg StringSetAsync to the Expiration/ValueCondition overload, so the InOrder
        // matcher targets that overload.
        Received.InOrder(() =>
        {
            db.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(),
                Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>());
            db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        });

        // Belt: lock the middle of the three-way order machine-side. The single send must have been captured
        // (it occurs before the delete in the consumer body), so a future refactor that drops or reorders the
        // send after the delete is caught here rather than slipping through the Redis-only InOrder chain above.
        Assert.Single(send.Sent);

        await db.Received(1).StringSetAsync(
            (RedisKey)L2ProjectionKeys.ExecutionData(m.EntryId), (RedisValue)m.Data,
            Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>());
        await db.Received(1).KeyDeleteAsync(
            (RedisKey)L2ProjectionKeys.ExecutionData(m.DeleteEntryId), Arg.Any<CommandFlags>());
    }
}
