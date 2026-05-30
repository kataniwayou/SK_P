using BaseConsole.Core.Configuration;
using BaseConsole.Core.Messaging;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BaseConsole.Core.DependencyInjection;

/// <summary>
/// MassTransit bus skeleton (CONSOLE-04). Registers the RabbitMQ bus, the Singleton ambient
/// <see cref="ICorrelationAccessor"/>, and all three correlation filters bus-wide (the inbound
/// consume filter + both outbound send/publish filters). The concrete console supplies only the
/// consumer-registration lambda (D-06 — base = infra, concrete = consumers; empty this phase,
/// Phase 19 adds consumers).
/// </summary>
public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Wires the bus, the accessor, and the correlation pipeline. RabbitMQ host/credentials are
    /// read via the <c>cfg.Require</c> fail-fast helper (T-18-05: the missing-key exception names
    /// the key, never the value). The auto-registered bus health check keeps its default tags
    /// <c>["ready","masstransit"]</c> (CONSOLE-HEALTH-03) — the bus-check tag-override hook is
    /// deliberately NOT called (custom tags would REPLACE the defaults).
    /// </summary>
    public static IServiceCollection AddBaseConsoleMessaging(
        this IServiceCollection services, IConfiguration cfg,
        Action<IBusRegistrationConfigurator> configureConsumers)
    {
        var rabbitHost = cfg.Require("RabbitMq:Host");
        var rabbitUser = cfg.Require("RabbitMq:Username");
        var rabbitPass = cfg.Require("RabbitMq:Password");

        services.AddSingleton<ICorrelationAccessor, AsyncLocalCorrelationAccessor>();

        services.AddMassTransit(x =>
        {
            configureConsumers(x);   // concrete seam — EMPTY this phase (Phase 19 adds consumers)
            x.UsingRabbitMq((ctx, c) =>
            {
                c.Host(rabbitHost, h => { h.Username(rabbitUser); h.Password(rabbitPass); });
                c.UseConsumeFilter(typeof(InboundCorrelationConsumeFilter<>), ctx);   // CORR-01 bus-wide (open-generic)
                c.UseSendFilter(typeof(OutboundCorrelationSendFilter<>), ctx);        // CORR-02
                c.UsePublishFilter(typeof(OutboundCorrelationPublishFilter<>), ctx);  // CORR-02
                c.ConfigureEndpoints(ctx);
            });
        });

        return services;
    }
}
