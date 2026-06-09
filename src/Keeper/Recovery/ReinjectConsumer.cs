using Keeper.Health;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Keeper.Recovery;

/// <summary>KEEP-05: the Keeper REINJECT state — reads L2[entryId] to confirm the recovered input data is
/// still present, then re-injects a reconstructed <see cref="EntryStepDispatch"/> (carrying the D-01
/// <see cref="KeeperReinject.Payload"/> step config) to <c>queue:{ProcessorId:D}</c> — the same target a
/// direct dispatch uses. The read throws the DELIBERATE terminal <see cref="RecoveryDataGoneException"/>
/// INSIDE the retried op when the key is absent/empty, so a data-gone case surfaces as a thrown terminal
/// (→ skp-dlq-1, D-04) rather than a silent ack. Runs only after the gate opens (base D-03).</summary>
public sealed class ReinjectConsumer(
    IConnectionMultiplexer redis, ISendEndpointProvider sendProvider, IL2HealthGate gate,
    IOptions<RetryOptions> retryOptions, IOptions<RecoveryOptions> recoveryOptions,
    IOptions<BackupOptions> backupOptions)
    : RecoveryConsumerBase<KeeperReinject>(redis, sendProvider, gate, retryOptions, recoveryOptions, backupOptions)
{
    protected override async Task HandleAsync(KeeperReinject m, CancellationToken ct)
    {
        // Read confirms the input data is still present (present/absent gate). The reconstructed dispatch
        // carries m.Payload (the author config), NOT the L2 blob — the blob stays keyed at
        // ExecutionData(entryId) and the dispatch re-points the processor at it. IN-04: use STRLEN, not a
        // full StringGet, so the gate does not pull the entire blob over the wire just to confirm presence.
        // STRLEN returns 0 for BOTH a missing key AND an empty value, preserving the exact absent-OR-empty
        // terminal semantics (KeyExists would be WRONG here: an empty-string key EXISTS).
        await Guard(async () =>
        {
            if (await Db.StringLengthAsync(L2ProjectionKeys.ExecutionData(m.EntryId)) == 0)
                throw new RecoveryDataGoneException();   // D-04 terminal — absent OR empty
            return true;
        }, ct);

        var dispatch = new EntryStepDispatch(m.WorkflowId, m.StepId, m.ProcessorId, m.Payload)
        {
            CorrelationId = m.CorrelationId,
            ExecutionId = m.ExecutionId,
            EntryId = m.EntryId,
        };
        var ep = await Send.GetSendEndpoint(new Uri($"queue:{m.ProcessorId:D}"));
        // IN-01: the inner broker Send uses CancellationToken.None to match ProcessorPipeline's send
        // convention ("do not abort a broker send once started"). The outer Guard keeps ct so the
        // bounded RetryLoop still observes bus shutdown between attempts.
        await Guard(() => ep.Send(dispatch, CancellationToken.None), ct);
    }
}
