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
}
