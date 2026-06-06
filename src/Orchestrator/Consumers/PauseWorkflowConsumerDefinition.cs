using MassTransit;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.Options;

namespace Orchestrator.Consumers;

/// <summary>
/// Retry/endpoint config seam for <see cref="PauseWorkflowConsumer"/> (PAUSE-04 / D-07). Binds the
/// DEDICATED shared fan-out endpoint <c>"orchestrator-pauseresume"</c> (NOT "orchestrator") so
/// Pause+Resume own their own retry + <c>ConcurrentMessageLimit</c> and do not throttle
/// Start/Stop/Result (RESEARCH §5b / A3). <c>ConcurrentMessageLimit = 1</c> serializes delivery so a
/// duplicate Pause/Resume replays against idempotent Quartz transitions — NO lock, NO stripe (D-07).
/// <para>
/// <b>Per-endpoint retry ownership (RESEARCH §5):</b> <c>UseMessageRetry</c> is per-endpoint and
/// Pause+Resume SHARE this endpoint, so ONLY this definition registers <c>UseMessageRetry</c>; the
/// <see cref="ResumeWorkflowConsumerDefinition"/> inherits it. No
/// <c>r.Ignore&lt;WorkflowRootNotFoundException&gt;()</c> — there is no L2 hydration on this path.
/// </para>
/// </summary>
public sealed class PauseWorkflowConsumerDefinition : ConsumerDefinition<PauseWorkflowConsumer>
{
    private readonly IOptions<RetryOptions> _retryOptions;

    public PauseWorkflowConsumerDefinition(IOptions<RetryOptions> retryOptions)
    {
        _retryOptions = retryOptions;
        EndpointName = "orchestrator-pauseresume";   // DEDICATED shared base name (Pause + Resume)
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<PauseWorkflowConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        consumerConfigurator.ConcurrentMessageLimit = 1;                              // D-07 serial — no lock/stripe
        // Bounded immediate retry of infra faults -> _error. D-10: Limit bound per process from "Retry".
        // Owns retry for the SHARED endpoint (Resume def does NOT re-register it — RESEARCH §5).
        endpointConfigurator.UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit));
    }
}
