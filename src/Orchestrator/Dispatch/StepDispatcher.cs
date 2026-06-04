using MassTransit;
using Messaging.Contracts;
using Orchestrator.Observability;

namespace Orchestrator.Dispatch;

/// <summary>
/// The sole implementation of <see cref="IStepDispatcher"/> (D-01) — the verbatim build-and-Send
/// block extracted from <c>WorkflowFireJob</c>. <c>Send</c> (NOT <c>Publish</c>, D-10) to the
/// per-processor queue <c>queue:{processorId:D}</c>; an infra fault on <c>Send</c> propagates.
/// </summary>
public sealed class StepDispatcher(ISendEndpointProvider sendProvider, OrchestratorMetrics metrics) : IStepDispatcher
{
    /// <inheritdoc />
    public async Task DispatchAsync(Guid workflowId, Guid stepId, Guid processorId, string payload,
        Guid correlationId, Guid executionId, string entryId, CancellationToken ct)
    {
        var msg = new EntryStepDispatch(workflowId, stepId, processorId, payload)
        {
            CorrelationId = correlationId,
            ExecutionId = executionId,
            EntryId = entryId,
        };

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
