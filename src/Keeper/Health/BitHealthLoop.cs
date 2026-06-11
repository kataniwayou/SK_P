using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Keeper.Recovery;

namespace Keeper.Health;

/// <summary>KEEP-01/02 (D-06): proactive BIT loop. Probes L2 each Probe:DelaySeconds tick; edge-triggered —
/// publishes PauseAll once on healthy→unhealthy and ResumeAll once on unhealthy→healthy, NOT per tick.
/// <para>
/// KEEP-04 (D-04): on the SAME edges, this loop also drives the keeper-recovery receive endpoint's
/// <c>Stop</c>/<c>Start</c> through the Plan-02 <see cref="RecoveryEndpointHandle"/> singleton — the unhealthy
/// edge <c>Stop</c>s consumption (messages accumulate non-destructively on the broker; gate-closed never
/// dequeue-and-drops) and the healthy edge <c>Start</c>s it (drain). The endpoint Stop/Start is ADDITIVE to the
/// existing gate.Open/Close + PauseAll/ResumeAll signalling on these edges (gate.Open/Close are still read by
/// the recovery consumers / tests and are NOT removed). All edge actions live inside the one WR-01 try, so a
/// Stop/Start throw leaves prevHealthy un-advanced and the next tick re-applies the (idempotent) edge.
/// </para></summary>
public sealed class BitHealthLoop(
    L2ProbeRecovery probe,
    IL2HealthGate gate,
    IBus bus,
    RecoveryEndpointHandle endpointHandle,
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
                        // D-04 / KEEP-04: resume keeper-recovery consumption (drain). Start(ct) returns a
                        // ReceiveEndpointHandle (NOT a Task) in 8.5.5; await its .Ready so a resume failure also
                        // lands in the WR-01 catch and leaves prevHealthy un-advanced. Null-guarded for the brief
                        // startup window before RecoveryEndpointBinder sets the handle (accepted residual T-52-11).
                        if (endpointHandle.Handle is { } h)
                            await h.ReceiveEndpoint.Start(stoppingToken).Ready;
                        await bus.Publish(new ResumeAll { CorrelationId = NewId.NextGuid() }, stoppingToken);
                        logger.LogInformation("L2 healthy — gate OPEN, recovery endpoint STARTED, ResumeAll broadcast");
                    }
                    else
                    {
                        gate.Close();
                        // D-04 / KEEP-04: stop keeper-recovery consumption (basic.cancel) so ops accumulate
                        // non-destructively on the broker while the gate is closed. Null-guarded (T-52-11).
                        if (endpointHandle.Handle is { } h)
                            await h.ReceiveEndpoint.Stop(stoppingToken);
                        await bus.Publish(new PauseAll { CorrelationId = NewId.NextGuid() }, stoppingToken);
                        logger.LogWarning("L2 unhealthy — gate CLOSED, recovery endpoint STOPPED, PauseAll broadcast");
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
