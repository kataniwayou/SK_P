using MassTransit;

namespace Orchestrator.Consumers;

/// <summary>
/// Endpoint config seam for <see cref="ResumeWorkflowConsumer"/> (PAUSE-04 / D-07). Binds the SAME
/// dedicated shared fan-out endpoint <c>"orchestrator-pauseresume"</c> as
/// <see cref="PauseWorkflowConsumerDefinition"/> and sets <c>ConcurrentMessageLimit = 1</c> (serial —
/// NO lock, NO stripe). It deliberately does NOT register a second <c>UseMessageRetry</c>:
/// <c>UseMessageRetry</c> is per-endpoint and the Pause definition already owns retry for this shared
/// endpoint (RESEARCH §5). No <c>IOptions&lt;RetryOptions&gt;</c> needed here.
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
        consumerConfigurator.ConcurrentMessageLimit = 1; // D-07 serial; retry owned by Pause def on the shared endpoint
    }
}
