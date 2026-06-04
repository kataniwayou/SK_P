using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.Options;

namespace Orchestrator.Consumers;

/// <summary>
/// Endpoint/retry config seam for <see cref="ResultConsumer"/> (ORCH-RESULT-02). Binds
/// the STABLE shared competing-consumer queue <see cref="OrchestratorQueues.Result"/>
/// (<c>"orchestrator-result"</c>) — NOT the per-replica fan-out <c>"orchestrator"</c> endpoint and NOT
/// an <c>InstanceId</c>/<c>Temporary</c> endpoint (D-03): a result is consumed exactly once across the
/// consumer set, never broadcast.
/// <para>
/// <b>24.1 / D-24.1-05:</b> the boot gate + scheduled redelivery (and the
/// <c>rabbitmq_delayed_message_exchange</c> plugin dependency) are REMOVED. Only
/// <c>UseMessageRetry(Immediate(3))</c> remains — a bounded immediate retry of genuine infra faults
/// from <c>Send</c> → <c>_error</c>. An L1 miss is a graceful business-ack inside the consumer (no
/// throw), so there is no longer any gate-closed exception to reschedule.
/// </para>
/// </summary>
public sealed class ResultConsumerDefinition : ConsumerDefinition<ResultConsumer>
{
    private readonly IOptions<RetryOptions> _retryOptions;

    public ResultConsumerDefinition(IOptions<RetryOptions> retryOptions)
    {
        _retryOptions = retryOptions;
        EndpointName = OrchestratorQueues.Result;   // "orchestrator-result" — shared competing-consumer
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<ResultConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        // Bounded immediate retry of genuine infra faults (Send failures) → _error. No scheduled
        // redelivery (gate removed, 24.1 / D-24.1-05) → no plugin dependency. D-10: the retry Limit is
        // bound per process from the "Retry" config section (default Immediate(3)) — the single source
        // both this UseMessageRetry and Phase 32's final-attempt check read.
        endpointConfigurator.UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit));
    }
}
