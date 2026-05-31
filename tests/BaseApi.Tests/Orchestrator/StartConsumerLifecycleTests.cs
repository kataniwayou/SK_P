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
/// ORCH-START-RELOAD-01 goal-backward proof (supersedes Phase 23 ORCH-CONSUME-01): the conditionless
/// <see cref="StartOrchestrationConsumer"/> UNCONDITIONALLY hydrates + (re)schedules the consumed
/// workflow into a real <see cref="WorkflowL1Store"/> + a real Quartz RAMJobStore scheduler — even when
/// the workflow is ALREADY in L1 (no existence skip), and a stop→start revives a live job. The
/// gate-closed path THROWS <see cref="GateClosedException"/> (D-06 redelivery, NOT ack-drop).
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

    private static (StartOrchestrationConsumer consumer, WorkflowL1Store store, WorkflowLifecycle lifecycle) Build(
        IConnectionMultiplexer mux, IScheduler scheduler, StartupGate gate)
    {
        var store = new WorkflowL1Store();
        var workflowScheduler = new WorkflowScheduler(scheduler, TimeProvider.System);
        var lifecycle = new WorkflowLifecycle(
            mux, store, workflowScheduler, TimeProvider.System, NullLogger<WorkflowLifecycle>.Instance);
        var consumer = new StartOrchestrationConsumer(
            gate, lifecycle, NullLogger<StartOrchestrationConsumer>.Instance);
        return (consumer, store, lifecycle);
    }

    // ----- ORCH-START-RELOAD-01: start hydrates + schedules ONLY the consumed workflow -----------

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
            var (consumer, store, _) = Build(mux, scheduler, gate);

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

    // ----- ORCH-START-RELOAD-01: a Start for a workflow ALREADY in L1 re-hydrates + reschedules ---
    // (conditionless reload — no existence skip).

    [Fact]
    public async Task StartAlreadyInL1_ReHydratesAndReschedules_NoSkip()
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
            var (consumer, store, _) = Build(mux, scheduler, gate);

            // First Start hydrates + schedules.
            await consumer.Consume(OrchestratorTestStubs.Context(new StartOrchestration([workflowId]), ct));
            Assert.Equal(1, store.Count);
            var jobKey = new JobKey(jobId.ToString("D"));
            Assert.True(await scheduler.CheckExists(jobKey, ct));

            // Second Start with the SAME workflow already in L1 re-runs the lifecycle (no skip):
            // teardown unschedules the old job, then hydrate+schedule re-applies + reschedules it.
            await consumer.Consume(OrchestratorTestStubs.Context(new StartOrchestration([workflowId]), ct));

            // Still exactly one entry + exactly one live job (re-hydrated, not skipped, not duplicated).
            Assert.Equal(1, store.Count);
            Assert.True(store.TryGet(workflowId, out var entry));
            Assert.Contains(stepId, entry.Steps.Keys);
            Assert.True(await scheduler.CheckExists(jobKey, ct));
            Assert.Single(await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), ct));
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }

    // ----- ORCH-START-RELOAD-01: stop -> start revives a live Quartz job -------------------------

    [Fact]
    public async Task StopThenStart_RevivesLiveJob()
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
            var (start, store, lifecycle) = Build(mux, scheduler, gate);
            var stop = new StopOrchestrationConsumer(gate, lifecycle, NullLogger<StopOrchestrationConsumer>.Instance);

            await start.Consume(OrchestratorTestStubs.Context(new StartOrchestration([workflowId]), ct));
            var jobKey = new JobKey(jobId.ToString("D"));
            Assert.True(await scheduler.CheckExists(jobKey, ct));

            // Stop deletes the job (keeps L1 — D-07).
            await stop.Consume(OrchestratorTestStubs.Context(new StopOrchestration([workflowId]), ct));
            Assert.False(await scheduler.CheckExists(jobKey, ct));

            // Start again revives a live job.
            await start.Consume(OrchestratorTestStubs.Context(new StartOrchestration([workflowId]), ct));
            Assert.True(await scheduler.CheckExists(jobKey, ct));
            Assert.True(store.TryGet(workflowId, out _));
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }

    // ----- ORCH-GATE-01 / D-06: gate-closed Start THROWS (redelivery), no hydrate/schedule -------

    [Fact]
    public async Task GateClosed_Start_Throws_NoSchedule()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var values = OrchestratorTestStubs.RootWithStep(workflowId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var mux = OrchestratorTestStubs.PresentL2(values, out _);

        var gate = new StartupGate(); // NOT ready

        var scheduler = await NewRamSchedulerAsync(ct);
        try
        {
            var (consumer, store, _) = Build(mux, scheduler, gate);

            // D-06: gate closed -> THROW (so the message is scheduled-redelivered, NOT ack-dropped).
            await Assert.ThrowsAsync<GateClosedException>(
                () => consumer.Consume(OrchestratorTestStubs.Context(new StartOrchestration([workflowId]), ct)));

            Assert.Equal(0, store.Count); // nothing hydrated
            Assert.Empty(await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), ct)); // nothing scheduled
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }
}
