using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Observability;
using BaseProcessor.Core.Validation;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace BaseProcessor.Core.Processing;

/// <summary>
/// The framework's <see cref="IConsumer{EntryStepDispatch}"/> — the processor-half of the execution
/// round-trip (EXEC-02..EXEC-10 + CONFIG-02). It is the symmetric mirror of the orchestrator's
/// <c>ResultConsumer</c>: read+existence-check+validate input from <c>L2[data(entryId)]</c> (the
/// dispatch <see cref="EntryStepDispatch.Payload"/> is CONFIG, never input — D-07), invoke the
/// <c>ProcessAsync</c> seam via <see cref="BaseProcessor.ExecuteAsync"/> (EXEC-04), then per result
/// output-validate -> mint a new <c>Guid</c> entryId -> write the output to <c>L2[data(newEntryId)]</c>
/// with the CONFIG-02 TTL (D-15 write is INFRA — it throws) -> mint a per-result executionId -> build one
/// <see cref="StepCompleted"/>, and Send each result one-by-one to <c>queue:orchestrator-result</c>,
/// acking the dispatch only after all sends.
/// <para>
/// Phase 43 (D-03): straight-through only. The retired <c>flag[H]</c>/CAS dedup (RETIRE-01) and the
/// content-addressing + manifest fan-out (RETIRE-02) are gone — per result it mints a real <c>Guid</c>
/// entryId, writes <c>L2[data(entryId)]</c>, and sends ONE typed <see cref="IStepResult"/> record. The
/// Pre/In/Post pipeline is Phase 44; this is minimal compile-and-pass on the new contracts.
/// </para>
/// <para>
/// <b>Business-ack vs infra-throw (D-15, mirrors <c>WorkflowLifecycle.IsBusiness</c>):</b>
/// missing-input, input/output schema-validation failure, an empty result list, and a caught
/// transform exception are BUSINESS outcomes — a <see cref="StepFailed"/>/<see cref="StepCancelled"/>/
/// no-message is sent and the dispatch is acked, never a throw. The ONLY faults that escape are INFRA: an L2 read fault,
/// the L2 OUTPUT-WRITE fault (D-15 — never silently lose output), and a broker <c>Send</c> fault —
/// each propagates to the bounded <c>Immediate(3)</c> retry (configured at the runtime bind, Plan 03)
/// -> <c>_error</c>.
/// </para>
/// <para>
/// <b>At-least-once tradeoff (D-15):</b> a fault mid-batch (output write or Send) re-runs the whole
/// dispatch on retry, re-sending already-sent results. The orchestrator's <c>ResultConsumer</c> is
/// L1-idempotent (an already-advanced step / unknown id is a graceful business-ack), so the duplicate
/// is safe.
/// </para>
/// </summary>
public sealed class EntryStepDispatchConsumer(
    IConnectionMultiplexer redis,
    IProcessorContext context,
    BaseProcessor processor,
    IOptions<ProcessorLivenessOptions> options,
    ISendEndpointProvider sendProvider,
    ProcessorMetrics metrics,
    ILogger<EntryStepDispatchConsumer> logger) : IConsumer<EntryStepDispatch>
{
    public async Task Consume(ConsumeContext<EntryStepDispatch> ctx)
    {
        var dispatch = ctx.Message;

        // METRIC-05 / D-07: count EVERY dispatch consumed at the entry point, tagged ProcessorId.
        // An Immediate(3) retry re-runs Consume → re-increments — accepted rate noise (D-07).
        // context.Id is Guid? but Consume runs ONLY post-MarkHealthy (the runtime binds queue:{id:D}
        // AFTER Healthy via ProcessorStartupOrchestrator), so identity IS resolved here — the bang is
        // justified (Landmine 2; not a NRE). Tag key is literal PascalCase "ProcessorId" (the collector
        // preserves tag-key case so `sum by (ProcessorId)` works); value .ToString("D") matches queue:{id:D}.
        // NO workflowId (T-30-04 cardinality). service_instance_id is ambient from Plan 01.
        metrics.DispatchConsumed.Add(1,
            new KeyValuePair<string, object?>("ProcessorId", context.Id!.Value.ToString("D")));

        var ct = ctx.CancellationToken;
        var opts = options.Value;

        // A Redis fault on GetDatabase / StringGetAsync / StringSetAsync is INFRA and propagates
        // (no catch) -> Immediate(3) retry. Only ProcessAsync exceptions are caught (business).
        var db = redis.GetDatabase();

        // ---- 1. Input resolution + validation (EXEC-02/03, D-07, source-step keyed on the sentinel) ----
        // D-03/D-07: the L2 READ-skip routes through the single SourceStep.IsSource predicate (never an
        // ad-hoc == Guid.Empty) — a Guid.Empty (source-step) dispatch deterministically skips the read
        // rather than constructing a malformed key (T-43-06). An absent L2 value for a required-input
        // step is a business Failed (unchanged outcome). The dedup gate (flag[H]) is RETIRED (RETIRE-01).
        string inputData;
        var raw = SourceStep.IsSource(dispatch.EntryId)
            ? RedisValue.Null
            : await db.StringGetAsync(L2ProjectionKeys.ExecutionData(dispatch.EntryId));
        if (raw.IsNullOrEmpty)
        {
            if (!string.IsNullOrWhiteSpace(context.InputDefinition))
            {
                // Required-input step, but no input present in L2 — Failed BEFORE ProcessAsync (D-07).
                // EntryId is emitted as a scope VALUE under the fixed ExecutionLogScope.EntryId key by the
                // inbound execution-scope filter — never interpolated into the message template (T-31-08).
                logger.LogInformation(
                    "Dispatch {CorrelationId}: input absent from L2 — Failed (business)",
                    dispatch.CorrelationId);
                await SendResult(BuildFailed(dispatch, "Input data not found in L2 for entryId."), "failed");
                return;
            }

            // Source step (InputDefinition null) -> empty input is fine.
            inputData = string.Empty;
        }
        else
        {
            inputData = raw.ToString();
        }

        if (!string.IsNullOrEmpty(inputData)
            && !ProcessorJsonSchemaValidator.TryValidate(context.InputDefinition, inputData, out var inErrors))
        {
            // Input failed its definition — Failed BEFORE ProcessAsync (EXEC-03).
            logger.LogInformation(
                "Dispatch {CorrelationId}: input failed inputDefinition — Failed (business)", dispatch.CorrelationId);
            await SendResult(BuildFailed(dispatch, string.Join("; ", inErrors)), "failed");
            return;
        }

        // ---- 2. Invoke the seam (EXEC-04) with framework-owned outcomes (D-09) ----
        IReadOnlyList<ProcessResult> results;
        try
        {
            results = await processor.ExecuteAsync(inputData, dispatch.Payload, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Token-tripped cancellation is a business Cancelled outcome (EXEC-08).
            await SendResult(BuildCancelled(dispatch, "Processing was cancelled."), "cancelled");
            return;
        }
        catch (Exception ex)
        {
            // Any other transform exception is a business Failed carrying the message (EXEC-08).
            await SendResult(BuildFailed(dispatch, ex.Message), "failed");
            return;
        }

        // ---- 3. Per-result output-validate -> mint Guid entryId -> write-L2(TTL) -> send ONE Step* (D-03) ----
        // Straight-through (RETIRE-02): no content-addressing, no manifest, no outbound flag pre-write, no
        // CAS flip. Per result mint a fresh Guid entryId (the real L2 data key — D-06a), write
        // L2[data(entryId)], and send one StepCompleted carrying that Guid. An EMPTY result list sends
        // nothing (the orchestrator simply observes no continuation); a schema-failing blob is a
        // whole-dispatch business Failed. Duplicates on retry are tolerated downstream (at-least-once).
        foreach (var r in results)
        {
            if (!ProcessorJsonSchemaValidator.TryValidate(context.OutputDefinition, r.OutputData, out var outErrors))
            {
                // A blob that fails its definition is a whole-dispatch business Failed: send StepFailed
                // (EntryId = Guid.Empty) and return. Nothing is written for this dispatch.
                await SendResult(BuildFailed(dispatch, string.Join("; ", outErrors)), "failed");
                return;
            }

            var newEntryId  = NewId.NextGuid();   // D-06a: the REAL Guid data key carried on StepCompleted
            var executionId = NewId.NextGuid();   // mint once — scoped value == sent StepCompleted.ExecutionId

            // LOG-04/LOG-01: a nested BeginScope carrying the MINTED ExecutionId + Guid entryId. MEL
            // inner-overrides-outer, so the write LogRecord on this Completed path reports these ids rather
            // than the inbound ones the outer execution-scope filter carried (D-05). MINIMAL: wraps ONLY the
            // L2 write + log line. Values are .ToString() under the fixed ExecutionLogScope keys — never
            // interpolated into a template (T-18-04).
            using (logger.BeginScope(new Dictionary<string, object>
            {
                [ExecutionLogScope.ExecutionId] = executionId.ToString(),
                [ExecutionLogScope.EntryId]     = newEntryId.ToString(),
            }))
            {
                // D-15: the OUTPUT WRITE is INFRA — NO catch. A transient Redis fault here throws so the
                // whole dispatch retries rather than emitting a Completed with no L2 data behind it.
                await db.StringSetAsync(
                    L2ProjectionKeys.ExecutionData(newEntryId),
                    r.OutputData,
                    expiry: TimeSpan.FromSeconds(opts.ExecutionDataTtlSeconds));

                logger.LogInformation(
                    "Dispatch {CorrelationId}: step output written (scoped execution ids)",
                    dispatch.CorrelationId);
            }

            // One result = one StepCompleted carrying the real Guid entryId. An infra Send fault PROPAGATES
            // (D-15) — a retry re-runs the whole dispatch (at-least-once; the orchestrator is L1-idempotent).
            await SendResult(BuildCompleted(dispatch, executionId, newEntryId), "completed");
        }
        // returns normally -> ACK only after all sends (D-15).
    }

    /// <summary>
    /// D-08 — the SINGLE Send owner. EVERY result (the early Failed/Cancelled returns AND the per-result
    /// Completed loop) flows through here so no send path is uncounted. Resolves the result endpoint,
    /// sends one business-outcome <see cref="IStepResult"/> record, then (METRIC-05 / D-04) increments
    /// <c>processor_result_sent</c> AFTER the confirmed Send, tagged <c>ProcessorId</c> + <c>outcome</c>.
    /// Sent with <see cref="CancellationToken.None"/> so a Cancelled/Failed signal is delivered even when
    /// the inbound dispatch token has tripped — the business outcome must always reach the orchestrator
    /// (an infra Send fault still propagates, D-15, BEFORE the counter increments — only confirmed sends count).
    /// <para>
    /// Phase 43 (Pitfall 4): the Step* records carry NO <c>Outcome</c> wire field, so the metric
    /// <c>outcome</c> tag is supplied explicitly by each build path (<paramref name="outcomeLabel"/>) ∈
    /// {completed, failed, cancelled} — 3 bounded values, no filter needed (T-30-05). The bang on
    /// <c>context.Id</c> is justified: SendResult only runs inside Consume, which runs post-MarkHealthy
    /// (Landmine 2). NO workflowId (T-30-04 cardinality).
    /// </para>
    /// </summary>
    private async Task SendResult(IStepResult result, string outcomeLabel)
    {
        var endpoint = await sendProvider.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"));
        await endpoint.Send((object)result, CancellationToken.None);
        metrics.ResultSent.Add(1,
            new KeyValuePair<string, object?>("ProcessorId", context.Id!.Value.ToString("D")),
            new KeyValuePair<string, object?>("outcome", outcomeLabel));
    }

    // ---- Builders (Pattern 3 / D-06a/D-06b): inherit ids, copy body CorrelationId (EXEC-10), mint ExecutionId ----

    // executionId is minted ONCE in the Completed-path loop and passed in (NOT minted here) so the
    // nested BeginScope value and this result's ExecutionId are the SAME value (LOG-04 — the scoped id
    // equals the sent StepCompleted.ExecutionId the line reports). EntryId = the freshly minted Guid data
    // key (D-06a — the REAL key, no Guid.Empty default on StepCompleted).
    private static StepCompleted BuildCompleted(EntryStepDispatch d, Guid executionId, Guid newEntryId) =>
        new(d.WorkflowId, d.StepId, d.ProcessorId)
        {
            CorrelationId = d.CorrelationId,
            ExecutionId = executionId,
            EntryId = newEntryId,
        };

    private static StepFailed BuildFailed(EntryStepDispatch d, string error) =>
        new(d.WorkflowId, d.StepId, d.ProcessorId)
        {
            CorrelationId = d.CorrelationId,
            ExecutionId = NewId.NextGuid(),
            EntryId = Guid.Empty,
            ErrorMessage = error,
        };

    private static StepCancelled BuildCancelled(EntryStepDispatch d, string msg) =>
        new(d.WorkflowId, d.StepId, d.ProcessorId)
        {
            CorrelationId = d.CorrelationId,
            ExecutionId = NewId.NextGuid(),
            EntryId = Guid.Empty,
            CancellationMessage = msg,
        };
}
