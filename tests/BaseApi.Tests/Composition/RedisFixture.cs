using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Composition;

/// <summary>
/// Per-test-class Redis fixture mirroring <see cref="BaseApi.Tests.Persistence.PostgresFixture"/>.
/// Connects to the host-side compose Redis at <c>localhost:6380</c> (D-11) and isolates each
/// test class via a unique <see cref="KeyPrefix"/> = <c>"test:cls-{Guid:N}:"</c>
/// (D-12 / TEST-REDIS-01).
///
/// <para>
/// <b>D-09 lifetime:</b> One <see cref="IConnectionMultiplexer"/> per fixture instance. With ~50
/// test classes that consume <see cref="Phase8WebAppFactory"/>, peak concurrent multiplexer
/// count is well within Redis maxclients default budget (10000). Per-class disposal guarantees
/// TCP-connection accounting under xUnit v3 parallel class execution.
/// </para>
///
/// <para>
/// <b>D-10 cleanup contract:</b> <see cref="DisposeAsync"/> SCANs MATCH <c>"{KeyPrefix}*"</c>,
/// issues <c>KeyDeleteAsync(keys)</c>, re-SCANs with the same prefix, and asserts count == 0.
/// Throws on violation (fail-loud — TEST-REDIS-03 verbatim). <c>FLUSHDB</c> is FORBIDDEN
/// (would destroy keys from parallel-running test classes; hides genuine leaks).
/// Cursor-based SCAN preserves the L2-PROJECT-07 forbidden-<see cref="IServer.Keys"/>/<c>KEYS</c>
/// invariant from day one (production code uses SCAN-only — this fixture sets the reference pattern).
/// </para>
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    // D-11 — hardcoded localhost:6380. Mirrors PostgresFixture's hardcoded
    // localhost:5433 (PostgresFixture.cs:28). No env-var override; if compose isn't
    // running, the first PING fails-loud and tests stop (correct behavior).
    public string ConnectionString { get; } =
        "localhost:6380,abortConnect=false,connectTimeout=5000";

    // D-12 / TEST-REDIS-01 — per-instance Guid prefix. Production prefix "skp:"
    // (INFRA-REDIS-05) and test prefix family "test:cls-*" share no overlap, so
    // a residual test-key leak surfaces immediately in the redis-cli --scan
    // phase-close gate (TEST-REDIS-04).
    public string KeyPrefix { get; } = $"test:cls-{Guid.NewGuid():N}:";

    public IConnectionMultiplexer Multiplexer { get; private set; } = default!;

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
            var db = Multiplexer.GetDatabase();
            var server = Multiplexer.GetServer(Multiplexer.GetEndPoints()[0]);

            // SCAN MATCH "{KeyPrefix}*" — cursor-based, non-blocking.
            // server.KeysAsync uses SCAN under the hood (NOT KEYS — L2-PROJECT-07).
            var toDelete = new List<RedisKey>();
            await foreach (var key in server.KeysAsync(pattern: $"{KeyPrefix}*", pageSize: 250))
            {
                toDelete.Add(key);
            }

            if (toDelete.Count > 0)
            {
                await db.KeyDeleteAsync(toDelete.ToArray());
            }

            // Re-SCAN and assert count == 0 — TEST-REDIS-03 fail-loud discipline.
            // pageSize: 1000 here (vs 250 above) per RESEARCH Pitfall 7 defensive — larger page
            // reduces page-boundary edge-case risk on the verification SCAN.
            var residualCount = 0;
            await foreach (var _ in server.KeysAsync(pattern: $"{KeyPrefix}*", pageSize: 1000))
            {
                residualCount++;
            }

            if (residualCount > 0)
            {
                throw new InvalidOperationException(
                    $"RedisFixture cleanup violation: {residualCount} residual keys matching " +
                    $"pattern '{KeyPrefix}*' after SCAN+DEL. This indicates a leaked-key bug " +
                    $"in a Phase 12+ test. FLUSHDB is FORBIDDEN — investigate the offending test.");
            }
        }
        finally
        {
            // Always dispose the multiplexer even if the SCAN/DEL assertion threw.
            await Multiplexer.DisposeAsync();
        }
    }
}
