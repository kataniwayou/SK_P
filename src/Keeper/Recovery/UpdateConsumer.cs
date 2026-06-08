using Keeper.Health;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Keeper.Recovery;

/// <summary>KEEP-04: the Keeper UPDATE state — writes the validated data to the L2 composite-backup key
/// WITH the <see cref="BackupOptions.TtlDays"/> TTL (crash-backstop only; the backup is normally deleted
/// by CLEANUP/INJECT). TTL is applied ONLY here at the call site — never baked into the key builder
/// (Pattern C). Runs only after the gate opens (base D-03); the write goes through the RetryLoop Guard
/// and re-throws on exhaustion → skp-dlq-1 (D-04).</summary>
public sealed class UpdateConsumer(
    IConnectionMultiplexer redis, ISendEndpointProvider sendProvider, IL2HealthGate gate,
    IOptions<RetryOptions> retryOptions, IOptions<RecoveryOptions> recoveryOptions,
    IOptions<BackupOptions> backupOptions)
    : RecoveryConsumerBase<KeeperUpdate>(redis, sendProvider, gate, retryOptions, recoveryOptions, backupOptions)
{
    protected override async Task HandleAsync(KeeperUpdate m, CancellationToken ct)
    {
        var key = L2ProjectionKeys.CompositeBackup(m.CorrelationId, m.WorkflowId, m.ProcessorId, m.ExecutionId);
        await Guard(() => Db.StringSetAsync(key, m.ValidatedData, expiry: TimeSpan.FromDays(TtlDays)), ct);
    }
}
