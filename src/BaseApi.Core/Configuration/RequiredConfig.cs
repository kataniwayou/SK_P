using Microsoft.Extensions.Configuration;

namespace BaseApi.Core.Configuration;

/// <summary>
/// Boundary-level configuration accessors that fail fast with an actionable message when a
/// required key is missing. Replaces the inconsistent <c>cfg["X"]!</c> null-forgiving operator
/// (NRE-prone — stack trace points at the consuming SDK, not the misconfiguration) and the
/// bare <c>cfg.GetConnectionString(...)</c> (same diagnostic gap) used at four sites pre-WR-03:
/// <see cref="DependencyInjection.ObservabilityServiceCollectionExtensions"/> (Service:Name +
/// Service:Version), <see cref="DependencyInjection.HealthServiceCollectionExtensions"/>
/// (ConnectionStrings:Postgres), and
/// <see cref="DependencyInjection.PersistenceServiceCollectionExtensions"/>
/// (ConnectionStrings:Postgres).
///
/// <para>
/// Swagger's <c>cfg["Service:Name"] ?? "sk-api"</c> fallback is intentional (the Swagger doc
/// title is operationally non-critical) and is NOT migrated to this helper.
/// </para>
/// </summary>
internal static class RequiredConfig
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
