using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BaseConsole.Core.Health;

/// <summary>
/// Inner-listener <see cref="IHealthCheck"/> that surfaces the OUTER host's MassTransit bus state so
/// the embedded <c>/health/ready</c> probe reflects real bus readiness (CONSOLE-HEALTH-03).
///
/// <para>
/// <b>Open-Q 1 resolution.</b> The embedded health listener (<c>EmbeddedHealthEndpointService</c>)
/// runs its own minimal-Kestrel DI container, so it cannot see the outer host's auto-registered bus
/// readiness check directly. This check is registered in the INNER container with the OUTER
/// <see cref="IServiceProvider"/> injected; at check time it resolves the outer bus and reads its
/// health programmatically, then maps the result onto the inner <c>/health/ready</c> probe.
/// </para>
///
/// <para>
/// <b>Programmatic bus health surface (build-confirmed against MassTransit 8.5.5):</b> the bus is read
/// via <see cref="IBusControl.CheckHealth()"/>, which returns a <see cref="BusHealthResult"/> carrying a
/// <see cref="BusHealthStatus"/> (<c>Healthy</c> / <c>Degraded</c> / <c>Unhealthy</c>) and a description.
/// This mirrors the data the framework's own auto-registered bus check reports — no hand-rolled latch.
/// </para>
///
/// <para>
/// If the bus has not been built/registered yet (e.g. the host is still starting and the singleton is
/// not resolvable), the check returns an Unhealthy result with a "Bus not started" message so
/// readiness never reports a stale-Healthy state (T-18-11).
/// </para>
///
/// <para>
/// This check answers ONLY bus readiness — it never touches Redis. Liveness stays self-only elsewhere.
/// </para>
/// </summary>
public sealed class BusReadyHealthCheck : IHealthCheck
{
    private readonly IServiceProvider _outer;

    /// <param name="outer">
    /// The OUTER host's service provider. The bus singleton is resolved from here at check time (it lives
    /// in the outer container, not the inner listener container).
    /// </param>
    public BusReadyHealthCheck(IServiceProvider outer) => _outer = outer;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // Resolve the bus from the OUTER provider. IBusControl is the programmatic readiness surface
        // exposing CheckHealth(); it is registered as a Singleton by AddMassTransit. Not-yet-built bus
        // (null) => Unhealthy, so readiness never reports a false-positive (T-18-11).
        var bus = _outer.GetService<IBusControl>();
        if (bus is null)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Bus not started"));
        }

        var result = bus.CheckHealth();
        var health = result.Status switch
        {
            BusHealthStatus.Healthy => HealthCheckResult.Healthy(result.Description),
            // Degraded and Unhealthy both fail readiness — a degraded bus must not receive traffic.
            _ => HealthCheckResult.Unhealthy(result.Description, result.Exception),
        };

        return Task.FromResult(health);
    }
}
