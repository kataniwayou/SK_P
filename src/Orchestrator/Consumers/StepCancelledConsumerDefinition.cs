using MassTransit;
using Messaging.Contracts;

namespace Orchestrator.Consumers;

/// <summary>
/// Endpoint config seam for <see cref="StepCancelledConsumer"/> (D-07). Co-locates on the SAME shared
/// competing-consumer queue <see cref="OrchestratorQueues.Result"/> (<c>"orchestrator-result"</c>) as the
/// other three typed result consumers.
/// <para>
/// <b>Intentional no-op (Pitfall 4):</b> NO bus retry on <c>orchestrator-result</c> (Phase-53 D-01) —
/// a send that exhausts the in-code RetryLoop throws → RabbitMQ nack-requeue (broker redelivery),
/// no <c>_error</c>, no dead-letter. This sibling deliberately registers nothing.
/// </para>
/// </summary>
public sealed class StepCancelledConsumerDefinition : ConsumerDefinition<StepCancelledConsumer>
{
    public StepCancelledConsumerDefinition()
    {
        EndpointName = OrchestratorQueues.Result;   // "orchestrator-result" — shared competing-consumer
    }

    // Intentional no-op: NO bus retry on orchestrator-result (Phase-53 D-01) — send-exhaust throws → broker redelivery.
}
