using BaseConsole.Core.Health;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestrator.Consumers;
using Orchestrator.Dispatch;
using Orchestrator.L1;
using Xunit;
using ExecutionResult = Messaging.Contracts.ExecutionResult;   // disambiguate from MassTransit.ExecutionResult

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// ORCH-GATE-01 goal-backward proof of gate-closed never-drop on the result path:
/// <list type="bullet">
///   <item>with the startup gate CLOSED, <see cref="ResultConsumer.Consume"/> THROWS
///   <see cref="GateClosedException"/> — it does NOT ack-return (the throw is what reaches the
///   scheduled-redelivery middleware so the one-time result survives hydration);</item>
///   <item>after <c>MarkReady</c>, re-consuming the same result succeeds and dispatches its matching
///   continuation — the message that would have been redelivered is now processed.</item>
/// </list>
/// </summary>
public sealed class GateClosedRedeliverTests
{
    private static StepProjection Step(int entryCondition, Guid processorId, string payload, params Guid[] nextStepIds) =>
        new(EntryCondition: entryCondition, ProcessorId: processorId, Payload: payload, NextStepIds: [.. nextStepIds]);

    [Fact]
    public async Task GateClosed_Throws_ThenAfterMarkReady_Dispatches()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = Substitute.For<IStepDispatcher>();

        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();
        var nextProcessorId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Completed, nextProcessorId, "{}"),
        };
        var store = new WorkflowL1Store();
        store.Upsert(workflowId, new WorkflowL1([], "*/5 * * * *", Guid.NewGuid(), steps)
        {
            Liveness = new LivenessProjection(DateTime.UtcNow, Interval: 300, Status: "active"),
        });

        var gate = new StartupGate(); // left CLOSED
        var consumer = new ResultConsumer(
            gate, store, new StepAdvancement(), dispatcher, NullLogger<ResultConsumer>.Instance);

        var result = new ExecutionResult(workflowId, completedStepId, Guid.NewGuid(), StepOutcome.Completed)
        {
            CorrelationId = Guid.NewGuid(),
        };

        // Gate closed -> THROW (not ack), so redelivery reschedules the message.
        await Assert.ThrowsAsync<GateClosedException>(
            () => consumer.Consume(OrchestratorTestStubs.Context(result, ct)));

        await dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());

        // Hydration completes -> gate opens -> the redelivered message is now processed + dispatched.
        gate.MarkReady();
        await consumer.Consume(OrchestratorTestStubs.Context(result, ct));

        await dispatcher.Received(1).DispatchAsync(
            workflowId, nextStepId, nextProcessorId, Arg.Any<string>(),
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
