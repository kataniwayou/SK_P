using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Identity;
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
    ILogger<EntryStepDispatchConsumer> logger) : IConsumer<EntryStepDispatch>
{
    public async Task Consume(ConsumeContext<EntryStepDispatch> ctx)
    {
        var dispatch = ctx.Message;
        var ct = ctx.CancellationToken;
        var opts = options.Value;

        // A Redis fault on GetDatabase / StringGetAsync / StringSetAsync is INFRA and propagates
        // (no catch) -> Immediate(3) retry. Only ProcessAsync exceptions are caught (business).
        var db = redis.GetDatabase();

        // ---- 1. Input resolution + validation (EXEC-02/03, D-07, no-input source decision) ----
        string inputData;

        if (dispatch.EntryId == Guid.Empty)
        {
            // No entryId => source processor: do NOT read L2 (locked no-input decision).
            inputData = string.Empty;

            if (!string.IsNullOrWhiteSpace(context.InputDefinition))
            {
                // A required-input step arrived with no entryId — Failed BEFORE ProcessAsync (EXEC-03).
                logger.LogInformation(
                    "Dispatch {CorrelationId}: required input but no entryId — Failed (business)",
                    dispatch.CorrelationId);
                await SendOne(BuildFailed(dispatch, "Input data not found: no entryId provided for a step requiring input."), ct);
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
                    await SendOne(BuildFailed(dispatch, "Input data not found in L2 for entryId."), ct);
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
            await SendOne(BuildFailed(dispatch, string.Join("; ", inErrors)), ct);
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
            await SendOne(BuildCancelled(dispatch, "Processing was cancelled."), ct);
            return;
        }
        catch (Exception ex)
        {
            // Any other transform exception is a business Failed carrying the message (EXEC-08).
            await SendOne(BuildFailed(dispatch, ex.Message), ct);
            return;
        }

        // ---- 3. Per-result output-validate -> mint -> write-L2(TTL) -> build (EXEC-05/06) ----
        var built = new List<ExecutionResult>(results.Count);
        foreach (var r in results)
        {
            if (!ProcessorJsonSchemaValidator.TryValidate(context.OutputDefinition, r.OutputData, out var outErrors))
            {
                // Output failed its definition — Failed, EntryId stays Guid.Empty, nothing written (EXEC-05).
                built.Add(BuildFailed(dispatch, string.Join("; ", outErrors)));
                continue;
            }

            var newEntryId = NewId.NextGuid();

            // D-15: the OUTPUT WRITE is INFRA — NO catch. A transient Redis fault here throws so the
            // whole dispatch retries rather than emitting a Completed with no L2 data behind it.
            await db.StringSetAsync(
                L2ProjectionKeys.ExecutionData(newEntryId),
                r.OutputData,
                expiry: TimeSpan.FromSeconds(opts.ExecutionDataTtlSeconds));

            built.Add(BuildCompleted(dispatch, newEntryId));
        }

        // ---- 4. One-by-one Send, then ack (EXEC-07/08/09, D-14/D-15) ----
        // An empty list sends nothing — ack only (EXEC-08). Otherwise resolve the endpoint once and
        // Send each result individually (never a batched list). A Send fault is INFRA — it propagates.
        if (built.Count == 0)
            return;

        var endpoint = await sendProvider.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"));
        foreach (var er in built)
            await endpoint.Send(er, ct);
        // returns normally -> ACK only after ALL sends (D-15).
    }

    /// <summary>Resolves the result endpoint and sends a single <see cref="ExecutionResult"/> (early business returns).</summary>
    private async Task SendOne(ExecutionResult result, CancellationToken ct)
    {
        var endpoint = await sendProvider.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"));
        await endpoint.Send(result, ct);
    }

    // ---- Builders (Pattern 3 / D-11/D-13): inherit ids, copy body CorrelationId (EXEC-10), mint ExecutionId ----

    private static ExecutionResult BuildCompleted(EntryStepDispatch d, Guid newEntryId) =>
        new(d.WorkflowId, d.StepId, d.ProcessorId, StepOutcome.Completed)
        {
            CorrelationId = d.CorrelationId,
            ExecutionId = NewId.NextGuid(),
            EntryId = newEntryId,
        };

    private static ExecutionResult BuildFailed(EntryStepDispatch d, string error) =>
        new(d.WorkflowId, d.StepId, d.ProcessorId, StepOutcome.Failed)
        {
            CorrelationId = d.CorrelationId,
            ExecutionId = NewId.NextGuid(),
            EntryId = Guid.Empty,
            ErrorMessage = error,
        };

    private static ExecutionResult BuildCancelled(EntryStepDispatch d, string msg) =>
        new(d.WorkflowId, d.StepId, d.ProcessorId, StepOutcome.Cancelled)
        {
            CorrelationId = d.CorrelationId,
            ExecutionId = NewId.NextGuid(),
            EntryId = Guid.Empty,
            CancellationMessage = msg,
        };
}
