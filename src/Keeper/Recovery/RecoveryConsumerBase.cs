using BaseConsole.Core.Resilience;
using Keeper.Health;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Keeper.Recovery;

/// <summary>D-02/D-03: shared base for the five Keeper recovery-state consumers. Owns the two
/// cross-cutting concerns so each subclass body is just its distinct state op:
/// <list type="number">
///   <item>D-03 gate-wait — awaits <see cref="IL2HealthGate.WaitForOpenAsync"/> ONCE at Consume entry
///   (before any L2 op) under a linked CTS bounded by <c>RecoveryOptions.GateWaitSeconds</c>. On bound
///   exhaustion it throws the TRANSIENT <see cref="RecoveryGateTimeoutException"/> so the endpoint
///   UseMessageRetry re-attempts (MassTransit v8 dead-letters thrown deliveries to skp-dlq-1; it does
///   NOT broker-requeue — mirror the L2ProbeRecovery await-inside-Consume precedent).</item>
///   <item>D-04 RetryLoop — the <see cref="Guard{T}"/>/<see cref="Guard"/> helpers wrap each L2 op + Send
///   in the relocated <see cref="RetryLoop"/> and re-throw <c>.Error</c> on exhaustion, so a give-up
///   dead-letters to skp-dlq-1 via the inherited ConsolidatedErrorTransportFilter (no per-consumer
///   ConfigureError).</item>
/// </list>
/// <see cref="IConnectionMultiplexer"/> is the SAME DI singleton AddBaseConsole already registers
/// (mirrors <c>L2ProbeRecovery</c>) — do NOT register a new one.</summary>
public abstract class RecoveryConsumerBase<TMessage>(
    IConnectionMultiplexer redis,
    ISendEndpointProvider sendProvider,
    IL2HealthGate gate,
    IOptions<RetryOptions> retryOptions,
    IOptions<RecoveryOptions> recoveryOptions,
    IOptions<BackupOptions> backupOptions) : IConsumer<TMessage>
    where TMessage : class, IKeeperRecoverable
{
    protected IDatabase Db => redis.GetDatabase();
    protected ISendEndpointProvider Send => sendProvider;
    protected int RetryLimit => retryOptions.Value.Limit;
    protected int TtlDays => backupOptions.Value.TtlDays;

    public async Task Consume(ConsumeContext<TMessage> context)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        // WR-02: GateWaitSeconds parks this Consume (holding its broker channel) for up to that bound — it
        // MUST stay below the deployed RabbitMQ consumer_timeout (broker default 30 min) or the broker
        // force-closes the parked channel. The two configs live in different systems and cannot be validated
        // together at build time; see RecoveryOptions.GateWaitSeconds for the operator note.
        cts.CancelAfter(TimeSpan.FromSeconds(recoveryOptions.Value.GateWaitSeconds));
        try
        {
            await gate.WaitForOpenAsync(cts.Token);   // D-03: await ONCE at entry, before any L2 op
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested
            && !context.CancellationToken.IsCancellationRequested)
        {
            // Gate still closed at the GateWaitSeconds bound → TRANSIENT marker the endpoint retry re-attempts.
            throw new RecoveryGateTimeoutException();
        }

        await HandleAsync(context.Message, context.CancellationToken);   // dispatch to the per-state body
    }

    /// <summary>The per-state body (UPDATE/REINJECT/INJECT/DELETE/CLEANUP). Runs only after the gate is
    /// open; every L2 op + Send inside it should go through <see cref="Guard"/>/<see cref="Guard{T}"/>.</summary>
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

/// <summary>D-03: the TRANSIENT gate-wait-timeout marker — thrown when the bounded once-at-entry
/// <see cref="IL2HealthGate.WaitForOpenAsync"/> is still closed at <c>RecoveryOptions.GateWaitSeconds</c>.
/// The endpoint UseMessageRetry re-attempts it (the gate is expected to open soon); only after retry
/// exhaustion does it dead-letter to skp-dlq-1. DISTINCT from the TERMINAL
/// <see cref="RecoveryDataGoneException"/>.</summary>
public sealed class RecoveryGateTimeoutException : Exception
{
    public RecoveryGateTimeoutException() : base("Recovery gate did not open within the bounded wait window") { }
}
