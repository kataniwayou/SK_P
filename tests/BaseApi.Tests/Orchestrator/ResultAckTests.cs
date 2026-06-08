using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestrator.Consumers;
using Orchestrator.Dispatch;
using Orchestrator.L1;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// ORCH-RESULT-ACK-01 goal-backward proof of the result-consume ack split (gate OPEN):
/// <list type="bullet">
///   <item>an unknown <c>(workflowId, stepId)</c> result acks (no throw, no dispatch);</item>
///   <item>a completed step with no matching next step acks (no throw, no dispatch);</item>
///   <item>a Completed result with a matching next step dispatches ONE straight-through continuation
///   carrying the result's Guid EntryId + a regenerated executionId;</item>
///   <item>an injected infra fault on the broker <c>Send</c> propagates (does not ack-swallow).</item>
/// </list>
/// <para>
/// Phase 43 (D-03/D-06e): the result path is L1-ONLY — the effect-first dedup gate (RETIRE-01) and the
/// content-addressed manifest unbundle (RETIRE-02) are gone, so the consumer no longer takes an
/// <c>IConnectionMultiplexer</c>. One <see cref="StepCompleted"/> = one item. (D-07: exercised via
/// <see cref="StepCompletedConsumer"/>, the Completed arm of the TypedResultConsumer<T> family that
/// replaced the retired ResultConsumer.)
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

    private static StepCompletedConsumer Build(WorkflowL1Store store, IStepDispatcher dispatcher) =>
        // Phase 43: L1-only — no IConnectionMultiplexer ctor param (the dedup gate + manifest read are retired).
        // D-07: StepCompletedConsumer (Outcome=Completed) — the body that replaced the retired ResultConsumer.
        new(store, new StepAdvancement(), dispatcher, OrchestratorTestStubs.Metrics(),
            NullLogger<StepCompleted>.Instance);

    // ----- R5: an id ABSENT from L1 acks cleanly, lifecycle-agnostic (no throw, no _error) -------

    [Fact]
    public async Task ResultForIdAbsentFromL1_AcksGracefully_NoThrow_NoDispatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = Substitute.For<IStepDispatcher>();
        var store = new WorkflowL1Store(); // empty — the id is absent from L1 regardless of lifecycle

        var consumer = Build(store, dispatcher);
        var result = new StepCompleted(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
        };

        // No throw (clean business-ack) — the message is consumed, not redelivered, not DLQ'd.
        await consumer.Consume(OrchestratorTestStubs.Context(result, ct));

        await dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ----- unknown (workflowId, stepId) -> ack (no throw, no dispatch) ---------------------------

    [Fact]
    public async Task UnknownWorkflowStep_Acks_NoThrow_NoDispatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = Substitute.For<IStepDispatcher>();
        var store = new WorkflowL1Store(); // empty — workflow absent

        var consumer = Build(store, dispatcher);
        var result = new StepCompleted(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
        };

        // No throw (clean ack).
        await consumer.Consume(OrchestratorTestStubs.Context(result, ct));

        await dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ----- completed step with NO matching next step -> ack (no throw, no dispatch) --------------

    [Fact]
    public async Task NoMatchingNextStep_Acks_NoThrow_NoDispatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = Substitute.For<IStepDispatcher>();

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

        var consumer = Build(store, dispatcher);
        var result = new StepCompleted(workflowId, completedStepId, Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
        };

        await consumer.Consume(OrchestratorTestStubs.Context(result, ct));

        await dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ----- straight-through: a matched next step dispatches ONE continuation (D-03) --------------

    [Fact]
    public async Task MatchedNextStep_DispatchesOneContinuation_StraightThrough()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = new RecordingDispatcher();

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

        var resultEntryId = Guid.NewGuid();   // the StepCompleted's Guid data key (D-06a)
        var consumer = Build(store, dispatcher);
        var result = new StepCompleted(workflowId, completedStepId, Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            EntryId = resultEntryId,
        };

        await consumer.Consume(OrchestratorTestStubs.Context(result, ct));

        // ONE continuation dispatched carrying the result's Guid EntryId + the matched successor's ids
        // (straight-through — no dedup gate, no manifest unbundle).
        var dispatched = Assert.Single(dispatcher.Calls);
        Assert.Equal(workflowId, dispatched.WorkflowId);
        Assert.Equal(nextStepId, dispatched.StepId);
        Assert.Equal(nextProcessorId, dispatched.ProcessorId);
        Assert.Equal(resultEntryId, dispatched.EntryId);
        Assert.NotEqual(Guid.Empty, dispatched.ExecutionId); // regenerated lineage
    }

    // ----- injected infra fault on Send propagates (does not ack-swallow) ------------------------

    [Fact]
    public async Task InfraFaultOnSend_Propagates()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = Substitute.For<IStepDispatcher>();
        dispatcher.DispatchAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new MassTransitException("stub: broker Send fault"));

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

        var consumer = Build(store, dispatcher);
        var result = new StepCompleted(workflowId, completedStepId, Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            EntryId = Guid.NewGuid(),
        };

        // Infra fault must NOT be ack-swallowed — it propagates so the bounded retry -> _error.
        await Assert.ThrowsAsync<MassTransitException>(
            () => consumer.Consume(OrchestratorTestStubs.Context(result, ct)));
    }

    /// <summary>
    /// A concrete recording <see cref="IStepDispatcher"/> — captures each <c>DispatchAsync</c> call's
    /// args so the dispatch assertions are deterministic (avoids NSubstitute matcher fragility when the
    /// same captured call is asserted with mixed concrete/Arg matchers). Phase 43: entryId is a Guid.
    /// </summary>
    private sealed class RecordingDispatcher : IStepDispatcher
    {
        public sealed record Call(Guid WorkflowId, Guid StepId, Guid ProcessorId, string Payload,
            Guid CorrelationId, Guid ExecutionId, Guid EntryId);

        public List<Call> Calls { get; } = [];

        public Task DispatchAsync(Guid workflowId, Guid stepId, Guid processorId, string payload,
            Guid correlationId, Guid executionId, Guid entryId, CancellationToken ct)
        {
            Calls.Add(new Call(workflowId, stepId, processorId, payload, correlationId, executionId, entryId));
            return Task.CompletedTask;
        }
    }
}
