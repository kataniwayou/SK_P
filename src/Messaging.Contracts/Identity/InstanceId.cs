namespace Messaging.Contracts.Identity;

/// <summary>
/// Single source of truth for the per-replica instance identity (KEY-03 / D-04). Resolves from the
/// env precedence <c>POD_NAME → HOSTNAME → MachineName → GUID</c>, the GUID final fallback rendered
/// via <c>ToString("N")</c> (32-digit, no hyphens) — byte-identical to the two existing observability
/// copies in <c>BaseApi.Core</c>/<c>BaseConsole.Core</c>. Hoisted into the <c>Messaging.Contracts</c>
/// leaf (the only assembly all callers reference without a cycle; BCL-only body) so the Phase-60
/// liveness writer and the existing OTel resources can share ONE mechanism (no new mechanism, KEY-03).
/// </summary>
public static class InstanceId
{
    /// <summary>
    /// Resolves the per-replica instance id: <c>POD_NAME ?? HOSTNAME ?? MachineName ?? GUID(N)</c>.
    /// The <c>ToString("N")</c> fallback is locked (DO NOT change the format specifier).
    /// </summary>
    public static string Resolve() =>
        Environment.GetEnvironmentVariable("POD_NAME")
        ?? Environment.GetEnvironmentVariable("HOSTNAME")
        ?? Environment.MachineName
        ?? Guid.NewGuid().ToString("N");
}
