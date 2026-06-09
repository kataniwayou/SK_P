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
            // PauseResumeSchedulingTests cycle).
            await workflowScheduler.PauseAsync(w1.job, ct);
            await workflowScheduler.PauseAsync(w2.job, ct);
            Assert.Equal(TriggerState.Paused, await scheduler.GetTriggerState(new TriggerKey(w1.job.ToString("D")), ct));
            Assert.Equal(TriggerState.Paused, await scheduler.GetTriggerState(new TriggerKey(w2.job.ToString("D")), ct));

            var consumer = new ResumeAllConsumer(store, lifecycle, workflowScheduler, NullLogger<ResumeAllConsumer>.Instance);
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

            var consumer = new ResumeAllConsumer(store, lifecycle, workflowScheduler, NullLogger<ResumeAllConsumer>.Instance);
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

    /// <summary>
    /// GAP-49-2 regression (D-08 Option A). Drives the TRUE production path over a real RAM scheduler:
    /// <see cref="PauseAllConsumer"/> -> scheduler-wide <c>PauseAll()</c> (adds every group to Quartz's
    /// <c>pausedTriggerGroups</c>) -> <see cref="ResumeAllConsumer"/> -> per-job reschedule THEN the
    /// group-level <c>ResumeAll()</c> clear (the empirically-verified API: <c>ResumeTriggers(AnyGroup())</c>
    /// does NOT clear <c>pausedTriggerGroups</c> in Quartz 3.18 RAMJobStore). The decisive assertion: a BRAND-NEW workflow
    /// scheduled AFTER the pause/resume cycle is born <c>Normal</c> with a future fire time — NOT
    /// <c>Paused</c>. BEFORE the fix (no group-flag clear) this is <c>Paused</c> and the test FAILS; AFTER
    /// the fix it is <c>Normal</c> and the test PASSES.
    /// </summary>
    [Fact]
    [Trait("Phase", "49")]
    public async Task Normal_After_PauseAll_Resume_Cycle()
    {
        var ct = TestContext.Current.CancellationToken;
        var scheduler = await NewRamSchedulerAsync(ct);
        try
        {
            var store = new WorkflowL1Store();
            var workflowScheduler = new WorkflowScheduler(scheduler, TimeProvider.System);

            // W1 exists at pause time; W2 is the brand-new workflow scheduled AFTER recovery (the GAP probe).
            var w1 = (id: Guid.NewGuid(), job: Guid.NewGuid(), step: Guid.NewGuid(), proc: Guid.NewGuid());
            var w2 = (id: Guid.NewGuid(), job: Guid.NewGuid(), step: Guid.NewGuid(), proc: Guid.NewGuid());
            var values = OrchestratorTestStubs.RootWithStep(w1.id, w1.job, w1.step, w1.proc, EveryFiveMinutes)
                .Concat(OrchestratorTestStubs.RootWithStep(w2.id, w2.job, w2.step, w2.proc, EveryFiveMinutes))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            var mux = OrchestratorTestStubs.PresentL2(values, out _);
            var lifecycle = new WorkflowLifecycle(
                mux, store, workflowScheduler, TimeProvider.System, NullLogger<WorkflowLifecycle>.Instance);

            // Seed W1 into L1 + Quartz (Normal).
            await lifecycle.HydrateAndScheduleAsync(w1.id, ct);
            Assert.Equal(TriggerState.Normal, await scheduler.GetTriggerState(new TriggerKey(w1.job.ToString("D")), ct));

            // TRUE pause path: scheduler-wide PauseAll() sets pausedTriggerGroups -> W1 Paused.
            var pause = new PauseAllConsumer(workflowScheduler, NullLogger<PauseAllConsumer>.Instance);
            await pause.Consume(OrchestratorTestStubs.Context(new PauseAll { CorrelationId = Guid.NewGuid() }, ct));
            Assert.Equal(TriggerState.Paused, await scheduler.GetTriggerState(new TriggerKey(w1.job.ToString("D")), ct));

            // TRUE resume path: per-job fresh reschedule THEN group-flag clear -> W1 back to Normal.
            var resume = new ResumeAllConsumer(store, lifecycle, workflowScheduler, NullLogger<ResumeAllConsumer>.Instance);
            await resume.Consume(OrchestratorTestStubs.Context(new ResumeAll { CorrelationId = Guid.NewGuid() }, ct));
            Assert.Equal(TriggerState.Normal, await scheduler.GetTriggerState(new TriggerKey(w1.job.ToString("D")), ct));

            // THE GAP-49-2 ASSERTION: a brand-new workflow scheduled AFTER the cycle must be born Normal
            // (pausedTriggerGroups was cleared), with a future fire time — not stuck Paused until restart.
            await lifecycle.HydrateAndScheduleAsync(w2.id, ct);
            var w2Key = new TriggerKey(w2.job.ToString("D"));
            Assert.Equal(TriggerState.Normal, await scheduler.GetTriggerState(w2Key, ct));
            var w2NextFire = (await scheduler.GetTrigger(w2Key, ct))!.GetNextFireTimeUtc();
            Assert.NotNull(w2NextFire);
            Assert.True(w2NextFire > DateTimeOffset.UtcNow, "post-cycle workflow must have a future fire time");
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }
}
