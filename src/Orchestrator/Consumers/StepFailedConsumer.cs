using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using Orchestrator.Dispatch;
using Orchestrator.L1;
using Orchestrator.Observability;

namespace Orchestrator.Consumers;

/// <summary>
/// The <see cref="StepOutcome.Failed"/> arm of the <see cref="TypedResultConsumer{TMessage}"/> family
/// (D-07 / ORCH-01). Advances only successors gated on <c>PreviousFailed</c> (or <c>Always</c>); the only
/// per-type knob is <see cref="Outcome"/>, no status if/switch.
/// </summary>
public sealed class StepFailedConsumer(
    IWorkflowL1Store store,
    StepAdvancement advancement,
    IStepDispatcher dispatcher,
    OrchestratorMetrics metrics,
    ILogger<StepFailed> logger)
    : TypedResultConsumer<StepFailed>(store, advancement, dispatcher, metrics, logger)
{
    protected override StepOutcome Outcome => StepOutcome.Failed;
}
