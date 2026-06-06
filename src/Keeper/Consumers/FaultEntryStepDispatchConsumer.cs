using System;
using Keeper.Observability;
using Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;

namespace Keeper.Consumers;

/// <summary>
/// Production fault intake for <see cref="Fault{T}"/> of <see cref="EntryStepDispatch"/> on the stable
/// shared durable queue <see cref="KeeperQueues.FaultRecovery"/> ("keeper-fault-recovery", D-03).
/// KHARD-03 keystone: the full recover/probe/reinject/park/pause/resume body lives ONCE in
/// <see cref="KeeperRecoveryHandler"/>; this consumer is a one-line delegation that supplies the two
/// per-type deltas — the <c>fault_type=dispatch</c> tag and the re-inject endpoint
/// (<c>queue:{ProcessorId:D}</c>, EntryStepDispatch's origin processor queue).
/// </summary>
public sealed class FaultEntryStepDispatchConsumer(KeeperRecoveryHandler handler)
    : IConsumer<Fault<EntryStepDispatch>>
{
    public Task Consume(ConsumeContext<Fault<EntryStepDispatch>> context) =>
        handler.HandleAsync(context, KeeperMetricTags.FaultTypeDispatch,
            inner => new Uri($"queue:{inner.ProcessorId:D}"), context.CancellationToken);
}
