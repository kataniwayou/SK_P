using Keeper.Health;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Keeper.Recovery;

/// <summary>KEEP-07: the Keeper DELETE state — deletes the L2 execution-data key (GC only). Runs only
/// after the gate opens (base D-03); the delete goes through the RetryLoop Guard and re-throws on
/// exhaustion → skp-dlq-1 (D-04).</summary>
public sealed class DeleteConsumer(
    IConnectionMultiplexer redis, ISendEndpointProvider sendProvider, IL2HealthGate gate,
    IOptions<RetryOptions> retryOptions, IOptions<RecoveryOptions> recoveryOptions,
    IOptions<BackupOptions> backupOptions)
    : RecoveryConsumerBase<KeeperDelete>(redis, sendProvider, gate, retryOptions, recoveryOptions, backupOptions)
{
    protected override async Task HandleAsync(KeeperDelete m, CancellationToken ct)
        => await Guard(() => Db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(m.EntryId)), ct);
}
