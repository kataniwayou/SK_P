using System;
using System.Collections.Generic;
using System.Diagnostics;
using Keeper.Observability;
using Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;

namespace Keeper.Consumers;

/// <summary>
/// Production fault intake for <see cref="Fault{T}"/> of <see cref="EntryStepDispatch"/> on the stable
/// shared durable queue <see cref="KeeperQueues.FaultRecovery"/> ("keeper-fault-recovery", D-03).
/// Observe-and-ack (D-06): double-unwrap the inner fault payload to the verbatim inner
/// <see cref="IExecutionCorrelated"/>, open a MANUAL CorrelationId scope from the inner message, open the
/// 5-id execution scope via <see cref="ExecutionLogScope.BuildState"/>, emit ONE Information log, and ack.
/// NO recovery in Phase 35 (re-inject is Phase 36).
/// <para>
/// <b>The manual CorrelationId scope is load-bearing (SC2/SC3 / T-35-06).</b> A <see cref="Fault{T}"/>
/// envelope is NEITHER <see cref="IExecutionCorrelated"/> NOR <see cref="ICorrelated"/>, so the bus-wide
/// inbound correlation filter falls back to a FRESH Guid for it (see InboundCorrelationConsumeFilter) —
/// the propagated correlationId would be lost. This consumer restores it by scoping
/// <see cref="CorrelationKeys.LogScope"/> from <c>inner.CorrelationId</c> directly.
/// </para>
/// <para>
/// Security (T-35-04 / T-35-05): all ids, <c>inner.H</c>, and the exception text are STRUCTURED PARAMS
/// under fixed template holes / scope keys — never interpolated. Only <c>ExceptionType</c> + <c>Message</c>
/// are surfaced; the exception's stack frames are NOT logged at Information (bounds attribute size / info-leak).
/// </para>
/// </summary>
public sealed class FaultEntryStepDispatchConsumer(
    ILogger<FaultEntryStepDispatchConsumer> logger, L2ProbeRecovery recovery, KeeperMetrics metrics)
    : IConsumer<Fault<EntryStepDispatch>>
{
    public async Task Consume(ConsumeContext<Fault<EntryStepDispatch>> context)
    {
        var inner = context.Message.Message;   // double .Message — verbatim inner IExecutionCorrelated
        var ex    = context.Message.Exceptions is { Length: > 0 } exs ? exs[0] : null;

        // KMET-02/03: intake — start the intake→terminal timer and count the consumed fault. ProcessorId is
        // the only bounded id label (no workflowId). fault_type="dispatch" (this consumer's closed enum).
        var procId = inner.ProcessorId.ToString("D");
        var sw     = Stopwatch.StartNew();
        metrics.FaultConsumed.Add(1, KeeperMetricTags.FaultTags(KeeperMetricTags.FaultTypeDispatch, procId));

        using (logger.BeginScope(new Dictionary<string, object> { [CorrelationKeys.LogScope] = inner.CorrelationId.ToString() }))
        using (logger.BeginScope(ExecutionLogScope.BuildState(inner)))
        {
            logger.LogInformation(
                "Keeper fault intake: {FaultType} for H={H} — {ExceptionType}: {ExceptionMessage}",
                nameof(EntryStepDispatch), inner.H, ex?.ExceptionType, ex?.Message);
        }

        // PAUSE-01 (D-03): publish PauseWorkflow at intake — BEFORE probing — so the orchestrator pauses
        // the workflow's Quartz job and the L2 outage stops spreading. Publish (NOT Send) so message-type
        // binding fans the signal out to the orchestrator's per-replica endpoint; CorrelationId carried.
        await context.Publish(
            new PauseWorkflow(inner.WorkflowId, inner.H) { CorrelationId = inner.CorrelationId },
            context.CancellationToken);
        metrics.WorkflowPaused.Add(1, new KeyValuePair<string, object?>(KeeperMetricTags.ProcessorId, procId)); // workflow-scoped (no fault_type)

        // PROBE-01/05: the bounded probe loop is AWAITED inside Consume — the broker delivery stays
        // un-acked until the loop exits, so ack-after-loop is automatic (no early Task.CompletedTask).
        var outcome = await recovery.RunAsync(inner.EntryId, inner.H, procId, context.CancellationToken);
        if (outcome == ProbeOutcome.Recovered)
        {
            // PROBE-03 (spike-proven, GATE_EXIT=0): re-inject the VERBATIM inner to its origin endpoint.
            // Send (NOT Publish), same H. PROBE-06: NO Keeper-side dedup — the receiver's surviving Phase-31
            // flag[H] gate collapses any duplicate re-inject. EntryStepDispatch's origin is its processor's queue.
            var endpoint = await context.GetSendEndpoint(new Uri($"queue:{inner.ProcessorId:D}"));
            await endpoint.Send(inner, context.CancellationToken);

            // PAUSE-05 (D-04): resume on recovery — AFTER the re-inject, still inside the Recovered branch.
            await context.Publish(
                new ResumeWorkflow(inner.WorkflowId, inner.H) { CorrelationId = inner.CorrelationId },
                context.CancellationToken);

            // KMET-02/03: recovered terminal — resume + recovered counters + the intake→terminal duration
            // (seconds, NOT milliseconds — the buckets are seconds).
            metrics.WorkflowResumed.Add(1, new KeyValuePair<string, object?>(KeeperMetricTags.ProcessorId, procId));
            metrics.Recovered.Add(1, KeeperMetricTags.FaultTags(KeeperMetricTags.FaultTypeDispatch, procId));
            metrics.RecoveryDuration.Record(sw.Elapsed.TotalSeconds,
                new KeyValuePair<string, object?>(KeeperMetricTags.Outcome, KeeperMetricTags.OutcomeRecovered),
                new KeyValuePair<string, object?>(KeeperMetricTags.ProcessorId, procId));
        }
        else
        {
            // PROBE-04 (D-09/D-10): park the ORIGINAL Fault<T> envelope (carries Exceptions[] for triage) to
            // keeper-dlq, then return → ack. A fault in THIS Send is infra → Immediate(N) → DLQ-1 (consistent).
            var dlq = await context.GetSendEndpoint(new Uri($"queue:{KeeperQueues.DeadLetter}"));
            await dlq.Send(context.Message, context.CancellationToken);

            // KMET-02/03: gave-up terminal — dlq counter (reason+fault_type+ProcessorId) + the duration.
            metrics.DlqPushed.Add(1,
                new KeyValuePair<string, object?>(KeeperMetricTags.Reason, KeeperMetricTags.ReasonProbeExhausted),
                new KeyValuePair<string, object?>(KeeperMetricTags.FaultType, KeeperMetricTags.FaultTypeDispatch),
                new KeyValuePair<string, object?>(KeeperMetricTags.ProcessorId, procId));
            metrics.RecoveryDuration.Record(sw.Elapsed.TotalSeconds,
                new KeyValuePair<string, object?>(KeeperMetricTags.Outcome, KeeperMetricTags.OutcomeGaveUp),
                new KeyValuePair<string, object?>(KeeperMetricTags.ProcessorId, procId));
        }
    }
}
