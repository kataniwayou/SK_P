namespace Messaging.Contracts;

/// <summary>
/// Single source of truth for the stable shared Keeper competing-consumer queue name.
/// Mirrors the <see cref="OrchestratorQueues"/> static-class precedent (D-08 discretion).
/// </summary>
public static class KeeperQueues
{
    /// <summary>D-13: gate-open-only recovery consumer queue — Phase 46 binds the five Keeper-state
    /// consumers here. The sole surviving Keeper queue after the v3.x reactive-path teardown (RETIRE-03,
    /// Phase 48); the retired reactive fault-recovery + DLQ-2 consts were removed in that teardown.</summary>
    public const string Recovery = "keeper-recovery";
}
