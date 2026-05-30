using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BaseApi.Tests.Console;

/// <summary>
/// CONSOLE-01/03/04/05 boot path: the <see cref="ConsoleTestHostFixture"/> composes the full three-call
/// AddBaseConsole* seam in-memory with DEAD Redis + unreachable RabbitMQ, and the host builds and starts
/// without throwing. <c>IBusControl</c> (the MassTransit-registered bus) is resolvable — the bus is wired
/// even though no live broker exists this phase (the in-memory transport / harness is exercised separately
/// in the correlation-filter tests).
/// </summary>
public sealed class ConsoleHostBootTests : IClassFixture<ConsoleTestHostFixture>
{
    private readonly ConsoleTestHostFixture _fixture;

    public ConsoleHostBootTests(ConsoleTestHostFixture fixture) => _fixture = fixture;

    [Fact]
    public void Host_Boots_With_Dead_Deps_And_Bus_Is_Resolvable()
    {
        // The fixture's InitializeAsync already called Host.StartAsync() against dead Redis/RabbitMQ
        // without throwing — reaching this assertion is itself the soft-dep boot-resilience proof.
        Assert.NotNull(_fixture.Host);

        // CONSOLE-04: the bus skeleton is registered. IBusControl is the MassTransit Singleton that
        // BusReadyHealthCheck reads for /health/ready (IBusHealth does not exist in 8.5.5 — Plan 18-03).
        var bus = _fixture.Host.Services.GetService<IBusControl>();
        Assert.NotNull(bus);
    }
}
