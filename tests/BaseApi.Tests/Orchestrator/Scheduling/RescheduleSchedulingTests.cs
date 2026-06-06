using System;
using System.Threading;
using System.Threading.Tasks;
using Orchestrator.Scheduling;
using Quartz;
using Quartz.Impl;
using Xunit;

namespace BaseApi.Tests.Orchestrator.Scheduling;

/// <summary>
/// WR-02 / D-06: drives the <see cref="WorkflowScheduler.RescheduleAsync"/> <c>replaced is null</c>
/// fallback on a never-scheduled (purged-equivalent) jobId — no prior job, no prior trigger on the
/// deterministic key — and asserts the schedule is RE-ESTABLISHED (a live <see cref="TriggerState.Normal"/>
/// trigger with a future next-fire), NOT that <c>ScheduleJob</c> throws <c>JobPersistenceException</c>.
/// Also asserts the re-created job carries the <c>workflowId</c> job-data so the resurrected
/// <see cref="WorkflowFireJob"/> keeps its workflow context.
/// <para>
/// Hermetic RAM-scheduler harness cloned from <see cref="PauseResumeSchedulingTests"/>: a unique
/// <c>quartz.scheduler.instanceName = test-{Guid:N}</c> RAM store per scheduler (parallel-class
/// isolation), <c>new WorkflowScheduler(scheduler, TimeProvider.System)</c>, and EVERY Quartz call
/// passes <c>TestContext.Current.CancellationToken</c> (xUnit1051), with a try/finally Shutdown.
/// </para>
/// </summary>
public sealed class RescheduleSchedulingTests
{
    private const string EveryFiveMinutes = "*/5 * * * *";

    private static async Task<IScheduler> NewRamSchedulerAsync(CancellationToken ct)
    {
        // Unique instance name — StdSchedulerFactory binds schedulers in a SHARED process-wide repository
        // keyed by instance name; the default name collides across parallel test classes. A fresh GUID
        // name isolates each test's RAMJobStore.
        var props = new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"test-{Guid.NewGuid():N}",
        };
        var scheduler = await new StdSchedulerFactory(props).GetScheduler(ct);
        await scheduler.Start(ct);
        return scheduler;
    }

    [Fact]
    public async Task RescheduleReestablishesScheduleWhenNoPriorTrigger()
    {
        var ct = TestContext.Current.CancellationToken;
        var scheduler = await NewRamSchedulerAsync(ct);
        try
        {
            var sut = new WorkflowScheduler(scheduler, TimeProvider.System);
            var workflowId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            var jobKey = new JobKey(jobId.ToString("D"));
            var triggerKey = new TriggerKey(jobId.ToString("D"));

            // Purged-equivalent: never scheduled => no job, no trigger on the deterministic key.
            // RescheduleJob therefore returns null and the fallback must RE-CREATE the full job+trigger
            // (WR-02 / D-04) instead of throwing JobPersistenceException.
            await sut.RescheduleAsync(workflowId, jobId, EveryFiveMinutes, ct);

            // Re-established: exactly one trigger, Normal, future next-fire (D-06 asserts re-establishment, not a throw).
            var triggers = await scheduler.GetTriggersOfJob(jobKey, ct);
            var trigger = Assert.Single(triggers);
            Assert.Equal(TriggerState.Normal, await scheduler.GetTriggerState(triggerKey, ct));
            var nextFire = trigger.GetNextFireTimeUtc();
            Assert.NotNull(nextFire);
            Assert.True(nextFire > DateTimeOffset.UtcNow, "re-established trigger must fire in the future (no misfire)");

            // The re-created job carries the workflowId job-data so a later fire keeps its workflow context.
            var job = await scheduler.GetJobDetail(jobKey, ct);
            Assert.NotNull(job);
            Assert.Equal(workflowId.ToString("D"), job!.JobDataMap.GetString("workflowId"));
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }
}
