using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Observability;
using BaseProcessor.Core.Validation;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Hashing;
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

        // ---- 0. Effect-first dedup gate (D-06) ----
        // An inbound H already Ack means the effect (write + send + outbound Pending seed) completed on a
        // prior delivery -> drop + broker-ack, produce NO effect. The flag read is INFRA (no catch) ->
        // propagates to the Immediate(3) retry (Pattern 2). H="" (legacy/unset) never matches "Ack".
        if ((string?)await db.StringGetAsync(L2ProjectionKeys.Flag(dispatch.H)) == "Ack")
        {
            // D-10 (req-7): count the redelivery-collapse drop (how often a duplicate is dropped at the
            // flag[H] gate), tagged ProcessorId (same idiom as DispatchConsumed at :62-63). The bang is
            // justified post-MarkHealthy (Landmine 2). NO workflowId (T-30-04 cardinality).
            metrics.DispatchDeduped.Add(1,
                new KeyValuePair<string, object?>("ProcessorId", context.Id!.Value.ToString("D")));
            return;
        }

        // ---- 1. Input resolution + validation (EXEC-02/03, D-07, source-step keyed on InputDefinition) ----
        // Source-step input-skip keys on InputDefinition == null (req-2 / D-01), NOT on an empty EntryId:
        // an empty EntryId only short-circuits the L2 READ (a source step has no input key), and an absent
        // L2 value for a required-input step is a business Failed (unchanged outcome).
        string inputData;
        var raw = string.IsNullOrEmpty(dispatch.EntryId)
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
                await SendResult(BuildFailed(dispatch, "Input data not found in L2 for entryId."));
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

        // ---- 3. Per-result output-validate -> content-address -> write-L2(TTL) -> collect (EXEC-05/06, D-03/D-09) ----
        var manifestHashes = new List<string>(results.Count);
        foreach (var r in results)
        {
            if (!ProcessorJsonSchemaValidator.TryValidate(context.OutputDefinition, r.OutputData, out var outErrors))
            {
                // D-09: schema validation runs on each result DATA blob, never the manifest. A blob that
                // fails its definition is a whole-dispatch business Failed: send Failed (EntryId="", H="")
                // and return. Nothing is written, no blob is added to the manifest, and no outbound Pending
                // is pre-written (a Failed result short-circuits before the manifest read — Open Q1).
                await SendResult(BuildFailed(dispatch, string.Join("; ", outErrors)));
                return;
            }

            // D-03/D-09: content-address the blob. Writing the same blob twice targets the SAME key
            // (idempotent overwrite — a retry reproduces identical keys). executionId stays a freely
            // regenerated lineage id (excluded from H by construction, D-02).
            var blobHash    = MessageIdentity.HashBlob(r.OutputData);
            var executionId = NewId.NextGuid();   // mint once — scoped value == sent ExecutionResult.ExecutionId

            // LOG-04/LOG-01: a nested BeginScope carrying the MINTED ExecutionId + content-addressed blob
            // hash. MEL inner-overrides-outer, so the write LogRecord on this Completed path reports these
            // ids rather than the inbound ones the outer execution-scope filter carried (D-05). MINIMAL:
            // wraps ONLY the L2 write + log line. Values are .ToString() under the fixed ExecutionLogScope
            // keys — never interpolated into a template (T-18-04).
            using (logger.BeginScope(new Dictionary<string, object>
            {
                [ExecutionLogScope.ExecutionId] = executionId.ToString(),
                [ExecutionLogScope.EntryId]     = blobHash,
            }))
            {
                // D-15: the OUTPUT WRITE is INFRA — NO catch. A transient Redis fault here throws so the
                // whole dispatch retries rather than emitting a Completed with no L2 data behind it.
                await db.StringSetAsync(
                    L2ProjectionKeys.ExecutionData(blobHash),
                    r.OutputData,
                    expiry: TimeSpan.FromSeconds(opts.ExecutionDataTtlSeconds));

                manifestHashes.Add(blobHash);

                logger.LogInformation(
                    "Dispatch {CorrelationId}: step output written content-addressed (scoped execution ids)",
                    dispatch.CorrelationId);
            }
        }

        // ---- 4. Manifest assembly + outbound Pending pre-write + ONE send (D-06/D-08, Pitfall 4) ----
        // Collapse N result blobs into ONE deterministic result identity: serialize the manifest (a JSON
        // array of the blob hashes — "[]" when empty), content-address + write it, then send ONE
        // ExecutionResult carrying the manifest EntryId. An EMPTY result still sends a terminal "[]"
        // manifest result so the orchestrator observes-and-terminates and acks (req-3, Pitfall 4).
        var manifestJson    = System.Text.Json.JsonSerializer.Serialize(manifestHashes);   // ["<64hex>", ...] or "[]"
        var manifestEntryId = MessageIdentity.HashManifest(manifestJson);
        await db.StringSetAsync(
            L2ProjectionKeys.ExecutionData(manifestEntryId),
            manifestJson,
            expiry: TimeSpan.FromSeconds(opts.ExecutionDataTtlSeconds));

        // SENDER pre-write (D-06): the processor IS the sender of ExecutionResult, so it seeds the OUTBOUND
        // result's flag[resultH]="Pending" so the orchestrator's effect-first When.Exists flip (Plan 04)
        // has a key to flip Pending->Ack. Without this seed that flip is a no-op on an absent key and the
        // orchestrator-hop drop-on-Ack gate can NEVER fire. Unconditional set (idempotent on re-send);
        // Redis fault is INFRA -> propagates. Mirrors StepDispatcher's outbound flag pre-write (Plan 04).
        var resultH = MessageIdentity.ComputeH(
            dispatch.CorrelationId, dispatch.WorkflowId, dispatch.StepId, dispatch.ProcessorId, manifestEntryId);
        await db.StringSetAsync(
            L2ProjectionKeys.Flag(resultH), "Pending",
            expiry: TimeSpan.FromSeconds(opts.ExecutionDataTtlSeconds));

        // The caller passes the already-computed resultH so the pre-write and the message carry the
        // IDENTICAL H — one ComputeH call, no drift. An infra Send fault still PROPAGATES (D-15).
        await SendResult(BuildCompleted(dispatch, NewId.NextGuid(), manifestEntryId, resultH));

        // ---- 5. Effect-first CAS flip, then ack (D-06/D-07) ----
        // Flip the INBOUND dispatch's flag Pending->Ack ONLY after the effect (write + outbound Pending
        // seed + send) is produced. When.Exists = SET XX: a false return means Pending was lost (crash
        // residual) — NOT an error, do NOT throw (Pitfall 3); downstream H dedup absorbs the residual. Do
        // NOT flip the outbound resultH to Ack — the orchestrator owns that flip on its hop (Plan 04).
        // keepTtl: SET XX without KEEPTTL would CLEAR the sender's 300s TTL, making every deduped flag a
        // permanent skp:flag:* key (unbounded Redis growth). KEEPTTL preserves the bound so Ack flags drain.
        await db.StringSetAsync(L2ProjectionKeys.Flag(dispatch.H), "Ack", expiry: null, keepTtl: true, when: When.Exists);
        // returns normally -> ACK only after the send + flip (D-15).
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
    // The caller passes the already-computed resultH (= ComputeH over the manifest EntryId) so the
    // OUTBOUND flag[resultH]="Pending" pre-write and this message carry the IDENTICAL H — one ComputeH
    // call, no drift. EntryId = the content-addressed manifest entryId; H = resultH.
    private static ExecutionResult BuildCompleted(EntryStepDispatch d, Guid executionId, string manifestEntryId, string resultH) =>
        new(d.WorkflowId, d.StepId, d.ProcessorId, StepOutcome.Completed)
        {
            CorrelationId = d.CorrelationId,
            ExecutionId = executionId,
            EntryId = manifestEntryId,
            H = resultH,
        };

    private static ExecutionResult BuildFailed(EntryStepDispatch d, string error) =>
        new(d.WorkflowId, d.StepId, d.ProcessorId, StepOutcome.Failed)
        {
            CorrelationId = d.CorrelationId,
            ExecutionId = NewId.NextGuid(),
            EntryId = "",
            H = "",
            ErrorMessage = error,
        };

    private static ExecutionResult BuildCancelled(EntryStepDispatch d, string msg) =>
        new(d.WorkflowId, d.StepId, d.ProcessorId, StepOutcome.Cancelled)
        {
            CorrelationId = d.CorrelationId,
            ExecutionId = NewId.NextGuid(),
            EntryId = "",
            H = "",
            CancellationMessage = msg,
        };
}
