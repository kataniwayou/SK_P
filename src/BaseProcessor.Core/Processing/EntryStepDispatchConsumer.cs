using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Observability;
using MassTransit;
using Messaging.Contracts;

namespace BaseProcessor.Core.Processing;

/// <summary>
/// The framework's <see cref="IConsumer{EntryStepDispatch}"/> — the processor-half of the execution
/// round-trip (PIPE-01). Phase 44 reduces it to a THIN shell: the entry-point dispatch metric
/// (METRIC-05) plus a delegate to the <see cref="ProcessorPipeline"/>, which owns the full
/// Pre → In → Post → end-delete flow with terminal routing (RESEARCH Pattern 1 — the pipeline is a plain
/// object the hermetic facts construct directly, no MassTransit harness needed).
/// <para>
/// All business-vs-infra routing now lives in <see cref="ProcessorPipeline.RunAsync"/>: business outcomes
/// emit a <c>Step*</c> result and ack; infra outcomes emit the matching Keeper-state message; only a
/// send-exhaustion PROPAGATES (→ the runtime <c>UseMessageRetry</c> dead-letter latch → <c>_error</c>,
/// D-10). The consumer adds nothing beyond the metric and the delegate.
/// </para>
/// </summary>
public sealed class EntryStepDispatchConsumer(
    ProcessorPipeline pipeline,
    IProcessorContext context,
    ProcessorMetrics metrics) : IConsumer<EntryStepDispatch>
{
    public async Task Consume(ConsumeContext<EntryStepDispatch> ctx)
    {
        // METRIC-05 / D-07: count EVERY dispatch consumed at the entry point, tagged ProcessorId.
        // context.Id is Guid? but Consume runs ONLY post-MarkHealthy (the runtime binds queue:{id:D}
        // AFTER Healthy), so identity IS resolved here — the bang is justified (not a NRE). Tag key is
        // literal PascalCase "ProcessorId"; value .ToString("D") matches queue:{id:D}.
        metrics.DispatchConsumed.Add(1,
            new KeyValuePair<string, object?>("ProcessorId", context.Id!.Value.ToString("D")));

        await pipeline.RunAsync(ctx.Message, ctx.CancellationToken);
    }
}
