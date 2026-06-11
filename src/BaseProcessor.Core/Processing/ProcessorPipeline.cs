using BaseConsole.Core.Resilience;   // D-05: RetryLoop / RetryOutcome relocated here
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Observability;
using BaseProcessor.Core.Resilience;   // KeyAbsentException stays processor-side (Pre-read sentinel)
using BaseProcessor.Core.Validation;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace BaseProcessor.Core.Processing;

/// <summary>
/// The Phase-44 Pre → In → Post → end-delete pipeline runner (PIPE-01, RESEARCH Pattern 1). Extracted
/// from the old straight-through <see cref="EntryStepDispatchConsumer"/> so the five terminals are
/// testable without a MassTransit harness (a plain object the facts construct directly).
/// <para>
/// <b>Pre</b> (D-07/PIPE-02/03): a <c>SourceStep.IsSource</c> Guid.Empty dispatch skips the L2 read with
/// empty validatedData and arms NO end-delete; otherwise a bounded-retry read (absent/empty unified with a
/// Redis fault via <see cref="KeyAbsentException"/>) that exhausts routes to <c>KeeperReinject</c> and
/// returns WITHOUT arming end-delete (the input is left intact for the keeper, T-44-08); a read that
/// succeeds arms end-delete, then an input-schema failure is a business <c>StepFailed</c> (end-delete still
/// runs).
/// </para>
/// <para>
/// <b>In</b> (PIPE-05): the author seam in a try/catch — a <c>ProcessStatusException</c> maps by runtime
/// type to exactly one <c>Step*</c> record and aborts the batch (no Post); an unexpected exception ⇒
/// <c>StepFailed</c>.
/// </para>
/// <para>
/// <b>Post</b> (PIPE-06/07, per item in order): a completed item → write <c>L2[entryId]</c> with the
/// bounded <c>ExecutionDataTtl</c> (CONFIG-02/D-17 — so a terminal step's output key and repeated-fire
/// keys self-expire; bounded retry; exhaust → <c>KeeperInject</c>, batch NOT aborted) → <c>StepCompleted</c>
/// carrying the framework entryId + author executionId. A per-item business <c>failed</c> (author
/// <c>Failed</c> OR output-schema failure) → one <c>StepFailed</c>, batch NOT aborted (A3, N items → N
/// results). Phase-50 (D-01) removed the Model-B UPDATE/CLEANUP keeper sends + their composite backup
/// key; the real A18 slot-array forward/recovery pass lands in Phase 51.
/// </para>
/// <para>
/// <b>End-delete</b> (PIPE-08): a <c>finally</c> over every read-succeeded path — deletes <c>L2[entryId]</c>
/// of the inbound dispatch with bounded retry; exhaust → <c>KeeperDelete</c>. Skipped on the REINJECT path
/// and on a Guid.Empty source step (both leave <c>readSucceeded == false</c>).
/// </para>
/// <para>
/// <b>Resilience</b> (RESIL-01/D-09/D-10): every L2 op and every send is wrapped in
/// <see cref="RetryLoop"/> using <c>Retry:Limit</c>; a send that exhausts PROPAGATES (→ the bus
/// <c>UseMessageRetry</c> dead-letter latch → <c>_error</c>). The in-code retry owns per-op retries; the
/// bus retry is the OUTER latch, not a second L2/send retry (Pitfall 1).
/// </para>
/// </summary>
public sealed class ProcessorPipeline(
    IConnectionMultiplexer redis,
    IProcessorContext context,
    BaseProcessor processor,
    ISendEndpointProvider sendProvider,
    IOptions<RetryOptions> retryOptions,
    IOptions<ProcessorLivenessOptions> livenessOptions,
    ProcessorMetrics metrics,
    ILogger<ProcessorPipeline> logger)
{
    public async Task RunAsync(EntryStepDispatch d, CancellationToken ct)
    {
        var db = redis.GetDatabase();
        var limit = retryOptions.Value.Limit;
        // CONFIG-02 / D-17: bound execution-data TTL applied on every output write so a TERMINAL step's
        // output key (no successor step to end-delete it) and any key minted by a repeated cron fire are
        // bounded rather than leaking forever (the close-gate redis --scan net-zero invariant + the
        // compose Processor__ExecutionDataTtl override depend on this self-expiry).
        var executionDataTtl = TimeSpan.FromSeconds(livenessOptions.Value.ExecutionDataTtlSeconds);
        var readSucceeded = false;   // gates the finally end-delete (Pitfall 3)

        try
        {
            // ---- PRE ----
            string validatedData;
            if (SourceStep.IsSource(d.EntryId))          // NEVER inline == Guid.Empty
            {
                validatedData = string.Empty;            // skip read; readSucceeded stays false → no end-delete
            }
            else
            {
                var read = await RetryLoop.ExecuteAsync(async () =>
                {
                    var raw = await db.StringGetAsync(L2ProjectionKeys.ExecutionData(d.EntryId));
                    if (raw.IsNullOrEmpty) throw new KeyAbsentException();   // A2: unify absent/empty with Redis fault
                    return raw.ToString();
                }, limit, ct);

                if (!read.Succeeded)                      // infra(READ): Redis fault OR absent/empty, exhausted
                {
                    await SendKeeper(BuildReinject(d), limit, ct);   // KeeperReinject; END — no end-delete (input left intact)
                    return;                               // returns through finally, but readSucceeded==false → skip delete
                }
                readSucceeded = true;                     // ONLY now is end-delete armed
                validatedData = read.Value!;

                if (!ProcessorJsonSchemaValidator.TryValidate(context.InputDefinition, validatedData, out var inErrs))
                {
                    await SendResult(BuildFailed(d, string.Join("; ", inErrs)), limit, ct);  // business StepFailed
                    return;                               // finally STILL runs end-delete (read succeeded)
                }
            }

            // ---- IN ----
            List<ProcessItem> items;
            try { items = await processor.ExecuteAsync(validatedData, d.Payload, ct); }
            catch (ProcessStatusException e)
            {
                IStepResult result = e switch
                {
                    FailedException     => BuildFailed(d, e.Message),
                    CancelledException  => BuildCancelled(d, e.Message),
                    ProcessingException => BuildProcessing(d),          // message logged only (no wire field, D-05)
                    _                   => BuildFailed(d, e.Message),
                };
                if (e is ProcessingException) logger.LogInformation("ProcessAsync threw processing status: {Msg}", e.Message);
                await SendResult(result, limit, ct);                    // exactly ONE result; abort batch (no Post)
                return;                                                  // end-delete runs (read succeeded)
            }
            catch (Exception ex)                                        // unexpected ⇒ failed
            {
                await SendResult(BuildFailed(d, ex.Message), limit, ct);
                return;
            }

            // ---- POST (per item, in order) ----
            foreach (var item in items)
            {
                var outcome = item.Result;
                if (outcome == ProcessOutcome.Completed
                    && !ProcessorJsonSchemaValidator.TryValidate(context.OutputDefinition, item.Data, out _))
                    outcome = ProcessOutcome.Failed;                    // output-validation fail → business failed (A3: per-item, NOT abort)

                if (outcome == ProcessOutcome.Completed)
                {
                    // Phase-50 (D-01): the Model-B UPDATE-before-write keeper send is removed (the composite
                    // backup key it wrote is retired); the real A18 slot-array allocation lands in Phase 51.
                    var entryId = NewId.NextGuid();                     // framework mints the data key
                    var write = await RetryLoop.ExecuteAsync(
                        () => db.StringSetAsync(L2ProjectionKeys.ExecutionData(entryId), item.Data, executionDataTtl), limit, ct);  // CONFIG-02/D-17: bounded TTL so terminal/orphaned keys self-expire
                    if (!write.Succeeded)                               // output-write exhausted → failed(infra)
                    {
                        await SendKeeper(BuildInject(d, item), limit, ct);   // KeeperInject (infra route)
                        continue;                                       // next item — batch NOT aborted
                    }
                    // Phase-50 (D-01): the Model-B CLEANUP keeper send is removed (the redundant composite
                    // backup it deleted is retired); the real A18 retire-after-send lands in Phase 51.
                    using (logger.BeginScope(new Dictionary<string, object>
                    {
                        [ExecutionLogScope.ExecutionId] = item.ExecutionId.ToString(),   // author-minted (D-03/Pitfall 4)
                        [ExecutionLogScope.EntryId]     = entryId.ToString(),            // framework-minted
                    }))
                    {
                        await SendResult(BuildCompleted(d, item.ExecutionId, entryId), limit, ct);  // StepCompleted carries both ids
                    }
                }
                else // per-item business failed (author Failed OR output-validation fail)
                {
                    await SendResult(BuildFailed(d, "output failed schema validation"), limit, ct);  // one StepFailed; NOT abort
                }
            }
        }
        finally
        {
            // ---- END-DELETE (finally over every read-succeeded path; skip on REINJECT + Guid.Empty source) ----
            if (readSucceeded)
            {
                var del = await RetryLoop.ExecuteAsync(
                    () => db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(d.EntryId)), limit, ct);
                if (!del.Succeeded)
                    await SendKeeper(BuildDelete(d), limit, ct);        // infra(DELETE) → KeeperDelete
            }
        }
    }

    // ---- Send owners: every send wrapped in RetryLoop; send-exhaustion PROPAGATES (D-10 → bus _error). ----

    private async Task SendResult(IStepResult result, int limit, CancellationToken ct)
    {
        var ep = await sendProvider.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"));
        var sent = await RetryLoop.ExecuteAsync(
            async () => { await ep.Send((object)result, CancellationToken.None); return true; }, limit, ct);
        if (!sent.Succeeded) throw sent.Error!;   // D-10: propagate → UseMessageRetry → _error

        // METRIC-05 / GAP-49-5 (D-10): count EVERY genuinely-sent step result, tagged ProcessorId (same
        // context.Id!.Value.ToString("D") shape as EntryStepDispatchConsumer.DispatchConsumed) PLUS the
        // terminal outcome (completed/failed/cancelled/processing — MetricsRoundTripE2ETests asserts
        // expectOutcome:true). Placed AFTER the success guard so only a confirmed send increments the
        // counter (a propagated exhaustion above never reaches here).
        metrics.ResultSent.Add(1,
            new KeyValuePair<string, object?>("ProcessorId", context.Id!.Value.ToString("D")),
            new KeyValuePair<string, object?>("outcome", ResultOutcome(result)));
    }

    /// <summary>Maps the concrete <see cref="IStepResult"/> record to a stable lowercase outcome tag value
    /// (completed/failed/cancelled/processing) for the <c>processor_result_sent_total</c> outcome label.</summary>
    private static string ResultOutcome(IStepResult result) => result switch
    {
        StepCompleted  => "completed",
        StepFailed     => "failed",
        StepCancelled  => "cancelled",
        StepProcessing => "processing",
        _              => "failed",
    };

    private async Task SendKeeper(IKeeperRecoverable msg, int limit, CancellationToken ct)
    {
        var ep = await sendProvider.GetSendEndpoint(new Uri($"queue:{KeeperQueues.Recovery}"));   // A2: keeper-recovery
        var sent = await RetryLoop.ExecuteAsync(
            async () => { await ep.Send((object)msg, CancellationToken.None); return true; }, limit, ct);
        if (!sent.Succeeded) throw sent.Error!;   // D-10: propagate → _error
    }

    // ---- Builders (inherit-ids positional ctor + init; A1 id-sets) ----
    // A1: REINJECT/DELETE carry the INBOUND dispatch ExecutionId (d.ExecutionId — the pre-item-split entry,
    // may legitimately be Guid.Empty); INJECT carries the AUTHOR-MINTED item ExecutionId.

    private static StepCompleted   BuildCompleted(EntryStepDispatch d, Guid exec, Guid entryId) =>
        new(d.WorkflowId, d.StepId, d.ProcessorId) { CorrelationId = d.CorrelationId, ExecutionId = exec, EntryId = entryId };

    private static StepFailed      BuildFailed(EntryStepDispatch d, string err) =>
        new(d.WorkflowId, d.StepId, d.ProcessorId) { CorrelationId = d.CorrelationId, ExecutionId = NewId.NextGuid(), EntryId = Guid.Empty, ErrorMessage = err };

    private static StepCancelled   BuildCancelled(EntryStepDispatch d, string msg) =>
        new(d.WorkflowId, d.StepId, d.ProcessorId) { CorrelationId = d.CorrelationId, ExecutionId = NewId.NextGuid(), EntryId = Guid.Empty, CancellationMessage = msg };

    private static StepProcessing  BuildProcessing(EntryStepDispatch d) =>
        new(d.WorkflowId, d.StepId, d.ProcessorId) { CorrelationId = d.CorrelationId, ExecutionId = NewId.NextGuid() };

    private static KeeperReinject  BuildReinject(EntryStepDispatch d) =>
        new(d.WorkflowId, d.StepId, d.ProcessorId) { CorrelationId = d.CorrelationId, ExecutionId = d.ExecutionId, EntryId = d.EntryId, Payload = d.Payload };   // A1: inbound exec; D-01: carry Payload

    private static KeeperDelete    BuildDelete(EntryStepDispatch d) =>
        new(d.WorkflowId, d.StepId, d.ProcessorId) { CorrelationId = d.CorrelationId, ExecutionId = d.ExecutionId, EntryId = d.EntryId };   // A1: inbound exec

    private static KeeperInject    BuildInject(EntryStepDispatch d, ProcessItem item) =>
        new(d.WorkflowId, d.StepId, d.ProcessorId) { CorrelationId = d.CorrelationId, ExecutionId = item.ExecutionId };   // A1: item exec
}
