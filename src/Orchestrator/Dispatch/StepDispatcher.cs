using MassTransit;
using Messaging.Contracts;

namespace Orchestrator.Dispatch;

/// <summary>
/// The sole implementation of <see cref="IStepDispatcher"/> (D-01) — the verbatim build-and-Send
/// block extracted from <c>WorkflowFireJob</c>. <c>Send</c> (NOT <c>Publish</c>, D-10) to the
/// per-processor queue <c>queue:{processorId:D}</c>; an infra fault on <c>Send</c> propagates.
/// </summary>
public sealed class StepDispatcher(ISendEndpointProvider sendProvider) : IStepDispatcher
{
    /// <inheritdoc />
    public async Task DispatchAsync(Guid workflowId, Guid stepId, Guid processorId, string payload,
        Guid correlationId, Guid executionId, Guid entryId, CancellationToken ct)
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
    }
}
