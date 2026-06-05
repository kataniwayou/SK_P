using BaseApi.Tests.Console;
using BaseConsole.Core.DependencyInjection;
using Keeper.Consumers;
using MassTransit;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// KEEP-01 boot fixture — subclasses <see cref="ConsoleTestHostFixture"/> (reusing its free-port pick,
/// DEAD-Redis/unreachable-RabbitMQ in-memory config, and <c>IAsyncLifetime</c> Start/Stop) and OVERRIDES
/// <c>ConfigureBuilder</c> to compose Keeper's exact runtime seam: the three AddBaseConsole* calls PLUS
/// the placeholder consumer registration + the <c>RetryOptions</c> binding (mirrors Keeper's Program.cs).
/// <para>
/// Boots against dead Redis + unreachable RabbitMQ; the kept default readiness service flips readiness on
/// <c>Host.StartAsync</c> (D-06, Keeper has no hydration). <c>IBusControl</c> stays resolvable.
/// </para>
/// </summary>
public sealed class KeeperHostBootFixture : ConsoleTestHostFixture
{
    protected override void ConfigureBuilder(IHostApplicationBuilder builder)
    {
        builder.AddBaseConsoleObservability(builder.Configuration);
        builder.Services.AddBaseConsole(builder.Configuration);
        // IN-01: inject an explicit "Retry" section mirroring the live appsettings.json (Limit=3,
        // Strategy=Immediate) so the bind below is exercised against a REAL config value instead of
        // silently falling back to RetryOptions' property-initializer defaults. This keeps the boot
        // fixture honest: if RetryOptions later gains a required/validated key, the section is present.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Retry:Limit"]    = "3",
            ["Retry:Strategy"] = "Immediate",
        });
        // Bind RetryOptions so PlaceholderConsumerDefinition's IOptions<RetryOptions> ctor resolves.
        builder.Services.Configure<RetryOptions>(builder.Configuration.GetSection("Retry"));
        builder.Services.AddBaseConsoleMessaging(builder.Configuration,
            x => x.AddConsumer<PlaceholderConsumer, PlaceholderConsumerDefinition>());
    }
}
