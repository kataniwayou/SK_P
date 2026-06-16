using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using Orchestrator.L1;
using Orchestrator.Observability;
using Orchestrator.Recovery;

namespace Orchestrator.Consumers;

/// <summary>
/// Generic per-item result-advancement base (ORCH-01 / D-07). Lifted verbatim from the retired
/// straight-through <c>ResultConsumer</c> body: it consumes ONE typed <see cref="IStepResult"/>
/// (<see cref="StepCompleted"/>/<see cref="StepFailed"/>/<see cref="StepCancelled"/>/<see cref="StepProcessing"/>)
/// off the shared competing-consumer queue <c>orchestrator-result</c> and advances the workflow's DAG.
/// On an L1 hit it delegates to the L2-gated <see cref="OrchestratorResultPipeline"/>, which matches each
/// next step's entry condition against the per-type <see cref="Outcome"/> knob via the orchestrator
/// step-advancement helper and dispatches one continuation per match to <c>queue:{nextStep.ProcessorId}</c>.
/// <para>
/// <b>The ONLY per-type knob is <see cref="Outcome"/> (D-07):</b> routing is by message type via the
/// abstract <see cref="StepOutcome"/> each sealed subclass returns — there is deliberately NO status
/// <c>if</c>/<c>switch</c> anywhere. The hardcoded <c>StepOutcome.Completed</c> of the old
/// <c>ResultConsumer</c> is the single line that became this abstract member. A Keeper-INJECT'd
/// <see cref="StepCompleted"/> is byte-indistinguishable from a direct processor completion — both are
/// the same record, land on the same queue, and are processed by the same <see cref="StepCompletedConsumer"/>.
/// </para>
/// <para>
/// <b>L1 guard + L2-gated pipeline (Phase 71 — reverses 24.1's L1-only posture):</b> there is no boot gate.
/// Processors (and the Keeper INJECT path) send results freely at any time. An L1 MISS is the DEFINED
/// graceful outcome — log + return (ack), uniformly for unknown / stopped-drained / not-yet-hydrated ids —
/// NEVER a throw, never a DLQ. On an L1 hit the consume path delegates to the L2-gated
/// <see cref="OrchestratorResultPipeline"/> (gate <c>exist L2[messageId]</c> → FORWARD / RECOVERY / cleanup),
/// which now OWNS the <c>StepAdvancement.SelectNext</c> iteration + the downstream dispatch that the retired
/// L1-only loop used to do inline.
/// </para>
/// <para>
/// <b>Business-ack vs infra-throw split:</b> an unknown <c>(workflowId, stepId)</c> is a BUSINESS outcome —
/// a clean <c>return</c> (ack), never a throw. A null <c>context.MessageId</c> is an INFRA fault → throw
/// (broker redelivery), NOT a drop (A1). The pipeline's send-owners propagate a send-exhaust (throw → broker
/// redelivery, no <c>_error</c> — the orchestrator-result endpoint carries no bus retry, Phase-53 D-01).
/// </para>
/// </summary>
/// <typeparam name="TMessage">The typed step-result record this consumer advances on.</typeparam>
public abstract class TypedResultConsumer<TMessage>(
    IWorkflowL1Store store,
    OrchestratorResultPipeline pipeline,
    OrchestratorMetrics metrics,
    ILogger<TMessage> logger) : IConsumer<TMessage>
    where TMessage : class, IStepResult
{
    /// <summary>The ONLY per-type knob (D-07): the outcome the pipeline's step-advancement matches
    /// successors against. No status if/switch lives anywhere — each sealed subclass returns its
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

        // A1 (Phase 71): context.MessageId is the gate key (the inbound result's broker MessageId). A null is
        // an INFRA fault → throw (broker redelivery), NOT a silent drop.
        if (context.MessageId is null)
            throw new InvalidOperationException("result envelope missing MessageId");

        // Phase 71 (reverses 24.1): delegate to the L2-gated pipeline. It runs the gate
        // (exist L2[messageId]) then FORWARD (owning the SelectNext iteration + downstream dispatch) or
        // RECOVERY or the gated cleanup tail. The per-type Outcome knob is threaded in unchanged. An infra
        // send-exhaust inside the pipeline propagates → broker redelivery (the endpoint has no bus retry).
        await pipeline.RunAsync(m, context.MessageId.Value, Outcome, completed, wf.Steps, context.CancellationToken);
        // returns normally -> ACK.
    }
}
