using MassTransit;
using MassTransit.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BaseApi.Tests.Composition;

/// <summary>
/// D-01: the in-memory MassTransit harness is the DEFAULT test transport for HTTP integration
/// facts that publish. Program.cs already calls AddBaseApiMessaging → AddMassTransit, so we must
/// remove the real bus first (ConfigureTestServices runs AFTER the app's own ConfigureServices),
/// then AddMassTransitTestHarness(). The harness IPublishEndpoint satisfies OrchestrationService's
/// ctor injection → /start|/stop return 204 in-process (kills the ~1m40s publish-timeout hangs).
/// <para>
/// A1 resolution: <c>services.RemoveMassTransit()</c> is NOT part of MassTransit 8.5.5's public
/// IServiceCollection surface (verified — CS1061 at compile, no such symbol in MassTransit.dll).
/// We therefore use the documented MANUAL FALLBACK: <see cref="ServiceCollectionDescriptorExtensions.RemoveAll"/>
/// the bus service types (<see cref="IBusControl"/>, <see cref="IBus"/>, <see cref="IPublishEndpoint"/>,
/// <see cref="ISendEndpointProvider"/>) PLUS the MassTransit bus <see cref="IHostedService"/>
/// descriptor (the <c>MassTransitHostedService</c> registered by AddMassTransit) before calling
/// AddMassTransitTestHarness(). Removing the hosted service prevents the real RabbitMQ bus from
/// starting alongside the in-memory harness.
/// </para>
/// </summary>
public sealed class HarnessWebAppFactory : Phase8WebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            // A1 manual fallback (RemoveMassTransit() not in 8.5.5): strip the real bus registrations.
            // MassTransit ships its own RemoveAll<T> overload, so call the Microsoft DI extension
            // by its static class to disambiguate (CS0305 otherwise).
            var busTypes = new[]
            {
                typeof(IBusControl), typeof(IBus),
                typeof(IPublishEndpoint), typeof(ISendEndpointProvider),
            };
            var toRemove = services
                .Where(d => busTypes.Contains(d.ServiceType)
                            || (d.ServiceType == typeof(IHostedService)
                                && d.ImplementationType?.FullName?.StartsWith("MassTransit.") == true))
                .ToList();
            foreach (var d in toRemove)
            {
                services.Remove(d);
            }

            services.AddMassTransitTestHarness();  // in-memory transport; registers IPublishEndpoint + ITestHarness
        });
    }
}
