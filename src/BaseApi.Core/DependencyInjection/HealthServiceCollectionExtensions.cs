using BaseApi.Core.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BaseApi.Core.DependencyInjection;

/// <summary>
/// Phase 5 health wiring: IStartupGate singleton + AddHealthChecks chain (self/live, startup,
/// ready via NpgSql) + StartupCompletionService hosted service that flips the gate on host
/// start. Phase 8 replaces StartupCompletionService with MigrationRunner (single
/// AddHostedService swap; the rest of the chain stays).
/// </summary>
internal static class HealthServiceCollectionExtensions
{
    internal static IServiceCollection AddBaseApiHealth(
        this IServiceCollection services, IConfiguration cfg)
    {
        services.AddSingleton<IStartupGate, StartupGate>();
        services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
            .AddCheck<StartupHealthCheck>("startup", tags: new[] { "startup", "ready" })
            .AddNpgSql(cfg.GetConnectionString("Postgres")!, tags: new[] { "ready" });

        services.AddHostedService<StartupCompletionService>();
        return services;
    }
}
