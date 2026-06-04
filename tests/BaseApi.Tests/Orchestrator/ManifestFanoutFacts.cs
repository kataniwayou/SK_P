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
/// req-6 (N x M manifest fan-out + redeliver dedup) hermetic facts for the Plan-31-04
/// <see cref="ResultConsumer"/> consumer half. The orchestrator reads the manifest from
/// <c>data[m.EntryId]</c>, deserializes a <c>string[]</c>, and dispatches one continuation per
/// (manifest item x matched <see cref="StepAdvancement.SelectNext"/> successor); each child carries the
/// item EntryId + a freshly regenerated executionId, and the deterministic child <c>H</c> (computed
/// inside <c>DispatchAsync</c> over (corr, wf, successorStep, successorProc, itemEntryId), executionId
/// excluded) makes an orchestrator redelivery reproduce the SAME child H -> deduped (Pitfall 5).
/// <list type="bullet">
///   <item><b>TwoItems</b> — a 2-item manifest x 1 successor -> 2 dispatches, each a distinct item EntryId.</item>
///   <item><b>EmptyManifest</b> — <c>"[]"</c> -> 0 dispatches, no throw, the flag flip is still attempted (ack).</item>
///   <item><b>RedeliverDedupes</b> — the child H per (item, successor) is deterministic; a result whose
///   <c>flag[m.H]</c> is already <c>"Ack"</c> is dropped (no dispatch) — the redelivery is deduped.</item>
/// </list>
/// All hermetic (no RealStack trait).
/// </summary>
public sealed class ManifestFanoutFacts
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

    /// <summary>
    /// A substitute Redis mux whose <c>data[manifestEntryId]</c> returns <paramref name="manifestJson"/>,
    /// whose <c>flag[ackH]</c> returns <c>"Ack"</c> (when supplied), and everything else returns Null.
    /// </summary>
    private static IConnectionMultiplexer Mux(string manifestEntryId, string manifestJson, string? ackH, out IDatabase db)
    {
        var local = Substitute.For<IDatabase>();
        local.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var key = ((RedisKey)ci[0]).ToString();
                if (key == L2ProjectionKeys.ExecutionData(manifestEntryId)) return (RedisValue)manifestJson;
                if (ackH is not null && key == L2ProjectionKeys.Flag(ackH)) return (RedisValue)"Ack";
                return RedisValue.Null;
            });
        db = local;
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(local);
        return mux;
    }

    private static ResultConsumer Build(WorkflowL1Store store, RecordingDispatcher dispatcher, IConnectionMultiplexer mux) =>
        new(store, new StepAdvancement(), dispatcher, mux, OrchestratorTestStubs.Metrics(), NullLogger<ResultConsumer>.Instance);

    // ----- a 2-item manifest x 1 successor -> 2 dispatches, distinct item EntryIds ----------------

    [Fact]
    public async Task TwoItemManifest_OneSuccessor_FansOutTwoDispatches_DistinctEntryIds()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();
        var nextProcessorId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Completed, nextProcessorId, "{\"n\":1}"),
        };
        var store = new WorkflowL1Store();
        SeedWorkflow(store, workflowId, steps);

        var manifestEntryId = Guid.NewGuid().ToString("D");
        var itemA = MessageIdentity.HashBlob("blobA");
        var itemB = MessageIdentity.HashBlob("blobB");
        var manifestJson = System.Text.Json.JsonSerializer.Serialize(new[] { itemA, itemB });
        var resultH = MessageIdentity.ComputeH(Guid.NewGuid(), workflowId, completedStepId, Guid.NewGuid(), manifestEntryId);

        var dispatcher = new RecordingDispatcher();
        var mux = Mux(manifestEntryId, manifestJson, ackH: null, out _);
        var consumer = Build(store, dispatcher, mux);

        var result = new ExecutionResult(workflowId, completedStepId, Guid.NewGuid(), StepOutcome.Completed)
        {
            CorrelationId = Guid.NewGuid(),
            EntryId = manifestEntryId,
            H = resultH,
        };

        await consumer.Consume(OrchestratorTestStubs.Context(result, ct));

        // 2 items x 1 successor = 2 dispatches, each to the successor's step/processor with a DISTINCT item EntryId.
        Assert.Equal(2, dispatcher.Calls.Count);
        Assert.All(dispatcher.Calls, c => Assert.Equal(nextStepId, c.StepId));
        Assert.All(dispatcher.Calls, c => Assert.Equal(nextProcessorId, c.ProcessorId));
        Assert.Equal(new[] { itemA, itemB }.OrderBy(x => x), dispatcher.Calls.Select(c => c.EntryId).OrderBy(x => x));
        Assert.Equal(2, dispatcher.Calls.Select(c => c.EntryId).Distinct().Count());
    }

    // ----- an empty manifest "[]" -> 0 dispatches, no throw, flag flip attempted (ack) ------------

    [Fact]
    public async Task EmptyManifest_ZeroDispatches_NoThrow_StillFlipsFlag()
    {
        var ct = TestContext.Current.CancellationToken;

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

        var manifestEntryId = Guid.NewGuid().ToString("D");
        var resultH = MessageIdentity.ComputeH(Guid.NewGuid(), workflowId, completedStepId, Guid.NewGuid(), manifestEntryId);

        var dispatcher = new RecordingDispatcher();
        var mux = Mux(manifestEntryId, manifestJson: "[]", ackH: null, out var db);
        var consumer = Build(store, dispatcher, mux);

        var result = new ExecutionResult(workflowId, completedStepId, Guid.NewGuid(), StepOutcome.Completed)
        {
            CorrelationId = Guid.NewGuid(),
            EntryId = manifestEntryId,
            H = resultH,
        };

        await consumer.Consume(OrchestratorTestStubs.Context(result, ct));   // no throw — terminal manifest

        Assert.Empty(dispatcher.Calls);   // zero items -> zero fan-out
        // The effect-first flip is still attempted (a terminal manifest is observed-and-acked).
        var flagKey = L2ProjectionKeys.Flag(resultH);
        Assert.Contains(db.ReceivedCalls(), c =>
            c.GetMethodInfo().Name == nameof(IDatabase.StringSetAsync)
            && c.GetArguments().Length > 1
            && c.GetArguments()[0] is RedisKey sk && sk.ToString() == flagKey
            && c.GetArguments()[1] is RedisValue sv && sv.ToString() == "Ack");
    }

    // ----- redeliver: deterministic child H; an already-Ack result is dropped (no dispatch) -------

    [Fact]
    public async Task Redeliver_ChildHIsDeterministic_AndAckResultIsDropped()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();
        var nextProcessorId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Completed, nextProcessorId, "{}"),
        };
        var store = new WorkflowL1Store();
        SeedWorkflow(store, workflowId, steps);

        var manifestEntryId = Guid.NewGuid().ToString("D");
        var itemEntryId = MessageIdentity.HashBlob("the-item");
        var manifestJson = System.Text.Json.JsonSerializer.Serialize(new[] { itemEntryId });
        var resultH = MessageIdentity.ComputeH(correlationId, workflowId, completedStepId, Guid.NewGuid(), manifestEntryId);

        // The child H per (item, successor) is DETERMINISTIC (executionId excluded): two "deliveries"
        // recompute the SAME value, so a redelivery reproduces it and is deduped at the next hop.
        var childH1 = MessageIdentity.ComputeH(correlationId, workflowId, nextStepId, nextProcessorId, itemEntryId);
        var childH2 = MessageIdentity.ComputeH(correlationId, workflowId, nextStepId, nextProcessorId, itemEntryId);
        Assert.Equal(childH1, childH2);

        // Delivery 1: flag[resultH] not Ack -> one fan-out dispatch.
        var dispatcher1 = new RecordingDispatcher();
        var mux1 = Mux(manifestEntryId, manifestJson, ackH: null, out _);
        var consumer1 = Build(store, dispatcher1, mux1);
        var result = new ExecutionResult(workflowId, completedStepId, Guid.NewGuid(), StepOutcome.Completed)
        {
            CorrelationId = correlationId,
            EntryId = manifestEntryId,
            H = resultH,
        };
        await consumer1.Consume(OrchestratorTestStubs.Context(result, ct));
        var dispatched = Assert.Single(dispatcher1.Calls);
        Assert.Equal(itemEntryId, dispatched.EntryId);

        // Delivery 2 (redelivery): flag[resultH] already "Ack" -> drop, NO extra dispatch (req-6).
        var dispatcher2 = new RecordingDispatcher();
        var mux2 = Mux(manifestEntryId, manifestJson, ackH: resultH, out _);
        var consumer2 = Build(store, dispatcher2, mux2);
        await consumer2.Consume(OrchestratorTestStubs.Context(result, ct));
        Assert.Empty(dispatcher2.Calls);
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
