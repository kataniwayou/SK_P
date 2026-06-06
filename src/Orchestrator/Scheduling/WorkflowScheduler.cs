using Quartz;

namespace Orchestrator.Scheduling;

/// <summary>
/// Schedules and tears down the per-workflow self-rescheduling one-shot Quartz job (ORCH-SCHED-01,
/// D-07/D-08). One job per workflow, keyed by <c>JobKey(jobId.ToString("D"))</c>; the trigger is a
/// one-shot <see cref="ISimpleTrigger"/> fired at the next Cronos occurrence, and
/// <see cref="WorkflowFireJob"/> reschedules a fresh trigger for the same job on each fire.
/// <para>
/// <b>Pitfall 4:</b> <see cref="WorkflowFireJob"/> is <c>[DisallowConcurrentExecution]</c> so a single
/// jobKey never double-fires (4a); <see cref="RescheduleAsync"/> adds a NEW trigger to the EXISTING
/// job rather than re-adding the job (4b); <see cref="UnscheduleAsync"/> calls <c>DeleteJob</c> which
/// removes the job and all its triggers atomically (4c).
/// </para>
/// </summary>
public sealed class WorkflowScheduler(IScheduler scheduler, TimeProvider timeProvider)
{
    private static JobKey KeyFor(Guid jobId) => new(jobId.ToString("D"));

    private static TriggerKey TriggerKeyFor(Guid jobId) => new(jobId.ToString("D"));

    /// <summary>
    /// Schedule the one-shot job for <paramref name="workflowId"/> keyed by <paramref name="jobId"/>,
    /// firing at the next Cronos occurrence of <paramref name="cron"/>. If there is no future
    /// occurrence, logs nothing and skips (business — D-09).
    /// </summary>
    public async Task ScheduleAsync(Guid workflowId, Guid jobId, string cron, CancellationToken ct)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var next = CronInterval.NextOccurrence(cron, nowUtc);
        if (next is not { } nextUtc)
        {
            return; // no future occurrence — business skip
        }

        var jobKey = KeyFor(jobId);
        var job = JobBuilder.Create<WorkflowFireJob>()
            .WithIdentity(jobKey)
            .UsingJobData("workflowId", workflowId.ToString("D"))
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity(TriggerKeyFor(jobId))
            .ForJob(jobKey)
            .StartAt(new DateTimeOffset(nextUtc, TimeSpan.Zero))
            .WithSimpleSchedule(s => s.WithMisfireHandlingInstructionFireNow())
            .Build();

        await scheduler.ScheduleJob(job, trigger, ct);
    }

    /// <summary>
    /// Schedule a NEW one-shot trigger for the EXISTING job <paramref name="jobId"/> at the next
    /// Cronos occurrence (Pitfall 4b — does NOT re-add the job). Skips when there is no future
    /// occurrence.
    /// </summary>
    public async Task RescheduleAsync(Guid jobId, string cron, CancellationToken ct)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var next = CronInterval.NextOccurrence(cron, nowUtc);
        if (next is not { } nextUtc)
        {
            return; // no future occurrence — business skip
        }

        var trigger = TriggerBuilder.Create()
            .WithIdentity(TriggerKeyFor(jobId))
            .ForJob(KeyFor(jobId))
            .StartAt(new DateTimeOffset(nextUtc, TimeSpan.Zero))
            .WithSimpleSchedule(s => s.WithMisfireHandlingInstructionFireNow())
            .Build();

        await scheduler.ScheduleJob(trigger, ct);
    }

    /// <summary>
    /// Delete the job <paramref name="jobId"/> and all of its triggers atomically (Pitfall 4c).
    /// </summary>
    public Task UnscheduleAsync(Guid jobId, CancellationToken ct) =>
        scheduler.DeleteJob(KeyFor(jobId), ct);

    /// <summary>Pause the job's current triggers (Quartz PauseJob) — Pause, D-06/D-08. Idempotent.</summary>
    public Task PauseAsync(Guid jobId, CancellationToken ct) =>
        scheduler.PauseJob(KeyFor(jobId), ct);

    /// <summary>Read the deterministic trigger's state (Normal=Running / Paused / None=Stopped) — D-05.
    /// Returns None for an unknown key (RESEARCH §4), never throws.</summary>
    public Task<TriggerState> GetTriggerStateAsync(Guid jobId, CancellationToken ct) =>
        scheduler.GetTriggerState(TriggerKeyFor(jobId), ct);
}
