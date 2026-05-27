using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BaseApi.Core.Health;

/// <summary>
/// <see cref="IHealthCheck"/> that reads <see cref="IStartupGate.IsReady"/>.
///
/// <para>
/// <b>Registration (D-03):</b> tagged <c>"startup"</c> + <c>"ready"</c> so it appears in
/// BOTH <c>/health/startup</c> (predicate <c>c.Tags.Contains("startup")</c>) AND
/// <c>/health/ready</c> (predicate <c>c.Tags.Contains("ready")</c>). The
/// <c>"self"</c> always-Healthy check is the ONLY check tagged <c>"live"</c> —
/// liveness MUST NOT check the DB (Pitfall 15).
/// </para>
///
/// <para>
/// <b>Access modifier (deviation from CONTEXT D-02 wording):</b> <c>public sealed</c>
/// — see remarks on <see cref="StartupGate"/>.
/// </para>
/// </summary>
public sealed class StartupHealthCheck(IStartupGate gate) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult(gate.IsReady
            ? HealthCheckResult.Healthy("Startup complete")
            : HealthCheckResult.Unhealthy("Startup not complete (migrations pending)"));
}
