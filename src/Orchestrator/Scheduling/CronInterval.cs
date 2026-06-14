using Cronos;
using Messaging.Contracts.Projections;

namespace Orchestrator.Scheduling;

/// <summary>
/// Cronos fire-time math for the orchestrator scheduler (D-08/D-09). Parses the stored 5- or 6-field
/// cron via a detector-resolved <see cref="CronFormat"/> — <see cref="CronFormat.Standard"/> for the
/// 5-field form, <see cref="CronFormat.IncludeSeconds"/> for the 6-field seconds form (CRON-01). The
/// format is resolved up front from the shared <see cref="CronFieldForm"/> rule (never via catch-retry,
/// D-02) so validator-accepts and scheduler-parses cannot desync. Computes the next occurrence and the
/// L1 liveness interval (delta-seconds between the next two occurrences) — the delta-math is
/// granularity-agnostic, so the 6-field seconds form yields sub-minute intervals with no further change.
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
    public static DateTime? NextOccurrence(string cron, DateTime nowUtc)
    {
        var format = CronFieldForm.IsSecondsForm(cron) ? CronFormat.IncludeSeconds : CronFormat.Standard;
        return CronExpression.Parse(cron, format).GetNextOccurrence(nowUtc);
    }

    /// <summary>
    /// The L1 liveness interval (D-09): delta-seconds between the next two occurrences of
    /// <paramref name="cron"/> after <paramref name="nowUtc"/> (Kind=Utc). Returns 0 when there is
    /// no future occurrence (or only one) — the business-skip path.
    /// </summary>
    public static int IntervalSeconds(string cron, DateTime nowUtc)
    {
        var format = CronFieldForm.IsSecondsForm(cron) ? CronFormat.IncludeSeconds : CronFormat.Standard;
        var expr = CronExpression.Parse(cron, format);
        var n1 = expr.GetNextOccurrence(nowUtc);
        var n2 = n1 is { } a ? expr.GetNextOccurrence(a) : null;
        return (n1, n2) is ({ } x, { } y) ? (int)Math.Round((y - x).TotalSeconds) : 0;
    }
}
