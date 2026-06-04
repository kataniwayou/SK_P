using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.Options;

namespace Orchestrator.Consumers;

/// <summary>
/// Endpoint/retry seam for <see cref="FaultUnscheduleConsumer"/> (Phase 32 / D-06). SAME
/// <c>EndpointName "orchestrator"</c> as Start/Stop so <c>ConfigureEndpoints</c> groups it onto the
/// per-replica fan-out endpoint <c>orchestrator-{instanceId}</c> (Pitfall 5: NOT the shared
/// <c>orchestrator-result</c> competing-consumer queue — every replica, including the schedule owner,
/// must receive the fault). <c>UseMessageRetry</c> bounds the unschedule's own Send/Quartz infra faults
/// from the SAME <see cref="RetryOptions.Limit"/> source as Start/Stop (D-01 — no desync).
/// <para>
/// OMITS <c>r.Ignore&lt;WorkflowRootNotFoundException&gt;()</c> — unlike Stop, the fault path's
/// <see cref="Orchestrator.Hydration.WorkflowLifecycle.UnscheduleOnlyAsync"/> returns a no-op on
/// absent-L1 and never throws that exception.
/// </para>
/// </summary>
public sealed class FaultUnscheduleConsumerDefinition : ConsumerDefinition<FaultUnscheduleConsumer>
{
    private readonly IOptions<RetryOptions> _retryOptions;

    public FaultUnscheduleConsumerDefinition(IOptions<RetryOptions> retryOptions)
    {
        _retryOptions = retryOptions;
        EndpointName = "orchestrator";   // SHARED per-replica fan-out base name (Pitfall 5 — load-bearing)
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<FaultUnscheduleConsumer> consumerConfigurator,
        IRegistrationContext context)
        => endpointConfigurator.UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit));
}
