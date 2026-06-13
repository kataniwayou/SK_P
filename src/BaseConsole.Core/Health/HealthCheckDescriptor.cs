using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BaseConsole.Core.Health;

/// <summary>
/// Generic seam (D-05) for a console to surface an extra inner-listener health check that needs OUTER
/// host state, WITHOUT BaseConsole.Core referencing the check's concrete type. Registered in the OUTER
/// container via services.AddSingleton(new HealthCheckDescriptor(...)); EmbeddedHealthEndpointService
/// enumerates them and folds each into the inner Kestrel container, bridging the OUTER provider via Factory.
/// Mirrors how BusReadyHealthCheck(_outer) reaches outer state — generalized to a collection.
/// </summary>
public sealed record HealthCheckDescriptor(
    string Name,
    string[] Tags,
    Func<IServiceProvider, IHealthCheck> Factory);
