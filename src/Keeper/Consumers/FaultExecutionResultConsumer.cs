using System;
using Keeper.Observability;
using Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;
using ExecutionResult = Messaging.Contracts.ExecutionResult;   // disambiguate from MassTransit.ExecutionResult

namespace Keeper.Consumers;

/// <summary>
/// Production fault intake for <see cref="Fault{T}"/> of <see cref="ExecutionResult"/> on the stable
/// shared durable queue <see cref="KeeperQueues.FaultRecovery"/> ("keeper-fault-recovery", D-03).
/// KHARD-03 keystone: the full recover/probe/reinject/park/pause/resume body lives ONCE in
/// <see cref="KeeperRecoveryHandler"/>; this consumer is a one-line delegation that supplies the two
/// per-type deltas — the <c>fault_type=result</c> tag and the re-inject endpoint
/// (<c>queue:{OrchestratorQueues.Result}</c>, ExecutionResult's origin orchestrator-result queue). The
/// <c>ExecutionResult</c> alias keeps the <c>Fault&lt;ExecutionResult&gt;</c> generic + lambda bound to the
/// contract type (not <c>MassTransit.ExecutionResult</c>).
/// </summary>
public sealed class FaultExecutionResultConsumer(KeeperRecoveryHandler handler)
    : IConsumer<Fault<ExecutionResult>>
{
    public Task Consume(ConsumeContext<Fault<ExecutionResult>> context) =>
        handler.HandleAsync(context, KeeperMetricTags.FaultTypeResult,
            inner => new Uri($"queue:{OrchestratorQueues.Result}"), context.CancellationToken);
}
