using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using Orchestrator.Hydration;

namespace Orchestrator.Consumers;

/// <summary>
/// Start consumer (ORCH-START-RELOAD-01 — supersedes Phase 23 ORCH-CONSUME-01). Conditionless reload:
/// for each WorkflowId in the body it UNCONDITIONALLY runs the shared
/// <see cref="WorkflowLifecycle"/> teardown (unschedule old Quartz job + clear L1 — Pitfall 4) then
/// hydrate+schedule (re-apply the current L2 definition + schedule). There is NO existence skip and NO
/// per-workflow stripe — duplicate-suppression now lives in the WebApi (D-04/D-05), so a Start for a
/// workflow lingering in L1 (e.g. after a Stop drain) re-hydrates and reschedules, reviving its job.
/// <para>
/// Boot gate REMOVED (24.1 / D-24.1-05, supersedes D-06 / ORCH-GATE-01): the consumer runs only after
/// the bus starts; per-workflow state is per-instance (no gate serialization), acceptable under
/// single-replica + the existing per-workflow handling. Business outcomes (absent root/step) are
/// logged + skipped INSIDE <see cref="WorkflowLifecycle"/>. Only INFRA faults propagate out of
/// <c>Consume</c> -> the definition's bounded retry -> <c>_error</c> (D-02); this consumer does NOT
/// catch-all infra.
/// </para>
/// </summary>
public sealed class StartOrchestrationConsumer(
    WorkflowLifecycle lifecycle,
    ILogger<StartOrchestrationConsumer> logger) : IConsumer<StartOrchestration>
{
    public async Task Consume(ConsumeContext<StartOrchestration> context)
    {
        foreach (var workflowId in context.Message.WorkflowIds)
        {
            // Conditionless (D-05): unschedule the old Quartz job + clear L1 (Pitfall 4), then re-hydrate
            // + reschedule from the current L2 definition. The immediate re-hydrate re-Upserts L1, so the
            // transient teardown remove is harmless. No existence skip, no stripe (WebApi dedups).
            logger.LogInformation("Start reload for WorkflowId={WorkflowId}", workflowId);
            await lifecycle.TeardownAsync(workflowId, context.CancellationToken);
            await lifecycle.HydrateAndScheduleAsync(workflowId, context.CancellationToken);
        }
        // returns normally -> ACK
    }
}
