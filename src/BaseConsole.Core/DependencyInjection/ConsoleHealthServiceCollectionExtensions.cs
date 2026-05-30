using BaseConsole.Core.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BaseConsole.Core.DependencyInjection;

/// <summary>
/// Console-side health wiring on the OUTER Generic-Host container: the <see cref="IStartupGate"/>
/// singleton, the outer <c>AddHealthChecks</c> chain (self/live + startup), the Phase-5
/// <see cref="StartupCompletionService"/> (flips the gate on host start), and the embedded
/// <see cref="EmbeddedHealthEndpointService"/> minimal-Kestrel listener (D-04).
///
/// <para>
/// <b>No database.</b> Unlike the API base library analog, there is NO <c>database health probe</c> here — the
/// console has no database. The bus readiness check is auto-registered by <c>AddBaseConsoleMessaging</c>
/// (tagged <c>"ready","masstransit"</c>) in the OUTER container.
/// </para>
///
/// <para>
/// <b>Two containers, deliberately (D-05).</b> The outer <see cref="StartupHealthCheck"/> is tagged
/// <c>"startup","ready"</c> here, mirroring the API base library's outer registration. The embedded
/// listener built by <see cref="EmbeddedHealthEndpointService"/> runs its OWN DI container where
/// <c>/health/ready</c> is the bus check and <c>/health/startup</c> is the gate check — the inner and
/// outer registrations are intentionally distinct.
/// </para>
/// </summary>
public static class ConsoleHealthServiceCollectionExtensions
{
    public static IServiceCollection AddBaseConsoleHealth(
        this IServiceCollection services, IConfiguration cfg)
    {
        services.AddSingleton<IStartupGate, StartupGate>();
        services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
            .AddCheck<StartupHealthCheck>("startup", tags: new[] { "startup", "ready" });
        // No database health probe — the console has no database. The bus 'ready' check is auto-registered by
        // AddBaseConsoleMessaging in the outer container.

        services.AddHostedService<StartupCompletionService>();      // Phase-5 MarkReady on StartAsync
        services.AddHostedService<EmbeddedHealthEndpointService>();  // independent inner Kestrel (D-04)
        return services;
    }
}
