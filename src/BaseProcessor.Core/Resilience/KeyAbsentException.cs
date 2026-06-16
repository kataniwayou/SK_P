namespace BaseProcessor.Core.Resilience;

/// <summary>A2: thrown by the Pre-read closure when L2[entryId] returns an absent/empty value, so an
/// absent/empty key unifies with a Redis exception into the single RetryLoop exhaustion path → infra(READ)
/// → ProcessorReinject. StackExchange.Redis returns RedisValue.Null for a missing key (no throw), so this
/// sentinel converts that into a retryable failure.</summary>
internal sealed class KeyAbsentException() : Exception("L2 key absent or empty.");
