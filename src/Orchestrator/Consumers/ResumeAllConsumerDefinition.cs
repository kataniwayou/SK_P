using MassTransit;

namespace Orchestrator.Consumers;

/// <summary>
/// Endpoint config seam for <see cref="ResumeAllConsumer"/> (ORCH-02 / D-08). Binds the SAME NEW
/// dedicated per-replica fan-out endpoint <c>"orchestrator-global-pauseresume"</c> as
/// <see cref="PauseAllConsumerDefinition"/> (one shared endpoint) and sets
/// <c>ConcurrentMessageLimit = 1</c> (serial — NO lock, NO stripe). This endpoint registers NO bus
/// retry (Phase-53 D-01): a send that exhausts the in-code RetryLoop throws → RabbitMQ nack-requeue
/// (broker redelivery), no <c>_error</c>, no dead-letter. No <c>IOptions&lt;RetryOptions&gt;</c>
/// needed here (parameterless ctor).
/// </summary>
public sealed class ResumeAllConsumerDefinition : ConsumerDefinition<ResumeAllConsumer>
{
    public ResumeAllConsumerDefinition()
    {
        EndpointName = "orchestrator-global-pauseresume";   // SAME base name as the Pause def (one shared endpoint)
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<ResumeAllConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        consumerConfigurator.ConcurrentMessageLimit = 1;   // serial; no bus retry on this endpoint (Phase-53 D-01)
    }
}
