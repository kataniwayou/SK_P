namespace Keeper.Health;

/// <summary>
/// MINIMAL keeper self-watchdog L1 record — deliberately timestamp-ONLY. Holds the UTC instant of the
/// BitHealthLoop's most recent tick so a silently-stalled loop (a wedged ProbeOnceAsync or a stuck trailing
/// Task.Delay) becomes observable on /health/live.
/// <para>
/// Intentionally NOT <c>ProcessorLivenessEntry</c> / <c>IProcessorLivenessState</c>: there is no status, no
/// per-schema summary, no Data payload — just a last-tick timestamp. The keeper has a single BIT loop; a
/// fresh timestamp proves it is still ticking, a stale/absent one proves it is not.
/// </para>
/// </summary>
public interface IKeeperLivenessState
{
    /// <summary>Stamp the most recent BIT-loop tick. Called unconditionally every tick (UTC).</summary>
    void Update(DateTime utcNow);

    /// <summary>The last stamped tick (UTC), or <c>null</c> before the loop's first tick.</summary>
    DateTime? Current { get; }
}
