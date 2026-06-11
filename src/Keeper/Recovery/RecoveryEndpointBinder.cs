using MassTransit;
using MassTransit.Middleware;   // Partitioner + Murmur3UnsafeHashGenerator (8.5.5 namespace — RESEARCH A1/A2)
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
/// <b>Endpoint config (re-homed from the old <c>ReinjectConsumerDefinition.ConfigureConsumer</c>):</b> the
/// callback registers the SHARED <see cref="Partitioner"/> on the <see cref="IKeeperRecoverable"/> 4-tuple
/// (three <c>UsePartitioner&lt;T&gt;</c>), the three <c>ConfigureConsumer&lt;T&gt;</c>, and — branching on
/// <see cref="RecoveryOptions.ExhaustionPolicy"/> (KEEP-05 / D-01..D-03) — the retry posture:
/// <list type="bullet">
///   <item><see cref="ExhaustionPolicy.Dlq1"/> (default, D-02): <c>UseMessageRetry(r =&gt; r.Immediate(limit))</c>
///   — on exhaustion the give-up re-throws and the inherited consolidated error filter (BaseConsole.Core's
///   once-per-endpoint <c>ConfigureError</c>) moves it to <c>skp-dlq-1</c>.</item>
///   <item><see cref="ExhaustionPolicy.SustainedOutage"/> (D-03): an UNBOUNDED interval retry
///   (<c>r.Interval(int.MaxValue, 1s)</c>) so a thrown delivery is held/redelivered in-process and NEVER
///   reaches the error transport — no <c>skp-dlq-1</c> dead-letter, the accepted poison-op spin. OQ-2:
///   the consolidated error MOVE is wired globally per-endpoint by BaseConsole.Core; the only way to
///   suppress the dead-letter without a message scheduler is to never let the retry pipeline exhaust, which
///   the unbounded interval retry achieves (SustainedOutageFacts proves NO ConsolidatedFault is produced).</item>
/// </list>
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
    IOptions<RetryOptions> retry,
    IOptions<RecoveryOptions> recovery,
    RecoveryEndpointHandle holder,
    ILogger<RecoveryEndpointBinder> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryLimit = retry.Value.Limit;
        var policy = recovery.Value.ExhaustionPolicy;

        // One SHARED partitioner so the same 4-tuple key collides into the same slot across all three
        // message types (REINJECT/INJECT/DELETE for one exec serialize; different execs run in parallel).
        // 8.5.5's Partitioner + Murmur3UnsafeHashGenerator live in MassTransit.Middleware. The Guid-keyed
        // endpoint overload is used (ReinjectConsumerDefinition.PartitionGuid derives a stable Guid from the
        // canonical 4-tuple string — preserved as a public static so RecoveryPartitionFacts still pins it).
        var partition = new Partitioner(recovery.Value.PartitionCount, new Murmur3UnsafeHashGenerator());

        var handle = connector.ConnectReceiveEndpoint(KeeperQueues.Recovery, (ctx, cfg) =>
        {
            // KEEP-05 (D-01..D-03): the policy-conditional retry posture. Dlq1 = bounded immediate retry then
            // dead-letter via the inherited consolidated error filter; SustainedOutage = unbounded interval
            // retry that never exhausts (no dead-letter; accepted spin). KEEP-04 pause/resume is SEPARATE
            // (D-05): a closed gate simply isn't consuming, so nothing dead-letters in either mode.
            if (policy == ExhaustionPolicy.SustainedOutage)
            {
                // D-03: hold/requeue, no dead-letter. An unbounded interval retry keeps the thrown delivery
                // cycling in-process and never propagates a fault to the error transport, so the consolidated
                // skp-dlq-1 move never fires. The 1s interval bounds the spin's broker/CPU pressure.
                cfg.UseMessageRetry(r => r.Interval(int.MaxValue, TimeSpan.FromSeconds(1)));
            }
            else
            {
                // D-02 (default): the existing dead-letter latch. On exhaustion the give-up re-throws →
                // inherited ConsolidatedErrorTransportFilter → skp-dlq-1.
                cfg.UseMessageRetry(r => r.Immediate(retryLimit));
            }

            cfg.UsePartitioner<KeeperReinject>(partition, p => ReinjectConsumerDefinition.PartitionGuid(p.Message));
            cfg.UsePartitioner<KeeperInject>(partition, p => ReinjectConsumerDefinition.PartitionGuid(p.Message));
            cfg.UsePartitioner<KeeperDelete>(partition, p => ReinjectConsumerDefinition.PartitionGuid(p.Message));

            cfg.ConfigureConsumer<ReinjectConsumer>(ctx);
            cfg.ConfigureConsumer<InjectConsumer>(ctx);
            cfg.ConfigureConsumer<DeleteConsumer>(ctx);
        });

        await handle.Ready;                  // queue declared + consumers attached
        holder.Handle = handle;              // expose to Plan 03's BitHealthLoop Stop/Start driver
        logger.LogInformation(
            "keeper-recovery endpoint connected (ExhaustionPolicy={Policy}); handle stored for pause/resume.",
            policy);

        // Hold the BackgroundService alive until shutdown; the connected endpoint lives with the bus and is
        // torn down by the host on stop (do NOT StopAsync here — that removes the endpoint).
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { /* graceful shutdown */ }
    }
}
