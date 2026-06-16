using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using Orchestrator.L1;
using Orchestrator.Observability;
using Orchestrator.Recovery;

namespace Orchestrator.Consumers;

/// <summary>
/// The <see cref="StepOutcome.Processing"/> arm of the <see cref="TypedResultConsumer{TMessage}"/> family
/// (D-07 / ORCH-01). Advances only successors gated on <c>PreviousProcessing</c> (or <c>Always</c>); the
/// only per-type knob is <see cref="Outcome"/>, no status if/switch.
/// </summary>
public sealed class StepProcessingConsumer(
    IWorkflowL1Store store,
    OrchestratorResultPipeline pipeline,
    OrchestratorMetrics metrics,
    ILogger<StepProcessing> logger)
    : TypedResultConsumer<StepProcessing>(store, pipeline, metrics, logger)
{
    protected override StepOutcome Outcome => StepOutcome.Processing;
}
