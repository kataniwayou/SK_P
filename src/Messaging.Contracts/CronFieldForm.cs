namespace Messaging.Contracts.Projections;

/// <summary>
/// Single source of truth (D-03/D-05) for cron field-count → format selection. Pure string
/// logic — NO Cronos dependency (keeps the contracts leaf parser-free). Both the Orchestrator
/// scheduler (CronInterval) and the BaseApi.Service validators consume this ONE rule (D-04) so
/// "validator-accepts ⟺ scheduler-parses-the-same-format" can never desynchronize.
/// 6 tokens → CronFormat.IncludeSeconds; 5 tokens → CronFormat.Standard; any other count → reject (D-01/D-02).
/// </summary>
public static class CronFieldForm
{
    /// <summary>true → 6-field seconds form (map to CronFormat.IncludeSeconds);
    /// false → 5-field standard form (map to CronFormat.Standard).</summary>
    public static bool IsSecondsForm(string expr) => FieldCount(expr) == 6;

    /// <summary>true when the trimmed expression has a usable 5- or 6-field count.
    /// Callers reject (return invalid) when this is false BEFORE touching Cronos (D-01/D-02).</summary>
    public static bool IsValidFieldCount(string expr) => FieldCount(expr) is 5 or 6;

    private static int FieldCount(string expr) =>
        string.IsNullOrWhiteSpace(expr)
            ? 0
            : expr.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
