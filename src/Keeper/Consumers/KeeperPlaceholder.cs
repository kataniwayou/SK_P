using Messaging.Contracts;   // ICorrelated

namespace Keeper.Consumers;

/// <summary>
/// Throwaway placeholder message LOCAL to Keeper (D-03) — exists only to materialize the stable
/// shared queue so KEEP-02 round-robin is live-verifiable in Phase 34. Replaced wholesale in
/// Phase 35 by the real Fault&lt;EntryStepDispatch&gt; / Fault&lt;ExecutionResult&gt; intake.
/// <para>
/// Implements <see cref="ICorrelated"/> so the bus-wide <c>InboundCorrelationConsumeFilter</c> reads
/// the correlation id from the body cleanly (v3.4.0 body-correlation model, D-01).
/// </para>
/// </summary>
public sealed record KeeperPlaceholder : ICorrelated
{
    /// <summary>Body-carried correlation id (placed under the fixed scope key by the inbound filter).</summary>
    public Guid CorrelationId { get; init; }
}
