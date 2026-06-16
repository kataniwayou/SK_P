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

    /// <summary>WR-02 (Phase 71): the TTL (seconds) applied to the copied L2 execution-data key when the
    /// <see cref="Recovery.OrchestratorInjectConsumer"/> completes the FORWARD-pass COPY the atomic Lua write
    /// could not finish. Mirrors the orchestrator pipeline's <c>OrchestratorRecoveryOptions.ExecutionDataTtlSeconds</c>
    /// (same "Recovery" config section, same 300s default) so the INJECT-escalation key gets the SAME bounded
    /// lifetime as the normal FORWARD path's <c>SET ... PX ExecutionDataTtl</c> — never written immortal
    /// (no immortal-key leak on a lost INJECT). The TTL is computed in C# here (NOT in Lua — the D-03 invariant,
    /// floored at 1s since a non-positive value would marshal to PX 0, a Redis server error). The Keeper owns
    /// this knob in its own namespace rather than referencing the Orchestrator config type — the T-34-01
    /// reference firewall forbids a Keeper → Orchestrator project coupling.</summary>
    public int ExecutionDataTtlSeconds { get; set; } = 300;
}
