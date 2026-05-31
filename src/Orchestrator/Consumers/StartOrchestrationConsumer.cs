using BaseConsole.Core.Health;
using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using Orchestrator.Hydration;
using Orchestrator.L1;

namespace Orchestrator.Consumers;

/// <summary>
/// Start consumer (ORCH-CONSUME-01). Gated tolerant-reload: for each WorkflowId in the body it
/// drops cleanly when the startup gate is closed or the per-workflow stripe is held (D-12/D-14),
/// then runs the shared <see cref="WorkflowLifecycle"/> teardown+hydrate+schedule (D-15) so the
/// live runtime re-applies the current L2 definition. Teardown reuses the Stop path; the stripe is
/// always released in a <c>finally</c>.
/// <para>
/// Ack split (ORCH-ACK-01): a gate-closed or stripe-held drop is a clean <c>return</c> (ack), NEVER
/// a throw (Pitfall 6 — no early control message reaches <c>_error</c>). Business outcomes (absent
/// root/step) are logged + skipped INSIDE <see cref="WorkflowLifecycle"/>. Only INFRA faults
/// propagate out of <c>Consume</c> -> the definition's bounded retry -> <c>_error</c> (D-02/D-17);
/// this consumer does NOT catch-all infra.
/// </para>
/// </summary>
public sealed class StartOrchestrationConsumer(
    IStartupGate gate,
    IWorkflowL1Store store,
    WorkflowLifecycle lifecycle,
    ILogger<StartOrchestrationConsumer> logger) : IConsumer<StartOrchestration>
{
    public async Task Consume(ConsumeContext<StartOrchestration> context)
    {
        if (!gate.IsReady)
        {
            // D-12: gate closed (initial hydration not complete) — ACK + drop, NEVER throw (Pitfall 6).
            logger.LogInformation("Gate closed — dropping Start (ack)");
            return;
        }

        foreach (var workflowId in context.Message.WorkflowIds)
        {
            if (!store.TryAcquire(workflowId))
            {
                // D-14: stripe held by an in-flight lifecycle op — drop (ack).
                logger.LogInformation("Stripe held for {WorkflowId} — dropping (ack)", workflowId);
                continue;
            }

            try
            {
                await lifecycle.TeardownAsync(workflowId, context.CancellationToken);          // D-15 tolerant teardown (reuses Stop)
                await lifecycle.HydrateAndScheduleAsync(workflowId, context.CancellationToken); // re-apply current L2 definition
            }
            finally
            {
                store.Release(workflowId); // always release the stripe
            }
        }
        // returns normally -> ACK
    }
}
