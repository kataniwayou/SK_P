using MassTransit;
using Messaging.Contracts;

namespace Keeper.Recovery;

/// <summary>D-02 — co-locates <see cref="DeleteConsumer"/> on the shared <see cref="KeeperQueues.Recovery"/>
/// endpoint. Endpoint-level retry + partitioner are owned SOLELY by <see cref="ReinjectConsumerDefinition"/> —
/// this sibling's <c>ConfigureConsumer</c> is an INTENTIONAL no-op (Pitfalls 1 &amp; 4, precedent
/// <c>FaultEntryStepDispatchConsumerDefinition</c>).</summary>
public sealed class DeleteConsumerDefinition : ConsumerDefinition<DeleteConsumer>
{
    public DeleteConsumerDefinition()
    {
        EndpointName = KeeperQueues.Recovery;   // "keeper-recovery" — shared endpoint
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<DeleteConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        // Intentional no-op — endpoint-level retry + partitioner owned solely by ReinjectConsumerDefinition.
    }
}
