using Microsoft.Extensions.Time.Testing;
using Orchestrator.Scheduling;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Pins the Cronos fire-time math (D-08/D-09): <see cref="CronInterval.IntervalSeconds"/> is the
/// delta-seconds between the next two occurrences, and <see cref="CronInterval.NextOccurrence"/>
/// returns a strictly-future UTC instant (or null when there is none). A
/// <see cref="FakeTimeProvider"/> pins "now" to a known UTC instant (Pitfall 3 — Cronos requires
/// Kind=Utc input).
/// </summary>
public sealed class CronIntervalTests
{
    // 2026-01-01T00:00:30Z — a fixed UTC instant 30s past the minute so */5 next-fire is 00:05:00.
    private static readonly DateTime PinnedUtc = new(2026, 1, 1, 0, 0, 30, DateTimeKind.Utc);

    [Fact]
    public void IntervalIsDeltaSecondsBetweenNextTwoOccurrences()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(PinnedUtc));

        var interval = CronInterval.IntervalSeconds("*/5 * * * *", time.GetUtcNow().UtcDateTime);

        Assert.Equal(300, interval); // every-5-minutes -> 300s between the next two fires
    }

    [Fact]
    public void NextOccurrence_IsStrictlyFuture_AndUtc()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(PinnedUtc));
        var nowUtc = time.GetUtcNow().UtcDateTime;

        var next = CronInterval.NextOccurrence("*/5 * * * *", nowUtc);

        Assert.NotNull(next);
        Assert.Equal(DateTimeKind.Utc, next!.Value.Kind);
        Assert.True(next.Value > nowUtc, "next occurrence must be strictly in the future");
    }

    [Fact]
    public void NoFutureOccurrence_ReturnsNullAndZeroInterval()
    {
        // A one-shot cron whose only matching instant is strictly in the past relative to a far-future
        // "now" yields no future occurrence: null next-occurrence and a 0 interval (business-skip path).
        var farFutureUtc = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        const string pastOnlyCron = "0 0 1 1 *"; // 00:00 on Jan 1 — but Cronos always finds the NEXT year.

        // To force "no future occurrence" deterministically, use a cron that can never match
        // (Feb 30 does not exist): minute 0, hour 0, day-of-month 30, month 2.
        const string impossibleCron = "0 0 30 2 *";

        Assert.Null(CronInterval.NextOccurrence(impossibleCron, farFutureUtc));
        Assert.Equal(0, CronInterval.IntervalSeconds(impossibleCron, farFutureUtc));
        // pastOnlyCron is referenced only to document the rejected approach; assert it DOES find one.
        Assert.NotNull(CronInterval.NextOccurrence(pastOnlyCron, farFutureUtc));
    }
}
