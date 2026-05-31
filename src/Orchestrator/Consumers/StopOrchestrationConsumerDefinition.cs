using MassTransit;

namespace Orchestrator.Consumers;

/// <summary>
/// Retry/redelivery/endpoint config seam for <see cref="StopOrchestrationConsumer"/>
/// (MSG-ACK-03/04 + ORCH-GATE-01) — mirrors <see cref="StartOrchestrationConsumerDefinition"/>
/// exactly. Same <c>EndpointName "orchestrator"</c> so <c>ConfigureEndpoints</c> groups both consumers
/// onto one per-replica fan-out endpoint (A2). <c>UseScheduledRedelivery</c> (outer) reschedules a
/// gate-closed <see cref="GateClosedException"/> past hydration (D-06); <c>UseMessageRetry</c> (inner)
/// bounds infra faults → <c>_error</c>. <see cref="GateClosedException"/> is NOT <c>Ignore&lt;&gt;</c>-listed.
/// </summary>
public sealed class StopOrchestrationConsumerDefinition : ConsumerDefinition<StopOrchestrationConsumer>
{
    public StopOrchestrationConsumerDefinition() => EndpointName = "orchestrator";   // SHARED base name (both defs)

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<StopOrchestrationConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        // ORDER (Pitfall 2): scheduled redelivery OUTER, immediate retry INNER (same policy as Start).
        endpointConfigurator.UseScheduledRedelivery(r =>
            r.Intervals(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(60)));
        endpointConfigurator.UseMessageRetry(r =>
        {
            r.Immediate(3);
            r.Ignore<WorkflowRootNotFoundException>();   // business failure never retries (D-07/D-08, MSG-ACK-03)
            // NOTE: do NOT Ignore<GateClosedException>() — it MUST reach the redelivery middleware (D-06).
        });
    }
}
