using Messaging.Contracts.Projections;

namespace BaseProcessor.Core.Liveness;

/// <summary>
/// D-08/L1-01: dedicated singleton L1 liveness record, updated by BOTH the startup orchestrator
/// (unhealthy writer) and the heartbeat (healthy writer), read by the Phase-61 self-watchdog probe.
/// Stores the SAME immutable <see cref="ProcessorLivenessEntry"/> written to L2 that iteration (D-09)
/// so L1 and L2 cannot desync. Publication is a volatile reference swap (D-10) — readable DURING
/// startup, a different discipline than <c>IProcessorContext</c>'s read-after-Healthy (WR-03), which
/// is why it is NOT on <c>IProcessorContext</c>.
/// </summary>
public interface IProcessorLivenessState
{
    /// <summary>Swap the current immutable record (called by both loops every iteration).</summary>
    void Update(ProcessorLivenessEntry entry);

    /// <summary>Snapshot read for the Phase-61 probe; null until the first <see cref="Update"/>.</summary>
    ProcessorLivenessEntry? Current { get; }
}
