using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using Orchestrator.Hydration;
using Orchestrator.L1;

namespace Orchestrator.Consumers;

/// <summary>
/// Global resume-all consumer (ORCH-02 / D-02). Resumes EVERY workflow on this replica PER-JOB by
/// enumerating the L1 <see cref="IWorkflowL1Store.WorkflowIds"/> snapshot and running the shared
/// <see cref="WorkflowLifecycle.ResumeAsync"/> for each — which acts ONLY when the trigger is
/// <c>Paused</c> (deletes the stale paused job and schedules a FRESH from-now trigger off L1's cron,
/// skip-to-next, no immediate refire). <c>None</c>(Stopped)/<c>Normal</c>(Running) are per-job no-ops, so
/// resume is idempotent under serial replay.
/// <para>
/// <b>LOAD-BEARING (D-02 / T-45-07 / Pitfall 2):</b> this consumer MUST NEVER call native
/// <c>scheduler.ResumeAll()</c> — a global unpause would fire the cross-workflow catch-up herd. Per-job
/// <c>ResumeAsync</c> only; idempotency + no-burst are inherited free from the existing fresh-from-now
/// reschedule. The broadcast carries ONLY a tracing <c>CorrelationId</c> (no-H, RETIRE-01).
/// </para>
/// </summary>
public sealed class ResumeAllConsumer(
    IWorkflowL1Store store,
    WorkflowLifecycle lifecycle,
    ILogger<ResumeAllConsumer> logger) : IConsumer<ResumeAll>
{
    public async Task Consume(ConsumeContext<ResumeAll> context)
    {
        // Structured template holes — NEVER interpolated (T-37-05 / T-45-09 / security V5). Only a Guid CorrelationId crosses.
        logger.LogInformation("Global ResumeAll CorrelationId={CorrelationId}", context.Message.CorrelationId);
        foreach (var workflowId in store.WorkflowIds)                          // L1 snapshot (IWorkflowL1Store.WorkflowIds)
            await lifecycle.ResumeAsync(workflowId, context.CancellationToken); // TriggerState==Paused guard + fresh-from-now reschedule INSIDE
        // returns normally -> ACK
    }
}
