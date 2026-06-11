using Keeper.Health;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Keeper.Recovery;

/// <summary>KEEP-06: the Keeper INJECT state. Phase-50 (D-01): the Model-B composite-backup
/// read→write→send→delete body is RETIRED with the composite-backup key. This is a compile-only,
/// shape-preserving no-op stub (NOT a throw — Pitfall 5, so the hermetic gate-open path stays green);
/// it runs only after the gate opens (base D-03). The real A18 forward-only INJECT body — write
/// <c>L2[m.EntryId]=m.Data</c>, send a reconstructed <see cref="StepCompleted"/>, delete
/// <c>m.DeleteEntryId</c> off the in-hand envelope (no composite read) — lands in Phase 52.</summary>
public sealed class InjectConsumer(
    IConnectionMultiplexer redis, ISendEndpointProvider sendProvider, IL2HealthGate gate,
    IOptions<RetryOptions> retryOptions, IOptions<RecoveryOptions> recoveryOptions)
    : RecoveryConsumerBase<KeeperInject>(redis, sendProvider, gate, retryOptions, recoveryOptions)
{
    protected override Task HandleAsync(KeeperInject m, CancellationToken ct)
        => Task.CompletedTask;   // Phase-50 (D-01) compile-only no-op stub; real A18 INJECT body is Phase 52
}
