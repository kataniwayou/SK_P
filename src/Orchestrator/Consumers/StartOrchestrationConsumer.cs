using System.Text.Json;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging;
using Orchestrator.Messaging;
using StackExchange.Redis;

namespace Orchestrator.Consumers;

/// <summary>
/// Start consumer (ORCH-CON-03/04). For each WorkflowId in the body, reads the L2 root from Redis
/// and logs to the scheduler-job-start seam under the body-sourced correlated scope (already opened
/// by the BaseConsole.Core inbound consume filter — do NOT re-open). NO Redis writes, NO Quartz.
/// <para>
/// Ack split (MSG-ACK-01/02): a WorkflowId absent from L2 is a BUSINESS failure — logged + skipped
/// (consume completes → ack), never thrown. An INFRA fault (e.g. Redis unreachable) propagates from
/// <c>GetDatabase</c>/<c>StringGetAsync</c> and is bounded by the definition's retry → _error.
/// </para>
/// </summary>
public sealed class StartOrchestrationConsumer(
    IConnectionMultiplexer redis,
    ILogger<StartOrchestrationConsumer> logger) : IConsumer<StartOrchestration>
{
    public async Task Consume(ConsumeContext<StartOrchestration> context)
    {
        // The inbound filter (BaseConsole.Core) already opened the "CorrelationId" scope from the
        // body — do NOT re-open it here.
        var db = redis.GetDatabase();   // infra fault here THROWS → retry → _error (D-08 / MSG-ACK-02)
        foreach (var workflowId in context.Message.WorkflowIds)
        {
            var raw = await db.StringGetAsync(OrchestratorL2Keys.Root(workflowId));
            if (raw.IsNullOrEmpty)
            {
                // BUSINESS failure → log + continue (ack), NEVER throw (D-07 / MSG-ACK-01).
                logger.LogWarning(
                    "Workflow {WorkflowId} absent from L2 — business failure, acking", workflowId);
                continue;
            }

            // Deserialize the read-shape (camelCase frozen Phase 17 D-08).
            _ = JsonSerializer.Deserialize<WorkflowRootProjection>(raw!);

            // ORCH-CON-04 seam — NO Redis write, NO Quartz schedule.
            logger.LogInformation("Scheduler job start (seam) for {WorkflowId}", workflowId);
        }
        // returns normally → ACK
    }
}
