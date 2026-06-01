using BaseApi.Core.DependencyInjection;
using BaseApi.Service.Features.Processor.Responders;
using BaseApi.Service.Features.Schema.Responders;
using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BaseApi.Service.Composition;

/// <summary>
/// Phase 25 (RPC-01/02/03) — Service-owned wrapper that supplies the two responder hooks to the
/// Core <c>AddBaseApiMessaging</c> join, keeping <c>Program.cs</c> thin (SC#3 ≤10 body lines) by
/// holding the consumer + explicit-endpoint lambdas here (mirrors the <see cref="AppFeatures"/>
/// aggregator). The Core extension stays firewall-clean: it never names a concrete consumer type —
/// these <c>BaseApi.Service</c> consumer types are bound only at this call site.
/// <list type="bullet">
///   <item><b>configureConsumers (D-05):</b> registers the two dual-response consumers.</item>
///   <item><b>configureEndpoints (D-06):</b> binds them on explicit named <c>ProcessorQueues.*</c>
///   <c>ReceiveEndpoint</c>s — NO <c>ConfigureEndpoints(context)</c> auto-naming.</item>
/// </list>
/// The Degraded health cap + publish path live inside <c>AddBaseApiMessaging</c> (MSG-WEBAPI-04,
/// untouched). <c>ProcessorService</c>/<c>SchemaService</c> are DI-registered by
/// <see cref="AppFeatures.AddAppFeatures"/>, so the consumers resolve them by ctor injection.
/// </summary>
internal static class ResponderMessaging
{
    public static IServiceCollection AddBaseApiResponderMessaging(
        this IServiceCollection services, IConfiguration cfg)
        => services.AddBaseApiMessaging(cfg,
            configureConsumers: x =>
            {
                x.AddConsumer<GetProcessorBySourceHashConsumer>();
                x.AddConsumer<GetSchemaDefinitionConsumer>();
            },
            configureEndpoints: (context, busCfg) =>
            {
                busCfg.ReceiveEndpoint(ProcessorQueues.IdentityQuery,
                    e => e.ConfigureConsumer<GetProcessorBySourceHashConsumer>(context));
                busCfg.ReceiveEndpoint(ProcessorQueues.SchemaQuery,
                    e => e.ConfigureConsumer<GetSchemaDefinitionConsumer>(context));
            });
}
