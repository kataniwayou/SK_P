using System.Collections.Concurrent;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Composition;

/// <summary>
/// Per-test-class Redis fixture mirroring <see cref="BaseApi.Tests.Persistence.PostgresFixture"/>.
/// Connects to the host-side compose Redis at <c>localhost:6380</c> (D-11).
///
/// <para>
/// <b>Phase 22 (D-20..D-23) shared-keyspace model:</b> the L2 key prefix is now the compile-time
/// const <c>L2ProjectionKeys.Prefix == "skp:"</c> (no configurable per-class key-prefix setting),
/// so tests run directly on the PROD keyspace (the same keyspace the close-gate <c>redis-cli --scan</c>
/// hashes). The old per-class unique prefix + <c>SCAN MATCH</c> cleanup is GONE:
/// a <c>skp:*</c> SCAN would now catch sibling test classes' keys (cross-test contamination, T-22-14)
/// and could mask or delete keys another class is mid-flight on. Instead this fixture tracks the
/// SPECIFIC keys each test created (<see cref="Track(RedisKey)"/> / <see cref="TrackedKeys"/>) and, on
/// <see cref="DisposeAsync"/>, deletes ONLY those known keys — never a wildcard SCAN.
/// </para>
///
/// <para>
/// <b>Parent-index discipline:</b> the shared <c>skp:</c> parent-index SET is the only contention point
/// on the now-shared keyspace. Tests that SADD a workflow id into it MUST SREM their own id (and run in
/// the single non-parallel <c>ParentIndex</c> collection) so the SET is empty between tests — the
/// triple-SHA close gate (<c>redis-cli --scan</c> BEFORE==AFTER) is the fail-loud proof (T-22-15).
/// </para>
///
/// <para>
/// <b>D-09 lifetime:</b> One <see cref="IConnectionMultiplexer"/> per fixture instance. Per-class
/// disposal guarantees TCP-connection accounting under xUnit v3 parallel class execution.
/// </para>
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    // D-11 — hardcoded localhost:6380. Mirrors PostgresFixture's hardcoded
    // localhost:5433 (PostgresFixture.cs:28). No env-var override; if compose isn't
    // running, the first PING fails-loud and tests stop (correct behavior).
    public string ConnectionString { get; } =
        "localhost:6380,abortConnect=false,connectTimeout=5000";

    /// <summary>
    /// Known keys this fixture's tests created. <see cref="DisposeAsync"/> deletes exactly these
    /// (no <c>skp:*</c> wildcard SCAN — D-23). Thread-safe for parallel-fact registration.
    /// </summary>
    public ConcurrentBag<RedisKey> TrackedKeys { get; } = new();

    public IConnectionMultiplexer Multiplexer { get; private set; } = default!;

    /// <summary>
    /// Registers a key for known-key cleanup on dispose. Call this for every L2 key a test seeds
    /// directly (root / step / processor) so the shared keyspace returns to its BEFORE state.
    /// </summary>
    public void Track(RedisKey key) => TrackedKeys.Add(key);

    public async ValueTask InitializeAsync()
    {
        // D-09 — per-fixture multiplexer. Synchronous Connect() is safe because the
        // connection string carries abortConnect=false; even a dead Redis lets
        // ConnectAsync return (PITFALLS P2 mitigation).
        Multiplexer = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // D-23 known-key cleanup: delete ONLY the keys this class registered. NO skp:* SCAN —
            // a wildcard SCAN on the now-shared prefix would catch sibling classes' keys (T-22-14).
            var keys = TrackedKeys.Distinct().ToArray();
            if (keys.Length > 0)
            {
                var db = Multiplexer.GetDatabase();
                await db.KeyDeleteAsync(keys);
            }
        }
        finally
        {
            // Always dispose the multiplexer even if the delete threw.
            await Multiplexer.DisposeAsync();
        }
    }
}
