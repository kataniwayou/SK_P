using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Observability;
using MassTransit;
using Messaging.Contracts;

namespace BaseProcessor.Core.Processing;

/// <summary>
/// The framework's <see cref="IConsumer{EntryStepDispatch}"/> — the processor-half of the execution
/// round-trip (PIPE-01). A THIN shell: the entry-point dispatch metric (METRIC-05) plus a delegate to the
/// <see cref="ProcessorPipeline"/>, which owns the full A18 dispatcher + forward/recovery flow (the pipeline
/// is a plain object the hermetic facts construct directly, no MassTransit harness needed).
/// <para>
/// Phase 51 plumbs the broker-assigned <c>ctx.MessageId</c> (D-09) — the slot-array branch key — into the
/// pipeline; a null MessageId is a contract violation (MassTransit always sets it on Send/Publish) and is a
/// fail-fast <see cref="InvalidOperationException"/> rather than a synthesized <c>Guid.Empty</c> key that
/// would collide across messages (T-51-03).
/// </para>
/// <para>
/// All business-vs-infra routing lives in <see cref="ProcessorPipeline.RunAsync"/>: business outcomes emit a
/// <c>Step*</c> result and ack; infra outcomes emit the matching Keeper-state message; only a send-exhaustion
/// PROPAGATES (→ the runtime <c>UseMessageRetry</c> dead-letter latch → <c>_error</c>, D-10). The consumer
/// adds nothing beyond the metric and the delegate.
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

        // D-09/T-51-03: the broker MessageId is the slot-array branch key — fail-fast on null rather than
        // synthesize a Guid.Empty key that would collide across messages.
        var messageId = ctx.MessageId ?? throw new InvalidOperationException(
            "EntryStepDispatch arrived with a null MessageId — MassTransit always sets it on Send/Publish; null is a contract violation.");
        await pipeline.RunAsync(ctx.Message, messageId, ctx.CancellationToken);
    }
}
