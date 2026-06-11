using MassTransit;

namespace Orchestrator.Consumers;

/// <summary>
/// Endpoint config seam for <see cref="StopOrchestrationConsumer"/>
/// (MSG-ACK-03/04) — mirrors <see cref="StartOrchestrationConsumerDefinition"/>
/// exactly. Same <c>EndpointName "orchestrator"</c> so <c>ConfigureEndpoints</c> groups both consumers
/// onto one per-replica fan-out endpoint (A2).
/// <para>
/// <b>Phase-53 D-01:</b> NO bus retry. A send that exhausts the in-code RetryLoop throws →
/// RabbitMQ nack-requeue (broker redelivery); there is no <c>_error</c> and no dead-letter on this
/// endpoint. <c>WorkflowRootNotFoundException</c> is never thrown (D-07 — the dead
/// <c>Ignore&lt;WorkflowRootNotFoundException&gt;</c> was removed with the retry block; the Stop
/// <c>UnscheduleOnly</c> path handles an absent root as a logged no-op/ACK).
/// </para>
/// </summary>
public sealed class StopOrchestrationConsumerDefinition : ConsumerDefinition<StopOrchestrationConsumer>
{
    public StopOrchestrationConsumerDefinition()
    {
        EndpointName = "orchestrator";   // SHARED base name (both defs)
    }

    // Intentional no-op: no bus retry (Phase-53 D-01) — send-exhaust throws → broker redelivery.
}
