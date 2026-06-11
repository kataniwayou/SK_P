namespace Keeper;

/// <summary>D-01/D-06: recovery-consumer knobs, bound from the "Recovery" appsettings section
/// (mirrors <see cref="ProbeOptions"/>). <see cref="PartitionCount"/> is the MassTransit UsePartitioner
/// slot count (D-06, default 8). <see cref="ExhaustionPolicy"/> (D-01) selects the keeper-wide
/// give-up posture for op/send failures that occur while the gate is OPEN: <c>Dlq1</c> (default, D-02 —
/// matches the v4 A4 single-DLQ posture) re-throws on exhaustion so the give-up dead-letters to
/// skp-dlq-1; <c>SustainedOutage</c> holds/requeues with no dead-letter (D-03, accepted poison-op spin).
/// Phase 52 (D-09) REMOVED the obsolete <c>GateWaitSeconds</c> — gating now happens at the endpoint
/// (pause/resume), not via a bounded in-Consume await.</summary>
public sealed class RecoveryOptions
{
    public int PartitionCount { get; set; } = 8;    // D-06 default

    /// <summary>D-01/D-02: the keeper-wide exhaustion posture, default <see cref="ExhaustionPolicy.Dlq1"/>.</summary>
    public ExhaustionPolicy ExhaustionPolicy { get; set; } = ExhaustionPolicy.Dlq1;
}

/// <summary>D-01: keeper-wide give-up policy for gate-open op/send exhaustion.
/// <list type="bullet">
///   <item><see cref="Dlq1"/> — re-throw on exhaustion → dead-letter to the consolidated skp-dlq-1 (D-02 default).</item>
///   <item><see cref="SustainedOutage"/> — hold/requeue, no dead-letter, accepted poison-op spin (D-03).</item>
/// </list></summary>
public enum ExhaustionPolicy { Dlq1, SustainedOutage }
