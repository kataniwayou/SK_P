using BaseConsole.Core.Configuration;
using BaseConsole.Core.Messaging;
using MassTransit;
using MassTransit.Middleware;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BaseConsole.Core.DependencyInjection;

/// <summary>
/// MassTransit bus skeleton (CONSOLE-04). Registers the RabbitMQ bus, the Singleton ambient
/// <see cref="ICorrelationAccessor"/>, and all three correlation filters bus-wide (the inbound
/// consume filter + both outbound send/publish filters). The concrete console supplies only the
/// consumer-registration lambda (D-06 — base = infra, concrete = consumers; empty this phase,
/// Phase 19 adds consumers).
/// </summary>
public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Wires the bus, the accessor, and the correlation pipeline. RabbitMQ host/credentials are
    /// read via the <c>cfg.Require</c> fail-fast helper (T-18-05: the missing-key exception names
    /// the key, never the value). The auto-registered bus health check keeps its default tags
    /// <c>["ready","masstransit"]</c> (CONSOLE-HEALTH-03) — the bus-check tag-override hook is
    /// deliberately NOT called (custom tags would REPLACE the defaults).
    /// <para>
    /// The optional <paramref name="configureBus"/> callback (Phase 24 D-06) exposes the
    /// <see cref="IBusRegistrationContext"/> AND the RabbitMQ bus-factory configurator so a concrete
    /// console can wire bus-factory-level middleware — e.g. a message scheduler for gate-closed
    /// scheduled redelivery — without the base library taking on any new infra dependency. The
    /// registration context is forwarded because some MassTransit bus-factory middleware needs it. The
    /// callback is invoked AFTER the correlation filters and BEFORE <c>ConfigureEndpoints</c>;
    /// passing null leaves behavior identical to prior phases.
    /// </para>
    /// </summary>
    public static IServiceCollection AddBaseConsoleMessaging(
        this IServiceCollection services, IConfiguration cfg,
        Action<IBusRegistrationConfigurator> configureConsumers,
        Action<IBusRegistrationContext, IRabbitMqBusFactoryConfigurator>? configureBus = null)
    {
        var rabbitHost = cfg.Require("RabbitMq:Host");
        var rabbitUser = cfg.Require("RabbitMq:Username");
        var rabbitPass = cfg.Require("RabbitMq:Password");

        services.AddSingleton<ICorrelationAccessor, AsyncLocalCorrelationAccessor>();

        services.AddMassTransit(x =>
        {
            configureConsumers(x);   // concrete seam — EMPTY this phase (Phase 19 adds consumers)

            // DLQ-04 (D-06): consolidate the post-exhaustion _error MOVE to ONE shared skp-dlq-1 across ALL
            // consoles (processor + orchestrator + Keeper). Lives in the once-per-endpoint, framework-deduped
            // AddConfigureEndpointsCallback (Pitfall 3 — error middleware is PER-ENDPOINT; it does NOT
            // double-register with the existing per-consumer UseMessageRetry). KEEP the fault-generation
            // filter upstream — Keeper's whole recovery model rides the Fault<T> pub/sub stream (removing it
            // would break Phases 33-35). Only the default move TARGET is replaced with the consolidated dest.
            x.AddConfigureEndpointsCallback((context, name, e) =>
            {
                e.ConfigureError(ep =>
                {
                    ep.UseFilter(new GenerateFaultFilter());                  // keep Fault<T> publication (Keeper rides it)
                    ep.UseFilter(new ConsolidatedErrorTransportFilter());     // move exhausted msg → skp-dlq-1 (replaces {queue}_error)
                });
            });

            x.UsingRabbitMq((ctx, c) =>
            {
                c.Host(rabbitHost, h => { h.Username(rabbitUser); h.Password(rabbitPass); });
                c.UseConsumeFilter(typeof(InboundCorrelationConsumeFilter<>), ctx);     // CORR-01 (OUTER) bus-wide (open-generic)
                c.UseConsumeFilter(typeof(InboundExecutionScopeConsumeFilter<>), ctx);  // LOG-02 (INNER) execution-id scope (D-02)
                c.UseSendFilter(typeof(OutboundCorrelationSendFilter<>), ctx);        // CORR-02
                c.UsePublishFilter(typeof(OutboundCorrelationPublishFilter<>), ctx);  // CORR-02

                // DLQ-1 (D-05/07 + GAP-49-3/D-09): declare the ONE consolidated dead-letter queue as a PASSIVE
                // parking queue — declared exactly ONCE, with a 7-day message TTL (604800000 ms — RabbitMQ
                // ms-as-int semantics, RESEARCH A4) — and bound to its fanout exchange skp-dlq-1, WITHOUT a
                // consuming receive endpoint. It is an operator/forensic sink, present in BOTH close-gate
                // snapshots so it does not drift the net-zero triple-SHA (Pitfall 1).
                //
                // WHY a topology BindQueue and NOT a ReceiveEndpoint (GAP-49-3 fix): a ReceiveEndpoint is a
                // CONSUMING endpoint — MassTransit binds a competing consumer to the queue and immediately
                // dequeues every arriving message. The ConsolidatedErrorTransportFilter forwards a
                // ConsolidatedFault to exchange:skp-dlq-1, but NO consumer is registered for it, so MassTransit
                // routed the unhandled message to skp-dlq-1_skipped (live depth 3) instead of letting it PARK
                // in skp-dlq-1 (depth 0). Declaring the queue + its fanout exchange binding through the publish
                // topology (the documented way to "create alternate/dead-letter exchanges and queues") leaves
                // skp-dlq-1 with NO consumer, so a message sent to exchange:skp-dlq-1 PARKS observably in the
                // ttl'd queue (SC2 STATE 2: skp-dlq-1 depth dlqBefore -> dlqBefore+1, NOT _skipped).
                //
                // DeployPublishTopology = true deploys this binding at bus START (not lazily on first publish):
                // the ConsolidatedFault is SENT (not published) by the filter, so without eager deploy the
                // exchange:skp-dlq-1 fanout would have no bound queue and the forwarded fault would be dropped.
                // The TTL arg applies only at queue-create time; if a skp-dlq-1 ever exists on the broker with
                // DIFFERENT args (e.g. from the old ReceiveEndpoint), an operator deletes it ONCE so this
                // passive declaration recreates it cleanly (Pitfall 2 — operator action, NOT code). The filter
                // still sends to exchange:skp-dlq-1 (its Dlq1Uri) and is unchanged — it never re-declares the
                // queue with default args, so the GAP-49-1 RabbitMQ 406 'inequivalent arg x-message-ttl'
                // poison-loop fix is preserved.
                c.DeployPublishTopology = true;
                c.Publish<ConsolidatedFault>(p =>
                {
                    // Exclude the type-named ConsolidatedFault exchange (nothing publishes this type — the
                    // filter SENDS it to exchange:skp-dlq-1); this hook exists only to register the
                    // skp-dlq-1 exchange->queue binding below.
                    p.BindQueue(ConsolidatedErrorTransportFilter.Dlq1, ConsolidatedErrorTransportFilter.Dlq1, q =>
                    {
                        q.SetQueueArgument("x-message-ttl", (int)TimeSpan.FromDays(7).TotalMilliseconds);
                    });
                });

                configureBus?.Invoke(ctx, c);   // OPTIONAL bus-factory seam (Phase 24 D-06) — scheduler/redelivery
                                                // wiring (e.g. c.UseDelayedMessageScheduler()) goes here, BEFORE
                                                // endpoints bind. Default null preserves all existing call sites;
                                                // base = infra firewall intact (forwards only ctx + the configurator).
                c.ConfigureEndpoints(ctx);
            });
        });

        return services;
    }
}
