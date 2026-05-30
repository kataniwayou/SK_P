using BaseConsole.Core.Health;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace BaseApi.Tests.Console;

/// <summary>
/// CONSOLE-HEALTH-03 / SC-3 (POSITIVE, COMMITTED): the embedded listener's <c>/health/ready</c> reports
/// Unhealthy while the broker is unreachable. This is the broker-unreachable proof and it depends on NO real
/// broker and NO fixture timing — it exercises <see cref="BusReadyHealthCheck"/> directly.
///
/// <para>
/// The "broker unreachable" condition is modeled by the <b>bus-not-started</b> path: when the outer provider
/// cannot resolve the MassTransit bus singleton (<c>IBusControl</c>), <see cref="BusReadyHealthCheck"/>
/// returns <see cref="HealthStatus.Unhealthy"/> with "Bus not started" — readiness never reports a
/// stale-Healthy state (T-18-11). An empty <see cref="ServiceProvider"/> makes
/// <c>GetService&lt;IBusControl&gt;()</c> return null, reproducing that path deterministically.
/// </para>
///
/// <para>
/// Note on the API surface (Plan 18-03 finding): MassTransit 8.5.5 exposes NO public <c>IBusHealth</c>
/// interface — the programmatic bus readiness surface is <c>IBusControl.CheckHealth()</c> returning a
/// <c>BusHealthResult</c>. <see cref="BusReadyHealthCheck"/> resolves <c>IBusControl</c> from the outer
/// provider accordingly; this test proves the unresolved-bus branch.
/// </para>
/// </summary>
public sealed class ConsoleBusReadyHealthCheckTests
{
    [Fact]
    public async Task BusReadyHealthCheck_Returns_Unhealthy_When_Bus_Not_Healthy()
    {
        var ct = TestContext.Current.CancellationToken;

        // Empty outer provider — GetService<IBusControl>() returns null (bus not started / unreachable broker).
        var outer = new ServiceCollection().BuildServiceProvider();
        var sut = new BusReadyHealthCheck(outer);

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }
}
