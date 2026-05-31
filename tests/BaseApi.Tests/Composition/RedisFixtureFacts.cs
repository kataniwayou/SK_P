using Messaging.Contracts.Projections;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Composition;

/// <summary>
/// Phase 22 (D-23) — verifies the rewritten <see cref="RedisFixture"/> invariants on the SHARED
/// <c>skp:</c> keyspace: hardcoded localhost:6380 connection string with abortConnect=false
/// (D-11 + D-04), and KNOWN-KEY cleanup — <see cref="RedisFixture.DisposeAsync"/> deletes ONLY the
/// keys registered via <see cref="RedisFixture.Track(RedisKey)"/>, with NO <c>skp:*</c> wildcard SCAN
/// (a wildcard SCAN would catch sibling classes' keys now that the prefix is shared, T-22-14).
/// The old per-class unique prefix + SCAN-MATCH residual fail-loud are GONE.
/// </summary>
[Trait("Phase12Wave", "B")]
public sealed class RedisFixtureFacts
{
    [Fact]
    public void ConnectionString_Is_Localhost6380_With_AbortConnectFalse()
    {
        var fixture = new RedisFixture();
        Assert.Equal(
            "localhost:6380,abortConnect=false,connectTimeout=5000",
            fixture.ConnectionString);
    }

    [Fact]
    public void TrackedKeys_Starts_Empty()
    {
        var fixture = new RedisFixture();
        Assert.Empty(fixture.TrackedKeys);
    }

    [Fact]
    public void Track_Registers_Key_For_Cleanup()
    {
        var fixture = new RedisFixture();
        RedisKey key = $"{L2ProjectionKeys.Prefix}{Guid.NewGuid():D}";
        fixture.Track(key);
        Assert.Contains(key, fixture.TrackedKeys);
    }

    [Fact]
    public async Task InitializeAsync_Connects_Multiplexer()
    {
        await using var fixture = new RedisFixture();
        await fixture.InitializeAsync();
        Assert.NotNull(fixture.Multiplexer);
        Assert.True(fixture.Multiplexer.IsConnected);
    }

    [Fact]
    public async Task DisposeAsync_With_No_Tracked_Keys_Does_Not_Throw()
    {
        // No keys tracked — DisposeAsync must complete without touching the keyspace (no SCAN).
        var fixture = new RedisFixture();
        await fixture.InitializeAsync();
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_Deletes_Only_Tracked_Keys()
    {
        // Seed two skp: keys; track ONLY the first. After dispose the tracked key is gone and the
        // untracked sibling SURVIVES — proving cleanup is known-key, not a skp:* wildcard sweep
        // (D-23 / T-22-14). Clean up the surviving sibling via a side channel so the global
        // close-gate SCAN snapshot stays byte-identical.
        var fixture = new RedisFixture();
        await fixture.InitializeAsync();

        RedisKey tracked = $"{L2ProjectionKeys.Prefix}{Guid.NewGuid():D}";
        RedisKey untracked = $"{L2ProjectionKeys.Prefix}{Guid.NewGuid():D}";

        var db = fixture.Multiplexer.GetDatabase();
        await db.StringSetAsync(tracked, "x");
        await db.StringSetAsync(untracked, "y");
        fixture.Track(tracked);

        await using var side = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var sideDb = side.GetDatabase();
        try
        {
            await fixture.DisposeAsync();   // deletes ONLY `tracked`

            Assert.False(await sideDb.KeyExistsAsync(tracked), "tracked key should be deleted on dispose");
            Assert.True(await sideDb.KeyExistsAsync(untracked), "untracked sibling key must survive (no skp:* SCAN)");
        }
        finally
        {
            await sideDb.KeyDeleteAsync(untracked);   // restore the close-gate keyspace snapshot
        }
    }
}
