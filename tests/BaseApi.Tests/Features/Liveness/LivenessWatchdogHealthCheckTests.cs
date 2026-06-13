using BaseProcessor.Core.Liveness;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace BaseApi.Tests.Features.Liveness;

/// <summary>
/// PROBE-01/02 (D-01/02/03/04) pure-unit proof of <see cref="LivenessWatchdogHealthCheck"/>: the watchdog
/// reads the in-memory L1 holder via the OUTER provider at check time and reports null/stale as Unhealthy,
/// fresh as Healthy, carrying the per-schema summary in <c>HealthCheckResult.Data</c> for the non-null cases.
/// No Redis/RMQ touched (D-01) — the holder + clock are fabricated and bridged through a stub provider.
/// Info-disclosure guard (T-61-04): no Data value or description leaks an instanceId / connection-string
/// token / stack-trace marker (mirrors ConsoleHealthLiveTests.Live_Body_Has_No_Secrets).
/// </summary>
[Trait("Phase", "61")]
public sealed class LivenessWatchdogHealthCheckTests
{
    // A fixed "now" the FakeTimeProvider returns; entries are positioned relative to it.
    private static readonly DateTime Now = new(2026, 6, 13, 12, 0, 0, DateTimeKind.Utc);
    private const int IntervalSeconds = 10;

    private static IServiceProvider BuildProvider(ProcessorLivenessEntry? current)
    {
        var state = Substitute.For<IProcessorLivenessState>();
        state.Current.Returns(current);

        var clock = new FakeTimeProvider();
        clock.SetUtcNow(new DateTimeOffset(Now));

        // GetRequiredService<T>() calls GetService(typeof(T)) under the hood — stub both resolutions.
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(IProcessorLivenessState)).Returns(state);
        sp.GetService(typeof(TimeProvider)).Returns(clock);
        return sp;
    }

    private static Task<HealthCheckResult> RunAsync(ProcessorLivenessEntry? current)
        => new LivenessWatchdogHealthCheck(BuildProvider(current))
            .CheckHealthAsync(new HealthCheckContext());

    [Fact]
    public async Task Null_L1_Reports_Unhealthy_LoopNotStarted()
    {
        // D-02: Current == null (the loop crashed before its first write).
        var result = await RunAsync(null);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("liveness loop not started", result.Description);
        // Null L1 carries no summary data (there is no entry to read it from).
        Assert.Empty(result.Data);
    }

    [Fact]
    public async Task Stale_L1_Reports_Unhealthy_With_Summary_In_Data()
    {
        // D-03: timestamp older than interval*2 grace ⇒ stale ⇒ Unhealthy.
        var staleTimestamp = Now.AddSeconds(-(IntervalSeconds * 2) - 1);
        var entry = ProcessorLivenessEntry.Create(null, null, null, staleTimestamp, IntervalSeconds);

        var result = await RunAsync(entry);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("liveness loop stale", result.Description);
        AssertSummaryDataPresent(result);
    }

    [Fact]
    public async Task Fresh_L1_Reports_Healthy_With_Summary_In_Data()
    {
        // Fresh: timestamp well within the interval*2 grace ⇒ Healthy.
        var freshTimestamp = Now.AddSeconds(-1);
        var entry = ProcessorLivenessEntry.Create(null, null, null, freshTimestamp, IntervalSeconds);

        var result = await RunAsync(entry);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("live", result.Description);
        AssertSummaryDataPresent(result);
    }

    [Fact]
    public async Task ExactBoundary_NowEqualsDeadline_Reports_Unhealthy_Stale()
    {
        // WR-01: stale at-or-past the boundary instant — at the EXACT boundary (now == timestamp + interval*2)
        // the watchdog reports Unhealthy, agreeing with the gate's `deadline <= now => stale`
        // (ProcessorLivenessGateUnitTests.ExactBoundary_DeadlineEqualsNow_CountsStale).
        var boundaryTimestamp = Now.AddSeconds(-(IntervalSeconds * 2)); // deadline == Now exactly
        var entry = ProcessorLivenessEntry.Create(null, null, null, boundaryTimestamp, IntervalSeconds);

        var result = await RunAsync(entry);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("liveness loop stale", result.Description);
        AssertSummaryDataPresent(result);
    }

    [Fact]
    public async Task OneTickBeforeBoundary_StrictlyFresh_Reports_Healthy()
    {
        // WR-01: one tick before the boundary (deadline = now + 1 tick) is strictly fresh => Healthy.
        var almostBoundary = Now.AddSeconds(-(IntervalSeconds * 2)).AddTicks(1); // deadline == Now + 1 tick
        var entry = ProcessorLivenessEntry.Create(null, null, null, almostBoundary, IntervalSeconds);

        var result = await RunAsync(entry);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("live", result.Description);
        AssertSummaryDataPresent(result);
    }

    private static void AssertSummaryDataPresent(HealthCheckResult result)
    {
        // PROBE-02: the per-schema summary rides in the result Data dictionary.
        Assert.True(result.Data.ContainsKey("inputSchema"));
        Assert.True(result.Data.ContainsKey("outputSchema"));
        Assert.True(result.Data.ContainsKey("configSchema"));

        // T-61-04 info-disclosure guard: no Data value or description leaks a secret.
        var surfaces = result.Data.Values
            .Select(v => v?.ToString() ?? string.Empty)
            .Append(result.Description ?? string.Empty);
        foreach (var s in surfaces)
        {
            Assert.DoesNotContain("Password=", s);
            Assert.DoesNotContain("password=", s);
            Assert.DoesNotContain("abortConnect", s);   // Redis connection-string token
            Assert.DoesNotContain("   at ", s);          // .NET stack-trace frame marker
        }
    }
}
