using MassTransit;
using Messaging.Contracts;

namespace Orchestrator.Consumers;

/// <summary>
/// Endpoint config seam for <see cref="StepProcessingConsumer"/> (D-07). Co-locates on the SAME shared
/// competing-consumer queue <see cref="OrchestratorQueues.Result"/> (<c>"orchestrator-result"</c>) as the
/// other three typed result consumers.
/// <para>
/// <b>Intentional no-op <c>ConfigureConsumer</c> (Pitfall 4):</b> endpoint-level retry for
/// <c>orchestrator-result</c> is owned solely by <see cref="StepCompletedConsumerDefinition"/>. Because
/// <c>UseMessageRetry</c> is PER-ENDPOINT (not per-consumer), only ONE definition on the shared endpoint
/// may register it; this sibling deliberately registers nothing.
/// </para>
/// </summary>
public sealed class StepProcessingConsumerDefinition : ConsumerDefinition<StepProcessingConsumer>
{
    public StepProcessingConsumerDefinition()
    {
        EndpointName = OrchestratorQueues.Result;   // "orchestrator-result" — shared competing-consumer
    }

    // Intentional no-op: endpoint-level retry owned by StepCompletedConsumerDefinition (Pitfall 4).
}
