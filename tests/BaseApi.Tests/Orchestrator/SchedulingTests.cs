using Orchestrator.Scheduling;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Proves ORCH-SCHED-01 against a real Quartz RAMJobStore scheduler: <see cref="WorkflowScheduler"/>
/// schedules exactly one started job per workflow, keyed by <c>JobKey(jobId.ToString("D"))</c>, with
/// each trigger in the Normal (not Paused) state.
/// </summary>
public sealed class SchedulingTests
{
    private static async Task<IScheduler> NewRamSchedulerAsync()
    {
        var factory = new StdSchedulerFactory();
        var scheduler = await factory.GetScheduler();
        await scheduler.Start();
        return scheduler;
    }

    [Fact]
    public async Task OneStartedJobPerWorkflow_KeyedByJobId()
    {
        var scheduler = await NewRamSchedulerAsync();
        try
        {
            var sut = new WorkflowScheduler(scheduler, TimeProvider.System);

            // 3 workflows, each with a distinct jobId and an every-5-minutes cron.
            var jobs = new[]
            {
                (workflowId: Guid.NewGuid(), jobId: Guid.NewGuid()),
                (workflowId: Guid.NewGuid(), jobId: Guid.NewGuid()),
                (workflowId: Guid.NewGuid(), jobId: Guid.NewGuid()),
            };

            foreach (var (workflowId, jobId) in jobs)
            {
                await sut.ScheduleAsync(workflowId, jobId, "*/5 * * * *", CancellationToken.None);
            }

            var keys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup());
            Assert.Equal(jobs.Length, keys.Count);

            foreach (var (_, jobId) in jobs)
            {
                var expected = new JobKey(jobId.ToString("D"));
                Assert.Contains(expected, keys);

                var triggers = await scheduler.GetTriggersOfJob(expected);
                Assert.NotEmpty(triggers);
                foreach (var trigger in triggers)
                {
                    var state = await scheduler.GetTriggerState(trigger.Key);
                    Assert.Equal(TriggerState.Normal, state); // started, not Paused
                }
            }
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false);
        }
    }

    [Fact]
    public async Task UnscheduleAsync_RemovesJobAndTriggers()
    {
        var scheduler = await NewRamSchedulerAsync();
        try
        {
            var sut = new WorkflowScheduler(scheduler, TimeProvider.System);
            var workflowId = Guid.NewGuid();
            var jobId = Guid.NewGuid();

            await sut.ScheduleAsync(workflowId, jobId, "*/5 * * * *", CancellationToken.None);
            Assert.Single(await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup()));

            await sut.UnscheduleAsync(jobId, CancellationToken.None);

            Assert.Empty(await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup()));
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false);
        }
    }
}
