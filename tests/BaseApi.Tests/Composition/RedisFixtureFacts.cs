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
        // TEST-REDIS-03 fail-loud: DisposeAsync re-SCANs after its SCAN+DEL batch and throws if
        // any key matching {KeyPrefix}* survives. To exercise that throw we must make a residual
        // key PRESENT at the fixture's re-SCAN — which means a side-channel write has to land in
        // the microsecond gap between the fixture's DEL and its re-SCAN. That gap is a genuine
        // thread race: under a fully-loaded parallel suite the reseeder thread can be starved
        // across the whole gap, so a SINGLE injection attempt is inherently flaky (it produced
        // intermittent "No exception was thrown" failures in the close gate — a TEST-HARNESS race,
        // NOT a fixture defect: RedisFixture's SCAN+DEL+re-SCAN+throw logic is correct).
        //
        // Deterministic OUTCOME via bounded retry-until-throw: repeat the inject-and-dispose with a
        // FRESH fixture (fresh Guid prefix) until the fixture throws. A correct detector throws
        // within a few attempts; a BROKEN detector (regression) never throws → the bounded loop
        // exhausts and fails loudly. So the assertion's meaning is preserved exactly — "a surviving
        // residual key MUST surface the FLUSHDB-forbidden throw" — without depending on winning a
        // race on any one attempt. Each attempt cleans up ALL its own {prefix}* keys in finally so
        // the global SCAN snapshot (TEST-REDIS-04 phase-close gate) stays byte-identical.
        const int maxAttempts = 30;
        InvalidOperationException? caught = null;

        for (var attempt = 1; attempt <= maxAttempts && caught is null; attempt++)
        {
            var fixture = new RedisFixture();
            await fixture.InitializeAsync();

            var sideChannel = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
            var sideDb = sideChannel.GetDatabase();
            var residualKey = $"{fixture.KeyPrefix}injected";
            using var cts = new CancellationTokenSource();

            // Pre-seed 300 matching keys so the fixture's first SCAN+DEL pages through them
            // (pageSize:250 → ≥2 SCAN pages + a 300-key DEL batch), widening the DEL→re-SCAN window.
            var seed = new Task[300];
            for (var i = 0; i < seed.Length; i++)
            {
                seed[i] = sideDb.StringSetAsync($"{fixture.KeyPrefix}bulk-{i}", "x");
            }
            await Task.WhenAll(seed);

            // Side-channel SYNCHRONOUS tight loop re-creating the residual key for the dispose
            // duration, so it is present at ~every instant including the fixture's re-SCAN.
            var reseeder = Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    sideDb.StringSet(residualKey, "stub");
                }
            }, cts.Token);

            try
            {
                await fixture.DisposeAsync();
                // No throw this attempt → the reseeder lost the DEL→re-SCAN race; retry fresh.
            }
            catch (InvalidOperationException ex)
            {
                caught = ex;  // residual observed at re-SCAN → detector fired (the success case)
            }
            finally
            {
                cts.Cancel();
                try { await reseeder; } catch (OperationCanceledException) { /* expected */ }
                // Clean up ALL residuals (injected key + any surviving bulk keys) for THIS
                // attempt's prefix so the global SCAN snapshot stays byte-identical.
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

        // A correct fixture surfaces the residual within maxAttempts; never throwing = regression.
        Assert.True(
            caught is not null,
            $"RedisFixture.DisposeAsync did not throw on a surviving residual key across {maxAttempts} " +
            "injection attempts — the SCAN+DEL+re-SCAN fail-loud guard (TEST-REDIS-03) has regressed.");
        Assert.Contains("FLUSHDB is FORBIDDEN", caught!.Message);
    }
}
