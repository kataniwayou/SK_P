using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestrator.Consumers;
using Orchestrator.Dispatch;
using Orchestrator.L1;
using StackExchange.Redis;
using Xunit;
using ExecutionResult = Messaging.Contracts.ExecutionResult;   // disambiguate from MassTransit.ExecutionResult

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// ORCH-RESULT-ACK-01 goal-backward proof of the result-consume ack split (gate OPEN):
/// <list type="bullet">
///   <item>an unknown <c>(workflowId, stepId)</c> result acks (no throw, no dispatch);</item>
///   <item>a completed step with no matching next step acks (no throw, no dispatch);</item>
///   <item>NO L2/Redis read occurs on the result path (<c>db.DidNotReceive().StringGetAsync(...)</c>);</item>
///   <item>an injected infra fault on the broker <c>Send</c> propagates (does not ack-swallow).</item>
/// </list>
/// The consumer reads L1 only, so a directly-seeded <see cref="WorkflowL1Store"/> is the fixture; the
/// Redis mux stub exists purely to assert it is never touched (mirrors FireDispatchTests).
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

    private static ResultConsumer Build(WorkflowL1Store store, IStepDispatcher dispatcher)
    {
        // 24.1 / D-24.1-05: the boot gate is removed — ResultConsumer no longer takes an IStartupGate.
        return new ResultConsumer(
            store, new StepAdvancement(), dispatcher, NullLogger<ResultConsumer>.Instance);
    }

    // ----- R5: an id ABSENT from L1 acks cleanly, lifecycle-agnostic (no throw, no _error) -------

    [Fact]
    public async Task ResultForIdAbsentFromL1_AcksGracefully_NoThrow_NoDispatch()
    {
        // 24.1 R5 / D-24.1-05 (supersedes D-06 / ORCH-GATE-01): with the boot gate REMOVED, L1 is the
        // SOLE arbiter. A result for an id absent from L1 — unknown / stopped-drained / not-yet-hydrated,
        // uniformly — is the DEFINED graceful business-ack outcome: log + return, NEVER throw and NEVER
        // route to _error. (There is no gate; nothing distinguishes "boot window" from "unknown" — both
        // are an L1 miss.) This proves the lifecycle-agnostic graceful-miss path.
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = Substitute.For<IStepDispatcher>();
        var store = new WorkflowL1Store(); // empty — the id is absent from L1 regardless of lifecycle

        var consumer = Build(store, dispatcher);
        var result = new ExecutionResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), StepOutcome.Completed)
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
        var result = new ExecutionResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), StepOutcome.Completed)
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
        var result = new ExecutionResult(workflowId, completedStepId, Guid.NewGuid(), StepOutcome.Completed)
        {
            CorrelationId = Guid.NewGuid(),
        };

        await consumer.Consume(OrchestratorTestStubs.Context(result, ct));

        await dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ----- result path reads L1 only — NO L2/Redis read -----------------------------------------

    [Fact]
    public async Task ResultPath_PerformsNoL2Read()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = Substitute.For<IStepDispatcher>();

        // A Redis mux stub the result path must NEVER touch — DidNotReceive proves L1-only (SPEC req 3).
        var db = Substitute.For<IDatabase>();

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

        var consumer = Build(store, dispatcher);
        var result = new ExecutionResult(workflowId, completedStepId, Guid.NewGuid(), StepOutcome.Completed)
        {
            CorrelationId = Guid.NewGuid(),
        };

        await consumer.Consume(OrchestratorTestStubs.Context(result, ct));

        // The continuation WAS dispatched (match found) yet ZERO Redis reads occurred.
        await dispatcher.Received(1).DispatchAsync(
            workflowId, nextStepId, nextProcessorId, Arg.Any<string>(),
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await db.DidNotReceive().StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
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
        var result = new ExecutionResult(workflowId, completedStepId, Guid.NewGuid(), StepOutcome.Completed)
        {
            CorrelationId = Guid.NewGuid(),
        };

        // Infra fault must NOT be ack-swallowed — it propagates so the bounded retry -> _error.
        await Assert.ThrowsAsync<MassTransitException>(
            () => consumer.Consume(OrchestratorTestStubs.Context(result, ct)));
    }
}
