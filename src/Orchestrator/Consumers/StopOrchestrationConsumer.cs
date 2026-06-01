using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using Orchestrator.Hydration;

namespace Orchestrator.Consumers;

/// <summary>
/// Stop consumer (ORCH-STOP-DRAIN-01 — supersedes Phase 23 ORCH-STOP-01). Conditionless drain-stop:
/// for each WorkflowId it UNCONDITIONALLY runs the shared
/// <see cref="WorkflowLifecycle.UnscheduleOnlyAsync"/>: resolve the jobId from L1,
/// <c>DeleteJob(JobKey(jobId))</c>, and KEEP the L1 entry (D-07) — ZERO L2 writes. Keeping L1 means a
/// late <c>ExecutionResult</c> for the stopped workflow still resolves in
/// L1 and dispatches its next steps (graceful drain of in-flight executions). There is NO existence
/// skip and NO per-workflow stripe — duplicate-suppression lives in the WebApi (D-04/D-05).
/// <para>
/// Boot gate REMOVED (24.1 / D-24.1-05, supersedes D-06 / ORCH-GATE-01): the consumer runs only after
/// the bus starts; Stop stays <c>UnscheduleOnlyAsync</c> (keep-L1 drain, D-07). Absent-from-L1 is a
/// business no-op inside <see cref="WorkflowLifecycle"/>; only INFRA faults propagate to the
/// definition's bounded retry -> <c>_error</c>.
/// </para>
/// </summary>
public sealed class StopOrchestrationConsumer(
    WorkflowLifecycle lifecycle,
    ILogger<StopOrchestrationConsumer> logger) : IConsumer<StopOrchestration>
{
    public async Task Consume(ConsumeContext<StopOrchestration> context)
    {
        foreach (var workflowId in context.Message.WorkflowIds)
        {
            // Conditionless keep-L1 (D-05/D-07): unschedule the Quartz job but KEEP the L1 entry so a
            // late result still drains. Do NOT call TeardownAsync (it would remove L1). No stripe.
            logger.LogInformation("Stop drain for WorkflowId={WorkflowId}", workflowId);
            await lifecycle.UnscheduleOnlyAsync(workflowId, context.CancellationToken);
        }
        // returns normally -> ACK
    }
}
