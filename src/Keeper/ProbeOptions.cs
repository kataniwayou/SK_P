namespace Keeper;

/// <summary>
/// PROBE-01 (D-04): bounded L2 probe-loop knobs. CONSTRAINT (LOAD-BEARING, D-04): DelaySeconds x MaxAttempts
/// MUST stay well under RabbitMQ's default 30-min consumer_timeout — the loop is awaited INSIDE Consume,
/// holding the broker delivery un-acked for that window. Defaults 5s x 12 = 60s (30x margin).
/// </summary>
public sealed class ProbeOptions
{
    public int DelaySeconds { get; set; } = 5;
    public int MaxAttempts  { get; set; } = 12;

    // KHARD-01 (D-A2): OUTER recover→reinject cap per H — distinct from MaxAttempts (the INNER probe-loop count). At cap, park the original Fault<T> once.
    public int RecoverAttemptCap { get; set; } = 3;
}
