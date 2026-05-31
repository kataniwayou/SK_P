using BaseConsole.Core.Health;
using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using Orchestrator.Hydration;
using Orchestrator.L1;

namespace Orchestrator.Consumers;

/// <summary>
/// Stop consumer (ORCH-STOP-01) — mirrors <see cref="StartOrchestrationConsumer"/>'s gating but is
/// teardown-only. For each WorkflowId it drops cleanly on a closed gate or a held stripe
/// (D-12/D-14), then runs the shared <see cref="WorkflowLifecycle.TeardownAsync"/>: resolve the
/// jobId from L1, <c>DeleteJob(JobKey(jobId))</c>, and clear the L1 entry — ZERO L2 writes (D-16).
/// The stripe is always released in a <c>finally</c>.
/// <para>
/// Ack split (ORCH-ACK-01): gate-closed / stripe-held / absent-from-L1 are clean acks (return),
/// NEVER throws. Only INFRA faults propagate to the definition's bounded retry -> <c>_error</c>.
/// </para>
/// </summary>
public sealed class StopOrchestrationConsumer(
    IStartupGate gate,
    IWorkflowL1Store store,
    WorkflowLifecycle lifecycle,
    ILogger<StopOrchestrationConsumer> logger) : IConsumer<StopOrchestration>
{
    public async Task Consume(ConsumeContext<StopOrchestration> context)
    {
        if (!gate.IsReady)
        {
            // D-12: gate closed — ACK + drop, NEVER throw (Pitfall 6).
            logger.LogInformation("Gate closed — dropping Stop (ack)");
            return;
        }

        foreach (var workflowId in context.Message.WorkflowIds)
        {
            if (!store.TryAcquire(workflowId))
            {
                // D-14: stripe held — drop (ack).
                logger.LogInformation("Stripe held for {WorkflowId} — dropping (ack)", workflowId);
                continue;
            }

            try
            {
                await lifecycle.TeardownAsync(workflowId, context.CancellationToken); // D-16: jobId DeleteJob + L1 clear, zero L2 writes
            }
            finally
            {
                store.Release(workflowId); // always release the stripe
            }
        }
        // returns normally -> ACK
    }
}
