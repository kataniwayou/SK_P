namespace Keeper.Recovery;

/// <summary>D-04: the DELIBERATE data-gone terminal — thrown inside a REINJECT/INJECT read closure when
/// the L2 key is absent/empty (StackExchange.Redis returns RedisValue.Null for a missing key, no throw).
/// Surfacing it as a thrown exception forces the same dead-letter route (→ skp-dlq-1 via the inherited
/// ConsolidatedErrorTransportFilter) as a natural Redis fault, rather than silently acking. Distinct from
/// the TRANSIENT <see cref="RecoveryGateTimeoutException"/> (which the endpoint UseMessageRetry
/// re-attempts). Analogous to BaseProcessor.Core's KeyAbsentException, but Keeper-local (Keeper does not
/// reference BaseProcessor.Core).</summary>
public sealed class RecoveryDataGoneException : Exception
{
    public RecoveryDataGoneException() : base("Recovery input data is gone (L2 read absent/empty)") { }
}
