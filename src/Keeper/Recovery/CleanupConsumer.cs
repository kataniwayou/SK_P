using Keeper.Health;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Keeper.Recovery;

/// <summary>KEEP-08: the Keeper CLEANUP state — deletes the redundant L2 composite-backup copy on the
/// happy path (net-zero composite invariant: after any non-crash path the backup is gone). Runs only
/// after the gate opens (base D-03); the delete goes through the RetryLoop Guard and re-throws on
/// exhaustion → skp-dlq-1 (D-04).</summary>
public sealed class CleanupConsumer(
    IConnectionMultiplexer redis, ISendEndpointProvider sendProvider, IL2HealthGate gate,
    IOptions<RetryOptions> retryOptions, IOptions<RecoveryOptions> recoveryOptions,
    IOptions<BackupOptions> backupOptions)
    : RecoveryConsumerBase<KeeperCleanup>(redis, sendProvider, gate, retryOptions, recoveryOptions, backupOptions)
{
    protected override async Task HandleAsync(KeeperCleanup m, CancellationToken ct)
        => await Guard(() => Db.KeyDeleteAsync(
            L2ProjectionKeys.CompositeBackup(m.CorrelationId, m.WorkflowId, m.ProcessorId, m.ExecutionId)), ct);
}
