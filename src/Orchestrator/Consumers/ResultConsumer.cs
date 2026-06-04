using MassTransit;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging;
using Orchestrator.Dispatch;
using Orchestrator.L1;
using Orchestrator.Observability;
using StackExchange.Redis;
using ExecutionResult = Messaging.Contracts.ExecutionResult;   // disambiguate from MassTransit.ExecutionResult

namespace Orchestrator.Consumers;

/// <summary>
/// Result consumer (ORCH-RESULT-02 / ORCH-ADVANCE-01/02 / ORCH-RESULT-ACK-01). Consumes
/// an <see cref="ExecutionResult"/> off the shared competing-consumer queue
/// <c>orchestrator-result</c> and advances the workflow's DAG: it reads the completed step + each next
/// step from L1 ONLY (no Redis/L2 read), matches each next step's entry condition against the result's
/// outcome via <see cref="StepAdvancement"/>, and dispatches one continuation per match through
/// <see cref="IStepDispatcher"/> to <c>queue:{nextStep.ProcessorId}</c>.
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
    IConnectionMultiplexer redis,
    OrchestratorMetrics metrics,
    ILogger<ResultConsumer> logger) : IConsumer<ExecutionResult>
{
    public async Task Consume(ConsumeContext<ExecutionResult> context)
    {
        var m = context.Message;

        // METRIC-04 (D-06): count EVERY consumed result at the TOP, BEFORE the dedup gate + L1 read, so
        // the dropped-on-Ack path AND the graceful L1-miss ack below are ALSO counted. ProcessorId is a
        // non-nullable Guid (no guard). Tagged ProcessorId only — no workflowId (cardinality); ambient sid.
        metrics.ResultConsumed.Add(1, new KeyValuePair<string, object?>("ProcessorId", m.ProcessorId.ToString("D")));

        // A Redis fault on GetDatabase / StringGetAsync / StringSetAsync is INFRA (no catch) -> propagates
        // to the definition's bounded Immediate(3) retry -> _error. Only an L1 miss / dangling edge is a
        // graceful business-ack (unchanged).
        var db = redis.GetDatabase();

        // ---- 0. Effect-first dedup gate (D-06, orchestrator hop) ----
        // An inbound result whose H is already "Ack" means the fan-out effect completed on a prior delivery
        // -> drop + broker-ack, produce NO further dispatch. H="" (Failed/Cancelled or legacy) never matches.
        if ((string?)await db.StringGetAsync(L2ProjectionKeys.Flag(m.H)) == "Ack")
            return;

        // L1-only read (D-08): TryGet then the step map — no Upsert/Remove/stripe TryAcquire, no L2 read here.
        if (!store.TryGet(m.WorkflowId, out var wf) || !wf.Steps.TryGetValue(m.StepId, out var completed))
        {
            // BUSINESS ack — unknown (wf,step) / drained / corrupt-projection (it never entered wf.Steps).
            // Mirrors WorkflowLifecycle.IsBusiness: log + return, NEVER throw (SPEC req 5).
            logger.LogInformation(
                "No L1 entry for ({WorkflowId}, {StepId}) — acking result (business)", m.WorkflowId, m.StepId);
            return;
        }

        // ---- 1. Manifest unbundle (D-08) ----
        // A Completed result carries a manifest at data[m.EntryId] (a JSON array of item content-addresses,
        // "[]" when empty). A Failed/Cancelled result carries EntryId="" — short-circuit to zero items (no
        // manifest read on an empty EntryId). A missing/garbled key degrades to zero items via the ?? guards
        // (T-31-11: server-written manifest, deserialize as string[], never throw on the business path).
        var items = string.IsNullOrEmpty(m.EntryId)
            ? System.Array.Empty<string>()
            : System.Text.Json.JsonSerializer.Deserialize<string[]>(
                  (string?)await db.StringGetAsync(L2ProjectionKeys.ExecutionData(m.EntryId)) ?? "[]")
              ?? System.Array.Empty<string>();

        // ---- 2. N x M fan-out (D-08) ----
        // For each manifest item x each matched successor (SelectNext = the M generator), dispatch one
        // continuation carrying the ITEM EntryId + a freshly regenerated executionId (lineage). The child H
        // is computed INSIDE DispatchAsync over (corr, wf, successorStep, successorProc, itemEntryId) — so an
        // orchestrator redelivery reproduces the SAME child H per (item, successor) and is deduped at the next
        // node (req-6 / Pitfall 5; executionId excluded). Merge falls out: different item EntryId -> different
        // H (both execute, distinct output, no override); identical item -> same H -> collapse (req-5). The
        // child H is independent of the PREDECESSOR step id (only the successor + item enter ComputeH).
        foreach (var itemEntryId in items)
            foreach (var (stepId, step) in advancement.SelectNext(m.Outcome, completed, wf.Steps))
                await dispatcher.DispatchAsync(
                    m.WorkflowId, stepId, step.ProcessorId, step.Payload,
                    m.CorrelationId, NewId.NextGuid(), itemEntryId, context.CancellationToken);

        // ---- 3. Effect-first flip, then ack (D-06/D-07) ----
        // Flip the inbound result's flag Pending->Ack ONLY after the fan-out effect. When.Exists = SET XX: a
        // false return means the Pending seed (the processor's outbound pre-write, Plan 03) was lost — NOT an
        // error, do NOT throw (D-07/T-31-14); the next-hop child-H dedup absorbs the residual.
        await db.StringSetAsync(L2ProjectionKeys.Flag(m.H), "Ack", when: When.Exists);
        // returns normally -> ACK. An infra fault from Send / Redis propagates -> Immediate(3) -> _error.
    }
}
