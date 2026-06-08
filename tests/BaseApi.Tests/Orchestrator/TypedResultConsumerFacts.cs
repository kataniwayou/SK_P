using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestrator.Consumers;
using Orchestrator.Dispatch;
using Orchestrator.L1;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Phase 46 / ORCH-01 (D-07): the <see cref="TypedResultConsumer{TMessage}"/> family advances workflow
/// steps off per-item <see cref="StepCompleted"/>/<see cref="StepFailed"/>/<see cref="StepCancelled"/>/
/// <see cref="StepProcessing"/> via its <c>Outcome</c> knob and <see cref="StepAdvancement.SelectNext"/>
/// — there is NO status if/switch; routing is purely by message type. Proves:
/// <list type="bullet">
///   <item>each sealed subclass advances ONLY the successor gated on its own outcome (e.g. a Failed-gated
///   successor advances for <see cref="StepFailedConsumer"/> but NOT <see cref="StepCompletedConsumer"/>),
///   preserving CorrelationId/WorkflowId lineage + seeding <c>entryId = m.EntryId</c>;</item>
///   <item>an L1 miss is a graceful business-ack — no throw, no dispatch;</item>
///   <item>a Keeper-INJECT'd <see cref="StepCompleted"/> is processed byte-indistinguishably from a
///   direct processor completion (same <see cref="StepCompletedConsumer"/>, identical DispatchAsync
///   effects) — the ORCH-01 proof.</item>
/// </list>
/// </summary>
public sealed class TypedResultConsumerFacts
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

    /// <summary>
    /// Builds the matching typed consumer for <paramref name="outcome"/> and feeds it a typed result of
    /// that same outcome — the per-type consumer + message pair routed purely by the subclass Outcome knob.
    /// The dispatcher is the only effect we assert; metrics are no-op test stubs.
    /// </summary>
    private static Task ConsumeFor(StepOutcome outcome, WorkflowL1Store store, IStepDispatcher dispatcher,
        Guid workflowId, Guid stepId, Guid processorId,
        Guid correlationId, Guid executionId, Guid entryId, CancellationToken ct)
    {
        var advancement = new StepAdvancement();
        return outcome switch
        {
            StepOutcome.Completed => new StepCompletedConsumer(store, advancement, dispatcher, OrchestratorTestStubs.Metrics(), NullLogger<StepCompleted>.Instance)
                .Consume(OrchestratorTestStubs.Context(new StepCompleted(workflowId, stepId, processorId) { CorrelationId = correlationId, ExecutionId = executionId, EntryId = entryId }, ct)),
            StepOutcome.Failed => new StepFailedConsumer(store, advancement, dispatcher, OrchestratorTestStubs.Metrics(), NullLogger<StepFailed>.Instance)
                .Consume(OrchestratorTestStubs.Context(new StepFailed(workflowId, stepId, processorId) { CorrelationId = correlationId, ExecutionId = executionId, EntryId = entryId }, ct)),
            StepOutcome.Cancelled => new StepCancelledConsumer(store, advancement, dispatcher, OrchestratorTestStubs.Metrics(), NullLogger<StepCancelled>.Instance)
                .Consume(OrchestratorTestStubs.Context(new StepCancelled(workflowId, stepId, processorId) { CorrelationId = correlationId, ExecutionId = executionId, EntryId = entryId }, ct)),
            StepOutcome.Processing => new StepProcessingConsumer(store, advancement, dispatcher, OrchestratorTestStubs.Metrics(), NullLogger<StepProcessing>.Instance)
                .Consume(OrchestratorTestStubs.Context(new StepProcessing(workflowId, stepId, processorId) { CorrelationId = correlationId, ExecutionId = executionId, EntryId = entryId }, ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
    }

    // ----- each subclass advances via SelectNext with ONLY its own Outcome ------------------------

    [Theory]
    [InlineData(StepOutcome.Completed)]
    [InlineData(StepOutcome.Failed)]
    [InlineData(StepOutcome.Cancelled)]
    [InlineData(StepOutcome.Processing)]
    [Trait("Phase", "46")]
    public async Task TypedResultConsumer_advances_via_SelectNext_outcome(StepOutcome outcome)
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = new RecordingDispatcher();

        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var matchStepId = Guid.NewGuid();          // gated on THIS outcome — must advance
        var matchProcessorId = Guid.NewGuid();
        const string matchPayload = "{\"go\":true}";
        var otherStepId = Guid.NewGuid();          // gated on a DIFFERENT outcome — must NOT advance

        // pick a different outcome int for the negative-control successor
        var otherOutcome = outcome == StepOutcome.Completed ? StepOutcome.Failed : StepOutcome.Completed;

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", matchStepId, otherStepId),
            [matchStepId] = Step((int)outcome, matchProcessorId, matchPayload),
            [otherStepId] = Step((int)otherOutcome, Guid.NewGuid(), "{}"),
        };
        var store = new WorkflowL1Store();
        SeedWorkflow(store, workflowId, steps);

        var correlationId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var entryId = Guid.NewGuid();

        await ConsumeFor(outcome, store, dispatcher, workflowId, completedStepId, Guid.NewGuid(),
            correlationId, executionId, entryId, ct);

        // EXACTLY the outcome-matched successor advances (the other-outcome successor is filtered out by
        // SelectNext using THIS subclass's Outcome knob — no status if/switch).
        var call = Assert.Single(dispatcher.Calls);
        Assert.Equal(workflowId, call.WorkflowId);             // lineage preserved
        Assert.Equal(correlationId, call.CorrelationId);
        Assert.Equal(matchStepId, call.StepId);                // the matched successor's ids
        Assert.Equal(matchProcessorId, call.ProcessorId);
        Assert.Equal(matchPayload, call.Payload);
        Assert.Equal(entryId, call.EntryId);                   // entryId = m.EntryId, straight through
        Assert.NotEqual(Guid.Empty, call.ExecutionId);         // regenerated lineage (NewId.NextGuid)
    }

    // ----- cross-outcome isolation: a Failed-gated successor advances for Failed but NOT Completed --

    [Fact]
    [Trait("Phase", "46")]
    public async Task StepCompletedConsumer_does_not_advance_a_Failed_gated_successor()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var failGatedStepId = Guid.NewGuid();

        // the only successor is Failed(2)-gated.
        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", failGatedStepId),
            [failGatedStepId] = Step((int)StepOutcome.Failed, Guid.NewGuid(), "{}"),
        };
        var store = new WorkflowL1Store();
        SeedWorkflow(store, workflowId, steps);

        // StepCompletedConsumer (Outcome=Completed) must NOT advance the Failed-gated successor.
        var completedDispatcher = new RecordingDispatcher();
        await ConsumeFor(StepOutcome.Completed, store, completedDispatcher, workflowId, completedStepId, Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ct);
        Assert.Empty(completedDispatcher.Calls);

        // StepFailedConsumer (Outcome=Failed) over the SAME L1 DOES advance it — proving the only knob is Outcome.
        var failedDispatcher = new RecordingDispatcher();
        await ConsumeFor(StepOutcome.Failed, store, failedDispatcher, workflowId, completedStepId, Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ct);
        Assert.Single(failedDispatcher.Calls);
    }

    // ----- L1 miss = graceful business-ack (no throw, no dispatch) --------------------------------

    [Fact]
    [Trait("Phase", "46")]
    public async Task L1_miss_acks_gracefully_no_throw_no_dispatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = new RecordingDispatcher();
        var store = new WorkflowL1Store();   // empty — every (wf,step) is a miss

        // No throw (clean business-ack), no dispatch.
        await ConsumeFor(StepOutcome.Completed, store, dispatcher, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ct);

        Assert.Empty(dispatcher.Calls);
    }

    // ----- ORCH-01: a Keeper-INJECT'd StepCompleted is indistinguishable from a direct one ---------

    [Fact]
    [Trait("Phase", "46")]
    public async Task Injected_StepCompleted_indistinguishable_from_direct()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var completedProcessorId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();
        var nextProcessorId = Guid.NewGuid();
        const string nextPayload = "{\"next\":true}";

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, completedProcessorId, "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Completed, nextProcessorId, nextPayload),
        };
        var store = new WorkflowL1Store();
        SeedWorkflow(store, workflowId, steps);

        var correlationId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var entryId = Guid.NewGuid();   // the SAME entryId/executionId on both — INJECT reconstructs identical ids

        // Two StepCompleted records with IDENTICAL ids: one a direct processor completion, one a
        // Keeper-INJECT reconstruction. They are the same record type — there is no flag distinguishing them.
        var direct = new StepCompleted(workflowId, completedStepId, completedProcessorId)
        { CorrelationId = correlationId, ExecutionId = executionId, EntryId = entryId };
        var injected = new StepCompleted(workflowId, completedStepId, completedProcessorId)
        { CorrelationId = correlationId, ExecutionId = executionId, EntryId = entryId };

        // The records themselves are equal (record value-equality) — the byte-indistinguishability premise.
        Assert.Equal(direct, injected);

        // Both flow through the SAME StepCompletedConsumer against the same L1 fixture.
        var directDispatcher = new RecordingDispatcher();
        await new StepCompletedConsumer(store, new StepAdvancement(), directDispatcher,
            OrchestratorTestStubs.Metrics(), NullLogger<StepCompleted>.Instance)
            .Consume(OrchestratorTestStubs.Context(direct, ct));

        var injectedDispatcher = new RecordingDispatcher();
        await new StepCompletedConsumer(store, new StepAdvancement(), injectedDispatcher,
            OrchestratorTestStubs.Metrics(), NullLogger<StepCompleted>.Instance)
            .Consume(OrchestratorTestStubs.Context(injected, ct));

        // Identical advancement effect: same count, same dispatched args (executionId is regenerated per
        // dispatch so it is excluded from the equality — every OTHER field must match exactly).
        var d = Assert.Single(directDispatcher.Calls);
        var i = Assert.Single(injectedDispatcher.Calls);
        Assert.Equal(d.WorkflowId, i.WorkflowId);
        Assert.Equal(d.StepId, i.StepId);
        Assert.Equal(d.ProcessorId, i.ProcessorId);
        Assert.Equal(d.Payload, i.Payload);
        Assert.Equal(d.CorrelationId, i.CorrelationId);
        Assert.Equal(d.EntryId, i.EntryId);
        Assert.Equal(nextStepId, d.StepId);            // both advanced the same matched successor
        Assert.Equal(nextProcessorId, d.ProcessorId);
        Assert.Equal(entryId, d.EntryId);              // entryId = m.EntryId, identical on both
    }

    /// <summary>
    /// A concrete recording <see cref="IStepDispatcher"/> capturing each <c>DispatchAsync</c> call's args
    /// (mirrors ResultAckTests.RecordingDispatcher) so dispatch assertions are deterministic.
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
