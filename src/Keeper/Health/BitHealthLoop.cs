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
        bool? prevHealthy = null;                                   // D-06: null = no prior tick (first probe is always a transition → D-12)
        var delay = TimeSpan.FromSeconds(opts.Value.DelaySeconds);  // OQ-2: reuse Probe:DelaySeconds

        while (!stoppingToken.IsCancellationRequested)
        {
            var healthy = await probe.ProbeOnceAsync(stoppingToken);   // RedisException → false INSIDE; non-Redis propagates

            if (prevHealthy != healthy)                                // EDGE: transition (or first tick) only
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
                prevHealthy = healthy;
            }

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }              // graceful shutdown — NOT a probe failure
        }
    }
}
