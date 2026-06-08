namespace Keeper;

/// <summary>D-10: composite-backup TTL knob (crash-backstop only — the backup is normally deleted by
/// the CLEANUP/INJECT Keeper states; the TTL is the net for a crash before that delete). Bound from
/// the "Backup" appsettings section (mirrors <see cref="ProbeOptions"/>). Default 2 days.</summary>
public sealed class BackupOptions
{
    public int TtlDays { get; set; } = 2;
}
