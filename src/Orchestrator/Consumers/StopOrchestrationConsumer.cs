using System.Text.Json;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging;
using Orchestrator.Messaging;
using StackExchange.Redis;

namespace Orchestrator.Consumers;

/// <summary>
/// Stop consumer (ORCH-CON-03/04) — mirrors <see cref="StartOrchestrationConsumer"/> exactly for
/// <see cref="StopOrchestration"/>. Reads the L2 root per WorkflowId and logs to the
/// scheduler-job-start seam; NO Redis writes, NO Quartz. Absent-from-L2 is a business failure
/// (logged + acked); infra faults propagate to the bounded retry pipeline.
/// </summary>
public sealed class StopOrchestrationConsumer(
    IConnectionMultiplexer redis,
    ILogger<StopOrchestrationConsumer> logger) : IConsumer<StopOrchestration>
{
    public async Task Consume(ConsumeContext<StopOrchestration> context)
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
            logger.LogInformation("Scheduler job stop (seam) for {WorkflowId}", workflowId);
        }
        // returns normally → ACK
    }
}
