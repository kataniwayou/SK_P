using NSubstitute;
using StackExchange.Redis;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Wave-0 shared Keeper test double (consumed by Plan 02's <c>KeeperProbeLoopTests</c>): a stateful
/// <see cref="IConnectionMultiplexer"/>/<see cref="IDatabase"/> pair whose Redis health can be flipped
/// <see cref="RedisHealth.Down"/> → <see cref="RedisHealth.Up"/> (and the half-open
/// <see cref="RedisHealth.HalfOpen"/>) on demand, so a test can simulate an L2 outage that recovers
/// mid-probe-loop.
/// <para>
/// Failure model (PROBE-01/02): while <see cref="RedisHealth.Down"/>, every probe op
/// (<c>StringGetAsync</c>/<c>StringSetAsync</c>/<c>KeyDeleteAsync</c>) throws a
/// <see cref="RedisConnectionException"/> (a <see cref="RedisException"/>-derived type — the superset
/// the probe loop's <c>catch (RedisException)</c> must catch, RESEARCH A2). While
/// <see cref="RedisHealth.HalfOpen"/>, the READ (<c>StringGetAsync</c>) succeeds but the WRITE
/// (<c>StringSetAsync</c>) / delete throws — proving PROBE-02 (a read-only-healthy L2 still counts as a
/// fault, the probe REQUIRES read AND write). While <see cref="RedisHealth.Up"/>, the read returns
/// <see cref="RedisValue.Null"/> and the write/delete return <c>true</c>.
/// </para>
/// <para>
/// <see cref="SetFailuresBeforeUp"/> arms a counter: the first <paramref name="count"/> probe ops throw
/// (as if Down), then health auto-flips to <see cref="RedisHealth.Up"/> — the canonical "fail N times,
/// then recover" shape for the bounded retry loop.
/// </para>
/// Backed by NSubstitute (the repo's established mocking library — no new package). The
/// <see cref="IConnectionMultiplexer"/> surface beyond <c>GetDatabase</c> and the <see cref="IDatabase"/>
/// surface beyond the three probe ops are left unconfigured (any other call returns NSubstitute's default
/// / throws for non-Task members) — the double is intentionally minimal to the probe helper's needs.
/// </summary>
public sealed class FakeRedis
{
    /// <summary>The simulated Redis connection health.</summary>
    public enum RedisHealth
    {
        /// <summary>All probe ops throw a <see cref="RedisConnectionException"/>.</summary>
        Down,
        /// <summary>Read succeeds; write/delete throw (PROBE-02 read-only-healthy ⇒ still a fault).</summary>
        HalfOpen,
        /// <summary>Read returns Null; write/delete return true.</summary>
        Up,
    }

    private volatile int _health;          // boxed RedisHealth; volatile for cross-await visibility
    private int _failuresRemaining;        // when > 0: throw then decrement; auto-flips Up at 0

    /// <summary>Constructs the double in the given initial health (default <see cref="RedisHealth.Down"/>).</summary>
    public FakeRedis(RedisHealth initial = RedisHealth.Down)
    {
        _health = (int)initial;
        Database = BuildDatabase();
        Multiplexer = BuildMultiplexer(Database);
    }

    /// <summary>The current simulated health.</summary>
    public RedisHealth Health => (RedisHealth)_health;

    /// <summary>The wrapped <see cref="IConnectionMultiplexer"/> — ctor-inject this into the SUT.</summary>
    public IConnectionMultiplexer Multiplexer { get; }

    /// <summary>The single <see cref="IDatabase"/> returned by <c>GetDatabase()</c>.</summary>
    public IDatabase Database { get; }

    /// <summary>Flip the simulated Redis fully up (read + write succeed).</summary>
    public void BringUp() { _failuresRemaining = 0; _health = (int)RedisHealth.Up; }

    /// <summary>Flip the simulated Redis fully down (every probe op throws).</summary>
    public void BringDown() { _failuresRemaining = 0; _health = (int)RedisHealth.Down; }

    /// <summary>Flip to half-open: read OK, write/delete throw (PROBE-02).</summary>
    public void HalfOpen() { _failuresRemaining = 0; _health = (int)RedisHealth.HalfOpen; }

    /// <summary>
    /// Arm "fail the next <paramref name="count"/> probe ops (as if Down), then auto-recover to
    /// <see cref="RedisHealth.Up"/>" — the canonical fail-then-succeed shape for the retry loop.
    /// </summary>
    public void SetFailuresBeforeUp(int count) { _failuresRemaining = count; _health = (int)RedisHealth.Down; }

    private static RedisConnectionException Boom() =>
        new(ConnectionFailureType.UnableToConnect, "fake-down");

    // A read op: throws while Down (or counting down failures); throws under nothing in HalfOpen; Null when Up.
    private RedisValue OnRead()
    {
        if (TryConsumeFailure()) throw Boom();
        return Health == RedisHealth.Down ? throw Boom() : RedisValue.Null;
    }

    // A write/delete op: throws while Down or HalfOpen (or counting down failures); true when Up.
    private bool OnWrite()
    {
        if (TryConsumeFailure()) throw Boom();
        return Health switch
        {
            RedisHealth.Up => true,
            _ => throw Boom(),   // Down OR HalfOpen → write fails
        };
    }

    // Returns true (and the caller throws) while the armed failure counter is positive; auto-flips Up at 0.
    private bool TryConsumeFailure()
    {
        if (_failuresRemaining <= 0) return false;
        if (--_failuresRemaining == 0) _health = (int)RedisHealth.Up;
        return true;
    }

    private IDatabase BuildDatabase()
    {
        var db = Substitute.For<IDatabase>();

        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(_ => Task.FromResult(OnRead()));

        db.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
                Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(_ => Task.FromResult(OnWrite()));

        // The newer StackExchange.Redis StringSetAsync overload (with the extra `keepTtl`/`SetWhen` params)
        // also routes through OnWrite — NSubstitute matches the most-specific configured overload by arg shape.
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(_ => Task.FromResult(OnWrite()));

        return db;
    }

    private static IConnectionMultiplexer BuildMultiplexer(IDatabase db)
    {
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }
}
