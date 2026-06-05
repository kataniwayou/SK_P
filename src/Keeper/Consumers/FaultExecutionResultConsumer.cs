using System;
using System.Collections.Generic;
using Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using ExecutionResult = Messaging.Contracts.ExecutionResult;   // disambiguate from MassTransit.ExecutionResult

namespace Keeper.Consumers;

/// <summary>
/// Production fault intake for <see cref="Fault{T}"/> of <see cref="ExecutionResult"/> on the stable
/// shared durable queue <see cref="KeeperQueues.FaultRecovery"/> ("keeper-fault-recovery", D-03).
/// Observe-and-ack (D-06): double-unwrap the inner fault payload to the verbatim inner
/// <see cref="IExecutionCorrelated"/>, open a MANUAL CorrelationId scope from the inner message, open the
/// 5-id execution scope via <see cref="ExecutionLogScope.BuildState"/>, emit ONE Information log, and ack.
/// NO recovery in Phase 35 (re-inject is Phase 36).
/// <para>
/// <b>The manual CorrelationId scope is load-bearing (SC2/SC3 / T-35-06).</b> A <see cref="Fault{T}"/>
/// envelope is NEITHER <see cref="IExecutionCorrelated"/> NOR <see cref="ICorrelated"/>, so the bus-wide
/// inbound correlation filter falls back to a FRESH Guid for it — the propagated correlationId would be
/// lost. This consumer restores it by scoping <see cref="CorrelationKeys.LogScope"/> from
/// <c>inner.CorrelationId</c> directly.
/// </para>
/// <para>
/// Security (T-35-04 / T-35-05): all ids, <c>inner.H</c>, and the exception text are STRUCTURED PARAMS
/// under fixed template holes / scope keys — never interpolated. Only <c>ExceptionType</c> + <c>Message</c>
/// are surfaced; the exception's stack frames are NOT logged at Information (bounds attribute size / info-leak).
/// </para>
/// </summary>
public sealed class FaultExecutionResultConsumer(
    ILogger<FaultExecutionResultConsumer> logger, L2ProbeRecovery recovery)
    : IConsumer<Fault<ExecutionResult>>
{
    public async Task Consume(ConsumeContext<Fault<ExecutionResult>> context)
    {
        var inner = context.Message.Message;   // double .Message — verbatim inner IExecutionCorrelated
        var ex    = context.Message.Exceptions is { Length: > 0 } exs ? exs[0] : null;

        using (logger.BeginScope(new Dictionary<string, object> { [CorrelationKeys.LogScope] = inner.CorrelationId.ToString() }))
        using (logger.BeginScope(ExecutionLogScope.BuildState(inner)))
        {
            logger.LogInformation(
                "Keeper fault intake: {FaultType} for H={H} — {ExceptionType}: {ExceptionMessage}",
                nameof(ExecutionResult), inner.H, ex?.ExceptionType, ex?.Message);
        }

        // PROBE-01/05: the bounded probe loop is AWAITED inside Consume — the broker delivery stays
        // un-acked until the loop exits, so ack-after-loop is automatic (no early Task.CompletedTask).
        var outcome = await recovery.RunAsync(inner.EntryId, inner.H, context.CancellationToken);
        if (outcome == ProbeOutcome.Recovered)
        {
            // PROBE-03 (spike-proven, GATE_EXIT=0): re-inject the VERBATIM inner to its origin endpoint.
            // Send (NOT Publish), same H. PROBE-06: NO Keeper-side dedup — the receiver's surviving Phase-31
            // flag[H] gate collapses any duplicate re-inject. ExecutionResult's origin is the orchestrator-result queue.
            var endpoint = await context.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"));
            await endpoint.Send(inner, context.CancellationToken);
        }
        else
        {
            // PROBE-04 (D-09/D-10): park the ORIGINAL Fault<T> envelope (carries Exceptions[] for triage) to
            // keeper-dlq, then return → ack. A fault in THIS Send is infra → Immediate(N) → DLQ-1 (consistent).
            var dlq = await context.GetSendEndpoint(new Uri($"queue:{KeeperQueues.DeadLetter}"));
            await dlq.Send(context.Message, context.CancellationToken);
        }
    }
}
