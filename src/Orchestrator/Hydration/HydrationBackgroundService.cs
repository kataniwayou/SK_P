using BaseConsole.Core.Health;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orchestrator.L1;
using Orchestrator.Messaging;
using StackExchange.Redis;

namespace Orchestrator.Hydration;

/// <summary>
/// Startup bulk hydration (ORCH-STARTUP-01, D-12/D-13): on boot it <c>SMEMBERS</c> the L2 parent
/// index, hydrates+schedules every workflow into L1 via <see cref="WorkflowLifecycle"/>, and only
/// then flips the startup gate via <see cref="IStartupGate.MarkReady"/> — readiness reflects
/// initial-hydration-complete, NOT bare host start (the base library's
/// <c>StartupCompletionService</c> is removed by type in <c>Program.cs</c>).
/// <para>
/// <b>Resilience (ORCH-ACK-01 / T-23-08):</b> a non-GUID parent-index member is skipped via
/// <c>Guid.TryParse</c>; a corrupt/absent workflow entry is caught by the per-workflow business
/// guard so the host stays up and the rest hydrate. Only INFRA faults (Redis unreachable) trip the
/// bounded-backoff retry — the gate NEVER opens while Redis is down (D-13), so readiness stays
/// Unhealthy and the platform restarts on the probe threshold rather than the host crash-looping.
/// </para>
/// </summary>
public sealed class HydrationBackgroundService(
    IConnectionMultiplexer redis,
    WorkflowLifecycle lifecycle,
    IStartupGate gate,
    ILogger<HydrationBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = InitialDelay;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var db = redis.GetDatabase(); // infra fault THROWS -> caught below -> bounded backoff
                var ids = await db.SetMembersAsync(OrchestratorL2Keys.ParentIndex());

                foreach (var raw in ids)
                {
                    if (!Guid.TryParse(raw, out var workflowId))
                    {
                        // Corrupt parent-index member — skip, host stays up (ORCH-ACK-01).
                        logger.LogWarning("Parent-index member {Member} is not a GUID — skipping (business)", raw);
                        continue;
                    }

                    try
                    {
                        await lifecycle.HydrateAndScheduleAsync(workflowId, stoppingToken);
                    }
                    catch (Exception ex) when (WorkflowLifecycle.IsBusiness(ex))
                    {
                        // Corrupt/missing entry -> skip, host stays up (ORCH-ACK-01 / T-23-08).
                        logger.LogWarning(ex, "Skipping corrupt workflow {WorkflowId} during hydration", workflowId);
                    }
                }

                gate.MarkReady(); // D-12 — gate flips HERE, only on initial-hydration-complete.
                logger.LogInformation("Initial hydration complete; startup gate marked ready.");
                return;
            }
            catch (Exception ex) when (WorkflowLifecycle.IsInfra(ex))
            {
                // Redis unreachable -> retry; gate stays CLOSED (D-13). Never crash-loop the host.
                logger.LogWarning(ex, "Redis unavailable during hydration; retrying in {Delay}", delay);
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return; // shutdown requested mid-backoff
                }

                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, MaxDelay.TotalSeconds));
            }
        }
    }
}
