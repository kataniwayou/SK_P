using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Keeper.Recovery;

/// <summary>ORCV-06 / D-06: the Keeper INJECT state for the ORCHESTRATOR FORWARD escalation. Mirrors
/// <see cref="ProcessorInjectConsumer"/>'s write-then-send shape, but where the processor INJECT writes
/// in-hand envelope data, the orchestrator INJECT COMPLETES THE COPY the FORWARD-Post tail could not
/// finish: it reads the origin data key (<c>L2[OriginEntryId]</c>) and, when present, SETs the new data
/// key (<c>L2[EntryId]</c>) with that value, then dispatches a reconstructed <see cref="EntryStepDispatch"/>
/// (carrying the next-step config <see cref="OrchestratorInject.Payload"/>) to
/// <c>queue:{NextProcessorId:D}</c> — the same target a direct dispatch uses. INJECT is non-destructive:
/// it deletes NO key (D-09 — DELETE is the only deleting keeper state, spec §8). Every L2 op +
/// GetSendEndpoint + Send goes through the bounded RetryLoop
/// <see cref="RecoveryConsumerBase{TMessage}.Guard"/>; gating happens at the endpoint (D-04).</summary>
public sealed class OrchestratorInjectConsumer(
    IConnectionMultiplexer redis, ISendEndpointProvider sendProvider,
    IOptions<RetryOptions> retryOptions, IOptions<RecoveryOptions> recoveryOptions)
    : RecoveryConsumerBase<OrchestratorInject>(redis, sendProvider, retryOptions)
{
    // WR-02 (Phase 71): the data TTL for the copied key, computed in C# (NOT in Lua — the D-03 anti-desync
    // invariant) and floored at 1s (a non-positive value would marshal to PX 0, a Redis server error). Sourced
    // from the SAME bounded "Recovery" ExecutionDataTtlSeconds knob the orchestrator FORWARD path uses, so the
    // INJECT-escalation copy gets the same lifetime as the normal SET ... PX ExecutionDataTtl path.
    private readonly TimeSpan _executionDataTtl =
        TimeSpan.FromSeconds(Math.Max(1, recoveryOptions.Value.ExecutionDataTtlSeconds));

    protected override async Task HandleAsync(OrchestratorInject m, CancellationToken ct)
    {
        // 1) complete the index+data COPY (origin -> newEntryId) the FORWARD pass couldn't finish: read the
        // origin data key, then SET the new key with that value — both through Guard so a transient Redis
        // failure routes to the exhaustion policy. Absent origin (HasValue==false) is a no-write no-op:
        // INJECT never deletes, so nothing is removed; the dispatch below still proceeds (forward-only).
        // WR-02: the SET carries the bounded ExecutionDataTtl (mirroring the FORWARD Lua's 'PX' ARGV[4]) so a
        // lost/redelivery-stranded INJECT can never leave the copied key immortal (no immortal-key leak).
        var v = await Guard(() => Db.StringGetAsync(L2ProjectionKeys.ExecutionData(m.OriginEntryId)), ct);
        if (v.HasValue)
            await Guard(() => Db.StringSetAsync(L2ProjectionKeys.ExecutionData(m.EntryId), v, _executionDataTtl), ct);

        // 2) send the reconstructed EntryStepDispatch -> queue:{NextProcessorId:D} (the downstream target).
        var dispatch = new EntryStepDispatch(m.WorkflowId, m.NextStepId, m.NextProcessorId, m.Payload)
        {
            CorrelationId = m.CorrelationId,
            ExecutionId = m.ExecutionId,
            EntryId = m.EntryId,        // the newEntryId just copied into
        };
        // IN-01: resolve the send endpoint through Guard too, so a transient GetSendEndpoint failure routes
        // through the bounded RetryLoop like every other op. The inner broker Send uses CancellationToken.None
        // (do not abort a broker send once started); the outer Guard keeps ct for bus-shutdown observation.
        var ep = await Guard(() => Send.GetSendEndpoint(new Uri($"queue:{m.NextProcessorId:D}")), ct);
        await Guard(() => ep.Send(dispatch, CancellationToken.None), ct);
    }
}
