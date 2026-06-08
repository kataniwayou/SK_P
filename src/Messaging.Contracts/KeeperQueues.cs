namespace Messaging.Contracts;

/// <summary>
/// Single source of truth for the stable shared Keeper competing-consumer queue name.
/// Mirrors the <see cref="OrchestratorQueues"/> static-class precedent (D-08 discretion).
/// </summary>
public static class KeeperQueues
{
    /// <summary>
    /// Stable shared competing-consumer queue for Keeper fault-recovery work. DURABLE
    /// (NOT InstanceId/Temporary) so it survives in BOTH close-gate rabbitmq snapshots →
    /// net-zero triple-SHA (Phase 38). Phase 35's real Fault&lt;T&gt; consumers reuse this
    /// SAME endpoint name, so the const survives the placeholder swap.
    /// </summary>
    public const string FaultRecovery = "keeper-fault-recovery";

    /// <summary>D-13: gate-open-only recovery consumer queue — Phase 46 binds the four Keeper-state
    /// consumers here. Distinct from the reactive <see cref="FaultRecovery"/> queue (retired 47/48).</summary>
    public const string Recovery = "keeper-recovery";

    /// <summary>
    /// DLQ-2 (D-08): terminal probe give-up queue. Plain durable, NO x-message-ttl — its depth is the
    /// PRIMARY operator alert (Phase 39), so it MUST persist until an operator drains it (contrast DLQ-1's TTL).
    /// </summary>
    public const string DeadLetter = "keeper-dlq";
}
