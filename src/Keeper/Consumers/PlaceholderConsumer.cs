using MassTransit;
using Microsoft.Extensions.Logging;

namespace Keeper.Consumers;

/// <summary>
/// Throwaway no-op consumer (D-03) — topology proof only. Bound to the stable shared
/// <see cref="Messaging.Contracts.KeeperQueues.FaultRecovery"/> queue via
/// <see cref="PlaceholderConsumerDefinition"/>. The body is intentionally a single log line
/// (no Redis/L1/dispatch logic) — it exists solely to make the durable competing-consumer
/// queue real so KEEP-02 round-robin is live-verifiable. Replaced in Phase 35.
/// </summary>
public sealed class PlaceholderConsumer(ILogger<PlaceholderConsumer> logger)
    : IConsumer<KeeperPlaceholder>
{
    public Task Consume(ConsumeContext<KeeperPlaceholder> context)
    {
        logger.LogInformation("Keeper placeholder consumed (topology proof only)");
        return Task.CompletedTask;
    }
}
