using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using Orchestrator.Dispatch;
using Orchestrator.L1;
using Orchestrator.Observability;

namespace Orchestrator.Consumers;

/// <summary>
/// Result consumer (ORCH-RESULT-02 / ORCH-ADVANCE-01/02 / ORCH-RESULT-ACK-01). Consumes
/// a <see cref="StepCompleted"/> off the shared competing-consumer queue
/// <c>orchestrator-result</c> and advances the workflow's DAG: it reads the completed step + each next
/// step from L1 ONLY (no Redis/L2 read), matches each next step's entry condition against the
/// (hardcoded) <see cref="StepOutcome.Completed"/> outcome via <see cref="StepAdvancement"/>, and
/// dispatches one continuation per match through <see cref="IStepDispatcher"/> to
/// <c>queue:{nextStep.ProcessorId}</c>.
/// <para>
/// <b>L1-only, lifecycle-agnostic (24.1 / D-24.1-05, supersedes D-06 / ORCH-GATE-01):</b> the boot
/// gate is REMOVED. Processors send results freely at any time; L1 is the SOLE arbiter (D-08
/// strengthened). An L1 hit advances; an L1 MISS is the DEFINED graceful outcome — log + return (ack),
/// uniformly for unknown / stopped-drained / not-yet-hydrated ids — never a throw, never a DLQ.
/// Accepted tradeoff: a result arriving in the boot window before its L1 entry is (re)hydrated is
/// gracefully acked (consumed, not advanced); the never-drop guarantee relaxes to
/// best-effort-against-L1.
/// </para>
/// <para>
/// <b>Business-ack vs infra-throw split (ORCH-RESULT-ACK-01, mirrors
/// <see cref="Orchestrator.Hydration.WorkflowLifecycle.IsBusiness"/>):</b> an unknown
/// <c>(workflowId, stepId)</c> and a completed step with no matching next step are BUSINESS outcomes —
/// a clean <c>return</c> (ack), never a throw. A corrupt-but-deserialized projection is likewise a
/// business skip: <see cref="StepAdvancement.SelectNext"/> is pure int comparison + dictionary lookup
/// and cannot throw on it, and a projection that fails to deserialize never lands in
/// <c>wf.Steps</c> (so it hits the unknown-step ack path). The ONLY exception that escapes is an INFRA
/// fault from the broker <c>Send</c> (there is no Redis read on this path) — it propagates to the
/// definition's bounded retry -> <c>_error</c>.
/// </para>
/// </summary>
public sealed class ResultConsumer(
    IWorkflowL1Store store,
    StepAdvancement advancement,
    IStepDispatcher dispatcher,
    OrchestratorMetrics metrics,
    ILogger<ResultConsumer> logger) : IConsumer<StepCompleted>
{
    public async Task Consume(ConsumeContext<StepCompleted> context)
    {
        var m = context.Message;

        // METRIC-04: count EVERY consumed result at the TOP, BEFORE the L1 read, so the graceful L1-miss
        // ack below is ALSO counted. ProcessorId is a non-nullable Guid (no guard). Tagged ProcessorId
        // only — no workflowId (cardinality); ambient sid.
        metrics.ResultConsumed.Add(1, new KeyValuePair<string, object?>("ProcessorId", m.ProcessorId.ToString("D")));

        // L1-only read (D-08): TryGet then the step map — no Redis read on this path (D-06e: the result
        // path is L1-only; Redis left the consumer). Only an L1 miss / dangling edge is a graceful
        // business-ack; an infra fault from the broker Send propagates -> Immediate(3) -> _error.
        if (!store.TryGet(m.WorkflowId, out var wf) || !wf.Steps.TryGetValue(m.StepId, out var completed))
        {
            // BUSINESS ack — unknown (wf,step) / drained / corrupt-projection (it never entered wf.Steps).
            // Mirrors WorkflowLifecycle.IsBusiness: log + return, NEVER throw (SPEC req 5).
            logger.LogInformation(
                "No L1 entry for ({WorkflowId}, {StepId}) — acking result (business)", m.WorkflowId, m.StepId);
            return;
        }

        // ---- Straight-through advance (D-03/D-06e/A4) ----
        // One StepCompleted = one item. SelectNext hardcodes StepOutcome.Completed (Pitfall 4 —
        // StepCompleted carries no Outcome by design; the per-type knob is Phase 46). For each matched
        // successor, dispatch one continuation carrying this result's Guid EntryId + a freshly regenerated
        // executionId (lineage). No dedup, no manifest, no Redis. An infra Send fault propagates.
        foreach (var (stepId, step) in advancement.SelectNext(StepOutcome.Completed, completed, wf.Steps))
            await dispatcher.DispatchAsync(
                m.WorkflowId, stepId, step.ProcessorId, step.Payload,
                m.CorrelationId, NewId.NextGuid(), m.EntryId, context.CancellationToken);
        // returns normally -> ACK. An infra fault from Send propagates -> Immediate(3) -> _error.
    }
}
