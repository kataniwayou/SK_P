using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using Orchestrator.Scheduling;

namespace Orchestrator.Consumers;

/// <summary>
/// Global pause-all consumer (ORCH-02 / D-01). Halts the cron for EVERY workflow on this replica by
/// calling the scheduler-wide <see cref="WorkflowScheduler.PauseAllAsync"/> (Quartz <c>PauseAll</c>) —
/// scheduler-wide, NOT per-job. Idempotent: a duplicate delivery re-invokes <c>PauseAll</c>, which is a
/// Quartz no-op for already-paused groups (no exception). There is NO lock, NO stripe — serialization is
/// the definition's <c>ConcurrentMessageLimit = 1</c> plus idempotent Quartz transitions plus
/// redelivery-on-crash. The broadcast carries ONLY a tracing <c>CorrelationId</c> (no-H, RETIRE-01).
/// </summary>
public sealed class PauseAllConsumer(
    WorkflowScheduler scheduler,
    ILogger<PauseAllConsumer> logger) : IConsumer<PauseAll>
{
    public async Task Consume(ConsumeContext<PauseAll> context)
    {
        // Structured template holes — NEVER interpolated (T-37-05 / T-45-09 / security V5). Only a Guid CorrelationId crosses.
        logger.LogWarning("Global PauseAll CorrelationId={CorrelationId}", context.Message.CorrelationId);
        await scheduler.PauseAllAsync(context.CancellationToken);   // idempotent (re-pause = Quartz no-op, D-01)
        // returns normally -> ACK
    }
}
