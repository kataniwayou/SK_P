namespace Keeper.Health;

/// <summary>
/// Lock-free timestamp holder for the minimal keeper self-watchdog. <c>public sealed</c> so
/// <c>services.AddSingleton&lt;IKeeperLivenessState, KeeperLivenessState&gt;()</c> resolves across the
/// assembly boundary without <c>InternalsVisibleTo</c> (same rationale as <c>ProcessorLivenessState</c>).
/// <para>
/// A <c>DateTime?</c> cannot be <c>volatile</c>, so instead of a volatile reference swap this stores the
/// raw tick count in a <c>long</c> and uses <see cref="Interlocked"/> for atomic, lock-free read/write —
/// 0-warning and safe across the BIT-loop writer thread and the health-probe reader thread.
/// </para>
/// </summary>
public sealed class KeeperLivenessState : IKeeperLivenessState
{
    // 0 = "never stamped" sentinel. Update is only ever called with a 2024+ UTC instant (DateTime.Ticks
    // far from 0), so 0 is unambiguously "no tick yet" — there is no real tick that produces 0 ticks.
    private long _ticks;

    public void Update(DateTime utcNow) => Interlocked.Exchange(ref _ticks, utcNow.Ticks);

    public DateTime? Current
    {
        get
        {
            var t = Interlocked.Read(ref _ticks);
            return t == 0 ? null : new DateTime(t, DateTimeKind.Utc);
        }
    }
}
