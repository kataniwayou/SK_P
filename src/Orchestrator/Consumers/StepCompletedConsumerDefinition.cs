using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.Options;

namespace Orchestrator.Consumers;

/// <summary>
/// Endpoint/retry config seam for <see cref="StepCompletedConsumer"/> (D-07 / ORCH-01). Binds the STABLE
/// shared competing-consumer queue <see cref="OrchestratorQueues.Result"/> (<c>"orchestrator-result"</c>)
/// — NOT a per-replica fan-out / <c>InstanceId</c>/<c>Temporary</c> endpoint: a result is consumed
/// exactly once across the consumer set, never broadcast. All four typed result consumers
/// (Completed/Failed/Cancelled/Processing) co-locate on this SAME endpoint as competing consumers.
/// <para>
/// <b>THIS definition OWNS the endpoint-level retry</b> for <c>orchestrator-result</c>. Because
/// <c>UseMessageRetry</c> is PER-ENDPOINT (not per-consumer) and the three sibling definitions colocate
/// on the SAME endpoint, only this definition may register it — the siblings' <c>ConfigureConsumer</c>
/// are intentional no-ops (Pitfall 4, mirrors <c>FaultEntryStepDispatchConsumerDefinition</c>).
/// </para>
/// </summary>
public sealed class StepCompletedConsumerDefinition : ConsumerDefinition<StepCompletedConsumer>
{
    private readonly IOptions<RetryOptions> _retryOptions;

    public StepCompletedConsumerDefinition(IOptions<RetryOptions> retryOptions)
    {
        _retryOptions = retryOptions;
        EndpointName = OrchestratorQueues.Result;   // "orchestrator-result" — shared competing-consumer
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<StepCompletedConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        // Bounded immediate retry of genuine infra faults (Send failures) → _error. Limit is bound per
        // process from the "Retry" config section (default Immediate(3)) — the single source of truth both
        // this UseMessageRetry and Phase 32's final-attempt check read. Single-owner on the shared endpoint
        // (Pitfall 4): the sibling typed-result definitions leave ConfigureConsumer an intentional no-op.
        endpointConfigurator.UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit));
    }
}
