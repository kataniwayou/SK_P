using MassTransit;

namespace Orchestrator.Consumers;

/// <summary>
/// Endpoint config seam for <see cref="StartOrchestrationConsumer"/>
/// (MSG-ACK-03/04). Shares the base <c>EndpointName "orchestrator"</c> with the Stop
/// definition so <c>ConfigureEndpoints</c> groups both consumers onto one per-replica fan-out
/// endpoint (A2).
/// <para>
/// <b>Phase-53 D-01:</b> NO bus retry. A send that exhausts the in-code RetryLoop throws →
/// RabbitMQ nack-requeue (broker redelivery); there is no <c>_error</c> and no dead-letter on this
/// endpoint. <c>WorkflowRootNotFoundException</c> is never thrown (D-07 — the dead
/// <c>Ignore&lt;WorkflowRootNotFoundException&gt;</c> guarded a throw that does not exist and was
/// removed with the retry block; <c>WorkflowLifecycle</c> handles an absent root as a logged
/// no-op/ACK).
/// </para>
/// </summary>
public sealed class StartOrchestrationConsumerDefinition : ConsumerDefinition<StartOrchestrationConsumer>
{
    public StartOrchestrationConsumerDefinition()
    {
        EndpointName = "orchestrator";   // SHARED base name (both defs)
    }

    // Intentional no-op: no bus retry (Phase-53 D-01) — send-exhaust throws → broker redelivery.
}
