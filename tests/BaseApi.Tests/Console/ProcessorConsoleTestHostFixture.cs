using BaseConsole.Core.DependencyInjection;
using BaseProcessor.Core.DependencyInjection;
using BaseProcessor.Core.Liveness;
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
