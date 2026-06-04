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
using ExecutionResult = Messaging.Contracts.ExecutionResult;   // disambiguate from MassTransit.ExecutionResult

namespace BaseProcessor.Core.Processing;

/// <summary>
/// The framework's <see cref="IConsumer{EntryStepDispatch}"/> — the processor-half of the execution
/// round-trip (EXEC-02..EXEC-10 + CONFIG-02). It is the symmetric mirror of the orchestrator's
/// <c>ResultConsumer</c>: read+existence-check+validate input from <c>L2[data(entryId)]</c> (the
/// dispatch <see cref="EntryStepDispatch.Payload"/> is CONFIG, never input — D-07), invoke the
/// <c>ProcessAsync</c> seam via <see cref="BaseProcessor.ExecuteAsync"/> (EXEC-04), then per result
/// output-validate -> mint a new entryId -> write the output to <c>L2[data(newEntryId)]</c> with the
/// CONFIG-02 TTL (D-15 write is INFRA — it throws) -> mint a per-result executionId -> build one
/// <see cref="ExecutionResult"/>, and Send each result one-by-one to <c>queue:orchestrator-result</c>,
/// acking the dispatch only after all sends.
/// <para>
/// <b>Business-ack vs infra-throw (D-15, mirrors <c>WorkflowLifecycle.IsBusiness</c>):</b>
/// missing-input, input/output schema-validation failure, an empty result list, and a caught
/// transform exception are BUSINESS outcomes — a <c>Failed</c>/<c>Cancelled</c>/no-message is sent
/// and the dispatch is acked, never a throw. The ONLY faults that escape are INFRA: an L2 read fault,
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

        // ---- 1. Input resolution + validation (EXEC-02/03, D-07, no-input source decision) ----
        string inputData;

        if (string.IsNullOrEmpty(dispatch.EntryId))
        {
            // No entryId => source processor: do NOT read L2 (locked no-input decision).
            inputData = string.Empty;

            if (!string.IsNullOrWhiteSpace(context.InputDefinition))
            {
                // A required-input step arrived with no entryId — Failed BEFORE ProcessAsync (EXEC-03).
                logger.LogInformation(
                    "Dispatch {CorrelationId}: required input but no entryId — Failed (business)",
                    dispatch.CorrelationId);
                await SendResult(BuildFailed(dispatch, "Input data not found: no entryId provided for a step requiring input."));
                return;
            }
        }
        else
        {
            var raw = await db.StringGetAsync(L2ProjectionKeys.ExecutionData(dispatch.EntryId));
            if (raw.IsNullOrEmpty)
            {
                if (!string.IsNullOrWhiteSpace(context.InputDefinition))
                {
                    // Required input absent from L2 — Failed BEFORE ProcessAsync (D-07).
                    logger.LogInformation(
                        "Dispatch {CorrelationId}: input absent from L2 for entryId {EntryId} — Failed (business)",
                        dispatch.CorrelationId, dispatch.EntryId);
                    await SendResult(BuildFailed(dispatch, "Input data not found in L2 for entryId."));
                    return;
                }

                // No input definition — absent data is fine (no-input def): pass "".
                inputData = string.Empty;
            }
            else
            {
                inputData = raw.ToString();
            }
        }

        if (!string.IsNullOrEmpty(inputData)
            && !ProcessorJsonSchemaValidator.TryValidate(context.InputDefinition, inputData, out var inErrors))
        {
            // Input failed its definition — Failed BEFORE ProcessAsync (EXEC-03).
            logger.LogInformation(
                "Dispatch {CorrelationId}: input failed inputDefinition — Failed (business)", dispatch.CorrelationId);
            await SendResult(BuildFailed(dispatch, string.Join("; ", inErrors)));
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
            await SendResult(BuildCancelled(dispatch, "Processing was cancelled."));
            return;
        }
        catch (Exception ex)
        {
            // Any other transform exception is a business Failed carrying the message (EXEC-08).
            await SendResult(BuildFailed(dispatch, ex.Message));
            return;
        }

        // ---- 3. Per-result output-validate -> mint -> write-L2(TTL) -> build (EXEC-05/06) ----
        var built = new List<ExecutionResult>(results.Count);
        foreach (var r in results)
        {
            if (!ProcessorJsonSchemaValidator.TryValidate(context.OutputDefinition, r.OutputData, out var outErrors))
            {
                // Output failed its definition — Failed, EntryId stays the empty string, nothing written (EXEC-05).
                built.Add(BuildFailed(dispatch, string.Join("; ", outErrors)));
                continue;
            }

            var newEntryId  = NewId.NextGuid();
            var executionId = NewId.NextGuid();   // mint once — scoped value == sent ExecutionResult.ExecutionId

            // LOG-04/LOG-01: a nested BeginScope carrying the MINTED ExecutionId + output EntryId. MEL
            // inner-overrides-outer, so the write/send LogRecord on this Completed path reports these
            // minted ids rather than the inbound Guid.Empty the outer execution-scope filter skipped
            // (D-05). MINIMAL: wraps ONLY the L2 write + this result's build — the early Failed/Cancelled
            // SendResult paths stay outside (Pitfall 2). Values are .ToString() under the fixed
            // ExecutionLogScope keys — never interpolated into a template (T-18-04).
            using (logger.BeginScope(new Dictionary<string, object>
            {
                [ExecutionLogScope.ExecutionId] = executionId.ToString(),
                [ExecutionLogScope.EntryId]     = newEntryId.ToString(),
            }))
            {
                // D-15: the OUTPUT WRITE is INFRA — NO catch. A transient Redis fault here throws so the
                // whole dispatch retries rather than emitting a Completed with no L2 data behind it.
                // Plan 02 shim: newEntryId is still a minted Guid LOCAL this plan; render it to a string
                // content address via .ToString("D") to feed the now-string ExecutionData(string) +
                // BuildCompleted. Plan 03 replaces the mint with MessageIdentity.HashBlob (a real 64-hex).
                await db.StringSetAsync(
                    L2ProjectionKeys.ExecutionData(newEntryId.ToString("D")),
                    r.OutputData,
                    expiry: TimeSpan.FromSeconds(opts.ExecutionDataTtlSeconds));

                built.Add(BuildCompleted(dispatch, executionId, newEntryId.ToString("D")));

                // LOG-04/LOG-01: the Completed-path LogRecord. It is emitted INSIDE this nested scope so it
                // carries the MINTED ExecutionId + output EntryId (this scope), AND — via the bus-wide
                // InboundExecutionScopeConsumeFilter that wraps this runtime-connected consume (its outer
                // scope is open here) — the inbound WorkflowId/StepId/ProcessorId. The five execution ids
                // are reported ONLY as scope VALUES under the fixed ExecutionLogScope keys; the message text
                // references CorrelationId via template like the sibling business-Failed lines and NEVER
                // interpolates an execution id (T-18-04).
                logger.LogInformation(
                    "Dispatch {CorrelationId}: step Completed — output written, result built (scoped execution ids)",
                    dispatch.CorrelationId);
            }
        }

        // ---- 4. One-by-one Send, then ack (EXEC-07/08/09, D-14/D-15) ----
        // An empty list sends nothing — ack only (EXEC-08). Otherwise resolve the endpoint once and
        // Send each result individually (never a batched list). A Send fault is INFRA — it propagates.
        if (built.Count == 0)
            return;

        foreach (var er in built)
            // Every result flows through the single SendResult owner (D-08) so processor_result_sent is
            // incremented after each confirmed Send. SendResult resolves the endpoint per result; this is
            // acceptable. An infra Send fault still PROPAGATES (D-15).
            await SendResult(er);
        // returns normally -> ACK only after ALL sends (D-15).
    }

    /// <summary>
    /// D-08 — the SINGLE Send owner. EVERY result (the early Failed/Cancelled returns AND the per-result
    /// Completed loop) flows through here so no send path is uncounted. Resolves the result endpoint,
    /// sends one business-outcome <see cref="ExecutionResult"/>, then (METRIC-05 / D-04) increments
    /// <c>processor_result_sent</c> AFTER the confirmed Send, tagged <c>ProcessorId</c> + <c>outcome</c>.
    /// Sent with <see cref="CancellationToken.None"/> so a Cancelled/Failed signal is delivered even when
    /// the inbound dispatch token has tripped — the business outcome must always reach the orchestrator
    /// (an infra Send fault still propagates, D-15, BEFORE the counter increments — only confirmed sends count).
    /// <para>
    /// The build paths (BuildCompleted/BuildFailed/BuildCancelled) NEVER emit <c>StepOutcome.Processing</c>
    /// (Pitfall 3), so <c>outcome</c> ∈ {completed, failed, cancelled} — 3 bounded values, no filter needed
    /// (T-30-05). The bang on <c>context.Id</c> is justified: SendResult only runs inside Consume, which
    /// runs post-MarkHealthy (Landmine 2). NO workflowId (T-30-04 cardinality).
    /// </para>
    /// </summary>
    private async Task SendResult(ExecutionResult result)
    {
        var endpoint = await sendProvider.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"));
        await endpoint.Send(result, CancellationToken.None);
        metrics.ResultSent.Add(1,
            new KeyValuePair<string, object?>("ProcessorId", context.Id!.Value.ToString("D")),
            new KeyValuePair<string, object?>("outcome", OutcomeLabel(result.Outcome)));
    }

    /// <summary>
    /// Maps a build-path <see cref="StepOutcome"/> to its pinned, lower-case Prometheus label literal
    /// (METRIC-05 / IN-04). Returns interned string constants — NO per-send allocation, unlike
    /// <c>Outcome.ToString().ToLowerInvariant()</c>. Critically, the literals are decoupled from the
    /// C# enum member names: an enum rename can no longer silently change an emitted Prometheus label.
    /// Build paths NEVER emit <see cref="StepOutcome.Processing"/> (Pitfall 3), so the value set is the
    /// bounded {completed, failed, cancelled}; <see cref="StepOutcome.Processing"/> is mapped defensively
    /// to <c>"processing"</c> to keep the switch exhaustive without a throw on a never-taken arm.
    /// </summary>
    private static string OutcomeLabel(StepOutcome outcome) => outcome switch
    {
        StepOutcome.Completed  => "completed",
        StepOutcome.Failed     => "failed",
        StepOutcome.Cancelled  => "cancelled",
        StepOutcome.Processing => "processing",   // never reached on a send path (Pitfall 3) — kept for exhaustiveness
        _                      => outcome.ToString().ToLowerInvariant(),
    };

    // ---- Builders (Pattern 3 / D-11/D-13): inherit ids, copy body CorrelationId (EXEC-10), mint ExecutionId ----

    // executionId is minted ONCE in the Completed-path loop and passed in (NOT minted here) so the
    // nested BeginScope value and this result's ExecutionId are the SAME value (LOG-04 — the scoped id
    // equals the sent ExecutionResult.ExecutionId the line reports).
    private static ExecutionResult BuildCompleted(EntryStepDispatch d, Guid executionId, string newEntryId) =>
        new(d.WorkflowId, d.StepId, d.ProcessorId, StepOutcome.Completed)
        {
            CorrelationId = d.CorrelationId,
            ExecutionId = executionId,
            EntryId = newEntryId,
        };

    private static ExecutionResult BuildFailed(EntryStepDispatch d, string error) =>
        new(d.WorkflowId, d.StepId, d.ProcessorId, StepOutcome.Failed)
        {
            CorrelationId = d.CorrelationId,
            ExecutionId = NewId.NextGuid(),
            EntryId = "",
            ErrorMessage = error,
        };

    private static ExecutionResult BuildCancelled(EntryStepDispatch d, string msg) =>
        new(d.WorkflowId, d.StepId, d.ProcessorId, StepOutcome.Cancelled)
        {
            CorrelationId = d.CorrelationId,
            ExecutionId = NewId.NextGuid(),
            EntryId = "",
            CancellationMessage = msg,
        };
}
