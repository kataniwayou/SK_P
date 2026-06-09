using BaseApi.Tests.Console;
using BaseConsole.Core.DependencyInjection;
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
/// the <c>RetryOptions</c>/<c>ProbeOptions</c> bindings + the <c>L2ProbeRecovery</c> helper registration
/// (mirrors Keeper's Program.cs). After the Phase-48 v3.x teardown (RETIRE-03) the reactive Fault&lt;T&gt;
/// consumers no longer exist, so the messaging seam registers no consumers — KeeperHostBootTests only asserts
/// <c>IBusControl</c> resolves, which the no-reactive-consumers form satisfies.
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
        builder.Services.Configure<RetryOptions>(builder.Configuration.GetSection("Retry"));
        // Mirror Program.cs — bind ProbeOptions + register the BIT-probe helper (IConnectionMultiplexer is
        // already a singleton via AddBaseConsole).
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Probe:DelaySeconds"] = "5",
            ["Probe:MaxAttempts"]  = "12",
        });
        builder.Services.Configure<global::Keeper.ProbeOptions>(builder.Configuration.GetSection("Probe"));
        builder.Services.AddSingleton<global::Keeper.Recovery.L2ProbeRecovery>();
        builder.Services.AddBaseConsoleMessaging(builder.Configuration, x => { });
    }
}
