using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using Orchestrator.Dispatch;
using Orchestrator.L1;
using Orchestrator.Observability;

namespace Orchestrator.Consumers;

/// <summary>
/// Generic per-item result-advancement base (ORCH-01 / D-07). Lifted verbatim from the retired
/// straight-through <c>ResultConsumer</c> body: it consumes ONE typed <see cref="IStepResult"/>
/// (<see cref="StepCompleted"/>/<see cref="StepFailed"/>/<see cref="StepCancelled"/>/<see cref="StepProcessing"/>)
/// off the shared competing-consumer queue <c>orchestrator-result</c> and advances the workflow's DAG:
/// it reads the completed step + each next step from L1 ONLY (no Redis/L2 read), matches each next
/// step's entry condition against the per-type <see cref="Outcome"/> knob via
/// <see cref="StepAdvancement"/>, and dispatches one continuation per match through
/// <see cref="IStepDispatcher"/> to <c>queue:{nextStep.ProcessorId}</c>.
/// <para>
/// <b>The ONLY per-type knob is <see cref="Outcome"/> (D-07):</b> routing is by message type via the
/// abstract <see cref="StepOutcome"/> each sealed subclass returns — there is deliberately NO status
/// <c>if</c>/<c>switch</c> anywhere. The hardcoded <c>StepOutcome.Completed</c> of the old
/// <c>ResultConsumer</c> is the single line that became this abstract member. A Keeper-INJECT'd
/// <see cref="StepCompleted"/> is byte-indistinguishable from a direct processor completion — both are
/// the same record, land on the same queue, and are processed by the same <see cref="StepCompletedConsumer"/>.
/// </para>
/// <para>
/// <b>L1-only, lifecycle-agnostic (24.1 / D-24.1-05):</b> there is no boot gate. Processors (and the
/// Keeper INJECT path) send results freely at any time; L1 is the SOLE arbiter. An L1 hit advances; an
/// L1 MISS is the DEFINED graceful outcome — log + return (ack), uniformly for unknown /
/// stopped-drained / not-yet-hydrated ids — NEVER a throw, never a DLQ.
/// </para>
/// <para>
/// <b>Business-ack vs infra-throw split:</b> an unknown <c>(workflowId, stepId)</c> and a completed step
/// with no matching next step are BUSINESS outcomes — a clean <c>return</c> (ack), never a throw.
/// <see cref="StepAdvancement.SelectNext"/> is pure int comparison + dictionary lookup and cannot throw;
/// a projection that fails to deserialize never lands in <c>wf.Steps</c> (it hits the ack path). The ONLY
/// exception that escapes is an INFRA fault from the broker <c>Send</c> (there is no Redis read on this
/// path) — it propagates to the definition's bounded retry -> <c>_error</c>.
/// </para>
/// </summary>
/// <typeparam name="TMessage">The typed step-result record this consumer advances on.</typeparam>
public abstract class TypedResultConsumer<TMessage>(
    IWorkflowL1Store store,
    StepAdvancement advancement,
    IStepDispatcher dispatcher,
    OrchestratorMetrics metrics,
    ILogger<TMessage> logger) : IConsumer<TMessage>
    where TMessage : class, IStepResult
{
    /// <summary>The ONLY per-type knob (D-07): the outcome <see cref="StepAdvancement.SelectNext"/>
    /// matches successors against. No status if/switch lives anywhere — each sealed subclass returns its
    /// compile-time constant here.</summary>
    protected abstract StepOutcome Outcome { get; }

    public async Task Consume(ConsumeContext<TMessage> context)
    {
        var m = context.Message;

        // METRIC-04: count EVERY consumed result at the TOP, BEFORE the L1 read, so the graceful L1-miss
        // ack below is ALSO counted. ProcessorId is a non-nullable Guid (no guard). Tagged ProcessorId
        // only — no workflowId (cardinality); ambient sid.
        metrics.ResultConsumed.Add(1, new KeyValuePair<string, object?>("ProcessorId", m.ProcessorId.ToString("D")));

        // L1-only read (D-08): TryGet then the step map — no Redis read on this path (D-06e). Only an L1
        // miss / dangling edge is a graceful business-ack; an infra fault from the broker Send propagates
        // -> Immediate(3) -> _error.
        if (!store.TryGet(m.WorkflowId, out var wf) || !wf.Steps.TryGetValue(m.StepId, out var completed))
        {
            // BUSINESS ack — unknown (wf,step) / drained / corrupt-projection (it never entered wf.Steps).
            // Log + return, NEVER throw (SPEC req 5).
            logger.LogInformation(
                "No L1 entry for ({WorkflowId}, {StepId}) — acking result (business)", m.WorkflowId, m.StepId);
            return;
        }

        // ---- Per-item advance by Outcome (D-03/D-06e/A4/D-07) ----
        // One typed result = one item. SelectNext matches successors against the per-type Outcome knob (no
        // status if/switch). For each matched successor, dispatch one continuation carrying this result's
        // Guid EntryId + the inbound result's ExecutionId UNCHANGED — only stepId + ProcessorId change per
        // successor; WorkflowId/CorrelationId/ExecutionId/EntryId all flow through, preserving the
        // per-instance lineage from ENTRY through every continuation. No dedup, no manifest, no Redis. An
        // infra Send fault propagates.
        foreach (var (stepId, step) in advancement.SelectNext(Outcome, completed, wf.Steps))
            await dispatcher.DispatchAsync(
                m.WorkflowId, stepId, step.ProcessorId, step.Payload,
                m.CorrelationId, m.ExecutionId, m.EntryId, context.CancellationToken);
        // returns normally -> ACK. An infra fault from Send propagates -> Immediate(3) -> _error.
    }
}
