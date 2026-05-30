using MassTransit;

namespace Orchestrator.Consumers;

/// <summary>
/// Retry/Ignore&lt;&gt;/endpoint config seam for <see cref="StartOrchestrationConsumer"/>
/// (MSG-ACK-03/04). Bounds infra faults with <c>UseMessageRetry(r.Immediate(3))</c> → _error and
/// <c>Ignore&lt;WorkflowRootNotFoundException&gt;</c> so an escaped business exception never
/// retry-storms. Shares the base <c>EndpointName "orchestrator"</c> with the Stop definition so
/// <c>ConfigureEndpoints</c> groups both consumers onto one per-replica fan-out endpoint (A2).
/// </summary>
public sealed class StartOrchestrationConsumerDefinition : ConsumerDefinition<StartOrchestrationConsumer>
{
    public StartOrchestrationConsumerDefinition() => EndpointName = "orchestrator";   // SHARED base name (both defs)

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<StartOrchestrationConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r =>
        {
            r.Immediate(3);
            r.Ignore<WorkflowRootNotFoundException>();   // business failure never retries (D-07/D-08, MSG-ACK-03)
        });
    }
}
