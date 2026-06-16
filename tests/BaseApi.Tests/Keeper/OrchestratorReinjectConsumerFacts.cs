using global::Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Phase 71 / ORCV-06 (D-07): the orchestrator REINJECT state reconstructs the right IStepResult subtype
/// from the carried StepOutcome discriminator (the ONLY status branch) and re-injects it to
/// queue:orchestrator-result. The union diagnostic fields ride discretely: Failed carries ErrorMessage,
/// Cancelled carries CancellationMessage; Completed/Processing carry neither. REINJECT deletes NOTHING.
/// </summary>
public sealed class OrchestratorReinjectConsumerFacts
{
    [Theory]
    [Trait("Phase", "71")]
    [InlineData(StepOutcome.Completed)]
    [InlineData(StepOutcome.Failed)]
    [InlineData(StepOutcome.Cancelled)]
    [InlineData(StepOutcome.Processing)]
    public async Task Reinject_reconstructs_matching_result_and_sends_to_orchestrator_result(StepOutcome outcome)
    {
        var ct = TestContext.Current.CancellationToken;
        var m = new OrchestratorReinject(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            EntryId = outcome == StepOutcome.Completed ? Guid.NewGuid() : Guid.Empty,
            Outcome = outcome,
            ErrorMessage = outcome == StepOutcome.Failed ? "boom" : null,
            CancellationMessage = outcome == StepOutcome.Cancelled ? "stopped" : null,
        };
        var db = RecoveryTestKit.Db();
        var send = new RecoveryTestKit.CapturingSendProvider();

        var consumer = new OrchestratorReinjectConsumer(
            RecoveryTestKit.Mux(db), send, RecoveryTestKit.Retry());

        var ctx = Substitute.For<ConsumeContext<OrchestratorReinject>>();
        ctx.Message.Returns(m);
        ctx.CancellationToken.Returns(ct);

        await consumer.Consume(ctx);

        // Exactly one result re-injected to queue:orchestrator-result.
        var (uri, msg) = Assert.Single(send.Sent);
        Assert.Equal(new Uri($"queue:{OrchestratorQueues.Result}"), uri);

        // The reconstructed subtype matches the carried outcome, with the union field populated/absent correctly.
        switch (outcome)
        {
            case StepOutcome.Completed:
                var completed = Assert.IsType<StepCompleted>(msg);
                Assert.Equal(m.EntryId, completed.EntryId);
                break;
            case StepOutcome.Failed:
                var failed = Assert.IsType<StepFailed>(msg);
                Assert.Equal("boom", failed.ErrorMessage);
                break;
            case StepOutcome.Cancelled:
                var cancelled = Assert.IsType<StepCancelled>(msg);
                Assert.Equal("stopped", cancelled.CancellationMessage);
                break;
            case StepOutcome.Processing:
                Assert.IsType<StepProcessing>(msg);
                break;
        }

        // Every subtype carries the shared ids forward.
        var result = Assert.IsAssignableFrom<IStepResult>(msg);
        Assert.Equal(m.WorkflowId, result.WorkflowId);
        Assert.Equal(m.StepId, result.StepId);
        Assert.Equal(m.ProcessorId, result.ProcessorId);
        Assert.Equal(m.CorrelationId, result.CorrelationId);
        Assert.Equal(m.ExecutionId, result.ExecutionId);

        // Non-destructive: REINJECT deletes NOTHING — BOTH overloads.
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(),   Arg.Any<CommandFlags>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
    }
}
