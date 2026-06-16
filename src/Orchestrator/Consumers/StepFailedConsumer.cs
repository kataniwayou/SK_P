using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using Orchestrator.L1;
using Orchestrator.Observability;
using Orchestrator.Recovery;

namespace Orchestrator.Consumers;

/// <summary>
/// The <see cref="StepOutcome.Failed"/> arm of the <see cref="TypedResultConsumer{TMessage}"/> family
/// (D-07 / ORCH-01). Advances only successors gated on <c>PreviousFailed</c> (or <c>Always</c>); the only
/// per-type knob is <see cref="Outcome"/>, no status if/switch.
/// </summary>
public sealed class StepFailedConsumer(
    IWorkflowL1Store store,
    OrchestratorResultPipeline pipeline,
    OrchestratorMetrics metrics,
    ILogger<StepFailed> logger)
    : TypedResultConsumer<StepFailed>(store, pipeline, metrics, logger)
{
    protected override StepOutcome Outcome => StepOutcome.Failed;
}
