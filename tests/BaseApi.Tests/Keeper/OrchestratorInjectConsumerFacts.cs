using global::Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Phase 71 / ORCV-06: the orchestrator INJECT state completes the index+data COPY the FORWARD pass could
/// not finish — it reads L2[OriginEntryId] and, when present, SETs L2[EntryId] with that value — then
/// dispatches exactly one reconstructed EntryStepDispatch (carrying the next-step Payload) to
/// queue:{NextProcessorId}. INJECT is non-destructive: it deletes NOTHING (BOTH KeyDeleteAsync overloads).
/// </summary>
public sealed class OrchestratorInjectConsumerFacts
{
    [Fact]
    [Trait("Phase", "71")]
    public async Task Inject_copies_origin_then_dispatches_to_next_processor_and_deletes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var m = new OrchestratorInject(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            EntryId = Guid.NewGuid(),         // newEntryId to copy-into / dispatch with
            OriginEntryId = Guid.NewGuid(),   // origin data key to copy FROM
            NextStepId = Guid.NewGuid(),
            NextProcessorId = Guid.NewGuid(),
            Payload = "{\"cfg\":7}",
        };
        // Seed the origin key present so the copy SET runs.
        var db = RecoveryTestKit.Db(new Dictionary<string, string>
        {
            [L2ProjectionKeys.ExecutionData(m.OriginEntryId)] = "{\"out\":1}",
        });
        var send = new RecoveryTestKit.CapturingSendProvider();

        var consumer = new OrchestratorInjectConsumer(
            RecoveryTestKit.Mux(db), send, RecoveryTestKit.Retry());

        var ctx = Substitute.For<ConsumeContext<OrchestratorInject>>();
        ctx.Message.Returns(m);
        ctx.CancellationToken.Returns(ct);

        await consumer.Consume(ctx);

        // The copy completed: SET L2[newEntryId] with the origin value (SE.Redis 2.13.1 binds the consumer's
        // 2-arg StringSetAsync to the Expiration/ValueCondition overload — match that 5-arg shape).
        await db.Received(1).StringSetAsync(
            (RedisKey)L2ProjectionKeys.ExecutionData(m.EntryId), (RedisValue)"{\"out\":1}",
            Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>());

        // Exactly one EntryStepDispatch was dispatched to queue:{NextProcessorId} carrying the next-step tuple.
        var (uri, msg) = Assert.Single(send.Sent);
        Assert.Equal(new Uri($"queue:{m.NextProcessorId:D}"), uri);
        var dispatch = Assert.IsType<EntryStepDispatch>(msg);
        Assert.Equal(m.Payload, dispatch.Payload);
        Assert.Equal(m.EntryId, dispatch.EntryId);
        Assert.Equal(m.NextStepId, dispatch.StepId);
        Assert.Equal(m.NextProcessorId, dispatch.ProcessorId);
        Assert.Equal(m.WorkflowId, dispatch.WorkflowId);
        Assert.Equal(m.CorrelationId, dispatch.CorrelationId);
        Assert.Equal(m.ExecutionId, dispatch.ExecutionId);

        // Non-destructive: INJECT deletes NOTHING — BOTH overloads (single-key AND multi-key).
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(),   Arg.Any<CommandFlags>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
    }
}
