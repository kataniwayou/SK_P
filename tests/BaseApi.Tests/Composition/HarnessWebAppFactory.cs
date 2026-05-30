using MassTransit;
using MassTransit.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BaseApi.Tests.Composition;

/// <summary>
/// D-01: the in-memory MassTransit harness is the DEFAULT test transport for HTTP integration
/// facts that publish. Program.cs already calls AddBaseApiMessaging → AddMassTransit, so we must
/// RemoveMassTransit() first (ConfigureTestServices runs AFTER the app's own ConfigureServices),
/// then AddMassTransitTestHarness(). The harness IPublishEndpoint satisfies OrchestrationService's
/// ctor injection → /start|/stop return 204 in-process (kills the ~1m40s publish-timeout hangs).
/// <para>
/// A1 resolution: <c>services.RemoveMassTransit()</c> is part of MassTransit 8.5.5's public
/// IServiceCollection surface and compiles against this pin — the manual RemoveAll fallback was
/// NOT required.
/// </para>
/// </summary>
public sealed class HarnessWebAppFactory : Phase8WebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveMassTransit();          // A1: exposed in 8.5.5 — removes the real bus
            services.AddMassTransitTestHarness();  // in-memory transport; registers IPublishEndpoint + ITestHarness
        });
    }
}
