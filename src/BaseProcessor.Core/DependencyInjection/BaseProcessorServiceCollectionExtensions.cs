using BaseConsole.Core.DependencyInjection;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Startup;
using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BaseProcessor.Core.DependencyInjection;

/// <summary>
/// The processor composition root (BPC-03). A single <c>AddBaseProcessor(cfg)</c> call folds the
/// BaseConsole.Core infra stack (<c>AddBaseConsole</c> — Redis soft-dep + embedded health) and the
/// bus skeleton (<c>AddBaseConsoleMessaging</c> — RabbitMQ + the three correlation filters), then
/// layers the processor's own runtime brain on top:
/// <list type="bullet">
///   <item>the two <c>IRequestClient</c>s targeting the WebApi responder endpoints on the
///   <c>exchange:{name}</c> scheme (RPC-04) — registered in the <c>configureConsumers</c> lambda;</item>
///   <item><see cref="ProcessorLivenessOptions"/> bound from the <c>"Processor"</c> section (CONFIG-01);</item>
///   <item><c>TimeProvider.System</c> (idempotent), <see cref="ISourceHashProvider"/>,
///   <see cref="IProcessorContext"/>, and the <see cref="ProcessorStartupOrchestrator"/> hosted service;</item>
///   <item>and the REMOVAL of the base library's <c>StartupCompletionService</c> so <c>MarkReady</c>
///   fires when the processor reaches Healthy (identity + definitions resolved), NOT at bare host
///   start (D-02 — mirrors <c>Orchestrator/Program.cs</c>).</item>
/// </list>
///
/// <para>
/// <b>NO dispatch consumer this phase.</b> The <c>EntryStepDispatch</c> consumer + the
/// <c>ProcessorLivenessHeartbeat</c> hosted service are Phase 27 / Plan 03 — they are NOT registered
/// here (no consumer is registered, so no receive endpoints are auto-bound this phase).
/// </para>
///
/// <para>
/// Observability (metrics-only OTel) stays a separate <c>AddBaseConsoleObservability</c> call on
/// <c>IHostApplicationBuilder</c> in the concrete <c>Program.cs</c> (it needs <c>ILoggingBuilder</c>),
/// mirroring the BaseConsole three-call seam.
/// </para>
/// </summary>
public static class BaseProcessorServiceCollectionExtensions
{
    public static IServiceCollection AddBaseProcessor(this IServiceCollection services, IConfiguration cfg)
    {
        // 1. BaseConsole infra: Redis soft-dep multiplexer + embedded minimal-Kestrel health surface.
        services.AddBaseConsole(cfg);

        // 2. Bus skeleton + the two request clients (RPC-04). The clients go in the configureConsumers
        //    lambda; they target exchange:{ProcessorQueues.name} so the request routes to the WebApi's
        //    named ReceiveEndpoint (confirmed Wave 0 — 26-01-SUMMARY). NO consumer is registered this
        //    phase: the dispatch consumer is Phase 27, so no receive endpoints are auto-bound here.
        services.AddBaseConsoleMessaging(cfg,
            x =>
            {
                x.AddRequestClient<GetProcessorBySourceHash>(new Uri("exchange:" + ProcessorQueues.IdentityQuery));
                x.AddRequestClient<GetSchemaDefinition>(new Uri("exchange:" + ProcessorQueues.SchemaQuery));
            });

        // 3. Liveness/heartbeat knobs (CONFIG-01) — four independent seconds-ints from the "Processor" section.
        services.Configure<ProcessorLivenessOptions>(cfg.GetSection("Processor"));

        // 4. TimeProvider for the backoff/retry clock (idempotent — the base library may not register it;
        //    verbatim Orchestrator/Program.cs:59).
        services.TryAddSingleton(TimeProvider.System);

        // 5. SourceHash seam (IDENT-03) — reflection over the assembly metadata attribute, fail-fast when absent.
        services.AddSingleton<ISourceHashProvider, AssemblyMetadataSourceHashProvider>();

        // 6. The mutable identity/Healthy holder shared by the orchestrator (writer) and the Phase 03
        //    heartbeat (reader).
        services.AddSingleton<IProcessorContext, ProcessorContext>();

        // 7. The two-loop startup orchestrator (identity-by-SourceHash + per-non-null-schema definition).
        services.AddHostedService<ProcessorStartupOrchestrator>();

        // 8. D-02: remove the base library's StartupCompletionService so MarkReady fires when the
        //    processor reaches Healthy (orchestrator completion), NOT at bare host start. The removal
        //    operates on IServiceCollection so it belongs HERE in the composition root (keeps the
        //    concrete Program.cs minimal — BPC-03). IStartupGate / the self/startup checks stay intact.
        //    COPIED VERBATIM from Orchestrator/Program.cs:63-68 (adapted to `services`).
        foreach (var d in services
                     .Where(d => d.ImplementationType == typeof(BaseConsole.Core.Health.StartupCompletionService))
                     .ToList())
        {
            services.Remove(d);
        }

        return services;
    }
}
