using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using Orchestrator.Hydration;

namespace Orchestrator.Consumers;

/// <summary>
/// Phase 32 (req-4 / D-06): the future-fire stop. Consumes the MassTransit-auto-published
/// <c>Fault&lt;EntryStepDispatch&gt;</c> (from the processor breaker's re-throw exhausting UseMessageRetry)
/// on a per-replica InstanceId+Temporary fan-out endpoint (mirrors Start/Stop, NOT shared orchestrator-result).
/// Extracts WorkflowId from Fault.Message, reuses the idempotent keep-L1 <see cref="WorkflowLifecycle.UnscheduleOnlyAsync"/>.
/// Only the schedule-owning replica acts; others no-op (absent-from-L1 business no-op inside lifecycle).
/// <para>
/// D-13: reads/writes NO flag[H] key and NO L2 marker (the processor already set the marker; the halt is
/// naturally idempotent — DO NOT seed flag[resultH]=Pending here, unlike the Completed-result pre-write).
/// The ctor carries ONLY <see cref="WorkflowLifecycle"/> + <see cref="ILogger{T}"/> (no Redis handle), so it
/// CANNOT touch flag[H] or the cancelled marker by construction (T-32-08c / Pitfall 4).
/// </para>
/// </summary>
public sealed class FaultUnscheduleConsumer(
    WorkflowLifecycle lifecycle,
    ILogger<FaultUnscheduleConsumer> logger) : IConsumer<Fault<EntryStepDispatch>>
{
    public async Task Consume(ConsumeContext<Fault<EntryStepDispatch>> context)
    {
        // Fault<T>.Message IS the original EntryStepDispatch (double .Message — proven by the Plan-01
        // FaultConsumerBindingFacts: Fault<EntryStepDispatch>.Message.WorkflowId round-trips, no fallback).
        var workflowId = context.Message.Message.WorkflowId;
        logger.LogWarning("Fault halt — unscheduling workflow {WorkflowId}", workflowId);

        // Reuse the idempotent keep-L1 unschedule (D-06). Absent-from-L1 ⇒ business no-op inside lifecycle,
        // so only the schedule-owning replica acts and a duplicate fault delivery is a harmless no-op.
        // NO flag[H] read/write (D-13). NO L2 marker write (the processor already set it).
        await lifecycle.UnscheduleOnlyAsync(workflowId, context.CancellationToken);
        // returns normally -> ACK
    }
}
