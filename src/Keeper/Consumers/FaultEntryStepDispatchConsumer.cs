using System.Collections.Generic;
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
public sealed class FaultEntryStepDispatchConsumer(ILogger<FaultEntryStepDispatchConsumer> logger)
    : IConsumer<Fault<EntryStepDispatch>>
{
    public Task Consume(ConsumeContext<Fault<EntryStepDispatch>> context)
    {
        var inner = context.Message.Message;   // double .Message — verbatim inner IExecutionCorrelated
        var ex    = context.Message.Exceptions is { Length: > 0 } exs ? exs[0] : null;

        using (logger.BeginScope(new Dictionary<string, object> { [CorrelationKeys.LogScope] = inner.CorrelationId.ToString() }))
        using (logger.BeginScope(ExecutionLogScope.BuildState(inner)))
        {
            logger.LogInformation(
                "Keeper fault intake: {FaultType} for H={H} — {ExceptionType}: {ExceptionMessage}",
                nameof(EntryStepDispatch), inner.H, ex?.ExceptionType, ex?.Message);
        }

        return Task.CompletedTask;   // observe-and-ack (D-06) — NO recovery in Phase 35
    }
}
