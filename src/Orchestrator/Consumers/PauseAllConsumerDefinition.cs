using MassTransit;

namespace Orchestrator.Consumers;

/// <summary>
/// Endpoint config seam for <see cref="PauseAllConsumer"/> (ORCH-02 / D-08). Binds the NEW
/// dedicated per-replica fan-out endpoint <c>"orchestrator-global-pauseresume"</c> — independent from
/// the per-workflow <c>"orchestrator-pauseresume"</c> so Phase 48 can drop the old endpoint with zero
/// entanglement (D-08). <c>ConcurrentMessageLimit = 1</c> serializes Pause/Resume on this replica so a
/// duplicate replays against idempotent Quartz transitions — NO lock, NO stripe.
/// <para>
/// <b>Phase-53 D-01:</b> this endpoint registers NO bus retry. A send that exhausts the in-code
/// RetryLoop throws → RabbitMQ nack-requeue (broker redelivery); no <c>_error</c>, no dead-letter.
/// <c>ConcurrentMessageLimit = 1</c> is serialization, orthogonal to retry, and is KEPT.
/// </para>
/// </summary>
public sealed class PauseAllConsumerDefinition : ConsumerDefinition<PauseAllConsumer>
{
    public PauseAllConsumerDefinition()
    {
        EndpointName = "orchestrator-global-pauseresume";   // NEW dedicated base name (D-08 — independent from orchestrator-pauseresume)
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<PauseAllConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        consumerConfigurator.ConcurrentMessageLimit = 1;   // serialize Pause/Resume on this replica (retry removed, Phase-53 D-01)
    }
}
