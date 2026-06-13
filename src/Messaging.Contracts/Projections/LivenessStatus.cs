namespace Messaging.Contracts.Projections;

/// <summary>
/// Single source of truth for the Path-1 liveness status value (CONTRACT-03 / D-03).
/// The processor (writer, Phase 26) and any reader consume this ONE const so they cannot desync.
/// Mirrors the L2ProjectionKeys / OrchestratorQueues static-const SoT shape. Consumed by
/// <see cref="LivenessProjection"/>'s status field.
/// <see cref="Unhealthy"/> was added for the two-state per-instance processor-liveness contract (Phase 59, STATE-01).
/// </summary>
public static class LivenessStatus
{
    public const string Healthy = "Healthy";       // existing — consumed by heartbeat + validator
    public const string Unhealthy = "Unhealthy";   // NEW (STATE-01, additive — two-state status)
}
