using MassTransit;
using MassTransit.Middleware;   // Partitioner + Murmur3UnsafeHashGenerator (all MassTransit.Middleware, 8.5.5 — RESEARCH A1/A2)
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keeper.Recovery;

/// <summary>
/// D-04 / OQ-1 — runtime-binds the shared <c>keeper-recovery</c> receive endpoint via
/// <see cref="IReceiveEndpointConnector.ConnectReceiveEndpoint(string, Action{IBusRegistrationContext, IReceiveEndpointConfigurator})"/>
/// instead of static auto-config, so the returned <see cref="HostReceiveEndpointHandle"/> is
/// <c>Stop</c>/<c>Start</c>-able for gate-closed non-destructive consume (KEEP-04). This mirrors the
/// processor's <c>ProcessorStartupOrchestrator</c>, which runtime-binds its <c>{id:D}</c> dispatch endpoint
/// the same way. The three recovery consumers are registered in <c>Program.cs</c> with
/// <c>ExcludeFromConfigureEndpoints()</c> so EXACTLY ONE source (this binder) configures the endpoint
/// (Pitfall 1 — a static + connect collision would throw / shadow the connected handle).
/// <para>
/// <b>Endpoint config (symmetric exec-path posture):</b> the callback registers the SHARED
/// <see cref="Partitioner"/> on the <see cref="IKeeperRecoverable"/> 4-tuple (three <c>UsePartitioner&lt;T&gt;</c>)
/// and the three <c>ConfigureConsumer&lt;T&gt;</c> — and NOTHING else. There is NO <c>UseMessageRetry</c> and
/// NO <c>ConfigureError</c>: the in-code <see cref="RecoveryConsumerBase{TMessage}.Guard"/> RetryLoop is the
/// ONLY retry, and on exhaustion it re-throws out of <c>Consume</c> so the delivery falls through to RabbitMQ
/// nack-requeue (broker redelivery) — no <c>_error</c>, no <c>skp-dlq-1</c> dead-letter. This makes the
/// keeper-recovery endpoint symmetric with the processor dispatch / orchestrator result endpoints (A18
/// "no bus retry / no error transport on the execution path"). KEEP-04 pause/resume is SEPARATE (D-05): a
/// closed gate simply isn't consuming, so nothing redelivers while paused.
/// </para>
/// <para>
/// <b>Startup posture (Pitfall 3, decided):</b> the endpoint is connected STARTED (the simplest posture,
/// matching the processor analog which connects started). The fail-safe-closed gate plus Plan 03's
/// first-healthy-edge Start keeps the BIT model coherent; the brief window where the endpoint is consumable
/// before the first BIT probe is acceptable because consumption only mutates L2 when ops SUCCEED and the
/// first BIT tick fires within <c>Probe:DelaySeconds</c>. A stricter posture (connect stopped, Plan 03
/// conditionally starts) can be layered later without re-homing this config.
/// </para>
/// </summary>
public sealed class RecoveryEndpointBinder(
    IReceiveEndpointConnector connector,
    IOptions<RecoveryOptions> recovery,
    RecoveryEndpointHandle holder,
    ILogger<RecoveryEndpointBinder> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // One SHARED partitioner so the same 4-tuple key collides into the same slot across all three
        // message types (REINJECT/INJECT/DELETE for one exec serialize; different execs run in parallel).
        // 8.5.5's Partitioner + Murmur3UnsafeHashGenerator live in MassTransit.Middleware. The Guid-keyed
        // endpoint overload is used (ReinjectConsumerDefinition.PartitionGuid derives a stable Guid from the
        // canonical 4-tuple string — preserved as a public static so RecoveryPartitionFacts still pins it).
        var partition = new Partitioner(recovery.Value.PartitionCount, new Murmur3UnsafeHashGenerator());

        var handle = connector.ConnectReceiveEndpoint(KeeperQueues.Recovery, (ctx, cfg) =>
        {
            // No UseMessageRetry, no ConfigureError — symmetric with the processor dispatch / orchestrator
            // result endpoints. The in-code Guard RetryLoop is the only retry; a Guard-exhaust throw falls
            // through to broker nack-requeue (no in-process retry, no dead-letter, no skp-dlq-1).
            cfg.UsePartitioner<KeeperReinject>(partition, p => ReinjectConsumerDefinition.PartitionGuid(p.Message));
            cfg.UsePartitioner<KeeperInject>(partition, p => ReinjectConsumerDefinition.PartitionGuid(p.Message));
            cfg.UsePartitioner<KeeperDelete>(partition, p => ReinjectConsumerDefinition.PartitionGuid(p.Message));

            cfg.ConfigureConsumer<ReinjectConsumer>(ctx);
            cfg.ConfigureConsumer<InjectConsumer>(ctx);
            cfg.ConfigureConsumer<DeleteConsumer>(ctx);
        });

        await handle.Ready;                  // queue declared + consumers attached
        holder.Handle = handle;              // expose to Plan 03's BitHealthLoop Stop/Start driver
        logger.LogInformation("keeper-recovery endpoint connected; handle stored for pause/resume.");

        // Hold the BackgroundService alive until shutdown; the connected endpoint lives with the bus and is
        // torn down by the host on stop (do NOT StopAsync here — that removes the endpoint).
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { /* graceful shutdown */ }
    }
}
