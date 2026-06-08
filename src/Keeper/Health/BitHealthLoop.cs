using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Keeper.Recovery;

namespace Keeper.Health;

/// <summary>KEEP-01/02 (D-06): proactive BIT loop. Probes L2 each Probe:DelaySeconds tick; edge-triggered —
/// publishes PauseAll once on healthy→unhealthy and ResumeAll once on unhealthy→healthy, NOT per tick.</summary>
public sealed class BitHealthLoop(
    L2ProbeRecovery probe,
    IL2HealthGate gate,
    IBus bus,
    IOptions<ProbeOptions> opts,
    ILogger<BitHealthLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // D-06: null = no prior tick, so the first probe is always treated as a transition (D-12). When that first
        // probe is healthy this opens the (fail-safe-closed) gate AND broadcasts one startup ResumeAll. That startup
        // ResumeAll is INTENTIONAL and harmless: ResumeAllConsumer is idempotent (it only acts on Paused triggers),
        // so a Keeper start/restart simply re-asserts a healthy/open posture across replicas with no spurious resumes.
        // This is locked by the GREEN BitHealthLoopTests "first healthy tick → 1 ResumeAll" assertion — do NOT
        // suppress it (WR-02: resolved-by-documentation, behavior intentionally unchanged).
        bool? prevHealthy = null;
        var delay = TimeSpan.FromSeconds(opts.Value.DelaySeconds);  // OQ-2: reuse Probe:DelaySeconds

        while (!stoppingToken.IsCancellationRequested)
        {
            var healthy = await probe.ProbeOnceAsync(stoppingToken);   // RedisException → false INSIDE; non-Redis propagates

            if (prevHealthy != healthy)                                // EDGE: transition (or first tick) only
            {
                try
                {
                    if (healthy)
                    {
                        gate.Open();
                        await bus.Publish(new ResumeAll { CorrelationId = NewId.NextGuid() }, stoppingToken);
                        logger.LogInformation("L2 healthy — gate OPEN, ResumeAll broadcast");
                    }
                    else
                    {
                        gate.Close();
                        await bus.Publish(new PauseAll { CorrelationId = NewId.NextGuid() }, stoppingToken);
                        logger.LogWarning("L2 unhealthy — gate CLOSED, PauseAll broadcast");
                    }
                    prevHealthy = healthy;                              // advance the edge ONLY after the broadcast actually went out
                }
                catch (OperationCanceledException) { break; }          // graceful shutdown — NOT a publish failure
                catch (Exception ex)
                {
                    // WR-01 (D-06): a transient bus.Publish failure (broker blip) must NOT fault ExecuteAsync and
                    // permanently kill the standing health gate. prevHealthy is intentionally NOT advanced here, so
                    // the next tick re-broadcasts the same edge. gate.Open()/Close() are idempotent, so a half-applied
                    // transition (gate moved, broadcast failed) self-corrects on the retry.
                    logger.LogError(ex, "BIT transition broadcast failed — will retry next tick");
                }
            }

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }              // graceful shutdown — NOT a probe failure
        }
    }
}
