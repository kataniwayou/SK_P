using MassTransit;
using Messaging.Contracts;

namespace Keeper.Consumers;

/// <summary>
/// Endpoint config seam for <see cref="FaultExecutionResultConsumer"/>. Colocates on the SAME stable
/// shared DURABLE queue <see cref="KeeperQueues.FaultRecovery"/> ("keeper-fault-recovery", D-03) as
/// <see cref="FaultEntryStepDispatchConsumer"/> — the two real fault consumers share one endpoint.
/// <para>
/// <b>ConfigureConsumer is an intentional no-op (RESEARCH Pitfall 3).</b> The MassTransit retry
/// middleware is PER-ENDPOINT, not per-consumer; since both fault consumers bind the ONE shared endpoint
/// <c>keeper-fault-recovery</c>, the endpoint-level retry is owned solely by
/// <see cref="FaultEntryStepDispatchConsumerDefinition"/>. Registering the retry middleware here too would
/// double-register the retry filter on the same endpoint. No <c>IOptions&lt;RetryOptions&gt;</c> ctor
/// dependency is taken because this definition never reads the retry budget.
/// </para>
/// </summary>
public sealed class FaultExecutionResultConsumerDefinition : ConsumerDefinition<FaultExecutionResultConsumer>
{
    public FaultExecutionResultConsumerDefinition()
    {
        EndpointName = KeeperQueues.FaultRecovery;   // "keeper-fault-recovery" — SAME shared endpoint
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<FaultExecutionResultConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        // Intentionally empty: the endpoint-level retry for keeper-fault-recovery is owned by
        // FaultEntryStepDispatchConsumerDefinition (single endpoint-retry owner — Pitfall 3). Adding
        // a retry middleware registration here would double-register the retry filter on the one shared endpoint.
    }
}
