using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Keeper.Recovery;

/// <summary>ORCV-06 / D-07: the Keeper REINJECT state for the ORCHESTRATOR result path. Mirrors
/// <see cref="ProcessorReinjectConsumer"/>'s reconstruct-then-send shape, but the divergence is the D-07
/// factory: it rebuilds the right <see cref="IStepResult"/> subtype from the carried
/// <see cref="OrchestratorReinject.Outcome"/> discriminator (the ONLY status branch in this consumer) and
/// re-injects it to <c>queue:orchestrator-result</c> — the same target the orchestrator's own result
/// consumer reads. The union diagnostic fields ride as discrete members: only Failed carries
/// <see cref="OrchestratorReinject.ErrorMessage"/>, only Cancelled carries
/// <see cref="OrchestratorReinject.CancellationMessage"/>; Completed/Processing carry none. An
/// unknown/invalid outcome degrades to a safe <see cref="StepFailed"/> via the exhaustive default (never
/// an exception or a mis-typed result, T-71-10). REINJECT is non-destructive: it deletes NO key (D-09).
/// Every GetSendEndpoint + Send goes through the bounded RetryLoop
/// <see cref="RecoveryConsumerBase{TMessage}.Guard"/>; gating happens at the endpoint (D-04).</summary>
public sealed class OrchestratorReinjectConsumer(
    IConnectionMultiplexer redis, ISendEndpointProvider sendProvider,
    IOptions<RetryOptions> retryOptions)
    : RecoveryConsumerBase<OrchestratorReinject>(redis, sendProvider, retryOptions)
{
    protected override async Task HandleAsync(OrchestratorReinject m, CancellationToken ct)
    {
        // D-07: the ONLY status branch — reconstruct the IStepResult subtype from the carried discriminator.
        // Completed/Processing carry no diagnostic field; Failed.ErrorMessage / Cancelled.CancellationMessage
        // are the only union fields. The exhaustive default degrades an unknown outcome to a safe StepFailed.
        IStepResult result = m.Outcome switch
        {
            StepOutcome.Completed => new StepCompleted(m.WorkflowId, m.StepId, m.ProcessorId)
            {
                CorrelationId = m.CorrelationId,
                ExecutionId = m.ExecutionId,
                EntryId = m.EntryId,
            },
            StepOutcome.Failed => new StepFailed(m.WorkflowId, m.StepId, m.ProcessorId)
            {
                CorrelationId = m.CorrelationId,
                ExecutionId = m.ExecutionId,
                ErrorMessage = m.ErrorMessage,
            },
            StepOutcome.Cancelled => new StepCancelled(m.WorkflowId, m.StepId, m.ProcessorId)
            {
                CorrelationId = m.CorrelationId,
                ExecutionId = m.ExecutionId,
                CancellationMessage = m.CancellationMessage,
            },
            StepOutcome.Processing => new StepProcessing(m.WorkflowId, m.StepId, m.ProcessorId)
            {
                CorrelationId = m.CorrelationId,
                ExecutionId = m.ExecutionId,
            },
            _ => new StepFailed(m.WorkflowId, m.StepId, m.ProcessorId)
            {
                CorrelationId = m.CorrelationId,
                ExecutionId = m.ExecutionId,
            },
        };

        // IN-01: resolve the send endpoint through Guard too, so a transient GetSendEndpoint failure routes
        // through the bounded RetryLoop. The boxed (object) Send re-injects the reconstructed result to the
        // orchestrator-result queue; the inner broker Send uses CancellationToken.None (do not abort a broker
        // send once started) while the outer Guard keeps ct for bus-shutdown observation.
        var ep = await Guard(() => Send.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}")), ct);
        await Guard(() => ep.Send((object)result, CancellationToken.None), ct);
    }
}
