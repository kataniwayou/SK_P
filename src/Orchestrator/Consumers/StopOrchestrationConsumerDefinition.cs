using MassTransit;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.Options;

namespace Orchestrator.Consumers;

/// <summary>
/// Retry/endpoint config seam for <see cref="StopOrchestrationConsumer"/>
/// (MSG-ACK-03/04) — mirrors <see cref="StartOrchestrationConsumerDefinition"/>
/// exactly. Same <c>EndpointName "orchestrator"</c> so <c>ConfigureEndpoints</c> groups both consumers
/// onto one per-replica fan-out endpoint (A2). <b>24.1 / D-24.1-05:</b> boot gate + scheduled
/// redelivery (+ the <c>rabbitmq_delayed_message_exchange</c> plugin dependency) REMOVED;
/// <c>UseMessageRetry</c> bounds infra faults → <c>_error</c>.
/// </summary>
public sealed class StopOrchestrationConsumerDefinition : ConsumerDefinition<StopOrchestrationConsumer>
{
    private readonly IOptions<RetryOptions> _retryOptions;

    public StopOrchestrationConsumerDefinition(IOptions<RetryOptions> retryOptions)
    {
        _retryOptions = retryOptions;
        EndpointName = "orchestrator";   // SHARED base name (both defs)
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<StopOrchestrationConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        // Bounded immediate retry of infra faults → _error. No scheduled redelivery (gate removed,
        // 24.1 / D-24.1-05) → no plugin dependency. D-10: the retry Limit is bound per process from the
        // "Retry" config section (default Immediate(3)).
        endpointConfigurator.UseMessageRetry(r =>
        {
            r.Immediate(_retryOptions.Value.Limit);
            r.Ignore<WorkflowRootNotFoundException>();   // business failure never retries (D-07/D-08, MSG-ACK-03)
        });
    }
}
