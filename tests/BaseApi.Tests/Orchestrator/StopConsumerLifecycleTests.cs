using Messaging.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestrator.Consumers;
using Orchestrator.Dispatch;
using Orchestrator.Hydration;
using Orchestrator.L1;
using Orchestrator.Scheduling;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using StackExchange.Redis;
using Xunit;
using ExecutionResult = Messaging.Contracts.ExecutionResult;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// ORCH-STOP-DRAIN-01 goal-backward proof (supersedes Phase 23 ORCH-STOP-01): the conditionless
/// <see cref="StopOrchestrationConsumer"/> resolves the workflow's jobId from L1, deletes the Quartz
/// job, but KEEPS the L1 entry (D-07 drain) and performs ZERO L2 writes. Because L1 persists, a late
/// <see cref="ExecutionResult"/> for the stopped workflow still resolves in L1 and dispatches its next
/// steps. 24.1 / D-24.1-05: the boot gate is removed — the consumer runs only after the bus starts,
/// so there is no gate-closed path (the prior GateClosedException test is deleted).
/// </summary>
public sealed class StopConsumerLifecycleTests
{
    private static async Task<IScheduler> NewRamSchedulerAsync(CancellationToken ct)
    {
        // Unique instance name per scheduler — StdSchedulerFactory binds schedulers in a SHARED
        // process-wide repository keyed by instance name; the default name collides across parallel
        // test classes. A fresh GUID name isolates each test's RAMJobStore.
        var props = new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"test-{Guid.NewGuid():N}",
        };
        var scheduler = await new StdSchedulerFactory(props).GetScheduler(ct);
        await scheduler.Start(ct);
        return scheduler;
    }

    private static (StopOrchestrationConsumer consumer, WorkflowL1Store store, WorkflowLifecycle lifecycle) Build(
        IConnectionMultiplexer mux, IScheduler scheduler)
    {
        var store = new WorkflowL1Store();
        var workflowScheduler = new WorkflowScheduler(scheduler, TimeProvider.System);
        var lifecycle = new WorkflowLifecycle(
            mux, store, workflowScheduler, TimeProvider.System, NullLogger<WorkflowLifecycle>.Instance);
        var consumer = new StopOrchestrationConsumer(
            lifecycle, NullLogger<StopOrchestrationConsumer>.Instance);
        return (consumer, store, lifecycle);
    }

    private static async Task AssertZeroL2Writes(IDatabase db)
    {
        // Byte-identical L2: the orchestrator NEVER mutates any skp: key (T-23-12 / ORCH-STOP-DRAIN-01).
        await db.DidNotReceive().StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
        await db.DidNotReceive().SetAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    // ----- ORCH-STOP-DRAIN-01: stop deletes job but KEEPS L1, zero L2 writes --------------------

    [Fact]
    public async Task StopDeletesJob_KeepsL1_ZeroL2Writes()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var processorId = Guid.NewGuid();
        var values = OrchestratorTestStubs.RootWithStep(workflowId, jobId, stepId, processorId);
        var mux = OrchestratorTestStubs.PresentL2(values, out var db);

        var scheduler = await NewRamSchedulerAsync(ct);
        try
        {
            var (consumer, store, lifecycle) = Build(mux, scheduler);

            // Seed L1 + schedule wfX exactly as a Start would (reads-only against PresentL2).
            await lifecycle.HydrateAndScheduleAsync(workflowId, ct);
            Assert.Equal(1, store.Count);
            var jobKey = new JobKey(jobId.ToString("D"));
            Assert.True(await scheduler.CheckExists(jobKey, ct));

            // Reads during hydration are allowed; the zero-WRITE invariant covers only mutations,
            // so clear the received-call log before the unschedule under test.
            db.ClearReceivedCalls();

            await consumer.Consume(OrchestratorTestStubs.Context(new StopOrchestration([workflowId]), ct));

            // Job gone, but the L1 entry REMAINS (D-07 drain).
            Assert.False(await scheduler.CheckExists(jobKey, ct));
            Assert.Empty(await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), ct));
            Assert.Equal(1, store.Count);
            Assert.True(store.TryGet(workflowId, out _));

            await AssertZeroL2Writes(db);
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }

    // ----- ORCH-STOP-DRAIN-01: a late result for the stopped workflow still resolves in L1 + ----
    // dispatches its next step (drain). Reuses the result-consume path over the kept L1 entry.

    [Fact]
    public async Task LateResult_AfterStop_StillResolvesInL1_AndDispatches()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var entryStepId = Guid.NewGuid();
        var entryProcessorId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();
        var nextProcessorId = Guid.NewGuid();

        // Seed an L1 entry directly whose entry step has a matching next step (EntryCondition 1 ==
        // (int)StepOutcome.Completed). The Stop must KEEP this so the late result can still advance.
        var store = new WorkflowL1Store();
        var steps = new Dictionary<Guid, Messaging.Contracts.Projections.StepProjection>
        {
            [entryStepId] = new(EntryCondition: 0, ProcessorId: entryProcessorId, Payload: "{}", NextStepIds: [nextStepId]),
            [nextStepId] = new(EntryCondition: (int)StepOutcome.Completed, ProcessorId: nextProcessorId, Payload: "{\"k\":1}", NextStepIds: []),
        };
        store.Upsert(workflowId, new WorkflowL1([entryStepId], "*/5 * * * *", jobId, steps));

        var scheduler = await NewRamSchedulerAsync(ct);
        try
        {
            // Stop: unschedule-only, KEEP L1. (No Quartz job was scheduled here — Stop is a no-op unschedule
            // because UnscheduleAsync(jobId) is idempotent; the point is the L1 entry must survive.)
            var workflowScheduler = new WorkflowScheduler(scheduler, TimeProvider.System);
            var mux = OrchestratorTestStubs.AbsentL2(out _); // result path never reads L2 anyway
            var lifecycle = new WorkflowLifecycle(
                mux, store, workflowScheduler, TimeProvider.System, NullLogger<WorkflowLifecycle>.Instance);
            var stop = new StopOrchestrationConsumer(lifecycle, NullLogger<StopOrchestrationConsumer>.Instance);

            await stop.Consume(OrchestratorTestStubs.Context(new StopOrchestration([workflowId]), ct));
            Assert.True(store.TryGet(workflowId, out _)); // L1 kept (drain)

            // Drive a late result for the stopped workflow's entry step through the ResultConsumer; it
            // resolves in the kept L1 entry and dispatches the matching next step.
            var dispatcher = Substitute.For<IStepDispatcher>();
            var advancement = new StepAdvancement();
            var resultConsumer = new ResultConsumer(
                store, advancement, dispatcher, NullLogger<ResultConsumer>.Instance);

            var correlationId = Guid.NewGuid();
            var executionId = Guid.NewGuid();
            var entryId = Guid.NewGuid();
            var result = new ExecutionResult(workflowId, entryStepId, entryProcessorId, StepOutcome.Completed)
            {
                CorrelationId = correlationId,
                ExecutionId = executionId,
                EntryId = entryId,
            };

            await resultConsumer.Consume(OrchestratorTestStubs.Context(result, ct));

            // The continuation for the matching next step was dispatched (ids from result, step data from L1).
            await dispatcher.Received(1).DispatchAsync(
                workflowId, nextStepId, nextProcessorId, "{\"k\":1}",
                correlationId, executionId, entryId, Arg.Any<CancellationToken>());
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }

}
