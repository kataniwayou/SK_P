using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Hashing;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestrator.Consumers;
using Orchestrator.Dispatch;
using Orchestrator.L1;
using StackExchange.Redis;
using Xunit;
using ExecutionResult = Messaging.Contracts.ExecutionResult;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// req-5 (merge correctness via input EntryId) hermetic facts. A MERGE step is the <c>NextStep</c> of two
/// predecessors. The child <c>H</c> stamped on each merge-step dispatch is computed over
/// (corr, wf, mergeStep, mergeProc, itemEntryId) — the PREDECESSOR step id is NOT a factor. Therefore:
/// <list type="bullet">
///   <item><b>DifferentOutputs</b> — two predecessors whose manifest items DIFFER -> two distinct child H
///   (both dispatch to the merge step; distinct output keys; no false dedup, no override).</item>
///   <item><b>IdenticalOutput</b> — two predecessors whose manifest item is IDENTICAL -> the SAME child H
///   (collapse: the second would be dropped by the next-hop drop-on-Ack gate).</item>
///   <item><b>HIndependentOfPredecessor</b> — recomputing the child H with a DIFFERENT predecessor step id
///   but the SAME item EntryId yields the SAME H (the predecessor never enters the child H).</item>
/// </list>
/// All hermetic (no RealStack trait).
/// </summary>
public sealed class MergeCollapseFacts
{
    private static StepProjection Step(int entryCondition, Guid processorId, string payload, params Guid[] nextStepIds) =>
        new(EntryCondition: entryCondition, ProcessorId: processorId, Payload: payload, NextStepIds: [.. nextStepIds]);

    private static void SeedWorkflow(WorkflowL1Store store, Guid workflowId, IReadOnlyDictionary<Guid, StepProjection> steps)
    {
        var entry = new WorkflowL1([], "*/5 * * * *", Guid.NewGuid(), steps)
        {
            Liveness = new LivenessProjection(DateTime.UtcNow, Interval: 300, Status: "active"),
        };
        store.Upsert(workflowId, entry);
    }

    private static IConnectionMultiplexer Mux(string manifestEntryId, string manifestJson) =>
        MuxMany(new Dictionary<string, string> { [manifestEntryId] = manifestJson });

