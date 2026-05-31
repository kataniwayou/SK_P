using BaseConsole.Core.Health;
using MassTransit;
using Microsoft.Extensions.Logging;
using Orchestrator.Dispatch;
using Orchestrator.L1;
using ExecutionResult = Messaging.Contracts.ExecutionResult;   // disambiguate from MassTransit.ExecutionResult

namespace Orchestrator.Consumers;

/// <summary>
/// Result consumer (ORCH-RESULT-02 / ORCH-ADVANCE-01/02 / ORCH-RESULT-ACK-01 / ORCH-GATE-01). Consumes
/// an <see cref="ExecutionResult"/> off the shared competing-consumer queue
/// <c>orchestrator-result</c> and advances the workflow's DAG: it reads the completed step + each next
/// step from L1 ONLY (no Redis/L2 read), matches each next step's entry condition against the result's
/// outcome via <see cref="StepAdvancement"/>, and dispatches one continuation per match through
/// <see cref="IStepDispatcher"/> to <c>queue:{nextStep.ProcessorId}</c>.
/// <para>
/// <b>Gate-closed never-drop (D-06):</b> while <see cref="IStartupGate.IsReady"/> is false the consumer
/// THROWS <see cref="GateClosedException"/> (it does NOT ack-return) so the message reaches the
/// scheduled-redelivery middleware and is reprocessed after hydration completes (<c>MarkReady</c>).
/// This inverts the Phase 23 Start/Stop ack-drop — a one-time processor result must never be lost.
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
    IStartupGate gate,
    IWorkflowL1Store store,
    StepAdvancement advancement,
    IStepDispatcher dispatcher,
    ILogger<ResultConsumer> logger) : IConsumer<ExecutionResult>
{
    public async Task Consume(ConsumeContext<ExecutionResult> context)
    {
        if (!gate.IsReady)
        {
            // D-06: gate closed (hydration incomplete) — THROW so the message is scheduled-redelivered
            // after MarkReady, NEVER ack-return (a one-time result must not be dropped). The throw flows
            // to UseScheduledRedelivery; GateClosedException is deliberately NOT Ignore<>-listed.
            logger.LogInformation("Gate closed — redelivering ExecutionResult (no ack)");
            throw new GateClosedException();
        }

        var m = context.Message;

        // L1-only read (D-08): TryGet then the step map — no Upsert/Remove/stripe TryAcquire, no L2.
        if (!store.TryGet(m.WorkflowId, out var wf) || !wf.Steps.TryGetValue(m.StepId, out var completed))
        {
            // BUSINESS ack — unknown (wf,step) / drained / corrupt-projection (it never entered wf.Steps).
            // Mirrors WorkflowLifecycle.IsBusiness: log + return, NEVER throw (SPEC req 5).
            logger.LogInformation(
                "No L1 entry for ({WorkflowId}, {StepId}) — acking result (business)", m.WorkflowId, m.StepId);
            return;
        }

        foreach (var (stepId, step) in advancement.SelectNext(m.Outcome, completed, wf.Steps))
        {
            await dispatcher.DispatchAsync(
                m.WorkflowId, stepId, step.ProcessorId, step.Payload,
                m.CorrelationId, m.ExecutionId, m.EntryId, context.CancellationToken);
        }
        // returns normally -> ACK. An infra fault from Send propagates -> Immediate(3) -> _error.
    }
}
