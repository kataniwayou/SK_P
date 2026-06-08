using MassTransit;

namespace Orchestrator.Consumers;

/// <summary>
/// Endpoint config seam for <see cref="ResumeAllConsumer"/> (ORCH-02 / D-08). Binds the SAME NEW
/// dedicated per-replica fan-out endpoint <c>"orchestrator-global-pauseresume"</c> as
/// <see cref="PauseAllConsumerDefinition"/> (one shared endpoint) and sets
/// <c>ConcurrentMessageLimit = 1</c> (serial — NO lock, NO stripe). It deliberately does NOT register a
/// second <c>UseMessageRetry</c>: <c>UseMessageRetry</c> is per-endpoint and the Pause definition already
/// owns retry for this shared endpoint (Pitfall 4 — a second registration double-wraps retry). No
/// <c>IOptions&lt;RetryOptions&gt;</c> needed here (parameterless ctor).
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
        consumerConfigurator.ConcurrentMessageLimit = 1;   // serial; retry owned by the Pause def — NO second UseMessageRetry (Pitfall 4)
    }
}
