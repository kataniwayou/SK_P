using BaseConsole.Core.Health;
using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using Orchestrator.Hydration;

namespace Orchestrator.Consumers;

/// <summary>
/// Start consumer (ORCH-START-RELOAD-01 — supersedes Phase 23 ORCH-CONSUME-01). Conditionless reload:
/// for each WorkflowId in the body, when the startup gate is open it UNCONDITIONALLY runs the shared
/// <see cref="WorkflowLifecycle"/> teardown (unschedule old Quartz job + clear L1 — Pitfall 4) then
/// hydrate+schedule (re-apply the current L2 definition + schedule). There is NO existence skip and NO
/// per-workflow stripe — duplicate-suppression now lives in the WebApi (D-04/D-05), so a Start for a
/// workflow lingering in L1 (e.g. after a Stop drain) re-hydrates and reschedules, reviving its job.
/// <para>
/// Gate-closed never-drop (D-06 — ORCH-GATE-01): when the gate is closed (initial hydration not
/// complete) the consumer THROWS <see cref="GateClosedException"/> so the scheduled-redelivery
/// middleware reschedules the message past hydration and it is reprocessed after <c>MarkReady</c> —
/// it is NEVER ack-dropped (this inverts the Phase 23 D-12 ack-drop). Business outcomes (absent
/// root/step) are logged + skipped INSIDE <see cref="WorkflowLifecycle"/>. Only INFRA faults
/// propagate out of <c>Consume</c> -> the definition's bounded retry -> <c>_error</c> (D-02);
/// this consumer does NOT catch-all infra.
/// </para>
/// </summary>
public sealed class StartOrchestrationConsumer(
    IStartupGate gate,
    WorkflowLifecycle lifecycle,
    ILogger<StartOrchestrationConsumer> logger) : IConsumer<StartOrchestration>
{
    public async Task Consume(ConsumeContext<StartOrchestration> context)
    {
        if (!gate.IsReady)
        {
            // D-06: gate closed — THROW so the message is redelivered after hydration (NEVER ack-drop).
            logger.LogInformation("Gate closed — redelivering Start (throw GateClosedException)");
            throw new GateClosedException();
        }

        foreach (var workflowId in context.Message.WorkflowIds)
        {
            // Conditionless (D-05): unschedule the old Quartz job + clear L1 (Pitfall 4), then re-hydrate
            // + reschedule from the current L2 definition. The immediate re-hydrate re-Upserts L1, so the
            // transient teardown remove is harmless. No existence skip, no stripe (WebApi dedups).
            await lifecycle.TeardownAsync(workflowId, context.CancellationToken);
            await lifecycle.HydrateAndScheduleAsync(workflowId, context.CancellationToken);
        }
        // returns normally -> ACK
    }
}
