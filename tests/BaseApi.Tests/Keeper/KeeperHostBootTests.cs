using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// KEEP-01 — the Keeper host boots the three-call AddBaseConsole* seam (+ the placeholder consumer)
/// against DEAD Redis + unreachable RabbitMQ without throwing, and <c>IBusControl</c> is resolvable.
/// Reaching the assertion is itself the soft-dep boot-resilience proof (the fixture's InitializeAsync
/// already ran <c>Host.StartAsync</c>, flipping readiness via the kept default readiness service — D-06).
/// </summary>
public sealed class KeeperHostBootTests : IClassFixture<KeeperHostBootFixture>
{
    private readonly KeeperHostBootFixture _fixture;

    public KeeperHostBootTests(KeeperHostBootFixture fixture) => _fixture = fixture;

    [Fact]
    public void Host_Boots_With_Dead_Deps_And_Bus_Is_Resolvable()
    {
        Assert.NotNull(_fixture.Host);

        // The MassTransit-registered bus is wired even with no live broker this phase.
        var bus = _fixture.Host.Services.GetService<IBusControl>();
        Assert.NotNull(bus);
    }
}
