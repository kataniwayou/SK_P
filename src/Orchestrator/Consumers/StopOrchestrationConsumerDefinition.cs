using MassTransit;

namespace Orchestrator.Consumers;

/// <summary>
/// Retry/Ignore&lt;&gt;/endpoint config seam for <see cref="StopOrchestrationConsumer"/>
/// (MSG-ACK-03/04) — mirrors <see cref="StartOrchestrationConsumerDefinition"/> exactly. Same
/// <c>EndpointName "orchestrator"</c> so <c>ConfigureEndpoints</c> groups both consumers onto one
/// per-replica fan-out endpoint (A2).
/// </summary>
public sealed class StopOrchestrationConsumerDefinition : ConsumerDefinition<StopOrchestrationConsumer>
{
    public StopOrchestrationConsumerDefinition() => EndpointName = "orchestrator";   // SHARED base name (both defs)

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<StopOrchestrationConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r =>
        {
            r.Immediate(3);
            r.Ignore<WorkflowRootNotFoundException>();   // business failure never retries (D-07/D-08, MSG-ACK-03)
        });
    }
}
