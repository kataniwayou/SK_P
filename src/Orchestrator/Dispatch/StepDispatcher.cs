using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Hashing;
using Messaging.Contracts.Projections;
using Orchestrator.Observability;
using StackExchange.Redis;

namespace Orchestrator.Dispatch;

/// <summary>
/// The sole implementation of <see cref="IStepDispatcher"/> (D-01) — the verbatim build-and-Send
/// block extracted from <c>WorkflowFireJob</c>. <c>Send</c> (NOT <c>Publish</c>, D-10) to the
/// per-processor queue <c>queue:{processorId:D}</c>; an infra fault on <c>Send</c> propagates.
/// <para>
/// Phase 31 (D-02/D-06): every dispatch carries a deterministic effect identity
/// <c>H = MessageIdentity.ComputeH(correlationId, workflowId, stepId, processorId, entryId)</c>
/// (executionId DELIBERATELY excluded — an orchestrator redelivery regenerates executionId but
/// reproduces the SAME H per (item, successor), so the next-hop drop-on-Ack gate dedups it, req-6).
/// The sender PRE-WRITES <c>flag[H]="Pending"</c> before the Send so the child arrives with its flag
/// present (D-06) — without this seed the receiver's <c>When.Exists</c> Pending-&gt;Ack flip is a no-op
/// on an absent key and the dedup gate can never fire. The pre-write is unconditional (a re-send of
/// Pending-&gt;Pending is idempotent; it only runs at SEND time, before the receiver processes, so it
/// never clobbers the receiver's effect-first Ack). A Redis fault on the pre-write is INFRA and
/// propagates (mirrors the Send-throw convention).
/// </para>
/// </summary>
public sealed class StepDispatcher(
    ISendEndpointProvider sendProvider,
    IConnectionMultiplexer redis,
    OrchestratorMetrics metrics) : IStepDispatcher
{
    // A generous fixed flag TTL keeps the skp:flag: namespace bounded. StepDispatcher has no IOptions
    // today (it is constructed both by DI and by the cron fire path), so a const mirrors the data-TTL
    // semantics without threading an options dependency through every call site.
    private static readonly TimeSpan FlagTtl = TimeSpan.FromSeconds(300);

    /// <inheritdoc />
    public async Task DispatchAsync(Guid workflowId, Guid stepId, Guid processorId, string payload,
        Guid correlationId, Guid executionId, string entryId, CancellationToken ct)
    {
        // D-02: the deterministic child H over the 5 identity fields (executionId excluded by
        // construction). The SAME (corr, wf, stepId, procId, entryId) always yields the SAME H — a
        // redelivery reproduces it and is deduped at the next hop (req-6 / Pitfall 5).
        var h = MessageIdentity.ComputeH(correlationId, workflowId, stepId, processorId, entryId);

        var msg = new EntryStepDispatch(workflowId, stepId, processorId, payload)
        {
            CorrelationId = correlationId,
            ExecutionId = executionId,
            EntryId = entryId,
            H = h,
        };

        // D-06: sender pre-writes flag[H]="Pending" so the child arrives with its flag present. The set
        // is unconditional (idempotent on re-send — a re-write of Pending is a no-op; the receiver's
        // effect-first Ack is never clobbered because this pre-write only runs at SEND time, before the
        // receiver processes). A Redis fault here is INFRA -> propagates (mirrors the Send-throw convention).
        var db = redis.GetDatabase();
        await db.StringSetAsync(L2ProjectionKeys.Flag(h), "Pending", expiry: FlagTtl);

        // D-10: Send (NOT Publish) to the per-processor queue. An infra fault here propagates.
        var endpoint = await sendProvider.GetSendEndpoint(new Uri($"queue:{processorId:D}"));
        await endpoint.Send(msg, ct);

        // METRIC-04 (D-04): SENT = count-AFTER-Send — an infra throw on Send correctly skips this
        // increment. Tagged ProcessorId only (no workflowId — cardinality, D-03/SPEC); the literal
        // PascalCase key is preserved by the collector exporter so `sum by (ProcessorId)` works verbatim.
        // The "D" format mirrors the queue:{processorId:D} naming. service_instance_id is ambient (Plan 01).
        metrics.DispatchSent.Add(1, new KeyValuePair<string, object?>("ProcessorId", processorId.ToString("D")));
    }
}
