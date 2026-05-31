using BaseConsole.Core.Health;
using Messaging.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
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
/// ORCH-CONSUME-01 goal-backward proof: the gated <see cref="StartOrchestrationConsumer"/> hydrates
/// AND schedules ONLY the consumed workflow into a real <see cref="WorkflowL1Store"/> + a real Quartz
/// RAMJobStore scheduler. The gate-closed path drops cleanly (ack) without hydrating or scheduling.
/// </summary>
public sealed class StartConsumerLifecycleTests
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

    private static (StartOrchestrationConsumer consumer, WorkflowL1Store store) Build(
        IConnectionMultiplexer mux, IScheduler scheduler, StartupGate gate)
    {
        var store = new WorkflowL1Store();
        var workflowScheduler = new WorkflowScheduler(scheduler, TimeProvider.System);
        var lifecycle = new WorkflowLifecycle(
            mux, store, workflowScheduler, TimeProvider.System, NullLogger<WorkflowLifecycle>.Instance);
        var consumer = new StartOrchestrationConsumer(
            gate, store, lifecycle, NullLogger<StartOrchestrationConsumer>.Instance);
        return (consumer, store);
    }

    // ----- ORCH-CONSUME-01: start hydrates + schedules ONLY the consumed workflow ---------------

    [Fact]
    public async Task StartHydratesOnlyConsumedWorkflow_AndSchedules()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var processorId = Guid.NewGuid();
        var values = OrchestratorTestStubs.RootWithStep(workflowId, jobId, stepId, processorId);
        var mux = OrchestratorTestStubs.PresentL2(values, out _);

        var gate = new StartupGate();
        gate.MarkReady();

        var scheduler = await NewRamSchedulerAsync(ct);
        try
        {
            var (consumer, store) = Build(mux, scheduler, gate);

            await consumer.Consume(OrchestratorTestStubs.Context(new StartOrchestration([workflowId]), ct));

            // Exactly the consumed workflow is in L1.
            Assert.Equal(1, store.Count);
            Assert.True(store.TryGet(workflowId, out var entry));
            Assert.Contains(stepId, entry.Steps.Keys);

            // Its one-shot job is scheduled, keyed by jobId.
            var jobKey = new JobKey(jobId.ToString("D"));
            Assert.True(await scheduler.CheckExists(jobKey, ct));
            var keys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), ct);
            Assert.Single(keys);
            Assert.Contains(jobKey, keys);
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }

    // ----- ORCH-CONSUME-01 / D-12: gate-closed drops the start (ack), no schedule ---------------

    [Fact]
    public async Task GateClosed_DropsStart_NoSchedule()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var values = OrchestratorTestStubs.RootWithStep(workflowId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var mux = OrchestratorTestStubs.PresentL2(values, out _);

        var gate = new StartupGate(); // NOT ready

        var scheduler = await NewRamSchedulerAsync(ct);
        try
        {
            var (consumer, store) = Build(mux, scheduler, gate);

            await consumer.Consume(OrchestratorTestStubs.Context(new StartOrchestration([workflowId]), ct)); // clean ack

            Assert.Equal(0, store.Count); // nothing hydrated
            Assert.Empty(await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), ct)); // nothing scheduled
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }
}
