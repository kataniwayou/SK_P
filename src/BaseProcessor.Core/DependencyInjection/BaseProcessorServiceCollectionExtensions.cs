using BaseConsole.Core.DependencyInjection;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Liveness;
using BaseProcessor.Core.Observability;
using BaseProcessor.Core.Processing;
using BaseProcessor.Core.Startup;
using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;

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
/// <b>Dispatch consumer (Phase 27 / EXEC-01).</b> The <see cref="EntryStepDispatchConsumer"/> IS
/// registered here for DI (so the runtime <c>ConnectReceiveEndpoint</c> can <c>ConfigureConsumer&lt;T&gt;</c>
/// at bind time), but it is EXCLUDED from the unconditional <c>ConfigureEndpoints(ctx)</c> inside
/// <c>AddBaseConsoleMessaging</c> via <c>.ExcludeFromConfigureEndpoints()</c> — otherwise a wrong-named
/// kebab <c>entry-step-dispatch</c> queue would be auto-bound at bus start (Pitfall 1). The correct
/// durable <c>{id:D}</c> endpoint is bound at runtime by <see cref="ProcessorStartupOrchestrator"/>,
/// AFTER identity resolves and BEFORE <c>MarkHealthy</c> (D-01/D-02/D-03). The
/// <see cref="ProcessorLivenessHeartbeat"/> hosted service IS registered here (step 7b).
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
        //    named ReceiveEndpoint (confirmed Wave 0 — 26-01-SUMMARY). The EntryStepDispatch consumer
        //    is registered here for DI but EXCLUDED from auto-endpoint config (the {id:D} endpoint is
        //    bound at runtime by ProcessorStartupOrchestrator — Phase 27 / EXEC-01). The
        //    ProcessorLivenessHeartbeat hosted service IS registered this phase — step 7b.
        services.AddBaseConsoleMessaging(cfg,
            x =>
            {
                x.AddRequestClient<GetProcessorBySourceHash>(new Uri("exchange:" + ProcessorQueues.IdentityQuery));
                x.AddRequestClient<GetSchemaDefinition>(new Uri("exchange:" + ProcessorQueues.SchemaQuery));

                // EXEC-01 / D-01: register the dispatch consumer for DI (so the runtime
                // ConnectReceiveEndpoint can ConfigureConsumer<T> at bind time) but EXCLUDE it from the
                // UNCONDITIONAL ConfigureEndpoints(ctx) inside AddBaseConsoleMessaging — otherwise a
                // wrong-named kebab "entry-step-dispatch" queue is auto-created at bus start (Pitfall 1).
                // The correct durable {id:D} endpoint is bound at runtime by ProcessorStartupOrchestrator,
                // AFTER identity resolves and BEFORE MarkHealthy (D-02/D-03).
                x.AddConsumer<EntryStepDispatchConsumer>().ExcludeFromConfigureEndpoints();
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

        // 6b. LOG-04: the ProcessorId log enricher — a custom OTel BaseProcessor<LogRecord> that appends
        //     ProcessorId from the singleton IProcessorContext.Id to EVERY processor LogRecord (null-safe
        //     — nothing before identity resolves). DI-RESOLVED so it reads the SINGLETON IProcessorContext
        //     (the instance AddProcessor overload cannot resolve DI). Registered ONLY here — processor-side
        //     — never in the shared AddBaseConsoleObservability block (L3: the orchestrator has no
        //     IProcessorContext and would throw at DI resolution). This is purely ADDITIVE: the
        //     IncludeScopes/ParseStateValues/OTLP options stay owned by the unchanged shared block; we only
        //     AddProcessor onto the same logger provider via the DI-resolved AddProcessor<T>() overload.
        services.AddSingleton<ProcessorIdLogEnricher>();
        services.ConfigureOpenTelemetryLoggerProvider((sp, lp) =>
            lp.AddProcessor(sp.GetRequiredService<ProcessorIdLogEnricher>()));

        // 6c. METRIC-05 (Landmine 1 — the compile-firewall fix): the code-owned "BaseProcessor" meter +
        //     its DI-singleton holder. Registered HERE inside AddBaseProcessor — NOT in
        //     BaseConsoleObservabilityExtensions — because BaseConsole.Core has NO project reference to
        //     BaseProcessor.Core (the dependency runs the other way; its only contract ref is
        //     Messaging.Contracts), so it cannot see ProcessorMetrics.MeterName. ConfigureOpenTelemetryMeterProvider
        //     is the exact meter-provider analog of the ConfigureOpenTelemetryLoggerProvider seam above (both
        //     from OpenTelemetry.Extensions.Hosting), attaching the meter additively to the MeterProvider that
        //     the shared AddBaseConsoleObservability built. Every Processor.* inherits this via AddBaseProcessor.
        services.AddSingleton<ProcessorMetrics>();
        services.ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(ProcessorMetrics.MeterName));

        // 7. The two-loop startup orchestrator (identity-by-SourceHash + per-non-null-schema definition).
        services.AddHostedService<ProcessorStartupOrchestrator>();

        // 7b. The only-when-Healthy liveness heartbeat (LIVE-01..06) — consumes the populated
        //     IProcessorContext, writes the frozen ProcessorProjection to skp:{id} with a sliding TTL.
        services.AddHostedService<ProcessorLivenessHeartbeat>();

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
