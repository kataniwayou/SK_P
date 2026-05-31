using MassTransit;

namespace Orchestrator.Consumers;

/// <summary>
/// Retry/redelivery/endpoint config seam for <see cref="StartOrchestrationConsumer"/>
/// (MSG-ACK-03/04 + ORCH-GATE-01). Shares the base <c>EndpointName "orchestrator"</c> with the Stop
/// definition so <c>ConfigureEndpoints</c> groups both consumers onto one per-replica fan-out
/// endpoint (A2).
/// <para>
/// <b>Middleware ORDER is load-bearing (Pitfall 2 / GitHub #1575):</b> <c>UseScheduledRedelivery</c>
/// is configured FIRST (outer — it removes-and-reschedules a thrown <see cref="GateClosedException"/>
/// past hydration so the Start is reprocessed after <c>MarkReady</c> — D-06), then
/// <c>UseMessageRetry(Immediate(3))</c> (inner) bounds infra faults → <c>_error</c> and
/// <c>Ignore&lt;WorkflowRootNotFoundException&gt;</c> keeps an escaped business exception from
/// retry-storming. <see cref="GateClosedException"/> is deliberately NOT <c>Ignore&lt;&gt;</c>-listed.
/// </para>
/// </summary>
public sealed class StartOrchestrationConsumerDefinition : ConsumerDefinition<StartOrchestrationConsumer>
{
    public StartOrchestrationConsumerDefinition() => EndpointName = "orchestrator";   // SHARED base name (both defs)

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<StartOrchestrationConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        // ORDER (Pitfall 2): scheduled redelivery OUTER, immediate retry INNER. A gate-closed throw is
        // rescheduled (5s/15s/30s/60s) to outlast hydration; after exhaustion a true outage routes to
        // _error. Same policy as ResultConsumerDefinition (A1, ~110s total).
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
