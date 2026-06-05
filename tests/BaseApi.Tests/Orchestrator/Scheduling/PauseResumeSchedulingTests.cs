using System;
using System.Threading;
using System.Threading.Tasks;
using Orchestrator.Scheduling;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using Xunit;

namespace BaseApi.Tests.Orchestrator.Scheduling;

/// <summary>
/// PAUSE-02/03/05 RAM-scheduler proof (Wave 0 RED). Drives <see cref="WorkflowScheduler"/> against a real
/// Quartz RAMJobStore and asserts the deterministic-TriggerKey state model the phase ships:
/// <list type="bullet">
///   <item><description><b>PAUSE-02 (D-08):</b> after <c>PauseAsync(jobId)</c> the workflow's trigger,
///   addressed by the DETERMINISTIC <c>TriggerKey(jobId.ToString("D"))</c>, is
///   <see cref="TriggerState.Paused"/> — proving Pause is Quartz <c>PauseJob</c> and
///   <c>GetTriggerState</c> is the sole source of truth. A fresh <c>ScheduleAsync</c> (before pause) is
///   <see cref="TriggerState.Normal"/> at that same key, proving the load-bearing Plan-02
///   <c>.WithIdentity(new TriggerKey(jobId.ToString("D")))</c> stamping is present.</description></item>
///   <item><description><b>PAUSE-03:</b> the resume sequence (GetTriggerStateAsync == Paused →
///   UnscheduleAsync → ScheduleAsync) yields exactly one trigger, <see cref="TriggerState.Normal"/>, with
///   a future next-fire time (no misfire).</description></item>
///   <item><description><b>PAUSE-05 (D-09):</b> a Stopped workflow resolves to
///   <see cref="TriggerState.None"/> and an already-running one to <see cref="TriggerState.Normal"/> — the
///   resume guard's two ignore branches are both non-Paused.</description></item>
/// </list>
/// RED until Plan 02 adds <c>PauseAsync</c>/<c>GetTriggerStateAsync</c> and the deterministic-TriggerKey
/// stamping — failing ONLY because those production members are absent (no harness errors). Each scheduler
/// uses a unique <c>quartz.scheduler.instanceName = test-{Guid:N}</c> RAM store, and EVERY Quartz call
/// passes <c>TestContext.Current.CancellationToken</c> (xUnit1051).
/// </summary>
public sealed class PauseResumeSchedulingTests
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
    public async Task PauseSuppressesFire()
    {
        var ct = TestContext.Current.CancellationToken;
        var scheduler = await NewRamSchedulerAsync(ct);
        try
        {
            var sut = new WorkflowScheduler(scheduler, TimeProvider.System);
            var workflowId = Guid.NewGuid();
            var jobId = Guid.NewGuid();
            var triggerKey = new TriggerKey(jobId.ToString("D"));

            await sut.ScheduleAsync(workflowId, jobId, EveryFiveMinutes, ct);

            // Proves the deterministic .WithIdentity stamping: the trigger is addressable by jobId AND Normal.
            Assert.Equal(TriggerState.Normal, await scheduler.GetTriggerState(triggerKey, ct));

            await sut.PauseAsync(jobId, ct);

            // PAUSE-02 / D-08: GetTriggerState is the sole source of truth — Paused suppresses the next fire.
            Assert.Equal(TriggerState.Paused, await scheduler.GetTriggerState(triggerKey, ct));
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }

    [Fact]
    public async Task ResumeReschedulesFresh()
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

            // Pause first, then run the resume sequence.
            await sut.ScheduleAsync(workflowId, jobId, EveryFiveMinutes, ct);
            await sut.PauseAsync(jobId, ct);
            Assert.Equal(TriggerState.Paused, await sut.GetTriggerStateAsync(jobId, ct));

            // PAUSE-03 resume: Paused → unschedule the paused job → schedule a fresh trigger.
            await sut.UnscheduleAsync(jobId, ct);
            await sut.ScheduleAsync(workflowId, jobId, EveryFiveMinutes, ct);

            // Exactly one trigger, Normal, with a future fire time (no misfire).
            var triggers = await scheduler.GetTriggersOfJob(jobKey, ct);
            var trigger = Assert.Single(triggers);
            Assert.Equal(TriggerState.Normal, await scheduler.GetTriggerState(triggerKey, ct));
            var nextFire = trigger.GetNextFireTimeUtc();
            Assert.NotNull(nextFire);
            Assert.True(nextFire > DateTimeOffset.UtcNow, "resumed trigger must fire in the future (no misfire)");
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }

    [Fact]
    public async Task ResumeIgnoresStoppedAndRunning()
    {
        var ct = TestContext.Current.CancellationToken;
        var scheduler = await NewRamSchedulerAsync(ct);
        try
        {
            var sut = new WorkflowScheduler(scheduler, TimeProvider.System);
            var workflowId = Guid.NewGuid();
            var jobId = Guid.NewGuid();

            // Stopped: the job was deleted → GetTriggerState resolves to None (resume ignore branch 1, D-09).
            await sut.ScheduleAsync(workflowId, jobId, EveryFiveMinutes, ct);
            await sut.UnscheduleAsync(jobId, ct);
            Assert.Equal(TriggerState.None, await sut.GetTriggerStateAsync(jobId, ct));

            // Already running: a plain ScheduleAsync leaves the trigger Normal (resume ignore branch 2, D-09).
            await sut.ScheduleAsync(workflowId, jobId, EveryFiveMinutes, ct);
            Assert.Equal(TriggerState.Normal, await sut.GetTriggerStateAsync(jobId, ct));
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }
}
