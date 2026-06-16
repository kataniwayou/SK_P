using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestrator.Consumers;
using Orchestrator.L1;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// ORCH-RESULT-ACK-01 goal-backward proof of the result-consume ack split:
/// <list type="bullet">
///   <item>an unknown <c>(workflowId, stepId)</c> result acks (no throw, no dispatch);</item>
///   <item>a completed step with no matching next step acks (no throw, no dispatch);</item>
///   <item>a Completed result with a matching next step dispatches ONE continuation to
///   <c>queue:{nextProcessorId}</c> carrying the inbound execution lineage (Phase 71: via the L2-gated
///   FORWARD pass, which mints a fresh per-slot newEntryId);</item>
///   <item>an injected infra fault on the broker <c>Send</c> propagates (does not ack-swallow).</item>
/// </list>
/// <para>
/// Phase 71: the result path is L2-gated — the consumer delegates to <see cref="Orchestrator.Recovery.OrchestratorResultPipeline"/>
/// (gate <c>exist L2[messageId]</c> → FORWARD/RECOVERY/cleanup). (D-07: exercised via
/// <see cref="StepCompletedConsumer"/>, the Completed arm of the TypedResultConsumer&lt;T&gt; family.)
/// </para>
/// </summary>
public sealed class ResultAckTests
{
    private static StepProjection Step(int entryCondition, Guid processorId, string payload, params Guid[] nextStepIds) =>
        new(EntryCondition: entryCondition, ProcessorId: processorId, Payload: payload, NextStepIds: [.. nextStepIds]);

    private static void SeedWorkflow(
        WorkflowL1Store store, Guid workflowId, IReadOnlyDictionary<Guid, StepProjection> steps)
    {
        var entry = new WorkflowL1([], "*/5 * * * *", Guid.NewGuid(), steps)
        {
            Liveness = new LivenessProjection(DateTime.UtcNow, Interval: 300, Status: "active"),
        };
        store.Upsert(workflowId, entry);
    }

    private static StepCompletedConsumer Build(WorkflowL1Store store, ISendEndpointProvider send) =>
        new(store, OrchestratorTestStubs.Pipeline(OrchestratorTestStubs.ForwardOkL2(out _), send),
            OrchestratorTestStubs.Metrics(), NullLogger<StepCompleted>.Instance);

    // ----- R5: an id ABSENT from L1 acks cleanly, lifecycle-agnostic (no throw, no _error) -------

    [Fact]
    public async Task ResultForIdAbsentFromL1_AcksGracefully_NoThrow_NoDispatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var send = new OrchestratorPipelineTestKit.CapturingSendProvider();
        var store = new WorkflowL1Store(); // empty — the id is absent from L1 regardless of lifecycle

        var consumer = Build(store, send);
        var result = new StepCompleted(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
        };

        // No throw (clean business-ack) — the message is consumed, not redelivered, not DLQ'd.
        await consumer.Consume(OrchestratorTestStubs.Context(result, ct));

        Assert.Empty(send.SentDispatch);
    }

    // ----- unknown (workflowId, stepId) -> ack (no throw, no dispatch) ---------------------------

    [Fact]
    public async Task UnknownWorkflowStep_Acks_NoThrow_NoDispatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var send = new OrchestratorPipelineTestKit.CapturingSendProvider();
        var store = new WorkflowL1Store(); // empty — workflow absent

        var consumer = Build(store, send);
        var result = new StepCompleted(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
        };

        await consumer.Consume(OrchestratorTestStubs.Context(result, ct));

        Assert.Empty(send.SentDispatch);
    }

    // ----- completed step with NO matching next step -> ack (no throw, no dispatch) --------------

    [Fact]
    public async Task NoMatchingNextStep_Acks_NoThrow_NoDispatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var send = new OrchestratorPipelineTestKit.CapturingSendProvider();

        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();

        // A Completed result, but the only next step is Failed(2)-gated — no match.
        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Failed, Guid.NewGuid(), "{}"),
        };
        var store = new WorkflowL1Store();
        SeedWorkflow(store, workflowId, steps);

        var consumer = Build(store, send);
        var result = new StepCompleted(workflowId, completedStepId, Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
        };

        await consumer.Consume(OrchestratorTestStubs.Context(result, ct));

        Assert.Empty(send.SentDispatch);
    }

    // ----- a matched next step dispatches ONE continuation (Phase 71: via the FORWARD pass) -------

    [Fact]
    public async Task MatchedNextStep_DispatchesOneContinuation()
    {
        var ct = TestContext.Current.CancellationToken;
        var send = new OrchestratorPipelineTestKit.CapturingSendProvider();

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
        SeedWorkflow(store, workflowId, steps);

        var resultEntryId = Guid.NewGuid();
        var resultExecutionId = Guid.NewGuid();   // the inbound instance lineage — must propagate unchanged
        var consumer = Build(store, send);
        var result = new StepCompleted(workflowId, completedStepId, Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = resultExecutionId,
            EntryId = resultEntryId,
        };

        await consumer.Consume(OrchestratorTestStubs.Context(result, ct));

        // ONE continuation dispatched to queue:{nextProcessorId} carrying the matched successor's ids + the
        // inbound execution lineage (the per-slot newEntryId is minted fresh, not the inbound EntryId).
        var (uri, dispatch) = Assert.Single(send.SentDispatch);
        Assert.Equal($"queue:{nextProcessorId:D}", uri.ToString());
        Assert.Equal(workflowId, dispatch.WorkflowId);
        Assert.Equal(nextStepId, dispatch.StepId);
        Assert.Equal(nextProcessorId, dispatch.ProcessorId);
        Assert.Equal(resultExecutionId, dispatch.ExecutionId);
        Assert.NotEqual(Guid.Empty, dispatch.EntryId);
    }

    // ----- injected infra fault on Send propagates (does not ack-swallow) ------------------------

    [Fact]
    public async Task InfraFaultOnSend_Propagates()
    {
        var ct = TestContext.Current.CancellationToken;
        // A send provider whose every Send throws — the pipeline's SendDispatch exhausts and PROPAGATES.
        var send = new ThrowingSendProvider();

        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Completed, Guid.NewGuid(), "{}"),
        };
        var store = new WorkflowL1Store();
        SeedWorkflow(store, workflowId, steps);

        var consumer = Build(store, send);
        var result = new StepCompleted(workflowId, completedStepId, Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            EntryId = Guid.NewGuid(),
        };

        // Infra fault must NOT be ack-swallowed — it propagates so the bounded retry -> _error.
        await Assert.ThrowsAsync<MassTransitException>(
            () => consumer.Consume(OrchestratorTestStubs.Context(result, ct)));
    }

    /// <summary>An <see cref="ISendEndpointProvider"/> whose every Send throws a broker fault.</summary>
    private sealed class ThrowingSendProvider : ISendEndpointProvider
    {
        public Task<ISendEndpoint> GetSendEndpoint(Uri address)
        {
            var endpoint = Substitute.For<ISendEndpoint>();
            endpoint.Send(Arg.Any<object>(), Arg.Any<CancellationToken>())
                .Returns<Task>(_ => throw new MassTransitException("stub: broker Send fault"));
            return Task.FromResult(endpoint);
        }

        public ConnectHandle ConnectSendObserver(ISendObserver observer) => throw new NotSupportedException();
    }
}
