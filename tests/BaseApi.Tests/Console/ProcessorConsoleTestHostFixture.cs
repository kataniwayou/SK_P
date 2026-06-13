using BaseConsole.Core.DependencyInjection;
using BaseProcessor.Core.DependencyInjection;
using BaseProcessor.Core.Liveness;
using BaseProcessor.Core.Startup;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Processor.Sample;
using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;

namespace BaseApi.Tests.Console;

/// <summary>
/// Phase 61 / PROBE-02 (D-01/04) — the PROCESSOR variant of <see cref="ConsoleTestHostFixture"/>.
///
/// <para>
/// Where the base fixture composes the bare three-call BaseConsole seam, this subclass composes the
/// processor composition root (<c>AddBaseProcessor</c>) exactly as <c>Processor.Sample/Program.cs</c> does
/// — observability + <c>AddBaseProcessor</c> (which folds the BaseConsole infra + bus + identity + liveness
/// + the embedded health listener) + the ONE concrete <see cref="BaseProcessorBase"/> seam
/// (<see cref="SampleProcessor"/>). The Plan-02 <c>liveness-watchdog</c> <see cref="HealthCheckDescriptor"/>
/// is registered transitively by <c>AddBaseProcessor</c>, so it arrives on the embedded <c>/health/live</c>
/// listener WITHOUT any per-app health wiring — this fixture is the end-to-end proof of that path.
/// </para>
///
/// <para>
/// <b>Dead-dep boot (D-01 / T-61-08).</b> The inherited <c>BuildConfig</c> points Redis + RabbitMQ at dead
/// ports; the watchdog reads ONLY the in-process L1 holder (never Redis/RMQ), so the host boots and
/// <c>/health/live</c> answers regardless. A dependency blip cannot flip liveness — only a genuinely
/// null/stale L1 loop does.
/// </para>
///
/// <para>
/// <b>L1 seeding.</b> <see cref="SeedLiveness"/> resolves the singleton <see cref="IProcessorLivenessState"/>
/// from the running host and swaps in a <see cref="ProcessorLivenessEntry"/> so a test can deterministically
/// drive the fresh/stale verdict immediately before its GET. Leaving <c>Current</c> unseeded exercises the
/// null ("liveness loop not started") verdict.
/// </para>
/// </summary>
public class ProcessorConsoleTestHostFixture : ConsoleTestHostFixture
{
    /// <summary>
    /// Composes the processor stack: metrics-only observability + <c>AddBaseProcessor</c> (folds the
    /// BaseConsole infra + the transitive <c>liveness-watchdog</c> descriptor) + the ONE concrete
    /// <see cref="BaseProcessorBase"/> seam — mirroring <c>Processor.Sample/Program.cs</c> verbatim so the
    /// host BUILDS (the EntryStepDispatchConsumer pipeline resolves <see cref="BaseProcessorBase"/>).
    /// </summary>
    protected override void ConfigureBuilder(IHostApplicationBuilder builder)
    {
        builder.AddBaseConsoleObservability(builder.Configuration);          // metrics-only OTel (no tracer)
        builder.Services.AddBaseProcessor(builder.Configuration);            // identity + liveness + dispatch + heartbeat (+ watchdog descriptor)
        builder.Services.AddSingleton<BaseProcessorBase, SampleProcessor>(); // the ONE concrete transform seam

        // [Rule 3 - Blocking] The two processor background loops (ProcessorStartupOrchestrator +
        // ProcessorLivenessHeartbeat) are removed so the in-process test host BOOTS. The startup
        // orchestrator's first act is AssemblyMetadataSourceHashProvider.Get(), which reads the ENTRY
        // assembly's [assembly: AssemblyMetadata("SourceHash", ...)] — emitted only onto a real
        // Processor.<Purpose>.dll by the Phase-28 MSBuild embed target, never onto the BaseApi.Tests entry
        // assembly. Without it the orchestrator throws inside Host.StartAsync and the fixture cannot start.
        // These loops are IRRELEVANT to this proof: the test seeds the L1 holder DIRECTLY via SeedLiveness
        // (the loops are the only OTHER L1 writers), and the watchdog/listener wiring under proof is
        // registered separately by AddBaseProcessor (the live-tagged HealthCheckDescriptor + the embedded
        // EmbeddedHealthEndpointService) and is left intact. We strip ONLY these two hosted services by
        // their concrete singleton type so the embedded listener + MassTransit bus hosted services survive.
        RemoveHostedService<ProcessorStartupOrchestrator>(builder.Services);
        RemoveHostedService<ProcessorLivenessHeartbeat>(builder.Services);
    }

    /// <summary>
    /// Removes the <c>IHostedService</c> wrapper that resolves the concrete singleton <typeparamref name="T"/>
    /// (registered by <c>AddBaseProcessor</c> as <c>AddHostedService(sp =&gt; sp.GetRequiredService&lt;T&gt;())</c>)
    /// PLUS the concrete singleton itself — without disturbing the embedded health listener or the MassTransit
    /// bus hosted service. The wrapper carries no <c>ImplementationType</c> (factory registration), so it is
    /// matched by invoking the factory against a probe provider that returns a sentinel: instead we match the
    /// concrete singleton descriptor by type and the hosted wrapper by its declaring-assembly factory.
    /// </summary>
    private static void RemoveHostedService<T>(IServiceCollection services) where T : class, IHostedService
    {
        // 1. Drop the hosted-service wrapper whose factory was authored in BaseProcessor.Core's composition
        //    root (the `sp => sp.GetRequiredService<T>()` lambda) AND whose service type is IHostedService.
        //    The lambda's closure method declaring-type lives in the BaseProcessor.Core assembly, distinct
        //    from MassTransit's own assembly and from BaseConsole.Core (the embedded listener), so this
        //    matches only the two processor loops. We further disambiguate the two by also removing the
        //    concrete singleton of T below; the wrapper resolving a now-absent T would throw, so remove both.
        foreach (var d in services
                     .Where(d => d.ServiceType == typeof(IHostedService)
                              && d.ImplementationFactory is not null
                              && d.ImplementationFactory.Method.DeclaringType?.Assembly == typeof(T).Assembly)
                     .ToList())
        {
            services.Remove(d);
        }

        // 2. Drop the concrete singleton (registered via ActivatorUtilities.CreateInstance factory). Matched
        //    by ServiceType == typeof(T) so only THIS loop's singleton is removed.
        foreach (var d in services.Where(d => d.ServiceType == typeof(T)).ToList())
        {
            services.Remove(d);
        }
    }

    /// <summary>
    /// Seeds the singleton L1 liveness holder so the watchdog produces a deterministic verdict on the next
    /// GET. Fresh = <c>(DateTime.UtcNow, 300)</c>; stale = <c>(DateTime.UtcNow.AddDays(-1), 0)</c>. All three
    /// per-schema outcomes are left null (=&gt; Success), so the summary fields are present in the body.
    /// </summary>
    public void SeedLiveness(DateTime timestamp, int interval)
        => Host.Services.GetRequiredService<IProcessorLivenessState>()
               .Update(ProcessorLivenessEntry.Create(null, null, null, timestamp, interval));
}
