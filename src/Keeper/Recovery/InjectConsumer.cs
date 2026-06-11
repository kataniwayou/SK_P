using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Keeper.Recovery;

/// <summary>KEEP-02: the Keeper INJECT state — the A18 forward-only body. The output data is in-hand on the
/// envelope (no presence read), so INJECT performs the three ops in STRICT order (Pitfall 5 — never
/// reorder; the source delete is the tail AFTER the confirmed send so a completed result is never lost by
/// deleting the source before the send lands): (1) write <c>L2[m.EntryId]=m.Data</c>, (2) send a
/// reconstructed <see cref="StepCompleted"/> to <c>queue:orchestrator-result</c> (A15), (3) delete
/// <c>L2[m.DeleteEntryId]</c>. Every op goes through the RetryLoop <see cref="RecoveryConsumerBase{TMessage}.Guard"/>;
/// gating happens at the endpoint (D-04).</summary>
public sealed class InjectConsumer(
    IConnectionMultiplexer redis, ISendEndpointProvider sendProvider,
    IOptions<RetryOptions> retryOptions, IOptions<RecoveryOptions> recoveryOptions)
    : RecoveryConsumerBase<KeeperInject>(redis, sendProvider, retryOptions, recoveryOptions)
{
    protected override async Task HandleAsync(KeeperInject m, CancellationToken ct)
    {
        // 1) write L2[entryId] = data (data in-hand on the envelope — forward-only, NO presence read)
        await Guard(() => Db.StringSetAsync(L2ProjectionKeys.ExecutionData(m.EntryId), m.Data), ct);

        // 2) send StepCompleted → orchestrator result queue (A15)
        var completed = new StepCompleted(m.WorkflowId, m.StepId, m.ProcessorId)
        {
            CorrelationId = m.CorrelationId,
            ExecutionId = m.ExecutionId,
            EntryId = m.EntryId,        // the REAL data key just written
        };
        var ep = await Send.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"));
        await Guard(() => ep.Send(completed, CancellationToken.None), ct);   // IN-01 inner send

        // 3) delete L2[deleteEntryId] (source cleanup tail — AFTER the confirmed send)
        await Guard(() => Db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(m.DeleteEntryId)), ct);
    }
}
