using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging;
using Orchestrator.Dispatch;
using Orchestrator.L1;
using Quartz;

namespace Orchestrator.Scheduling;

/// <summary>
/// The per-workflow fire job (ORCH-FIRE-01, D-05/D-10). On each fire it mints a fresh per-fire
/// correlationId (D-05, <c>NewId.NextGuid()</c>), <c>Send</c>s one <see cref="EntryStepDispatch"/>
/// per entry step to <c>queue:{processorId}</c> (Send, NOT Publish — D-10), refreshes the L1 liveness
/// timestamp in-memory (zero L2 writes), and reschedules a fresh trigger for the same job off the next
/// Cronos occurrence.
/// <para>
/// <b>[DisallowConcurrentExecution]</b> guarantees a single jobKey never double-fires (Pitfall 4a).
/// Business cases (workflow gone from L1, entry step missing) are logged + skipped — they NEVER throw.
/// Only infra faults (broker unreachable on Send) propagate.
/// </para>
/// <para>
/// Primary-ctor DI mirrors <c>StartOrchestrationConsumer</c>: the body's
/// <see cref="EntryStepDispatch.CorrelationId"/> is the source of truth (D-01) — the bus-wide outbound
/// filter stamps the envelope from the ambient accessor, but the fire path sets the body correlationId
/// directly since it runs outside any inbound consume scope.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public sealed class WorkflowFireJob(
    IWorkflowL1Store store,
    IStepDispatcher dispatcher,
    WorkflowScheduler scheduler,
    TimeProvider timeProvider,
    ILogger<WorkflowFireJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var raw = context.MergedJobDataMap.GetString("workflowId");
        if (!Guid.TryParse(raw, out var workflowId))
        {
            logger.LogWarning("WorkflowFireJob fired without a parseable workflowId job-data ({Raw}) — skipping", raw);
            return;
        }

        if (!store.TryGet(workflowId, out var wf))
        {
            // BUSINESS no-op — workflow gone from L1 since the job was scheduled (e.g. stop/reload).
            logger.LogInformation("Workflow {WorkflowId} absent from L1 on fire — skipping (business)", workflowId);
            return;
        }

        // D-05: fresh correlationId per fire, shared by every entry-step dispatch in this fire.
        var correlationId = NewId.NextGuid();

        // D-06 / LOG-05: WorkflowFireJob runs OUTSIDE the consume pipeline (a Quartz job), so neither the
        // correlation filter nor the execution-scope filter ever sees it. Open the scope explicitly here —
        // AFTER the mint, the point where BOTH ids are known — so the fire logs correlate with the
        // round-trip they trigger (CorrelationId via CorrelationKeys.LogScope, the ONE place the job owns
        // it; WorkflowId via ExecutionLogScope.WorkflowId). The early returns above fire BEFORE the mint
        // and are deliberately NOT wrapped (Pattern 6). Ids go ONLY into the scope dictionary, never into a
        // message template (T-18-04 / T-29-08).
        using (logger.BeginScope(new Dictionary<string, object>
        {
            [CorrelationKeys.LogScope]     = correlationId.ToString(),
            [ExecutionLogScope.WorkflowId] = workflowId.ToString(),
        }))
        {
            foreach (var entryStepId in wf.EntryStepIds)
            {
                if (!wf.Steps.TryGetValue(entryStepId, out var step))
                {
                    // BUSINESS skip — entry step not in the L1 step map.
                    logger.LogWarning(
                        "Entry step {StepId} of workflow {WorkflowId} missing from L1 steps — skipping (business)",
                        entryStepId, workflowId);
                    continue;
                }

                // D-01: the build-and-Send shape now lives in IStepDispatcher (the single owner). The
                // initial fire passes executionId = entryId = Guid.Empty; an infra fault on Send propagates.
                await dispatcher.DispatchAsync(
                    workflowId, entryStepId, step.ProcessorId, step.Payload,
                    correlationId, Guid.Empty, Guid.Empty, context.CancellationToken);
            }

            // L1 liveness refresh — in-memory only (NO L2 write). Replace the immutable LivenessProjection
            // record preserving interval/status with an updated timestamp.
            var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
            var current = wf.Liveness;
            wf.Liveness = current is null
                ? new LivenessProjection(nowUtc, 0, "active")
                : current with { Timestamp = nowUtc };

            // Self-reschedule off the next Cronos occurrence (Pitfall 4b — new trigger for the existing job).
            if (CronInterval.NextOccurrence(wf.Cron, nowUtc) is not null)
            {
                await scheduler.RescheduleAsync(wf.JobId, wf.Cron, context.CancellationToken);
            }
        }
    }
}
