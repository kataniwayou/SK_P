using MassTransit;
using Messaging.Contracts;

namespace Keeper.Recovery;

/// <summary>D-02 — co-locates <see cref="CleanupConsumer"/> on the shared <see cref="KeeperQueues.Recovery"/>
/// endpoint. Endpoint-level retry + partitioner are owned SOLELY by <see cref="UpdateConsumerDefinition"/> —
/// this sibling's <c>ConfigureConsumer</c> is an INTENTIONAL no-op (Pitfalls 1 &amp; 4, precedent
/// <c>FaultEntryStepDispatchConsumerDefinition</c>).</summary>
public sealed class CleanupConsumerDefinition : ConsumerDefinition<CleanupConsumer>
{
    public CleanupConsumerDefinition()
    {
        EndpointName = KeeperQueues.Recovery;   // "keeper-recovery" — shared endpoint
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<CleanupConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        // Intentional no-op — endpoint-level retry + partitioner owned solely by UpdateConsumerDefinition.
    }
}
