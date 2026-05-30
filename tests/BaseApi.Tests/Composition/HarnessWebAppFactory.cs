using MassTransit;
using MassTransit.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
/// We therefore use the documented MANUAL FALLBACK. The original 20-01 fallback removed only the
/// four bus <i>service</i> types (IBusControl/IBus/IPublishEndpoint/ISendEndpointProvider) + the
/// MassTransit IHostedService — but that left the rest of the AddMassTransit registration graph
/// (<c>IBusInstance</c>, <c>IBusDepot</c>, the bus health-check <c>IConfigureOptions</c>, …) in
/// place. <c>AddMassTransitTestHarness()</c> then added a SECOND bus, producing two failures the
/// 20-01 harness never hit because it was wired into no test class: (1) a duplicate
/// <c>masstransit-bus</c> health-check registration → <c>ArgumentException</c> at MapHealthChecks,
/// and (2) two <c>IBusInstance</c>s → <c>BusDepot</c> "same key MassTransit.IBus". The robust fix
/// is a COMPLETE strip: remove every descriptor that belongs to the MassTransit assembly (by
/// namespace on ServiceType / ImplementationType / ImplementationInstance) before re-adding the
/// test harness, so the in-memory bus is the only MassTransit registration in the container.
/// </para>
/// </summary>
public sealed class HarnessWebAppFactory : Phase8WebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            // A1 manual fallback (RemoveMassTransit() not in 8.5.5): COMPLETE strip of the real bus.
            // Remove every descriptor owned by the MassTransit assembly — service type, concrete
            // implementation type, or singleton instance — so AddMassTransitTestHarness() below is
            // the sole bus registration (no duplicate IBusInstance/health-check). The WebApi side is
            // publish-only (no consumers), so a clean-slate in-memory harness needs nothing carried
            // over from the real AddMassTransit graph.
            static bool IsMassTransit(Type? t) =>
                t?.Namespace?.StartsWith("MassTransit", StringComparison.Ordinal) == true;

            var toRemove = services
                .Where(d => IsMassTransit(d.ServiceType)
                            || IsMassTransit(d.ImplementationType)
                            || IsMassTransit(d.ImplementationInstance?.GetType()))
                .ToList();
            foreach (var d in toRemove)
            {
                services.Remove(d);
            }

            services.AddMassTransitTestHarness();  // in-memory transport; registers IPublishEndpoint + ITestHarness

            // Defense-in-depth: MassTransit registers its bus health check via an IConfigureOptions
            // delegate whose ImplementationType is null (factory) and so can escape the namespace
            // strip above. If a duplicate "masstransit-bus" registration survives, dedup by name
            // (keep last, preserve first-seen order) so DefaultHealthCheckService.ValidateRegistrations
            // does not throw at MapHealthChecks.
            services.Configure<HealthCheckServiceOptions>(options =>
            {
                var lastByName = new Dictionary<string, HealthCheckRegistration>();
                var order = new List<string>();
                foreach (var reg in options.Registrations)
                {
                    if (!lastByName.ContainsKey(reg.Name)) order.Add(reg.Name);
                    lastByName[reg.Name] = reg;
                }
                if (order.Count != options.Registrations.Count)
                {
                    options.Registrations.Clear();
                    foreach (var name in order) options.Registrations.Add(lastByName[name]);
                }
            });
        });
    }
}