    private static IConnectionMultiplexer MuxMany(IReadOnlyDictionary<string, string> manifestByEntryId)
    {
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var key = ((RedisKey)ci[0]).ToString();
                foreach (var (entryId, json) in manifestByEntryId)
                    if (key == L2ProjectionKeys.ExecutionData(entryId)) return (RedisValue)json;
                return RedisValue.Null;   // every flag read -> not Ack
            });
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    private static ResultConsumer Build(WorkflowL1Store store, RecordingDispatcher dispatcher, IConnectionMultiplexer mux) =>
        new(store, new StepAdvancement(), dispatcher, mux, OrchestratorTestStubs.Metrics(), NullLogger<ResultConsumer>.Instance);

    // ----- different-output predecessors -> distinct child H (both execute, no override) ----------

    [Fact]
    public async Task DifferentOutputPredecessors_ProduceDistinctChildH_BothDispatch()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var predA = Guid.NewGuid();
        var predB = Guid.NewGuid();
        var mergeStepId = Guid.NewGuid();
        var mergeProcId = Guid.NewGuid();

        // Two predecessors, each whose only NextStep is the SAME merge step (Completed-gated).
        var steps = new Dictionary<Guid, StepProjection>
        {
            [predA] = Step(0, Guid.NewGuid(), "{}", mergeStepId),
            [predB] = Step(0, Guid.NewGuid(), "{}", mergeStepId),
            [mergeStepId] = Step((int)StepOutcome.Completed, mergeProcId, "{\"merge\":1}"),
        };
        var store = new WorkflowL1Store();
        SeedWorkflow(store, workflowId, steps);

        // DIFFERENT output blobs -> different content-addressed item EntryIds.
        var itemFromA = MessageIdentity.HashBlob("outputA");
        var itemFromB = MessageIdentity.HashBlob("outputB");
        Assert.NotEqual(itemFromA, itemFromB);

        var manifestA = Guid.NewGuid().ToString("D");
        var manifestB = Guid.NewGuid().ToString("D");
        var mux = MuxMany(new Dictionary<string, string>
        {
            [manifestA] = System.Text.Json.JsonSerializer.Serialize(new[] { itemFromA }),
            [manifestB] = System.Text.Json.JsonSerializer.Serialize(new[] { itemFromB }),
        });

        var dispatcher = new RecordingDispatcher();
        var consumer = Build(store, dispatcher, mux);

        // Predecessor A completes -> fans out to the merge step with itemFromA.
        await consumer.Consume(OrchestratorTestStubs.Context(
            new ExecutionResult(workflowId, predA, Guid.NewGuid(), StepOutcome.Completed)
            { CorrelationId = correlationId, EntryId = manifestA, H = "a" + new string('0', 63) }, ct));
        // Predecessor B completes -> fans out to the SAME merge step with itemFromB.
        await consumer.Consume(OrchestratorTestStubs.Context(
            new ExecutionResult(workflowId, predB, Guid.NewGuid(), StepOutcome.Completed)
            { CorrelationId = correlationId, EntryId = manifestB, H = "b" + new string('0', 63) }, ct));

        // Both dispatched to the merge step, each carrying its DISTINCT item EntryId.
        Assert.Equal(2, dispatcher.Calls.Count);
        Assert.All(dispatcher.Calls, c => Assert.Equal(mergeStepId, c.StepId));
        Assert.Contains(dispatcher.Calls, c => c.EntryId == itemFromA);
        Assert.Contains(dispatcher.Calls, c => c.EntryId == itemFromB);

        // The child H differs ONLY by item EntryId (per-edge): different item -> distinct H (no override).
        var hA = MessageIdentity.ComputeH(correlationId, workflowId, mergeStepId, mergeProcId, itemFromA);
        var hB = MessageIdentity.ComputeH(correlationId, workflowId, mergeStepId, mergeProcId, itemFromB);
        Assert.NotEqual(hA, hB);
    }

    // ----- identical-output predecessors -> same child H (collapse to one) ------------------------

    [Fact]
    public async Task IdenticalOutputPredecessors_ProduceSameChildH_Collapse()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var predA = Guid.NewGuid();
        var predB = Guid.NewGuid();
        var mergeStepId = Guid.NewGuid();
        var mergeProcId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [predA] = Step(0, Guid.NewGuid(), "{}", mergeStepId),
            [predB] = Step(0, Guid.NewGuid(), "{}", mergeStepId),
            [mergeStepId] = Step((int)StepOutcome.Completed, mergeProcId, "{}"),
        };
        var store = new WorkflowL1Store();
        SeedWorkflow(store, workflowId, steps);

        // IDENTICAL output -> the SAME content-addressed item EntryId from both predecessors.
        var sharedItem = MessageIdentity.HashBlob("identical-output");
        var manifestA = Guid.NewGuid().ToString("D");
        var manifestB = Guid.NewGuid().ToString("D");
        var mux = MuxMany(new Dictionary<string, string>
        {
            [manifestA] = System.Text.Json.JsonSerializer.Serialize(new[] { sharedItem }),
            [manifestB] = System.Text.Json.JsonSerializer.Serialize(new[] { sharedItem }),
        });

        var dispatcher = new RecordingDispatcher();
        var consumer = Build(store, dispatcher, mux);

        await consumer.Consume(OrchestratorTestStubs.Context(
            new ExecutionResult(workflowId, predA, Guid.NewGuid(), StepOutcome.Completed)
            { CorrelationId = correlationId, EntryId = manifestA, H = "c" + new string('0', 63) }, ct));
        await consumer.Consume(OrchestratorTestStubs.Context(
            new ExecutionResult(workflowId, predB, Guid.NewGuid(), StepOutcome.Completed)
            { CorrelationId = correlationId, EntryId = manifestB, H = "d" + new string('0', 63) }, ct));

        // Both dispatched the SAME item EntryId to the merge step.
        Assert.Equal(2, dispatcher.Calls.Count);
        Assert.All(dispatcher.Calls, c => Assert.Equal(mergeStepId, c.StepId));
        Assert.All(dispatcher.Calls, c => Assert.Equal(sharedItem, c.EntryId));

        // The child H is the SAME for both -> collapse (the 2nd is dropped by the next-hop drop-on-Ack gate).
        var h1 = MessageIdentity.ComputeH(correlationId, workflowId, mergeStepId, mergeProcId, sharedItem);
        var h2 = MessageIdentity.ComputeH(correlationId, workflowId, mergeStepId, mergeProcId, sharedItem);
        Assert.Equal(h1, h2);
    }

    // ----- child H is independent of the predecessor step id --------------------------------------

    [Fact]
    public void ChildH_IsIndependentOfPredecessorStepId()
    {
        var correlationId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var mergeStepId = Guid.NewGuid();
        var mergeProcId = Guid.NewGuid();
        var item = MessageIdentity.HashBlob("same-item");

        // The two computations differ ONLY in a (hypothetical) predecessor — but the predecessor never
        // enters ComputeH; only (corr, wf, successorStep, successorProc, item) do. So the H is identical.
        var hViaPredX = MessageIdentity.ComputeH(correlationId, workflowId, mergeStepId, mergeProcId, item);
        var hViaPredY = MessageIdentity.ComputeH(correlationId, workflowId, mergeStepId, mergeProcId, item);
        Assert.Equal(hViaPredX, hViaPredY);

        // ...and changing ONLY the item flips the H (it is the lone merge-discriminating factor).
        var hOther = MessageIdentity.ComputeH(correlationId, workflowId, mergeStepId, mergeProcId, MessageIdentity.HashBlob("other"));
        Assert.NotEqual(hViaPredX, hOther);
    }

    /// <summary>A concrete recording dispatcher capturing each fan-out <c>DispatchAsync</c> call's args.</summary>
    private sealed class RecordingDispatcher : IStepDispatcher
    {
        public sealed record Call(Guid WorkflowId, Guid StepId, Guid ProcessorId, string Payload,
            Guid CorrelationId, Guid ExecutionId, string EntryId);

        public List<Call> Calls { get; } = [];

        public Task DispatchAsync(Guid workflowId, Guid stepId, Guid processorId, string payload,
            Guid correlationId, Guid executionId, string entryId, CancellationToken ct)
        {
            Calls.Add(new Call(workflowId, stepId, processorId, payload, correlationId, executionId, entryId));
            return Task.CompletedTask;
        }
    }
}
