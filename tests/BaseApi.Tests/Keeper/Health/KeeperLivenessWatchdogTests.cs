using Keeper;
using Keeper.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace BaseApi.Tests.Keeper.Health;

/// <summary>
/// 260614-b5c pure-unit proof of <see cref="KeeperLivenessWatchdogHealthCheck"/>: the watchdog reads the
/// in-memory timestamp-only holder via the OUTER provider at check time and reports null/stale as Unhealthy,
/// fresh as Healthy("live"). Deliberately MINIMAL — there is NO Data dictionary on any verdict (mirrors the
/// processor watchdog in shape, but timestamp-only, no per-schema summary). No Redis/RMQ touched — the holder
/// + clock + ProbeOptions are fabricated and bridged through a stub provider.
/// </summary>
public sealed class KeeperLivenessWatchdogTests
{
    // A fixed "now" the FakeTimeProvider returns; timestamps are positioned relative to it.
    private static readonly DateTime Now = new(2026, 6, 14, 12, 0, 0, DateTimeKind.Utc);
    private const int DelaySeconds = 10;

    private static IServiceProvider BuildProvider(DateTime? current, int delaySeconds)
    {
        var state = Substitute.For<IKeeperLivenessState>();
        state.Current.Returns(current);

        var clock = new FakeTimeProvider();
        clock.SetUtcNow(new DateTimeOffset(Now));

        // GetRequiredService<T>() calls GetService(typeof(T)) under the hood — stub all three resolutions.
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(IKeeperLivenessState)).Returns(state);
        sp.GetService(typeof(TimeProvider)).Returns(clock);
        sp.GetService(typeof(IOptions<ProbeOptions>))
            .Returns(Options.Create(new ProbeOptions { DelaySeconds = delaySeconds }));
        return sp;
    }

    private static Task<HealthCheckResult> RunAsync(DateTime? current, int delaySeconds = DelaySeconds)
        => new KeeperLivenessWatchdogHealthCheck(BuildProvider(current, delaySeconds))
            .CheckHealthAsync(new HealthCheckContext());

    [Fact]
    public async Task Null_Current_Reports_Unhealthy_BitLoopNotStarted()
    {
        var result = await RunAsync(null);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("BIT loop not started", result.Description);
        Assert.Empty(result.Data);   // timestamp-only — NO Data on any verdict
    }

    [Fact]
    public async Task Fresh_Current_Reports_Healthy_Live()
    {
        // Within the DelaySeconds*2 grace ⇒ Healthy("live").
        var result = await RunAsync(Now.AddSeconds(-1));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("live", result.Description);
        Assert.Empty(result.Data);
    }

    [Fact]
    public async Task Stale_Current_Reports_Unhealthy_BitLoopStale()
    {
        // Older than DelaySeconds*2 grace ⇒ stale ⇒ Unhealthy.
        var result = await RunAsync(Now.AddSeconds(-(DelaySeconds * 2) - 1));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("BIT loop stale", result.Description);
        Assert.Empty(result.Data);
    }

    [Fact]
    public async Task ExactBoundary_NowEqualsDeadline_Reports_Unhealthy_Stale()
    {
        // Strict >= discipline: at the EXACT boundary (now == Current + DelaySeconds*2) the watchdog is stale.
        var result = await RunAsync(Now.AddSeconds(-(DelaySeconds * 2)));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("BIT loop stale", result.Description);
        Assert.Empty(result.Data);
    }

    [Fact]
    public async Task OneTickBeforeBoundary_StrictlyFresh_Reports_Healthy_Live()
    {
        // One tick before the boundary is strictly fresh ⇒ Healthy("live").
        var result = await RunAsync(Now.AddSeconds(-(DelaySeconds * 2)).AddTicks(1));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("live", result.Description);
        Assert.Empty(result.Data);
    }
}
