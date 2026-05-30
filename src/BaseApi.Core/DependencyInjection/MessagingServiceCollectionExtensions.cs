using BaseApi.Core.Configuration;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BaseApi.Core.DependencyInjection;

/// <summary>
/// Phase 19 publish-only MassTransit join (MSG-WEBAPI-01). Registers <c>AddMassTransit</c> +
/// <c>UsingRabbitMq</c> so the WebApi can <c>IPublishEndpoint.Publish</c> the
/// <c>StartOrchestration</c>/<c>StopOrchestration</c> control records onto the bus.
/// <para>
/// <b>Publish-only — deliberately absent (D-02 / D-03):</b> NO <c>AddConsumer</c>, NO
/// <c>ConfigureEndpoints</c> (no receive endpoints), NO correlation consume/send/publish filters,
/// NO ambient correlation accessor. The WebApi is a pure publisher; correlation is set on the
/// message BODY at the call site (<c>OrchestrationService</c>), never via an outbound filter.
/// </para>
/// <para>
/// <b>Dependency firewall (MSG-WEBAPI-01 / T-19-dep-firewall):</b> this lives in
/// <c>BaseApi.Core</c> and references <c>Messaging.Contracts</c> only — NEVER
/// <c>BaseConsole.Core</c>.
/// </para>
/// <para>
/// <b>Bus health capped at Degraded (MSG-WEBAPI-04 / D-05 / T-19-broker-down):</b> the
/// auto-registered MassTransit bus health check is capped at
/// <see cref="HealthStatus.Degraded"/> via <c>MinimalFailureStatus</c> so a broker-down
/// condition never flips the CRUD <c>/health/ready</c> probe to 503 (broker is a hard dep for the
/// Start/Stop path ONLY). The bus-check <c>Tags</c> are left at their defaults
/// <c>["ready","masstransit"]</c> — overriding them REPLACES the defaults (Pitfall 7), so the hook
/// is deliberately NOT called.
/// </para>
/// </summary>
public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Wires the publish-only RabbitMQ bus. Host/credentials are read via the <c>cfg.Require</c>
    /// fail-fast helper (the missing-key exception names the key, never the value).
    /// </summary>
    public static IServiceCollection AddBaseApiMessaging(
        this IServiceCollection services, IConfiguration cfg)
    {
        var host = cfg.Require("RabbitMq:Host");
        var user = cfg.Require("RabbitMq:Username");
        var pass = cfg.Require("RabbitMq:Password");

        services.AddMassTransit(bus =>
        {
            bus.ConfigureHealthCheckOptions(o =>
            {
                // MSG-WEBAPI-04 / D-05: never escalate the bus check past Degraded so a
                // broker-down condition keeps CRUD /health/ready at 200. DO NOT touch o.Tags —
                // overriding REPLACES the defaults ["ready","masstransit"] (Pitfall 7).
                o.MinimalFailureStatus = HealthStatus.Degraded;
            });

            bus.UsingRabbitMq((context, busCfg) =>
            {
                busCfg.Host(host, h => { h.Username(user); h.Password(pass); });
                // Publish-only — NO ConfigureEndpoints, NO consumers, NO correlation filters (D-02).
            });
        });

        return services;
    }
}
