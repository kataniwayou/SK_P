using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Keeper.Recovery;

/// <summary>KEEP-03: the Keeper DELETE state — deletes the L2 execution-data key (GC only).
/// <c>KeyDeleteAsync</c> no-ops on a missing key, so DELETE drops-on-absent (KEEP-03, A18 line 217). The
/// delete goes through the RetryLoop Guard and re-throws on exhaustion → skp-dlq-1 (D-04); gating happens
/// at the endpoint (D-04).</summary>
public sealed class DeleteConsumer(
    IConnectionMultiplexer redis, ISendEndpointProvider sendProvider,
    IOptions<RetryOptions> retryOptions, IOptions<RecoveryOptions> recoveryOptions)
    : RecoveryConsumerBase<KeeperDelete>(redis, sendProvider, retryOptions, recoveryOptions)
{
    protected override async Task HandleAsync(KeeperDelete m, CancellationToken ct)
        => await Guard(() => Db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(m.EntryId)), ct);
}
