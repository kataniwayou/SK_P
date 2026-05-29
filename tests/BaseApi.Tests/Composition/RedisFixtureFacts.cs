using System.Text.RegularExpressions;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Composition;

/// <summary>
/// Phase 12 TEST-REDIS-01..03 — verifies RedisFixture invariants:
/// per-instance Guid KeyPrefix (D-12), hardcoded localhost:6380 connection string
/// with abortConnect=false (D-11 + D-04), SCAN+DEL+assert-zero cleanup discipline
/// (D-10), FLUSHDB-forbidden educational message on residual violation.
/// </summary>
[Trait("Phase12Wave", "B")]
public sealed class RedisFixtureFacts
{
    private static readonly Regex KeyPrefixRegex = new(@"^test:cls-[0-9a-f]{32}:$");

    [Fact]
    public void KeyPrefix_Matches_TestClsGuid_Regex()
    {
        var fixture = new RedisFixture();
        Assert.Matches(KeyPrefixRegex, fixture.KeyPrefix);
    }

    [Fact]
    public void KeyPrefix_Differs_Across_Instances()
    {
        var a = new RedisFixture();
        var b = new RedisFixture();
        Assert.NotEqual(a.KeyPrefix, b.KeyPrefix);
    }

    [Fact]
    public void ConnectionString_Is_Localhost6380_With_AbortConnectFalse()
    {
        var fixture = new RedisFixture();
        Assert.Equal(
            "localhost:6380,abortConnect=false,connectTimeout=5000",
            fixture.ConnectionString);
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
    public async Task DisposeAsync_With_Empty_Prefix_Does_Not_Throw()
    {
        // `await using` mirrors the other facts in this class; if InitializeAsync throws,
        // the multiplexer is still disposed (no TCP-connection leak).
        var fixture = new RedisFixture();
        await fixture.InitializeAsync();
        // No keys seeded — DisposeAsync must complete without throwing.
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_With_Residual_Matching_Key_Throws_InvalidOperationException()
    {
        // TEST-REDIS-03 fail-loud: DisposeAsync re-SCANs after its SCAN+DEL batch and
        // throws if any key matching {KeyPrefix}* survives. The fixture's own SCAN+DEL
        // only deletes the keys its first SCAN observed; a key that arrives AFTER that
        // first SCAN but is still present at re-SCAN time is a genuine leak and must
        // surface the throw.
        //
        // Deterministic injection without intercepting the fixture mid-dispose:
        //   1. Pre-seed 300 matching keys so the fixture's first SCAN+DEL pages through
        //      them (pageSize:250 → ≥2 SCAN pages + a 300-key DEL batch), making the
        //      DEL→re-SCAN window measurable rather than instantaneous.
        //   2. A side-channel multiplexer tight-loops re-creating one residual key for
        //      the duration of the dispose. With the widened window, the re-created key
        //      is reliably present at the fixture's re-SCAN, which observes a residual
        //      and throws.
        // The loop is stopped + the residual key deleted in finally so the global SCAN
        // snapshot (TEST-REDIS-04 phase-close gate) stays byte-identical.
        var fixture = new RedisFixture();
        await fixture.InitializeAsync();

        var sideChannel = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var sideDb = sideChannel.GetDatabase();
        var residualKey = $"{fixture.KeyPrefix}injected";
        using var cts = new CancellationTokenSource();

        // Pre-seed 300 deletable matching keys to widen the SCAN+DEL→re-SCAN window.
        var seed = new Task[300];
        for (var i = 0; i < seed.Length; i++)
        {
            seed[i] = sideDb.StringSetAsync($"{fixture.KeyPrefix}bulk-{i}", "x");
        }
        await Task.WhenAll(seed);

        var reseeder = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                await sideDb.StringSetAsync(residualKey, "stub");
            }
        }, cts.Token);

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await fixture.DisposeAsync());
            Assert.Contains("FLUSHDB is FORBIDDEN", ex.Message);
        }
        finally
        {
            cts.Cancel();
            try { await reseeder; } catch (OperationCanceledException) { /* expected */ }
            // Clean up ALL residuals (the injected key + any surviving bulk keys) so the
            // global SCAN snapshot (TEST-REDIS-04 phase-close gate) is byte-identical.
            var cleanupServer = sideChannel.GetServer(sideChannel.GetEndPoints()[0]);
            var leftover = new List<RedisKey>();
            await foreach (var key in cleanupServer.KeysAsync(pattern: $"{fixture.KeyPrefix}*", pageSize: 1000))
            {
                leftover.Add(key);
            }
            if (leftover.Count > 0)
            {
                await sideDb.KeyDeleteAsync(leftover.ToArray());
            }
            await sideChannel.DisposeAsync();
        }
    }
}
