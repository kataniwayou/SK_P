using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BaseConsole.Core.Health;

/// <summary>
/// <see cref="IHealthCheck"/> that reads <see cref="IStartupGate.IsReady"/>.
///
/// <para>
/// <b>Registration:</b> tagged <c>"startup"</c> + <c>"ready"</c> so it appears in BOTH
/// <c>/health/startup</c> AND <c>/health/ready</c>. The <c>"self"</c> always-Healthy check is
/// the ONLY check tagged <c>"live"</c> — liveness MUST stay independent of startup/readiness.
/// </para>
///
/// <para>
/// The console has no DB, so the Unhealthy message carries no migration-related suffix.
/// </para>
/// </summary>
public sealed class StartupHealthCheck(IStartupGate gate) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult(gate.IsReady
            ? HealthCheckResult.Healthy("Startup complete")
            : HealthCheckResult.Unhealthy("Startup not complete"));
}
