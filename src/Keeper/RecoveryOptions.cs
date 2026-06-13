namespace Keeper;

/// <summary>D-06: recovery-consumer knobs, bound from the "Recovery" appsettings section
/// (mirrors <see cref="ProbeOptions"/>). <see cref="PartitionCount"/> is the MassTransit UsePartitioner
/// slot count (D-06, default 8) — the only surviving knob now that the keeper-recovery endpoint is
/// symmetric with the exec path (no bus retry / no error transport, so no exhaustion-policy choice).
/// Phase 52 (D-09) REMOVED the obsolete <c>GateWaitSeconds</c> — gating now happens at the endpoint
/// (pause/resume), not via a bounded in-Consume await.</summary>
public sealed class RecoveryOptions
{
    public int PartitionCount { get; set; } = 8;    // D-06 default
}
