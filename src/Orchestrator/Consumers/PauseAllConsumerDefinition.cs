using MassTransit;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.Options;

namespace Orchestrator.Consumers;

/// <summary>
/// Retry/endpoint config seam for <see cref="PauseAllConsumer"/> (ORCH-02 / D-08). Binds the NEW
/// dedicated per-replica fan-out endpoint <c>"orchestrator-global-pauseresume"</c> — independent from
/// the per-workflow <c>"orchestrator-pauseresume"</c> so Phase 48 can drop the old endpoint with zero
/// entanglement (D-08). <c>ConcurrentMessageLimit = 1</c> serializes Pause/Resume on this replica so a
/// duplicate replays against idempotent Quartz transitions — NO lock, NO stripe.
/// <para>
/// <b>Per-endpoint retry ownership (Pitfall 4):</b> <c>UseMessageRetry</c> is per-endpoint and
/// PauseAll+ResumeAll SHARE this endpoint, so ONLY this definition registers <c>UseMessageRetry</c>; the
/// <see cref="ResumeAllConsumerDefinition"/> sets <c>ConcurrentMessageLimit = 1</c> only. No
/// <c>r.Ignore&lt;...&gt;()</c> — there is no L2 hydration business exception on this control path.
/// </para>
/// </summary>
public sealed class PauseAllConsumerDefinition : ConsumerDefinition<PauseAllConsumer>
{
    private readonly IOptions<RetryOptions> _retryOptions;

    public PauseAllConsumerDefinition(IOptions<RetryOptions> retryOptions)
    {
        _retryOptions = retryOptions;
        EndpointName = "orchestrator-global-pauseresume";   // NEW dedicated base name (D-08 — independent from orchestrator-pauseresume)
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<PauseAllConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        consumerConfigurator.ConcurrentMessageLimit = 1;                                       // serialize Pause/Resume on this replica
        // Owns retry for the SHARED endpoint (Resume def does NOT re-register it — Pitfall 4).
        endpointConfigurator.UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit));
    }
}
