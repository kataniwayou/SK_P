using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
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
/// ORCH-RESULT-02 / ORCH-ADVANCE-02 goal-backward proof of the result-consume continuation-dispatch
/// path (ContinuationDispatch). Drives a <see cref="ResultConsumer"/> against a real
/// <see cref="WorkflowL1Store"/> + a synthetic <see cref="CapturingDispatchConsumer"/> bound to the
/// short-name endpoint <c>{processorId:D}</c> (the queue a <c>Send</c> to <c>queue:{processorId:D}</c>
/// lands on — RESEARCH assumption A2). Asserts, from the USER's perspective:
/// <list type="bullet">
///   <item>a <c>Completed</c> result whose completed step has a <c>PreviousCompleted</c>-gated next step
///   produces exactly ONE captured <see cref="EntryStepDispatch"/> on <c>queue:{nextStep.ProcessorId}</c>
///   with CorrelationId/EntryId/ExecutionId/WorkflowId == the result's and StepId/ProcessorId/Payload ==
///   the next-step L1 projection's;</item>
///   <item>that single dispatch is consumed exactly once (competing-consumer, not broadcast).</item>
/// </list>
/// </summary>
public sealed class ResultConsumeTests
{
    /// <summary>
    /// Builds the in-memory MassTransit harness, binding a <see cref="CapturingDispatchConsumer"/>
    /// short-name <c>ReceiveEndpoint($"{processorId:D}")</c> per distinct next-step processorId so a
    /// continuation's <c>Send</c> to <c>queue:{processorId:D}</c> is captured (FireDispatchTests pattern).
    /// </summary>
    private static ServiceProvider BuildHarness(IEnumerable<Guid> processorIds)
    {
        var ids = processorIds.Distinct().ToArray();
        return new ServiceCollection()
            .AddLogging()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<CapturingDispatchConsumer>();
                x.UsingInMemory((ctx, cfg) =>
                {
                    foreach (var processorId in ids)
                    {
                        cfg.ReceiveEndpoint($"{processorId:D}", e => e.ConfigureConsumer<CapturingDispatchConsumer>(ctx));
                    }

                    cfg.ConfigureEndpoints(ctx);
                });
            })
            .BuildServiceProvider(true);
    }

    private static StepProjection Step(int entryCondition, Guid processorId, string payload, params Guid[] nextStepIds) =>
        new(EntryCondition: entryCondition, ProcessorId: processorId, Payload: payload, NextStepIds: [.. nextStepIds]);

    private static void SeedWorkflow(
        WorkflowL1Store store, Guid workflowId, IReadOnlyDictionary<Guid, StepProjection> steps)
    {
        var entry = new WorkflowL1(
            EntryStepIds: [],
            Cron: "*/5 * * * *",
            JobId: Guid.NewGuid(),
            Steps: steps)
        {
            Liveness = new LivenessProjection(DateTime.UtcNow, Interval: 300, Status: "active"),
        };
        store.Upsert(workflowId, entry);
    }

    /// <summary>
    /// Builds a <see cref="ResultConsumer"/> over the real harness-backed <see cref="StepDispatcher"/>.
    /// Plan 31-04: ResultConsumer reads flag[m.H] (dedup gate) + data[m.EntryId] (manifest). The Redis mux
    /// returns Null for the flag (never Ack -> pass the gate) and, for <c>data[manifestEntryId]</c>, a
    /// JSON array of <paramref name="manifestItems"/> (the items fanned out). The StepDispatcher's own
    /// flag[H]=Pending pre-write targets the same no-op mux.
    /// </summary>
    private static ResultConsumer Build(
        WorkflowL1Store store, ISendEndpointProvider sendProvider, string manifestEntryId, params string[] manifestItems)
    {
        var manifestJson = System.Text.Json.JsonSerializer.Serialize(manifestItems);
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var key = ((RedisKey)ci[0]).ToString();
                return key == L2ProjectionKeys.ExecutionData(manifestEntryId)
                    ? (RedisValue)manifestJson
                    : RedisValue.Null;
            });
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        return new ResultConsumer(
            store, new StepAdvancement(), new StepDispatcher(sendProvider, OrchestratorTestStubs.NoopRedis(), OrchestratorTestStubs.Metrics()),
            mux, OrchestratorTestStubs.Metrics(), NullLogger<ResultConsumer>.Instance);
    }

    // ----- ContinuationDispatch: one field-copied dispatch per matched next step -----------------

    [Fact]
    public async Task CompletedResult_DispatchesMatchingNextStep_WithCorrectFieldCopy()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var completedProcessorId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();
        var nextProcessorId = Guid.NewGuid();
        const string nextPayload = "{\"next\":true}";

        // completed step's only next step is Completed(1)-gated.
        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, completedProcessorId, "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Completed, nextProcessorId, nextPayload),
        };

        var store = new WorkflowL1Store();
        SeedWorkflow(store, workflowId, steps);

        await using var provider = BuildHarness([nextProcessorId]);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var correlationId = Guid.NewGuid();
            var executionId = Guid.NewGuid();
            var manifestEntryId = Guid.NewGuid().ToString("D");   // the result's manifest EntryId -> data[manifestEntryId]
            var itemEntryId = "ab" + Guid.NewGuid().ToString("N"); // the ONE manifest item that gets fanned out

            var consumer = Build(store, harness.Bus, manifestEntryId, itemEntryId);

            var result = new ExecutionResult(workflowId, completedStepId, completedProcessorId, StepOutcome.Completed)
            {
                CorrelationId = correlationId,
                ExecutionId = executionId,
                EntryId = manifestEntryId,
                H = "aaaa" + new string('0', 60),
            };

            await consumer.Consume(OrchestratorTestStubs.Context(result, ct));

            Assert.True(await harness.Consumed.Any<EntryStepDispatch>(ct));
            var dispatched = harness.Consumed.Select<EntryStepDispatch>(ct)
                .Select(c => c.Context.Message)
                .ToList();

            var msg = Assert.Single(dispatched);                 // one manifest item x one matched next step
            Assert.Equal(workflowId, msg.WorkflowId);            // copied from the result
            Assert.Equal(correlationId, msg.CorrelationId);
            Assert.NotEqual(Guid.Empty, msg.ExecutionId);        // regenerated lineage (NewId.NextGuid)
            Assert.Equal(itemEntryId, msg.EntryId);              // the fanned-out manifest ITEM EntryId
            Assert.Equal(nextStepId, msg.StepId);                // taken from the next-step L1 projection
            Assert.Equal(nextProcessorId, msg.ProcessorId);
            Assert.Equal(nextPayload, msg.Payload);
        }
        finally
        {
            await harness.Stop(ct);
        }
    }

    // ----- ResultConsume: a result is consumed exactly once (competing-consumer, not broadcast) ---

    [Fact]
    public async Task Result_ConsumedExactlyOnce_NotBroadcast()
    {
        var ct = TestContext.Current.CancellationToken;

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

        await using var provider = BuildHarness([nextProcessorId]);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var manifestEntryId = Guid.NewGuid().ToString("D");
            var itemEntryId = "cd" + Guid.NewGuid().ToString("N");
            var consumer = Build(store, harness.Bus, manifestEntryId, itemEntryId);
            var result = new ExecutionResult(workflowId, completedStepId, Guid.NewGuid(), StepOutcome.Completed)
            {
                CorrelationId = Guid.NewGuid(),
                EntryId = manifestEntryId,
                H = "bbbb" + new string('0', 60),
            };

            await consumer.Consume(OrchestratorTestStubs.Context(result, ct));

            var dispatched = harness.Consumed.Select<EntryStepDispatch>(ct).ToList();
            Assert.Single(dispatched); // one manifest item x one match -> a single continuation, consumed once
        }
        finally
        {
            await harness.Stop(ct);
        }
    }
}
