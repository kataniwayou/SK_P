using System;
using Keeper.Observability;
using Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;

namespace Keeper.Consumers;

/// <summary>
/// D-14 (DARK path): the v3.x result-path fault consumer. Its original message type
/// <c>Messaging.Contracts.ExecutionResult</c> was DELETED in v4.0.0 (replaced by the four typed
/// <c>Step*</c> records, D-06), so <c>Fault&lt;ExecutionResult&gt;</c> no longer resolves. Per D-14 the
/// reactive recovery FEATURE must remain present-and-compiling (its real retirement is RETIRE-03, Phases
/// 47/48) — so this consumer is RETARGETED off the deleted type onto the surviving result-path contract
/// <see cref="StepCompleted"/> (an <see cref="IStepResult"/> : <see cref="IExecutionCorrelated"/>) and kept
/// delegating to the single <see cref="KeeperRecoveryHandler"/> body. It is INTENTIONALLY NOT REGISTERED in
/// <c>Program.cs</c> for v4: the feature is carried into the running bus by
/// <see cref="FaultEntryStepDispatchConsumer"/> (bound to <c>Fault&lt;EntryStepDispatch&gt;</c>). This file
/// stays so the diff does not show the reactive recovery path disappearing wholesale (D-14 diff guard).
/// </summary>
public sealed class FaultExecutionResultConsumer(KeeperRecoveryHandler handler)
    : IConsumer<Fault<StepCompleted>>
{
    public Task Consume(ConsumeContext<Fault<StepCompleted>> context) =>
        handler.HandleAsync(context, KeeperMetricTags.FaultTypeResult,
            inner => new Uri($"queue:{OrchestratorQueues.Result}"), context.CancellationToken);
}
