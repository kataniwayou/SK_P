using Cronos;

namespace Orchestrator.Scheduling;

/// <summary>
/// Cronos fire-time math for the orchestrator scheduler (D-08/D-09). Parses the stored 5-field cron
/// via <see cref="CronFormat.Standard"/> (matches VALID-19) and computes the next occurrence and the
/// L1 liveness interval (delta-seconds between the next two occurrences).
/// <para>
/// <b>Pitfall 3 (UTC):</b> the <c>nowUtc</c> argument MUST be <see cref="DateTimeKind.Utc"/> — Cronos
/// throws on non-UTC input. Callers feed <c>timeProvider.GetUtcNow().UtcDateTime</c>, NEVER the
/// ambient wall-clock statics (which would also break TimeProvider-driven tests).
/// </para>
/// </summary>
public static class CronInterval
{
    /// <summary>
    /// The next strictly-future occurrence of <paramref name="cron"/> after <paramref name="nowUtc"/>
    /// (Kind=Utc), or null when there is none (business-skip path for the scheduler).
    /// </summary>
    public static DateTime? NextOccurrence(string cron, DateTime nowUtc) =>
        CronExpression.Parse(cron, CronFormat.Standard).GetNextOccurrence(nowUtc);

    /// <summary>
    /// The L1 liveness interval (D-09): delta-seconds between the next two occurrences of
    /// <paramref name="cron"/> after <paramref name="nowUtc"/> (Kind=Utc). Returns 0 when there is
    /// no future occurrence (or only one) — the business-skip path.
    /// </summary>
    public static int IntervalSeconds(string cron, DateTime nowUtc)
    {
        var expr = CronExpression.Parse(cron, CronFormat.Standard);
        var n1 = expr.GetNextOccurrence(nowUtc);
        var n2 = n1 is { } a ? expr.GetNextOccurrence(a) : null;
        return (n1, n2) is ({ } x, { } y) ? (int)Math.Round((y - x).TotalSeconds) : 0;
    }
}
