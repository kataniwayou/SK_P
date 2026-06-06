using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using Orchestrator.Hydration;

namespace Orchestrator.Consumers;

/// <summary>
/// Resume consumer (PAUSE-03 / PAUSE-05 / D-09). Resumes one <c>WorkflowId</c> by running the shared
/// <see cref="WorkflowLifecycle.ResumeAsync"/>, which acts ONLY when the trigger is <c>Paused</c>:
/// it deletes the stale paused job and schedules a FRESH from-now trigger off L1's cron (sidesteps
/// misfire). <c>None</c> (Stopped) and <c>Normal</c> (Running) are no-ops — a Stopped workflow stays
/// Stopped until an operator Start (T-37-06/07). Idempotent under serial replay; absent-from-L1 is a
/// business no-op. NO lock / NO <c>Wait(0)</c> drop-if-held stripe (D-07) — serialization is the
/// definition's <c>ConcurrentMessageLimit = 1</c> plus idempotent Quartz transitions plus
/// redelivery-on-crash.
/// </summary>
public sealed class ResumeWorkflowConsumer(
    WorkflowLifecycle lifecycle,
    ILogger<ResumeWorkflowConsumer> logger) : IConsumer<ResumeWorkflow>
{
    public async Task Consume(ConsumeContext<ResumeWorkflow> context)
    {
        var m = context.Message;
        // Structured template holes — NEVER interpolated (T-37-05 / security V5).
        logger.LogInformation("Resume WorkflowId={WorkflowId} H={H}", m.WorkflowId, m.H);
        await lifecycle.ResumeAsync(m.WorkflowId, context.CancellationToken);
        // returns normally -> ACK
    }
}
