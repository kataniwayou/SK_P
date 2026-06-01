using MassTransit;

namespace Orchestrator.Consumers;

/// <summary>
/// Retry/endpoint config seam for <see cref="StartOrchestrationConsumer"/>
/// (MSG-ACK-03/04). Shares the base <c>EndpointName "orchestrator"</c> with the Stop
/// definition so <c>ConfigureEndpoints</c> groups both consumers onto one per-replica fan-out
/// endpoint (A2).
/// <para>
/// <b>24.1 / D-24.1-05:</b> the boot gate + scheduled redelivery (and the
/// <c>rabbitmq_delayed_message_exchange</c> plugin dependency) are REMOVED. Only
/// <c>UseMessageRetry(Immediate(3))</c> remains (bounded infra-fault retry → <c>_error</c>) with
/// <c>Ignore&lt;WorkflowRootNotFoundException&gt;</c> keeping an escaped business exception from
/// retry-storming. There is no longer any gate-closed exception to reschedule.
/// </para>
/// </summary>
public sealed class StartOrchestrationConsumerDefinition : ConsumerDefinition<StartOrchestrationConsumer>
{
    public StartOrchestrationConsumerDefinition() => EndpointName = "orchestrator";   // SHARED base name (both defs)

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<StartOrchestrationConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        // Bounded immediate retry of infra faults → _error. No scheduled redelivery (gate removed,
        // 24.1 / D-24.1-05) → no plugin dependency.
        endpointConfigurator.UseMessageRetry(r =>
        {
            r.Immediate(3);
            r.Ignore<WorkflowRootNotFoundException>();   // business failure never retries (D-07/D-08, MSG-ACK-03)
        });
    }
}
