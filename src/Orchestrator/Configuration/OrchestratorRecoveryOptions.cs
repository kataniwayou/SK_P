namespace Orchestrator.Configuration;

/// <summary>
/// ORCV-02/03 (Phase 71): the orchestrator-side data-TTL knob for the result-recovery pipeline — the
/// orchestrator's equivalent of the processor's <c>ProcessorLivenessOptions.ExecutionDataTtlSeconds</c>.
/// Bound per process from the "Recovery" config section via <c>IOptions</c>.
/// <para>
/// <c>OrchestratorResultPipeline</c>'s single atomic FORWARD write uses this as the SINGLE source
/// of truth for BOTH the copied data key's PX and the index whole-hash PEXPIRE (the index TTL is derived
/// as <c>random[ttl, 2×ttl]</c> so it strictly outlives the data it points at — mirroring the processor's
/// <c>SlotTtl()</c> and preserving the Phase-68 TEST-06 index/data anti-desync guard: one knob → no desync).
/// TTLs are computed in C# and passed to Lua as ARGV — NO RNG inside the script.
/// </para>
/// </summary>
public sealed class OrchestratorRecoveryOptions
{
    /// <summary>The bounded TTL (seconds) applied to the copied L2 execution-data key on every FORWARD
    /// write, and the floor for the derived index TTL. Floored at 1s by the pipeline (a non-positive value
    /// would marshal to PX/PEXPIRE 0 — a Redis server error). Default 300s.</summary>
    public int ExecutionDataTtlSeconds { get; set; } = 300;
}
