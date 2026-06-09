using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using Orchestrator.Hydration;
using Orchestrator.L1;
using Orchestrator.Scheduling;

namespace Orchestrator.Consumers;

/// <summary>
/// Global resume-all consumer (ORCH-02 / D-02). Resumes EVERY workflow on this replica PER-JOB by
/// enumerating the L1 <see cref="IWorkflowL1Store.WorkflowIds"/> snapshot and running the shared
/// <see cref="WorkflowLifecycle.ResumeAsync"/> for each — which acts ONLY when the trigger is
/// <c>Paused</c> (deletes the stale paused job and schedules a FRESH from-now trigger off L1's cron,
/// skip-to-next, no immediate refire). <c>None</c>(Stopped)/<c>Normal</c>(Running) are per-job no-ops, so
/// resume is idempotent under serial replay. THEN, after the per-job loop, it calls ONE group-level
/// <see cref="WorkflowScheduler.ResumeAllGroupsAsync"/> (<c>scheduler.ResumeAll()</c>) to clear Quartz's
/// <c>pausedTriggerGroups</c> set — so workflows scheduled AFTER a prior scheduler-wide <c>PauseAll()</c>
/// are born <c>Normal</c> again instead of inheriting the paused group (GAP-49-2 / D-08 Option A).
/// <para>
/// <b>LOAD-BEARING ORDERING (GAP-49-2 / D-08 / T-49-01):</b> the binding guarantee is "no immediate-refire
/// herd," NOT "no group-level resume call ever." The group-level <c>ResumeAll()</c> MUST run strictly AFTER
/// the per-job <c>ResumeAsync</c> loop completes — by then every workflow trigger is already a
/// fresh-from-now <c>Normal</c> trigger (<c>StartAt >= now</c>), so the global resume finds NO stale paused
/// trigger to misfire-herd. Placing the group clear before (or instead of) the per-job loop would
/// re-introduce the cross-workflow catch-up herd the original lock guarded against. (<c>ResumeAll()</c> is
/// used rather than <c>ResumeTriggers(GroupMatcher.AnyGroup())</c> because only the former clears the
/// <c>pausedTriggerGroups</c> future-pause flag in Quartz 3.18 RAMJobStore — see
/// <see cref="WorkflowScheduler.ResumeAllGroupsAsync"/>.) The broadcast carries ONLY a tracing
/// <c>CorrelationId</c> (no-H, RETIRE-01).
/// </para>
/// </summary>
public sealed class ResumeAllConsumer(
    IWorkflowL1Store store,
    WorkflowLifecycle lifecycle,
    WorkflowScheduler scheduler,
    ILogger<ResumeAllConsumer> logger) : IConsumer<ResumeAll>
{
    public async Task Consume(ConsumeContext<ResumeAll> context)
    {
        // Structured template holes — NEVER interpolated (T-37-05 / T-45-09 / security V5). Only a Guid CorrelationId crosses.
        logger.LogInformation("Global ResumeAll CorrelationId={CorrelationId}", context.Message.CorrelationId);
        foreach (var workflowId in store.WorkflowIds)                          // L1 snapshot (IWorkflowL1Store.WorkflowIds)
            await lifecycle.ResumeAsync(workflowId, context.CancellationToken); // TriggerState==Paused guard + fresh-from-now reschedule INSIDE
        // GAP-49-2 / D-08: clear pausedTriggerGroups AFTER every per-job reschedule (load-bearing ordering —
        // by now every workflow trigger is a fresh-from-now Normal trigger, so this group-clear fires no herd).
        await scheduler.ResumeAllGroupsAsync(context.CancellationToken);
        // returns normally -> ACK
    }
}
