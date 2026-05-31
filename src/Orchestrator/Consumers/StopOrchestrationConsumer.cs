using BaseConsole.Core.Health;
using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using Orchestrator.Hydration;

namespace Orchestrator.Consumers;

/// <summary>
/// Stop consumer (ORCH-STOP-DRAIN-01 — supersedes Phase 23 ORCH-STOP-01). Conditionless drain-stop:
/// for each WorkflowId, when the startup gate is open it UNCONDITIONALLY runs the shared
/// <see cref="WorkflowLifecycle.UnscheduleOnlyAsync"/>: resolve the jobId from L1,
/// <c>DeleteJob(JobKey(jobId))</c>, and KEEP the L1 entry (D-07) — ZERO L2 writes. Keeping L1 means a
/// late <c>ExecutionResult</c> for the stopped workflow still resolves in
/// L1 and dispatches its next steps (graceful drain of in-flight executions). There is NO existence
/// skip and NO per-workflow stripe — duplicate-suppression lives in the WebApi (D-04/D-05).
/// <para>
/// Gate-closed never-drop (D-06 — ORCH-GATE-01): when the gate is closed the consumer THROWS
/// <see cref="GateClosedException"/> so the scheduled-redelivery middleware reschedules the message
/// past hydration and it is reprocessed after <c>MarkReady</c> — NEVER ack-dropped (inverts the Phase
/// 23 D-12 ack-drop). Absent-from-L1 is a business no-op inside <see cref="WorkflowLifecycle"/>; only
/// INFRA faults propagate to the definition's bounded retry -> <c>_error</c>.
/// </para>
/// </summary>
public sealed class StopOrchestrationConsumer(
    IStartupGate gate,
    WorkflowLifecycle lifecycle,
    ILogger<StopOrchestrationConsumer> logger) : IConsumer<StopOrchestration>
{
    public async Task Consume(ConsumeContext<StopOrchestration> context)
    {
        if (!gate.IsReady)
        {
            // D-06: gate closed — THROW so the message is redelivered after hydration (NEVER ack-drop).
            logger.LogInformation("Gate closed — redelivering Stop (throw GateClosedException)");
            throw new GateClosedException();
        }

        foreach (var workflowId in context.Message.WorkflowIds)
        {
            // Conditionless keep-L1 (D-05/D-07): unschedule the Quartz job but KEEP the L1 entry so a
            // late result still drains. Do NOT call TeardownAsync (it would remove L1). No stripe.
            await lifecycle.UnscheduleOnlyAsync(workflowId, context.CancellationToken);
        }
        // returns normally -> ACK
    }
}
