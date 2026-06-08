using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Messaging.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestrator.Consumers;
using Orchestrator.Hydration;
using Orchestrator.L1;
using Orchestrator.Scheduling;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using Xunit;

namespace BaseApi.Tests.Orchestrator.Consumers;

/// <summary>
/// ORCH-02 global resume (per-job, no herd — D-02). <see cref="ResumeAllConsumer"/> enumerates the L1
/// <see cref="IWorkflowL1Store.WorkflowIds"/> snapshot and runs the shared
/// <see cref="WorkflowLifecycle.ResumeAsync"/> for each, which acts ONLY on a <c>Paused</c> trigger
/// (delete-stale + fresh from-now reschedule). Driven over a real Quartz RAMJobStore (mirroring
/// <c>PauseResumeConsumerTests</c>): multiple workflows are seeded into L1 + Quartz and PAUSED, then one
/// <c>ResumeAll</c> must bring every paused trigger back to <see cref="TriggerState.Normal"/>; a workflow
/// left <c>Normal</c> (never paused) is ignored (no spurious resume). Each scheduler uses a unique
/// <c>quartz.scheduler.instanceName</c> RAM store; EVERY Quartz call carries
/// <c>TestContext.Current.CancellationToken</c>.
/// </summary>
public sealed class ResumeAllConsumerTests
{
    private const string EveryFiveMinutes = "*/5 * * * *";

    private static async Task<IScheduler> NewRamSchedulerAsync(CancellationToken ct)
    {
        var props = new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"test-{Guid.NewGuid():N}",
        };
        var scheduler = await new StdSchedulerFactory(props).GetScheduler(ct);
        await scheduler.Start(ct);
        return scheduler;
    }

    [Fact]
    public async Task Consume_Enumerates_WorkflowIds_And_Calls_ResumeAsync_Each()
    {
        var ct = TestContext.Current.CancellationToken;
        var scheduler = await NewRamSchedulerAsync(ct);
        try
        {
            var store = new WorkflowL1Store();
            var workflowScheduler = new WorkflowScheduler(scheduler, TimeProvider.System);

            // Build TWO workflows; each needs its own L2 stub, so wire a combined PresentL2 mux.
            var w1 = (id: Guid.NewGuid(), job: Guid.NewGuid(), step: Guid.NewGuid(), proc: Guid.NewGuid());
            var w2 = (id: Guid.NewGuid(), job: Guid.NewGuid(), step: Guid.NewGuid(), proc: Guid.NewGuid());
            var values = OrchestratorTestStubs.RootWithStep(w1.id, w1.job, w1.step, w1.proc, EveryFiveMinutes)
                .Concat(OrchestratorTestStubs.RootWithStep(w2.id, w2.job, w2.step, w2.proc, EveryFiveMinutes))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            var mux = OrchestratorTestStubs.PresentL2(values, out _);
            var lifecycle = new WorkflowLifecycle(
                mux, store, workflowScheduler, TimeProvider.System, NullLogger<WorkflowLifecycle>.Instance);

            await lifecycle.HydrateAndScheduleAsync(w1.id, ct);
            await lifecycle.HydrateAndScheduleAsync(w2.id, ct);

            // Pause BOTH per-job (the Paused precondition the resume guard acts on — mirrors the proven
            // PauseResumeSchedulingTests cycle). NOTE: scheduler-wide PauseAll() pauses the trigger GROUP,
            // so a fresh trigger added by the resume reschedule would inherit the paused group — the real
            // round trip is exercised end-to-end in the live RealStack gate; here we isolate the
            // enumerate-and-resume-each behavior this consumer owns from PauseAll's group semantics.
            await workflowScheduler.PauseAsync(w1.job, ct);
            await workflowScheduler.PauseAsync(w2.job, ct);
            Assert.Equal(TriggerState.Paused, await scheduler.GetTriggerState(new TriggerKey(w1.job.ToString("D")), ct));
            Assert.Equal(TriggerState.Paused, await scheduler.GetTriggerState(new TriggerKey(w2.job.ToString("D")), ct));

            var consumer = new ResumeAllConsumer(store, lifecycle, NullLogger<ResumeAllConsumer>.Instance);
            await consumer.Consume(OrchestratorTestStubs.Context(new ResumeAll { CorrelationId = Guid.NewGuid() }, ct));

            // Per-job ResumeAsync ran for EACH enumerated id: every paused trigger is back to Normal.
            Assert.Equal(TriggerState.Normal, await scheduler.GetTriggerState(new TriggerKey(w1.job.ToString("D")), ct));
            Assert.Equal(TriggerState.Normal, await scheduler.GetTriggerState(new TriggerKey(w2.job.ToString("D")), ct));
            // Exactly one job per workflow (no orphans from the delete-stale + fresh reschedule).
            Assert.Equal(2, (await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), ct)).Count);
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }

    [Fact]
    public async Task Resume_Of_Non_Paused_Trigger_Is_Ignored()
    {
        var ct = TestContext.Current.CancellationToken;
        var scheduler = await NewRamSchedulerAsync(ct);
        try
        {
            var store = new WorkflowL1Store();
            var workflowScheduler = new WorkflowScheduler(scheduler, TimeProvider.System);

            var workflowId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            var stepId = Guid.NewGuid();
            var processorId = Guid.NewGuid();
            var values = OrchestratorTestStubs.RootWithStep(workflowId, jobId, stepId, processorId, EveryFiveMinutes);
            var mux = OrchestratorTestStubs.PresentL2(values, out _);
            var lifecycle = new WorkflowLifecycle(
                mux, store, workflowScheduler, TimeProvider.System, NullLogger<WorkflowLifecycle>.Instance);

            // Seed but DO NOT pause — the trigger stays Normal (running).
            await lifecycle.HydrateAndScheduleAsync(workflowId, ct);
            var triggerKey = new TriggerKey(jobId.ToString("D"));
            Assert.Equal(TriggerState.Normal, await scheduler.GetTriggerState(triggerKey, ct));
            var beforeNextFire = (await scheduler.GetTrigger(triggerKey, ct))!.GetNextFireTimeUtc();

            var consumer = new ResumeAllConsumer(store, lifecycle, NullLogger<ResumeAllConsumer>.Instance);
            await consumer.Consume(OrchestratorTestStubs.Context(new ResumeAll { CorrelationId = Guid.NewGuid() }, ct));

            // Non-Paused -> ignored (no spurious resume): still Normal, same trigger (no delete/reschedule churn).
            Assert.Equal(TriggerState.Normal, await scheduler.GetTriggerState(triggerKey, ct));
            var afterNextFire = (await scheduler.GetTrigger(triggerKey, ct))!.GetNextFireTimeUtc();
            Assert.Equal(beforeNextFire, afterNextFire); // untouched — the resume guard short-circuited
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }
}
