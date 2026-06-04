using System.Diagnostics.Metrics;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestrator.Consumers;
using Orchestrator.Dispatch;
using Orchestrator.L1;
using Orchestrator.Observability;
using StackExchange.Redis;
using Xunit;
using ExecutionResult = Messaging.Contracts.ExecutionResult;   // disambiguate from MassTransit.ExecutionResult

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Phase 32 Plan 05 (req-3 orchestrator side / req-7 dedup; D-05/D-10/D-13) — the orchestrator half of
/// the in-flight stop. Goal-backward proof of the two <see cref="ResultConsumer"/> edits:
/// <list type="bullet">
///   <item><b>Dedup counter (D-10):</b> the existing <c>flag[m.H]=="Ack"</c> drop gate increments
///   <c>orchestrator_result_deduped</c> exactly once (tagged ProcessorId) and still drops — no dispatch,
///   no flip.</item>
///   <item><b>Check-and-drop (req-3 / D-05):</b> when the cancelled marker
///   <c>skp:cancelled:{m.WorkflowId:D}</c> is set, the consumer ack-and-discards — NO dispatch, NO
///   <c>orchestrator_result_deduped</c> increment, NO <c>StringSetAsync</c> to any key.</item>
///   <item><b>WorkflowId-keyed:</b> a result for a DIFFERENT (un-cancelled) workflow proceeds normally
///   even though some OTHER workflow's marker is set.</item>
///   <item><b>D-13 guard:</b> the cancelled path reads <c>L2ProjectionKeys.Cancelled(...)</c> ONLY,
///   never a <c>L2ProjectionKeys.Flag(...)</c> key beyond the existing flag-gate read, and writes no
///   flag key.</item>
/// </list>
/// The orchestrator does NOT set the marker and does NOT trip the breaker — it only checks-and-drops
/// (the trip is processor-side per D-01). Analog: <see cref="ResultAckTests"/> + <see cref="ResultConsumeTests"/>.
/// </summary>
public sealed class ResultCheckAndDropFacts
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

    /// <summary>Builds a ResultConsumer over the supplied redis mux + a real (no-collector) metrics holder.</summary>
    private static ResultConsumer Build(
        WorkflowL1Store store, IStepDispatcher dispatcher, IConnectionMultiplexer redis, OrchestratorMetrics metrics)
        => new(store, new StepAdvancement(), dispatcher, redis, metrics, NullLogger<ResultConsumer>.Instance);

    /// <summary>A real metrics holder whose <c>orchestrator_result_deduped</c> increments we can observe via a MeterListener.</summary>
    private static (OrchestratorMetrics Metrics, Func<long> DedupCount, Func<List<string>> DedupTagKeys) DedupObserved()
    {
        var meterFactory = new ServiceCollection()
            .AddMetrics().BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();
        var metrics = new OrchestratorMetrics(meterFactory);

        long count = 0;
        var tagKeys = new List<string>();
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "orchestrator_result_deduped")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            Interlocked.Add(ref count, measurement);
            foreach (var t in tags)
                tagKeys.Add(t.Key);
        });
        listener.Start();

        return (metrics, () => Interlocked.Read(ref count), () => tagKeys);
    }

    // ----- 1. flag[m.H]=="Ack" -> ONE dedup increment, dropped (no dispatch) ----------------------

    [Fact]
    public async Task FlagAlreadyAck_IncrementsDedupOnce_Drops_NoDispatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = Substitute.For<IStepDispatcher>();

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

        var resultH = "ac4eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci => ((RedisKey)ci[0]).ToString() == L2ProjectionKeys.Flag(resultH) ? (RedisValue)"Ack" : RedisValue.Null);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        var (metrics, dedupCount, dedupTagKeys) = DedupObserved();
        var consumer = Build(store, dispatcher, mux, metrics);
        var result = new ExecutionResult(workflowId, completedStepId, Guid.NewGuid(), StepOutcome.Completed)
        {
            CorrelationId = Guid.NewGuid(),
            EntryId = Guid.NewGuid().ToString("N"),
            H = resultH,
        };

        await consumer.Consume(OrchestratorTestStubs.Context(result, ct));

        // Exactly ONE orchestrator_result_deduped increment at the flag-Ack gate, tagged ProcessorId.
        Assert.Equal(1, dedupCount());
        Assert.Contains("ProcessorId", dedupTagKeys());

        // Dropped — no dispatch.
        await dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // No flip / no write of any kind on the dropped path.
        await db.DidNotReceive().StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
            Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    // ----- 2. cancelled marker set -> ack-and-discard (no dispatch, no dedup counter, no write) ----

    [Fact]
    public async Task CancelledMarkerSet_AckAndDiscards_NoDispatch_NoDedup_NoWrite()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = Substitute.For<IStepDispatcher>();

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

        var resultH = "cancccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"[..64];
        // flag[resultH] -> Null (pass the flag gate); cancelled[workflowId] -> "true" (drop here).
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci => ((RedisKey)ci[0]).ToString() == L2ProjectionKeys.Cancelled(workflowId)
                ? (RedisValue)L2ProjectionKeys.CancelledMarkerValue
                : RedisValue.Null);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        var (metrics, dedupCount, _) = DedupObserved();
        var consumer = Build(store, dispatcher, mux, metrics);
        var result = new ExecutionResult(workflowId, completedStepId, Guid.NewGuid(), StepOutcome.Completed)
        {
            CorrelationId = Guid.NewGuid(),
            EntryId = Guid.NewGuid().ToString("N"),
            H = resultH,
        };

        // Ack-and-discard — no throw.
        await consumer.Consume(OrchestratorTestStubs.Context(result, ct));

        // NO dispatch.
        await dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // NO dedup counter (this is the cancelled drop, NOT a flag[H]==Ack drop).
        Assert.Equal(0, dedupCount());

        // NO StringSetAsync to ANY key across all overloads (the orchestrator never WRITES the marker — D-13).
        Assert.DoesNotContain(db.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IDatabase.StringSetAsync));

        // The cancelled path read the cancelled marker only — never a Flag(...) key (D-13). The single
        // expected flag read is the existing flag-gate read of Flag(resultH); no OTHER flag key is touched.
        var cancelledKey = L2ProjectionKeys.Cancelled(workflowId);
        Assert.Contains(db.ReceivedCalls(), c =>
            c.GetMethodInfo().Name == nameof(IDatabase.StringGetAsync)
            && c.GetArguments().Length > 0 && c.GetArguments()[0] is RedisKey gk && gk.ToString() == cancelledKey);
    }

    // ----- 3. a DIFFERENT workflow's marker set -> THIS result proceeds normally (workflowId-keyed) -

    [Fact]
    public async Task OtherWorkflowCancelled_ThisResultProceeds_Dispatches()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = new RecordingDispatcher();

        var workflowId = Guid.NewGuid();            // THIS result's workflow — NOT cancelled
        var otherWorkflowId = Guid.NewGuid();       // a DIFFERENT workflow whose marker IS set
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

        var manifestEntryId = Guid.NewGuid().ToString("D");
        var itemEntryId = "aa" + Guid.NewGuid().ToString("N");
        var manifestJson = System.Text.Json.JsonSerializer.Serialize(new[] { itemEntryId });
        var resultH = "ddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddffff";

        // The OTHER workflow's marker is set; flag[resultH] Null; data[manifestEntryId] -> one-item manifest.
        // THIS result's cancelled[workflowId] is NOT set -> it must proceed.
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var key = ((RedisKey)ci[0]).ToString();
                if (key == L2ProjectionKeys.Cancelled(otherWorkflowId)) return (RedisValue)L2ProjectionKeys.CancelledMarkerValue;
                if (key == L2ProjectionKeys.ExecutionData(manifestEntryId)) return (RedisValue)manifestJson;
                return RedisValue.Null;
            });
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        var (metrics, _, _) = DedupObserved();
        var consumer = Build(store, dispatcher, mux, metrics);
        var result = new ExecutionResult(workflowId, completedStepId, Guid.NewGuid(), StepOutcome.Completed)
        {
            CorrelationId = Guid.NewGuid(),
            EntryId = manifestEntryId,
            H = resultH,
        };

        await consumer.Consume(OrchestratorTestStubs.Context(result, ct));

        // THIS un-cancelled workflow's result proceeded to the normal L1 fan-out path.
        var dispatched = Assert.Single(dispatcher.Calls);
        Assert.Equal(workflowId, dispatched.WorkflowId);
        Assert.Equal(nextStepId, dispatched.StepId);
        Assert.Equal(nextProcessorId, dispatched.ProcessorId);
        Assert.Equal(itemEntryId, dispatched.EntryId);
    }

    /// <summary>A concrete recording <see cref="IStepDispatcher"/> (mirrors ResultAckTests.RecordingDispatcher).</summary>
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
