using global::Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Phase 46 / KEEP-06: the Keeper INJECT state performs ordered ops — read composite → new entryId →
/// write L2[entryId] (NO TTL) → Send StepCompleted to the orchestrator result queue → delete the
/// composite copy. The op order is asserted via NSubstitute Received.InOrder. The injected StepCompleted
/// is byte-indistinguishable from a direct completion (ORCH-01): same type, same orchestrator-result
/// queue, carries executionId + a real (non-empty) entryId.
/// </summary>
public sealed class InjectConsumerFacts
{
    [Fact]
    [Trait("Phase", "46")]
    public async Task Inject_reads_then_writes_then_sends_then_deletes_in_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var m = new KeeperInject(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
        };
        var composite = L2ProjectionKeys.CompositeBackup(m.CorrelationId, m.WorkflowId, m.ProcessorId, m.ExecutionId);
        var db = RecoveryTestKit.Db(new Dictionary<string, string> { [composite] = "{\"blob\":1}" });

        // A single captured endpoint substitute so Received.InOrder can interleave db + send calls.
        var endpoint = Substitute.For<ISendEndpoint>();
        endpoint.Send(Arg.Any<StepCompleted>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        Uri? sentTo = null;
        StepCompleted? sentMsg = null;
        endpoint.When(e => e.Send(Arg.Any<StepCompleted>(), Arg.Any<CancellationToken>()))
            .Do(ci => sentMsg = (StepCompleted)ci[0]!);
        var send = Substitute.For<ISendEndpointProvider>();
        send.GetSendEndpoint(Arg.Any<Uri>()).Returns(ci => { sentTo = (Uri)ci[0]!; return Task.FromResult(endpoint); });

        var consumer = new InjectConsumer(
            RecoveryTestKit.Mux(db), send, RecoveryTestKit.OpenGate(),
            RecoveryTestKit.Retry(), RecoveryTestKit.Recovery(), RecoveryTestKit.Backup());

        await consumer.Consume(Substitute_Ctx(m, ct));

        // Order: read composite → write data key (no TTL) → Send StepCompleted → delete composite.
        Received.InOrder(() =>
        {
            db.StringGetAsync((RedisKey)composite, Arg.Any<CommandFlags>());
            db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>());
            endpoint.Send(Arg.Any<StepCompleted>(), Arg.Any<CancellationToken>());
            db.KeyDeleteAsync((RedisKey)composite, Arg.Any<CommandFlags>());
        });

        // The data-key write carries NO TTL (the bare 2-arg StringSetAsync overload).
        await db.Received(1).StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>());

        // ORCH-01 indistinguishability: StepCompleted to orchestrator-result, real entryId + executionId.
        Assert.Equal(new Uri($"queue:{OrchestratorQueues.Result}"), sentTo);
        Assert.NotNull(sentMsg);
        Assert.Equal(m.ExecutionId, sentMsg!.ExecutionId);
        Assert.NotEqual(Guid.Empty, sentMsg.EntryId);
        Assert.Equal(m.WorkflowId, sentMsg.WorkflowId);
        Assert.Equal(m.StepId, sentMsg.StepId);
        Assert.Equal(m.ProcessorId, sentMsg.ProcessorId);
        Assert.Equal(m.CorrelationId, sentMsg.CorrelationId);
    }

    private static ConsumeContext<KeeperInject> Substitute_Ctx(KeeperInject m, CancellationToken ct)
    {
        var ctx = Substitute.For<ConsumeContext<KeeperInject>>();
        ctx.Message.Returns(m);
        ctx.CancellationToken.Returns(ct);
        return ctx;
    }
}
