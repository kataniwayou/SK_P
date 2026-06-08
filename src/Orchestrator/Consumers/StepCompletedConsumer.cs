using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using Orchestrator.Dispatch;
using Orchestrator.L1;
using Orchestrator.Observability;

namespace Orchestrator.Consumers;

/// <summary>
/// The <see cref="StepOutcome.Completed"/> arm of the <see cref="TypedResultConsumer{TMessage}"/> family
/// (D-07 / ORCH-01). REPLACES the retired straight-through <c>ResultConsumer</c>. Processes a direct
/// processor <see cref="StepCompleted"/> AND a Keeper-INJECT'd one byte-indistinguishably — same record,
/// same <c>orchestrator-result</c> queue, same advancement effect. The only per-type knob is
/// <see cref="Outcome"/>; no status if/switch.
/// </summary>
public sealed class StepCompletedConsumer(
    IWorkflowL1Store store,
    StepAdvancement advancement,
    IStepDispatcher dispatcher,
    OrchestratorMetrics metrics,
    ILogger<StepCompleted> logger)
    : TypedResultConsumer<StepCompleted>(store, advancement, dispatcher, metrics, logger)
{
    protected override StepOutcome Outcome => StepOutcome.Completed;
}
