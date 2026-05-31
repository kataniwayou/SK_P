using MassTransit;
using Messaging.Contracts;

namespace Orchestrator.Consumers;

/// <summary>
/// Endpoint/retry config seam for <see cref="ResultConsumer"/> (ORCH-RESULT-02 / ORCH-GATE-01). Binds
/// the STABLE shared competing-consumer queue <see cref="OrchestratorQueues.Result"/>
/// (<c>"orchestrator-result"</c>) — NOT the per-replica fan-out <c>"orchestrator"</c> endpoint and NOT
/// an <c>InstanceId</c>/<c>Temporary</c> endpoint (D-03): a result is consumed exactly once across the
/// consumer set, never broadcast.
/// <para>
/// <b>Middleware ORDER is load-bearing (Pitfall 2 / GitHub #1575):</b> <c>UseScheduledRedelivery</c>
/// is configured FIRST (outer — it removes-and-reschedules a thrown message past hydration), then
/// <c>UseMessageRetry(Immediate(3))</c> (inner — bounded immediate retry of true infra faults). A
/// gate-closed <see cref="GateClosedException"/> reaches the redelivery layer and is rescheduled; it
/// is deliberately NOT <c>Ignore&lt;&gt;</c>-listed.
/// </para>
/// </summary>
public sealed class ResultConsumerDefinition : ConsumerDefinition<ResultConsumer>
{
    public ResultConsumerDefinition() => EndpointName = OrchestratorQueues.Result;   // "orchestrator-result" — shared competing-consumer

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<ResultConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        // ORDER (Pitfall 2): scheduled redelivery OUTER, immediate retry INNER. A gate-closed throw is
        // rescheduled (5s/15s/30s/60s) to outlast hydration; after exhaustion a true outage routes to
        // _error. The interval policy is Claude's-discretion per CONTEXT A1 (~110s total).
        endpointConfigurator.UseScheduledRedelivery(r =>
            r.Intervals(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(60)));
        endpointConfigurator.UseMessageRetry(r => r.Immediate(3));
        // NOTE: do NOT Ignore<GateClosedException>() — it MUST reach the redelivery middleware (D-06).
    }
}
