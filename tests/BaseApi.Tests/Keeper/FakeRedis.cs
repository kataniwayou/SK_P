using System.Collections.Concurrent;
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

    // KHARD-01: backs the per-H recover-attempt counter (skp:keeper:attempts:{H}). Independent of the
    // health flag — the cap counter is always reachable when Redis is Up (the cap-test condition); INCR
    // returns 1,2,3,... on consecutive calls, DEL removes the key (proving the no-leak park).
    private readonly ConcurrentDictionary<RedisKey, long> _counters = new();

    // WR-01 (T-40-05): per-key TTL-set state. A key gains an entry here the moment a PEXPIRE/EXPIRE
    // actually lands on it (records the milliseconds the TTL was set to). This lets the WR-01 regression
    // test assert (a) the counter key is BORN with a TTL atomically (n==1), and (b) later INCRs do NOT
    // clobber/reset it (the PEXPIRE-NX / ExpireWhen.HasNoExpiry no-clobber semantics).
    private readonly ConcurrentDictionary<RedisKey, long> _ttlMillis = new();

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

    /// <summary>KHARD-01: true while the per-H recover-attempt counter key is live — the cap test asserts
    /// this is <c>false</c> after the park (the counter was DEL'd, no leak).</summary>
    public bool CounterKeyExists(RedisKey key) => _counters.ContainsKey(key);

    /// <summary>KHARD-01/WR-01: the current per-H counter value (0 if absent) — lets a test wait deterministically
    /// for the handler's INCR to walk to a given n before asserting the TTL/no-clobber state.</summary>
    public long CounterValue(RedisKey key) => _counters.TryGetValue(key, out var v) ? v : 0;

    /// <summary>WR-01 (T-40-05): true while the key has a TTL recorded — the atomic-TTL test asserts the
    /// counter key is born WITH a TTL (n==1) so a crash can never leave an un-TTL'd counter leaking.</summary>
    public bool KeyHasTtl(RedisKey key) => _ttlMillis.ContainsKey(key);

    /// <summary>WR-01 (T-40-05): the TTL (in ms) currently recorded for the key, or <c>null</c> if none —
    /// lets the test assert later INCRs do NOT clobber/reset the first-write TTL (no-clobber semantics).</summary>
    public long? KeyTtlMillis(RedisKey key) => _ttlMillis.TryGetValue(key, out var ms) ? ms : null;

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

        // KHARD-01: the per-H recover-attempt counter ops (StringIncrement/KeyExpire) are backed by the
        // independent _counters dictionary — NOT routed through OnRead()/OnWrite()'s Down-throws (the cap
        // test always runs FakeRedis Up). Consecutive INCRs on the same key return 1,2,3,...
        db.StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(ci => Task.FromResult(_counters.AddOrUpdate((RedisKey)ci[0], (long)ci[1], (_, v) => v + (long)ci[1])));

        // WR-01 (T-40-05): KeyExpireAsync now HONOURS ExpireWhen so the no-clobber TTL semantics are testable.
        // ExpireWhen.HasNoExpiry sets the TTL only if none is already recorded (the set-once-on-create rule);
        // ExpireWhen.Always overwrites. Records the TTL (ms) in _ttlMillis so a test can assert the key is born
        // with a TTL and that later increments do not clobber it.
        db.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>(), Arg.Any<ExpireWhen>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var k  = (RedisKey)ci[0];
                var ts = (TimeSpan?)ci[1];
                var when = (ExpireWhen)ci[2];
                var ms = (long)(ts?.TotalMilliseconds ?? 0);
                if (when == ExpireWhen.HasNoExpiry)
                    return Task.FromResult(_ttlMillis.TryAdd(k, ms));   // no-clobber: only set if not already present
                _ttlMillis[k] = ms;
                return Task.FromResult(true);
            });

        // WR-01 (T-40-05): the atomic INCR + PEXPIRE-NX Lua path (KeeperRecoveryHandler) goes through
        // ScriptEvaluateAsync(string, RedisKey[], RedisValue[], flags). Implement the minimal semantics the
        // script needs: INCR KEYS[1]; if the result is 1 (first create) PEXPIRE KEYS[1] ARGV[1]; return the
        // count. The TTL is set-once on first create and NEVER clobbered on later increments — the same
        // no-clobber guarantee the previous ExpireWhen.HasNoExpiry round-trip gave, now atomic.
        db.ScriptEvaluateAsync(
                Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var keys = (RedisKey[])ci[1];
                var args = (RedisValue[])ci[2];
                var k = keys[0];
                var n = _counters.AddOrUpdate(k, 1, (_, v) => v + 1);
                if (n == 1)
                {
                    var ms = args is { Length: > 0 } ? (long)args[0] : 0;
                    _ttlMillis.TryAdd(k, ms);   // PEXPIRE only on first create (no clobber on later INCRs)
                }
                return Task.FromResult(RedisResult.Create(n));
            });

        // The newer StackExchange.Redis StringSetAsync overload (with the extra `keepTtl`/`SetWhen` params)
        // also routes through OnWrite — NSubstitute matches the most-specific configured overload by arg shape.
        // KeyDeleteAsync ALSO removes the counter key AND its TTL state so the cap test can prove the
        // DEL-on-park (no leak), and a re-INCR after a DEL is treated as a fresh first-create (TTL re-set).
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                _counters.TryRemove((RedisKey)ci[0], out _);
                _ttlMillis.TryRemove((RedisKey)ci[0], out _);
                return Task.FromResult(OnWrite());
            });

        return db;
    }

    private static IConnectionMultiplexer BuildMultiplexer(IDatabase db)
    {
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }
}
