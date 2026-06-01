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
    /// Wires the RabbitMQ bus. Host/port/credentials are read via configuration:
    /// host/username/password use the <c>cfg.Require</c> fail-fast helper (the missing-key
    /// exception names the key, never the value); <c>RabbitMq:Port</c> is OPTIONAL and defaults
    /// to 5672 so the compose-internal <c>rabbitmq:5672</c> path used by running containers is
    /// byte-unaffected. The in-process TEST WebApi overrides <c>RabbitMq__Port=5673</c> to reach
    /// the host-mapped broker port (TEST-RMQ-02 test-enablement, not new runtime behavior).
    /// <para>
    /// <b>Two optional seams (Phase 25 D-05/D-06 — RPC-01/02/03):</b> both default <c>null</c>, so
    /// a call with no hooks is BYTE-EQUIVALENT to the Phase-19 publish-only join (no consumers, no
    /// receive endpoints — the WebApi stays a pure publisher). When supplied:
    /// <list type="bullet">
    ///   <item><paramref name="configureConsumers"/> is the <c>AddConsumer&lt;T&gt;()</c> seam,
    ///   invoked on the <see cref="IBusRegistrationConfigurator"/> AFTER the Degraded health block.</item>
    ///   <item><paramref name="configureEndpoints"/> is the explicit-<c>ReceiveEndpoint</c> seam,
    ///   invoked inside the <c>UsingRabbitMq</c> closure (the only scope where the
    ///   <see cref="IBusRegistrationContext"/> needed by <c>ConfigureConsumer&lt;T&gt;(context)</c>
    ///   exists — Pitfall 1: a single consumer hook is insufficient).</item>
    /// </list>
    /// This deliberately does NOT call <c>busCfg.ConfigureEndpoints(context)</c> (D-06 anti-pattern):
    /// the Service supplies explicit named endpoints on <c>ProcessorQueues.*</c>, never MassTransit
    /// auto-naming. <b>Firewall (CONTRACT-01/D-05):</b> the hooks are typed in MassTransit interfaces
    /// only — Core never names a concrete consumer type, so <c>BaseApi.Core</c> still references
    /// <c>Messaging.Contracts</c> + MassTransit ONLY (no <c>BaseApi.Service</c>/<c>BaseConsole.Core</c>
    /// reference). The <c>MinimalFailureStatus = Degraded</c> block is untouched (MSG-WEBAPI-04).
    /// </para>
    /// </summary>
    public static IServiceCollection AddBaseApiMessaging(
        this IServiceCollection services, IConfiguration cfg,
        Action<IBusRegistrationConfigurator>? configureConsumers = null,                              // D-05: AddConsumer<T> seam
        Action<IBusRegistrationContext, IRabbitMqBusFactoryConfigurator>? configureEndpoints = null)  // D-06: ReceiveEndpoint seam
    {
        var host = cfg.Require("RabbitMq:Host");
        var port = cfg.GetValue<ushort>("RabbitMq:Port", 5672);
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

            configureConsumers?.Invoke(bus);   // D-05 seam — null default = publish-only (no consumers).

            bus.UsingRabbitMq((context, busCfg) =>
            {
                busCfg.Host(host, port, "/", h => { h.Username(user); h.Password(pass); });
                configureEndpoints?.Invoke(context, busCfg);   // D-06 seam — explicit ReceiveEndpoints supplied by the
                                                               // Service; NO ConfigureEndpoints(context) auto-naming.
                // Default (both hooks null): publish-only — NO ConfigureEndpoints, NO consumers,
                // NO correlation filters (D-02), byte-equivalent to the Phase-19 join.
            });
        });

        return services;
    }
}
