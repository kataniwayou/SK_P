using MassTransit;

namespace Orchestrator.Consumers;

/// <summary>
/// Endpoint config seam for <see cref="ResumeWorkflowConsumer"/> (PAUSE-04 / D-07). Binds the SAME
/// dedicated shared fan-out endpoint <c>"orchestrator-pauseresume"</c> as
/// <see cref="PauseWorkflowConsumerDefinition"/> and sets <c>ConcurrentMessageLimit = 1</c> (serial —
/// NO lock, NO stripe). This endpoint registers NO bus retry (Phase-53 D-01): a send that exhausts the
/// in-code RetryLoop throws → RabbitMQ nack-requeue (broker redelivery), no <c>_error</c>, no
/// dead-letter. No <c>IOptions&lt;RetryOptions&gt;</c> needed here (parameterless ctor).
/// </summary>
public sealed class ResumeWorkflowConsumerDefinition : ConsumerDefinition<ResumeWorkflowConsumer>
{
    public ResumeWorkflowConsumerDefinition()
    {
        EndpointName = "orchestrator-pauseresume";   // SAME dedicated shared base name as Pause def
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<ResumeWorkflowConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        consumerConfigurator.ConcurrentMessageLimit = 1; // D-07 serial; no bus retry on this endpoint (Phase-53 D-01)
    }
}
