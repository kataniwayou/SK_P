using Microsoft.Extensions.Configuration;

namespace BaseConsole.Core.Configuration;

/// <summary>
/// Boundary-level configuration accessors that fail fast with an actionable message when a
/// required key is missing. Replaces the inconsistent <c>cfg["X"]!</c> null-forgiving operator
/// (NRE-prone — stack trace points at the consuming SDK, not the misconfiguration) and the
/// bare <c>cfg.GetConnectionString(...)</c> (same diagnostic gap).
///
/// <para>
/// D-08 duplicate: this is the console-side copy of the API base library helper. The two libraries
/// must not reference each other, so the fail-fast accessors are intentionally duplicated.
/// <c>public</c> (not <c>internal</c>) so the composition-root extensions in later Phase-18
/// plans (messaging / health bootstrap) can reach <see cref="RequireConnectionString"/>.
/// </para>
/// </summary>
public static class RequiredConfig
{
    public static string Require(this IConfiguration cfg, string key)
        => cfg[key] ?? throw new InvalidOperationException(
            $"Required configuration key '{key}' is missing. Set it via appsettings.json, " +
            $"environment variables, or user secrets. See README.md.");

    public static string RequireConnectionString(this IConfiguration cfg, string name)
        => cfg.GetConnectionString(name) ?? throw new InvalidOperationException(
            $"Required connection string 'ConnectionStrings:{name}' is missing. " +
            $"Set it via appsettings.json, environment variables, or user secrets. See README.md.");
}
