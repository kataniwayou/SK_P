using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestrator.Consumers;
using Orchestrator.L1;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// ORCH-01 (D-07) + Phase 71: the <see cref="TypedResultConsumer{TMessage}"/> family advances workflow
/// steps off per-item <see cref="StepCompleted"/>/<see cref="StepFailed"/>/<see cref="StepCancelled"/>/
/// <see cref="StepProcessing"/> via its <c>Outcome</c> knob — there is NO status if/switch; routing is purely
/// by message type. Since Phase 71 the consume path delegates to the L2-gated
/// <see cref="Orchestrator.Recovery.OrchestratorResultPipeline"/>, whose FORWARD pass owns the SelectNext
/// iteration + the downstream <see cref="EntryStepDispatch"/> send (to <c>queue:{nextProcessorId}</c>),
/// minting a fresh per-slot newEntryId. Proves:
/// <list type="bullet">
///   <item>each sealed subclass advances ONLY the successor gated on its own outcome (e.g. a Failed-gated
///   successor advances for <see cref="StepFailedConsumer"/> but NOT <see cref="StepCompletedConsumer"/>),
///   preserving CorrelationId/WorkflowId/ExecutionId lineage;</item>
///   <item>an L1 miss is a graceful business-ack — no throw, no dispatch;</item>
///   <item>a Keeper-INJECT'd <see cref="StepCompleted"/> is processed byte-indistinguishably from a
///   direct processor completion (same <see cref="StepCompletedConsumer"/>, identical dispatch effects).</item>
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
    /// Builds the matching typed consumer for <paramref name="outcome"/> over a forward-OK pipeline and feeds it
    /// a typed result of that same outcome. The downstream EntryStepDispatch (captured via <paramref name="send"/>)
    /// is the effect we assert; metrics are no-op test stubs.
    /// </summary>
    private static Task ConsumeFor(
        StepOutcome outcome, WorkflowL1Store store, OrchestratorPipelineTestKit.CapturingSendProvider send,
        Guid workflowId, Guid stepId, Guid processorId,
        Guid correlationId, Guid executionId, Guid entryId, CancellationToken ct)
    {
        var redis = OrchestratorTestStubs.ForwardOkL2(out _);
        var pipeline = OrchestratorTestStubs.Pipeline(redis, send);
        return outcome switch
        {
            StepOutcome.Completed => new StepCompletedConsumer(store, pipeline, OrchestratorTestStubs.Metrics(), NullLogger<StepCompleted>.Instance)
                .Consume(OrchestratorTestStubs.Context(new StepCompleted(workflowId, stepId, processorId) { CorrelationId = correlationId, ExecutionId = executionId, EntryId = entryId }, ct)),
            StepOutcome.Failed => new StepFailedConsumer(store, pipeline, OrchestratorTestStubs.Metrics(), NullLogger<StepFailed>.Instance)
                .Consume(OrchestratorTestStubs.Context(new StepFailed(workflowId, stepId, processorId) { CorrelationId = correlationId, ExecutionId = executionId, EntryId = entryId }, ct)),
            StepOutcome.Cancelled => new StepCancelledConsumer(store, pipeline, OrchestratorTestStubs.Metrics(), NullLogger<StepCancelled>.Instance)
                .Consume(OrchestratorTestStubs.Context(new StepCancelled(workflowId, stepId, processorId) { CorrelationId = correlationId, ExecutionId = executionId, EntryId = entryId }, ct)),
            StepOutcome.Processing => new StepProcessingConsumer(store, pipeline, OrchestratorTestStubs.Metrics(), NullLogger<StepProcessing>.Instance)
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
        var send = new OrchestratorPipelineTestKit.CapturingSendProvider();

        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var matchStepId = Guid.NewGuid();          // gated on THIS outcome — must advance
        var matchProcessorId = Guid.NewGuid();
        const string matchPayload = "{\"go\":true}";
        var otherStepId = Guid.NewGuid();          // gated on a DIFFERENT outcome — must NOT advance

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

        await ConsumeFor(outcome, store, send, workflowId, completedStepId, Guid.NewGuid(),
            correlationId, executionId, entryId, ct);

        // EXACTLY the outcome-matched successor advances (the other-outcome successor is filtered out by
        // SelectNext using THIS subclass's Outcome knob — no status if/switch). The pipeline FORWARD pass
        // dispatches it to queue:{matchProcessorId} with a minted newEntryId.
        var (uri, dispatch) = Assert.Single(send.SentDispatch);
        Assert.Equal($"queue:{matchProcessorId:D}", uri.ToString());
        Assert.Equal(workflowId, dispatch.WorkflowId);             // lineage preserved
        Assert.Equal(correlationId, dispatch.CorrelationId);
        Assert.Equal(executionId, dispatch.ExecutionId);           // inbound execution lineage threaded through
        Assert.Equal(matchStepId, dispatch.StepId);                // the matched successor's ids
        Assert.Equal(matchProcessorId, dispatch.ProcessorId);
        Assert.Equal(matchPayload, dispatch.Payload);
        Assert.NotEqual(Guid.Empty, dispatch.EntryId);             // a minted per-slot newEntryId
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

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", failGatedStepId),
            [failGatedStepId] = Step((int)StepOutcome.Failed, Guid.NewGuid(), "{}"),
        };
        var store = new WorkflowL1Store();
        SeedWorkflow(store, workflowId, steps);

        // StepCompletedConsumer (Outcome=Completed) must NOT advance the Failed-gated successor.
        var completedSend = new OrchestratorPipelineTestKit.CapturingSendProvider();
        await ConsumeFor(StepOutcome.Completed, store, completedSend, workflowId, completedStepId, Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ct);
        Assert.Empty(completedSend.SentDispatch);

        // StepFailedConsumer (Outcome=Failed) over the SAME L1 DOES advance it — proving the only knob is Outcome.
        var failedSend = new OrchestratorPipelineTestKit.CapturingSendProvider();
        await ConsumeFor(StepOutcome.Failed, store, failedSend, workflowId, completedStepId, Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ct);
        Assert.Single(failedSend.SentDispatch);
    }

    // ----- L1 miss = graceful business-ack (no throw, no dispatch) --------------------------------

    [Fact]
    [Trait("Phase", "46")]
    public async Task L1_miss_acks_gracefully_no_throw_no_dispatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var send = new OrchestratorPipelineTestKit.CapturingSendProvider();
        var store = new WorkflowL1Store();   // empty — every (wf,step) is a miss

        // No throw (clean business-ack), no dispatch.
        await ConsumeFor(StepOutcome.Completed, store, send, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ct);

        Assert.Empty(send.SentDispatch);
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

        var direct = new StepCompleted(workflowId, completedStepId, completedProcessorId)
        { CorrelationId = correlationId, ExecutionId = executionId, EntryId = entryId };
        var injected = new StepCompleted(workflowId, completedStepId, completedProcessorId)
        { CorrelationId = correlationId, ExecutionId = executionId, EntryId = entryId };

        // The records themselves are equal (record value-equality) — the byte-indistinguishability premise.
        Assert.Equal(direct, injected);

        // Both flow through the SAME StepCompletedConsumer (forward-OK pipeline) against the same L1 fixture.
        var directSend = new OrchestratorPipelineTestKit.CapturingSendProvider();
        await new StepCompletedConsumer(store, OrchestratorTestStubs.Pipeline(OrchestratorTestStubs.ForwardOkL2(out _), directSend),
            OrchestratorTestStubs.Metrics(), NullLogger<StepCompleted>.Instance)
            .Consume(OrchestratorTestStubs.Context(direct, ct));

        var injectedSend = new OrchestratorPipelineTestKit.CapturingSendProvider();
        await new StepCompletedConsumer(store, OrchestratorTestStubs.Pipeline(OrchestratorTestStubs.ForwardOkL2(out _), injectedSend),
            OrchestratorTestStubs.Metrics(), NullLogger<StepCompleted>.Instance)
            .Consume(OrchestratorTestStubs.Context(injected, ct));

        // Identical advancement effect: same count, same dispatched args (the minted newEntryId is per-slot
        // fresh so it is excluded from the equality — every OTHER field must match exactly).
        var d = Assert.Single(directSend.SentDispatch).Dispatch;
        var i = Assert.Single(injectedSend.SentDispatch).Dispatch;
        Assert.Equal(d.WorkflowId, i.WorkflowId);
        Assert.Equal(d.StepId, i.StepId);
        Assert.Equal(d.ProcessorId, i.ProcessorId);
        Assert.Equal(d.Payload, i.Payload);
        Assert.Equal(d.CorrelationId, i.CorrelationId);
        Assert.Equal(d.ExecutionId, i.ExecutionId);
        Assert.Equal(nextStepId, d.StepId);            // both advanced the same matched successor
        Assert.Equal(nextProcessorId, d.ProcessorId);
    }

    // ----- RESIL-03 (R3): at-least-once / no-collapse on duplicate StepCompleted delivery ----------

    /// <summary>
    /// RESIL-03: the execution path is at-least-once and carries NO dedup key, so delivering the SAME
    /// <see cref="StepCompleted"/> (identical ids) TWICE into ONE forward-OK <see cref="StepCompletedConsumer"/>
    /// sharing ONE capturing send provider reproduces the dispatch effect TWICE (no collapse to 1, no throw).
    /// Each delivery uses a DISTINCT broker MessageId (the gate key) so both take the FORWARD branch.
    /// </summary>
    [Fact]
    [Trait("Phase", "47")]
    public async Task Duplicate_StepCompleted_reproduces_effect_no_collapse()
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
        var entryId = Guid.NewGuid();

        var msg = new StepCompleted(workflowId, completedStepId, completedProcessorId)
        { CorrelationId = correlationId, ExecutionId = executionId, EntryId = entryId };

        var send = new OrchestratorPipelineTestKit.CapturingSendProvider();
        var consumer = new StepCompletedConsumer(store,
            OrchestratorTestStubs.Pipeline(OrchestratorTestStubs.ForwardOkL2(out _), send),
            OrchestratorTestStubs.Metrics(), NullLogger<StepCompleted>.Instance);

        // Deliver the SAME message TWICE, each with a DISTINCT broker MessageId (both FORWARD).
        await consumer.Consume(OrchestratorTestStubs.Context(msg, ct, Guid.NewGuid()));
        await consumer.Consume(OrchestratorTestStubs.Context(msg, ct, Guid.NewGuid()));

        // No collapse: the second identical delivery is NOT deduped — the dispatch effect fires twice, no throw.
        Assert.Equal(2, send.SentDispatch.Count);
        Assert.Equal(nextStepId, send.SentDispatch[0].Dispatch.StepId);
        Assert.Equal(nextStepId, send.SentDispatch[1].Dispatch.StepId);
        Assert.Equal(nextProcessorId, send.SentDispatch[0].Dispatch.ProcessorId);
        Assert.Equal(nextProcessorId, send.SentDispatch[1].Dispatch.ProcessorId);
    }
}
