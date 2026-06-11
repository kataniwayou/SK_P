using BaseConsole.Core.Resilience;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Keeper.Recovery;

/// <summary>D-02: shared base for the three Keeper recovery-state consumers (REINJECT/INJECT/DELETE).
/// Owns the one surviving cross-cutting concern so each subclass body is just its distinct state op:
/// <list type="number">
///   <item>D-04 RetryLoop — the <see cref="Guard{T}"/>/<see cref="Guard"/> helpers wrap each L2 op + Send
///   in the relocated <see cref="RetryLoop"/> and re-throw <c>.Error</c> on exhaustion, so a give-up
///   dead-letters to skp-dlq-1 via the inherited ConsolidatedErrorTransportFilter (no per-consumer
///   ConfigureError).</item>
/// </list>
/// Phase 52 (D-04/D-09): the bounded once-at-entry gate-wait was REMOVED — gating now happens at the
/// keeper-recovery ENDPOINT (pause/resume driven by the BIT loop), so <c>Consume</c> dispatches straight
/// to <see cref="HandleAsync"/>. <see cref="IConnectionMultiplexer"/> is the SAME DI singleton AddBaseConsole
/// already registers (mirrors <c>L2ProbeRecovery</c>) — do NOT register a new one.</summary>
public abstract class RecoveryConsumerBase<TMessage>(
    IConnectionMultiplexer redis,
    ISendEndpointProvider sendProvider,
    IOptions<RetryOptions> retryOptions,
    IOptions<RecoveryOptions> recoveryOptions) : IConsumer<TMessage>
    where TMessage : class, IKeeperRecoverable
{
    protected IDatabase Db => redis.GetDatabase();
    protected ISendEndpointProvider Send => sendProvider;
    protected int RetryLimit => retryOptions.Value.Limit;

    /// <summary>The keeper-wide exhaustion posture (D-01). Read by the endpoint policy wiring (Plan 02);
    /// exposed here so the base ctor's <c>recoveryOptions</c> is observed and subclasses can branch on it.</summary>
    protected ExhaustionPolicy ExhaustionPolicy => recoveryOptions.Value.ExhaustionPolicy;

    public Task Consume(ConsumeContext<TMessage> context)
        => HandleAsync(context.Message, context.CancellationToken);   // gate now enforced at the ENDPOINT (D-04)

    /// <summary>The per-state body (REINJECT/INJECT/DELETE). Every L2 op + Send inside it should go
    /// through <see cref="Guard"/>/<see cref="Guard{T}"/>.</summary>
    protected abstract Task HandleAsync(TMessage m, CancellationToken ct);

    /// <summary>D-04: run <paramref name="op"/> through the bounded <see cref="RetryLoop"/> and re-throw the
    /// surfaced exhaustion error so the give-up dead-letters to skp-dlq-1.</summary>
    protected async Task<T> Guard<T>(Func<Task<T>> op, CancellationToken ct)
    {
        var outcome = await RetryLoop.ExecuteAsync(op, RetryLimit, ct);
        if (!outcome.Succeeded) throw outcome.Error!;
        return outcome.Value!;
    }

    /// <summary>Void-op overload of <see cref="Guard{T}"/> for Sends and deletes whose result is ignored.</summary>
    protected Task Guard(Func<Task> op, CancellationToken ct)
        => Guard(async () => { await op(); return true; }, ct);
}
