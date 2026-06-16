using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Keeper.Recovery;

/// <summary>KEEP-02: the Keeper INJECT state — the A18 forward-only body. The output data is in-hand on the
/// envelope (no presence read), so INJECT is non-destructive and performs exactly TWO effects:
/// (1) write <c>L2[m.EntryId]=m.Data</c>, then (2) send a reconstructed <see cref="StepCompleted"/> to
/// <c>queue:orchestrator-result</c> (A15). INJECT deletes NO key — DELETE is the only keeper state that
/// deletes (spec §8). Every op goes through the RetryLoop <see cref="RecoveryConsumerBase{TMessage}.Guard"/>;
/// gating happens at the endpoint (D-04).</summary>
public sealed class InjectConsumer(
    IConnectionMultiplexer redis, ISendEndpointProvider sendProvider,
    IOptions<RetryOptions> retryOptions)
    : RecoveryConsumerBase<KeeperInject>(redis, sendProvider, retryOptions)
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
        // IN-01: resolve the send endpoint through Guard too, so a transient GetSendEndpoint failure
        // (e.g. bus not yet fully started) routes through the bounded RetryLoop like every other op.
        var ep = await Guard(() => Send.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}")), ct);
        await Guard(() => ep.Send(completed, CancellationToken.None), ct);   // IN-01 inner send
    }
}
