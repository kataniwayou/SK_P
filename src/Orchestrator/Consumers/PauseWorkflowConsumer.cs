using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using Orchestrator.Hydration;

namespace Orchestrator.Consumers;

/// <summary>
/// Pause consumer (PAUSE-02 / D-08). Halts the cron for one <c>WorkflowId</c> by resolving its jobId
/// from L1 and running the shared <see cref="WorkflowLifecycle.PauseOnlyAsync"/>: <c>PauseJob</c> —
/// which preserves the job + trigger and KEEPS the L1 entry (D-06). Idempotent (re-pausing a paused
/// job is a Quartz no-op); absent-from-L1 is a business no-op. There is NO dedicated lock, NO
/// reference-counting set, and deliberately NOT the <c>IWorkflowL1Store</c> <c>Wait(0)</c>
/// drop-if-held stripe (D-07) — serialization is the definition's <c>ConcurrentMessageLimit = 1</c>
/// plus idempotent Quartz transitions plus redelivery-on-crash.
/// </summary>
public sealed class PauseWorkflowConsumer(
    WorkflowLifecycle lifecycle,
    ILogger<PauseWorkflowConsumer> logger) : IConsumer<PauseWorkflow>
{
    public async Task Consume(ConsumeContext<PauseWorkflow> context)
    {
        var m = context.Message;
        // Structured template holes — NEVER interpolated (T-37-05 / security V5).
        logger.LogInformation("Pause WorkflowId={WorkflowId} H={H}", m.WorkflowId, m.H);
        await lifecycle.PauseOnlyAsync(m.WorkflowId, context.CancellationToken);
        // returns normally -> ACK
    }
}
