using MassTransit;

namespace Orchestrator.Consumers;

/// <summary>
/// Endpoint config seam for <see cref="PauseWorkflowConsumer"/> (PAUSE-04 / D-07). Binds the
/// DEDICATED shared fan-out endpoint <c>"orchestrator-pauseresume"</c> (NOT "orchestrator") so
/// Pause+Resume own their own <c>ConcurrentMessageLimit</c> and do not throttle Start/Stop/Result
/// (RESEARCH §5b / A3). <c>ConcurrentMessageLimit = 1</c> serializes delivery so a duplicate
/// Pause/Resume replays against idempotent Quartz transitions — NO lock, NO stripe (D-07).
/// <para>
/// <b>Phase-53 D-01:</b> this endpoint registers NO bus retry. A send that exhausts the in-code
/// RetryLoop throws → RabbitMQ nack-requeue (broker redelivery); no <c>_error</c>, no dead-letter.
/// <c>ConcurrentMessageLimit = 1</c> is serialization, orthogonal to retry, and is KEPT.
/// </para>
/// </summary>
public sealed class PauseWorkflowConsumerDefinition : ConsumerDefinition<PauseWorkflowConsumer>
{
    public PauseWorkflowConsumerDefinition()
    {
        EndpointName = "orchestrator-pauseresume";   // DEDICATED shared base name (Pause + Resume)
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<PauseWorkflowConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        consumerConfigurator.ConcurrentMessageLimit = 1;   // D-07 serial — no lock/stripe (retry removed, Phase-53 D-01)
    }
}
