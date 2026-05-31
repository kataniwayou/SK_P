using BaseConsole.Core.Health;
using Messaging.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestrator.Consumers;
using Orchestrator.Hydration;
using Orchestrator.L1;
using Orchestrator.Scheduling;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// ORCH-STOP-01 goal-backward proof: the gated <see cref="StopOrchestrationConsumer"/> resolves the
/// workflow's jobId from L1, deletes the Quartz job, clears the L1 entry, and performs ZERO L2
/// writes — the byte-identical-L2 invariant (T-23-12) enforced by extended
/// <c>DidNotReceive().StringSetAsync/SetAddAsync/KeyDeleteAsync</c> guards. The gate-closed path drops
/// cleanly with no teardown.
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
        IConnectionMultiplexer mux, IScheduler scheduler, StartupGate gate)
    {
        var store = new WorkflowL1Store();
        var workflowScheduler = new WorkflowScheduler(scheduler, TimeProvider.System);
        var lifecycle = new WorkflowLifecycle(
            mux, store, workflowScheduler, TimeProvider.System, NullLogger<WorkflowLifecycle>.Instance);
        var consumer = new StopOrchestrationConsumer(
            gate, store, lifecycle, NullLogger<StopOrchestrationConsumer>.Instance);
        return (consumer, store, lifecycle);
    }

    private static async Task AssertZeroL2Writes(IDatabase db)
    {
        // Byte-identical L2: the orchestrator NEVER mutates any skp: key (T-23-12 / ORCH-STOP-01).
        await db.DidNotReceive().StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
        await db.DidNotReceive().SetAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    // ----- ORCH-STOP-01: stop deletes job + clears L1 + zero L2 writes --------------------------

    [Fact]
    public async Task StopDeletesJob_ClearsL1_ZeroL2Writes()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var processorId = Guid.NewGuid();
        var values = OrchestratorTestStubs.RootWithStep(workflowId, jobId, stepId, processorId);
        var mux = OrchestratorTestStubs.PresentL2(values, out var db);

        var gate = new StartupGate();
        gate.MarkReady();

        var scheduler = await NewRamSchedulerAsync(ct);
        try
        {
            var (consumer, store, lifecycle) = Build(mux, scheduler, gate);

            // Seed L1 + schedule wfX exactly as a Start would (reads-only against PresentL2).
            await lifecycle.HydrateAndScheduleAsync(workflowId, ct);
            Assert.Equal(1, store.Count);
            var jobKey = new JobKey(jobId.ToString("D"));
            Assert.True(await scheduler.CheckExists(jobKey, ct));

            // Reads during hydration are allowed; the zero-WRITE invariant covers only mutations,
            // so clear the received-call log before the teardown under test.
            db.ClearReceivedCalls();

            await consumer.Consume(OrchestratorTestStubs.Context(new StopOrchestration([workflowId]), ct));

            // Job gone, L1 entry removed.
            Assert.False(await scheduler.CheckExists(jobKey, ct));
            Assert.Empty(await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), ct));
            Assert.Equal(0, store.Count);
            Assert.False(store.TryGet(workflowId, out _));

            await AssertZeroL2Writes(db);
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }

    // ----- ORCH-STOP-01 / D-12: gate-closed drops the stop, no teardown -------------------------

    [Fact]
    public async Task GateClosed_DropsStop()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var processorId = Guid.NewGuid();
        var values = OrchestratorTestStubs.RootWithStep(workflowId, jobId, stepId, processorId);
        var mux = OrchestratorTestStubs.PresentL2(values, out var db);

        var readyGate = new StartupGate();
        readyGate.MarkReady();

        var scheduler = await NewRamSchedulerAsync(ct);
        try
        {
            // Seed via a ready lifecycle, then run a CLOSED-gate consumer over the SAME store/scheduler.
            var store = new WorkflowL1Store();
            var workflowScheduler = new WorkflowScheduler(scheduler, TimeProvider.System);
            var lifecycle = new WorkflowLifecycle(
                mux, store, workflowScheduler, TimeProvider.System, NullLogger<WorkflowLifecycle>.Instance);
            await lifecycle.HydrateAndScheduleAsync(workflowId, ct);

            var jobKey = new JobKey(jobId.ToString("D"));
            Assert.True(await scheduler.CheckExists(jobKey, ct));

            var closedGate = new StartupGate(); // NOT ready
            var consumer = new StopOrchestrationConsumer(
                closedGate, store, lifecycle, NullLogger<StopOrchestrationConsumer>.Instance);

            db.ClearReceivedCalls();
            await consumer.Consume(OrchestratorTestStubs.Context(new StopOrchestration([workflowId]), ct)); // clean ack

            // No teardown: job + L1 entry survive.
            Assert.True(await scheduler.CheckExists(jobKey, ct));
            Assert.Equal(1, store.Count);
            await AssertZeroL2Writes(db);
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }
}
