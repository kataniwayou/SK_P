using Quartz;
using Quartz.Impl.Matchers;

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
    /// <para>
    /// <b>WR-02 / D-04:</b> when no prior trigger exists on the deterministic key (the non-durable
    /// <see cref="WorkflowFireJob"/> may have been purged once it had no triggers), this method
    /// RE-CREATES the full job+trigger — mirroring <see cref="ScheduleAsync"/> — so the fallback
    /// re-establishes the schedule instead of throwing <c>JobPersistenceException</c>. The re-created
    /// job re-stamps the <paramref name="workflowId"/> job-data so the resurrected fire keeps its
    /// workflow context; hence <paramref name="workflowId"/> must be supplied by the caller (it cannot
    /// be recovered from a purged job).
    /// </para>
    /// </summary>
    public async Task RescheduleAsync(Guid workflowId, Guid jobId, string cron, CancellationToken ct)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var next = CronInterval.NextOccurrence(cron, nowUtc);
        if (next is not { } nextUtc)
        {
            return; // no future occurrence — business skip
        }

        var triggerKey = TriggerKeyFor(jobId);
        var trigger = TriggerBuilder.Create()
            .WithIdentity(triggerKey)
            .ForJob(KeyFor(jobId))
            .StartAt(new DateTimeOffset(nextUtc, TimeSpan.Zero))
            .WithSimpleSchedule(s => s.WithMisfireHandlingInstructionFireNow())
            .Build();

        // Pitfall 4b (deterministic-key edition): the previous one-shot trigger shares this
        // deterministic TriggerKey and is still in the store while the firing job body runs — Quartz
        // removes a completed no-repeat trigger only AFTER Execute returns. A blind ScheduleJob (add)
        // would therefore throw ObjectAlreadyExistsException on the same key. Replace the existing
        // trigger atomically instead: RescheduleJob removes the old trigger (WITHOUT deleting the
        // non-durable job) and stores the new one, returning null only when no prior trigger existed —
        // in which case there is nothing to collide with, so add a fresh one.
        var replaced = await scheduler.RescheduleJob(triggerKey, trigger, ct);
        if (replaced is null)
        {
            // No prior trigger on the deterministic key. The non-durable WorkflowFireJob may have been
            // purged (Quartz auto-removes a non-durable job once it has no triggers), so a bare
            // ScheduleJob(trigger) would throw JobPersistenceException. Re-create the FULL job+trigger —
            // mirroring ScheduleAsync — so the fallback re-establishes the schedule instead of throwing
            // (WR-02 / D-04). The re-created job MUST re-stamp the workflowId job-data or the resurrected
            // fire loses its workflow context. The trigger above already .ForJob(KeyFor(jobId)), so it
            // binds to the re-created job by key.
            var job = JobBuilder.Create<WorkflowFireJob>()
                .WithIdentity(KeyFor(jobId))
                .UsingJobData("workflowId", workflowId.ToString("D"))
                .Build();
            await scheduler.ScheduleJob(job, trigger, ct);
        }
    }

    /// <summary>
    /// Delete the job <paramref name="jobId"/> and all of its triggers atomically (Pitfall 4c).
    /// </summary>
    public Task UnscheduleAsync(Guid jobId, CancellationToken ct) =>
        scheduler.DeleteJob(KeyFor(jobId), ct);

    /// <summary>Pause the job's current triggers (Quartz PauseJob) — Pause, D-06/D-08. Idempotent.</summary>
    public Task PauseAsync(Guid jobId, CancellationToken ct) =>
        scheduler.PauseJob(KeyFor(jobId), ct);

    /// <summary>Scheduler-wide pause-all (ORCH-02, D-01). Idempotent — re-pausing already-paused groups is a Quartz no-op.</summary>
    public Task PauseAllAsync(CancellationToken ct) => scheduler.PauseAll(ct);

    /// <summary>
    /// Scheduler-wide resume-all GROUP-FLAG CLEAR (GAP-49-2 / D-08 Option A). Clears Quartz's
    /// <c>pausedTriggerGroups</c> set via <c>ResumeTriggers(AnyGroup())</c> so triggers ADDED AFTER a
    /// prior <see cref="PauseAllAsync"/> are no longer born <c>Paused</c>. MUST be called only AFTER the
    /// per-job <c>WorkflowLifecycle.ResumeAsync</c> loop has replaced every paused trigger with a
    /// fresh-from-now <c>Normal</c> trigger — by then no stale paused trigger remains, so clearing the
    /// group flag fires NO misfire herd. Idempotent: resuming non-paused groups is a Quartz no-op.
    /// </summary>
    public Task ResumeAllGroupsAsync(CancellationToken ct) =>
        scheduler.ResumeTriggers(GroupMatcher<TriggerKey>.AnyGroup(), ct);

    /// <summary>Read the deterministic trigger's state (Normal=Running / Paused / None=Stopped) — D-05.
    /// Returns None for an unknown key (RESEARCH §4), never throws.</summary>
    public Task<TriggerState> GetTriggerStateAsync(Guid jobId, CancellationToken ct) =>
        scheduler.GetTriggerState(TriggerKeyFor(jobId), ct);
}
