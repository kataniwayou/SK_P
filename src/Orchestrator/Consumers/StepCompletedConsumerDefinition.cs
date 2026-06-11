using MassTransit;
using Messaging.Contracts;

namespace Orchestrator.Consumers;

/// <summary>
/// Endpoint config seam for <see cref="StepCompletedConsumer"/> (D-07 / ORCH-01). Binds the STABLE
/// shared competing-consumer queue <see cref="OrchestratorQueues.Result"/> (<c>"orchestrator-result"</c>)
/// — NOT a per-replica fan-out / <c>InstanceId</c>/<c>Temporary</c> endpoint: a result is consumed
/// exactly once across the consumer set, never broadcast. All four typed result consumers
/// (Completed/Failed/Cancelled/Processing) co-locate on this SAME endpoint as competing consumers.
/// <para>
/// <b>Phase-53 D-01:</b> this endpoint registers NO bus retry. A send that exhausts the in-code
/// RetryLoop throws → RabbitMQ nack-requeue (broker redelivery); there is no <c>_error</c> and no
/// dead-letter. The sibling typed-result definitions (Failed/Cancelled/Processing) remain intentional
/// no-ops.
/// </para>
/// </summary>
public sealed class StepCompletedConsumerDefinition : ConsumerDefinition<StepCompletedConsumer>
{
    public StepCompletedConsumerDefinition()
    {
        EndpointName = OrchestratorQueues.Result;   // "orchestrator-result" — shared competing-consumer
    }

    // Intentional no-op: no bus retry (Phase-53 D-01) — send-exhaust throws → broker redelivery.
}
