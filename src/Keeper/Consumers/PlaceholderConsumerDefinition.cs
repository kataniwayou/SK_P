using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.Options;

namespace Keeper.Consumers;

/// <summary>
/// Endpoint/retry config seam for <see cref="PlaceholderConsumer"/>. Binds the STABLE shared
/// DURABLE competing-consumer queue <see cref="KeeperQueues.FaultRecovery"/> ("keeper-fault-recovery")
/// — NOT a per-replica auto-delete fan-out endpoint (D-02). Plain AddConsumer in Program.cs +
/// this stable EndpointName = ONE durable queue round-robined across replicas, present in BOTH
/// close-gate rabbitmq snapshots (net-zero triple-SHA, Pitfall 1).
/// </summary>
public sealed class PlaceholderConsumerDefinition : ConsumerDefinition<PlaceholderConsumer>
{
    private readonly IOptions<RetryOptions> _retryOptions;

    public PlaceholderConsumerDefinition(IOptions<RetryOptions> retryOptions)
    {
        _retryOptions = retryOptions;
        EndpointName = KeeperQueues.FaultRecovery;   // "keeper-fault-recovery" — stable, shared, durable
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<PlaceholderConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        // Bounded immediate retry of genuine infra faults → _error (DLQ-04 / D-09). The Limit is bound
        // per process from the "Retry" config section (default Immediate(3)) — the single source of truth.
        // NOTE: Immediate is intentionally the ONLY supported strategy at this milestone — RetryOptions.Strategy
        // (Interval/Exponential) is structured-for but NOT wired (shared deferral across ALL consoles, see
        // RetryOptions doc-comment). The config key binds but is deliberately not branched on here; wiring it
        // must be done UNIFORMLY across every console, not piecemeal in Keeper. Do not add Strategy-switching
        // here without the same change in Orchestrator/Processor — divergence is worse than the documented gap.
        endpointConfigurator.UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit));
    }
}
