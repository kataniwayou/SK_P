namespace Messaging.Contracts.Configuration;

/// <summary>D-10: the retry budget knob, bound per process from the "Retry" config section via IOptions.
/// Single source of truth for the retry Limit so Phase 32's final-attempt check (GetRetryAttempt()==Limit)
/// cannot desync from UseMessageRetry. Only the Immediate branch is implemented this phase (back-off is
/// structured-for, deferred per SPEC out-of-scope-as-default).</summary>
public sealed class RetryOptions
{
    public int Limit { get; set; } = 3;                                  // default Immediate(3)
    public RetryStrategy Strategy { get; set; } = RetryStrategy.Immediate;
}

public enum RetryStrategy
{
    Immediate = 0,   // the only implemented branch this phase
    Interval  = 1,   // structured-for, NOT wired (Phase 31 out-of-scope-as-default)
    Exponential = 2, // structured-for, NOT wired
}
