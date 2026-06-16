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
/// The A18 dispatcher + FORWARD pass runner (Phase 51, design doc lines 146-177). Extracted from the old
/// straight-through <see cref="EntryStepDispatchConsumer"/> so the terminals are testable without a
/// MassTransit harness (a plain object the facts construct directly).
/// <para>
/// <b>Dispatcher</b> (D-07/FWD-01): the entry point branches on <c>exist L2[messageId]</c> via a bounded
/// retry. An existence-check exhaustion routes to <c>KeeperReinject</c> and ENDS the round trip WITHOUT
/// deleting the source (the input is left intact for the keeper). On a present marker the RECOVERY pass runs
/// (implemented this phase — <see cref="RunRecoveryAsync"/>); otherwise the FORWARD pass runs.
/// </para>
/// <para>
/// <b>Forward — Pre</b> (PIPE-02/03): a <c>SourceStep.IsSource</c> Guid.Empty dispatch skips the L2 read with
/// empty validatedData; otherwise a bounded-retry read (absent/empty unified with a Redis fault via
/// <see cref="KeyAbsentException"/>) that exhausts routes to <c>KeeperReinject</c> and returns WITHOUT the
/// source-delete tail (input intact, T-51-04/FWD-01); a read that succeeds proceeds, and an input-schema
/// failure is a business <c>StepFailed</c> (the source-delete tail STILL runs).
/// </para>
/// <para>
/// <b>Forward — In</b> (PIPE-05): the author seam in a try/catch — a <c>ProcessStatusException</c> maps by
/// runtime type to exactly one <c>Step*</c> record and aborts the batch (the source-delete tail still runs);
/// an unexpected exception ⇒ <c>StepFailed</c>.
/// </para>
/// <para>
/// <b>Forward — Post</b> (ATOMIC-01, NODROP-01): per completed item, in order: (1) mint <c>entryId</c>;
/// (2) issue ONE atomic Lua <see cref="AtomicForwardWrite"/> (<c>ScriptEvaluateAsync</c>) that writes the
/// index slot <c>L2[messageId][slot]=entryId</c>, the whole-HASH index TTL (<see cref="SlotTtl"/> =
/// random[ExecutionDataTtl, 2×ExecutionDataTtl]), the data key <c>L2[entryId]=data</c>, and the data TTL
/// (== ExecutionDataTtl) in ONE server-side op — a concurrent reader/Recovery never observes a partial
/// index-without-data (or data-without-index) state (spec §4.3 step 3). An atomic-write exhaustion (index- OR
/// data-failure that retry could not clear, OR a deterministic server-side script error — bad ARGV, Lua
/// runtime error, value-too-large — that re-throws every attempt) routes to ONE <c>KeeperInject</c> carrying the EntryId/Data id-set (no silent
/// DROP path remains — spec §10 bullet 1); else <c>StepCompleted</c> to the orchestrator. The slot ordinal
/// increments ONLY for completed items (business-failed items consume no slot). TTLs are computed in C# and
/// passed as ARGV (no RNG inside Lua) so the Phase-68 TEST-06 index/data desync guard holds.
/// </para>
/// <para>
/// <b>Forward — source-delete tail</b> (FWD-03): an explicit inline tail (NOT a try/cleanup block — the
/// WR-01 race is RETIRED in Phase 51) reached ONLY on the no-REINJECT happy path. GATED (GATE-01) on no
/// forward-Post INJECT this pass (spec 4.3 final paragraph): any item escalated => tail SKIPPED, leaving the
/// index + input keys for the keeper + a later Recovery pass / index-TTL (removes the processor/keeper index race).
/// On the no-escalation path it deletes the inbound
/// <c>L2[entryId]</c> with bounded retry; exhaust → <c>KeeperDelete</c>. Skipped on a Guid.Empty source step.
/// </para>
/// <para>
/// <b>Resilience</b> (RESIL-01/D-09/Phase-53 D-01): every L2 op and every send is wrapped in
/// <see cref="RetryLoop"/> using <c>Retry:Limit</c>; a send that exhausts PROPAGATES (throws) → with NO bus
/// retry and NO error pipeline on the dispatch endpoint (the A18 <c>UseMessageRetry=none</c> end-state,
/// landed in Phase 53) the default is RabbitMQ nack-requeue (broker redelivery) — no dead-letter, no
/// <c>_error</c>. The in-code RetryLoop owns ALL per-op retries.
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
    /// <summary>IN-02: the on-wire retired-slot sentinel (A18: a retired slot carries <c>Guid.Empty</c>). A
    /// shared <c>static readonly</c> avoids re-materializing the string per retire and keeps the production
    /// write byte-identical to the test assertions in <c>PipelineRecoveryFacts</c> (which hardcode
    /// <c>(RedisValue)Guid.Empty.ToString()</c>).</summary>
    private static readonly RedisValue RetiredSlot = Guid.Empty.ToString();

    /// <summary>Spec §4.3 step 3 (ATOMIC-01): the atomic forward-Post write — index slot HSET + whole-HASH
    /// PEXPIRE + data SET-with-PX, in ONE server-side op. A compile-time <c>const</c> with parameterized
    /// KEYS/ARGV (no user data concatenated into the script text → injection-safe, T-69-04). A concurrent
    /// reader/Recovery never observes a partial index-without-data (or data-without-index) state.
    /// <para>
    /// KEYS[1] = L2[messageId] (the index HASH); KEYS[2] = L2[entryId] (the data key).
    /// ARGV[1] = slot ordinal; ARGV[2] = entryId(string); ARGV[3] = data;
    /// ARGV[4] = index TTL ms (<see cref="SlotTtl"/>, random[ExecutionDataTtl, 2×ExecutionDataTtl]);
    /// ARGV[5] = data TTL ms (== ExecutionDataTtl). The PEXPIRE is a WHOLE-HASH expire (NOT a per-field HEXPIRE
    /// — Redis 7.4 is not required), byte-for-byte matching the former <c>KeyExpireAsync(MessageIndex, SlotTtl())</c>.
    /// TTLs are computed in C# and passed as ARGV (no RNG inside Lua) so the Phase-68 TEST-06 desync guard holds.
    /// </para></summary>
    private const string AtomicForwardWrite = @"
        redis.call('HSET', KEYS[1], ARGV[1], ARGV[2])
        redis.call('PEXPIRE', KEYS[1], ARGV[4])
        redis.call('SET', KEYS[2], ARGV[3], 'PX', ARGV[5])
        return 1";

    /// <summary>D-05/D-06: the L2[messageId] index HASH whole-HASH TTL = <c>Random[ExecutionDataTtl, 2×ExecutionDataTtl]</c>
    /// — derived from the SAME <see cref="ProcessorLivenessOptions.ExecutionDataTtlSeconds"/> as the data key (single
    /// source of truth, so the index TTL can never DESYNC from the data TTL — the Phase-68 TEST-06 root cause), with a
    /// floor == the data TTL and a 2× ceiling so the index STRICTLY OUTLIVES the data it points at. That headroom is
    /// functional: while the index is alive a redelivery takes the RECOVERY path (re-send from the slot array, no input
    /// needed); once the index is gone it falls to the FORWARD path (needs the input → reinject-drop → MISSING). The
    /// jitter also avoids a synchronized expiry herd. <c>+1</c> makes the 2× ceiling inclusive; <c>Random.Shared</c> is
    /// the framework-shared thread-safe RNG.</summary>
    private TimeSpan SlotTtl()
    {
        // WR-69-01: floor at 1s. A non-positive ExecutionDataTtlSeconds would marshal to PEXPIRE/SET PX 0,
        // a Redis server error that exhausts the atomic write and launders a CONFIG fault as an infra exhaust
        // (every completed item escalates to the keeper indefinitely). The data TTL is floored identically in
        // RunAsync so both TTL sources stay the single ExecutionDataTtlSeconds (TEST-06 anti-desync guard).
        var ttl = Math.Max(1, livenessOptions.Value.ExecutionDataTtlSeconds);
        return TimeSpan.FromSeconds(Random.Shared.Next(ttl, 2 * ttl + 1));
    }

    public async Task RunAsync(EntryStepDispatch d, Guid messageId, CancellationToken ct)
    {
        var db = redis.GetDatabase();
        var limit = retryOptions.Value.Limit;
        // CONFIG-02 / D-17: bound execution-data TTL applied on every output write so a TERMINAL step's
        // output key and any key minted by a repeated cron fire are bounded rather than leaking forever.
        var executionDataTtl = TimeSpan.FromSeconds(Math.Max(1, livenessOptions.Value.ExecutionDataTtlSeconds)); // WR-69-01: floor at 1s (avoid SET PX 0 server error)

        // D-07/FWD-01: branch on exist L2[messageId]. Exhaust → REINJECT; END (no source delete, input intact).
        var exists = await RetryLoop.ExecuteAsync(
            () => db.KeyExistsAsync(L2ProjectionKeys.MessageIndex(messageId)), limit, ct);
        if (!exists.Succeeded)
        {
            await SendKeeper(BuildReinject(d), limit, ct);
            return;
        }

        if (exists.Value)
            await RunRecoveryAsync(d, messageId, db, limit, ct);                        // RECOVERY pass (implemented this phase)
        else
            await RunForwardAsync(d, messageId, db, limit, executionDataTtl, ct);
    }

    /// <summary>The A18 RECOVERY pass (design doc lines 179-203). Reached only when <c>L2[messageId]</c>
    /// EXISTS (a redelivery). HGETALL the slot array, build a temp list per slot, then re-send any
    /// <c>completed</c> result (a FRESH exec, D-03) BEFORE retiring the slot (SLOT-03 send-before-retire), then
    /// either REINJECT (any <c>infra_entryId</c> — replay owns the source lifecycle) ⊻ delete the
    /// source (the all-clear path). The two routes are mutually exclusive (RECOV-03).
    /// <para>
    /// Per-slot classification (Pattern 3 — clean not-exist and an L2 fault route differently): a clean
    /// <c>KeyExistsAsync == false</c> is <c>not-exist</c> → failed/DROP (no send, no retire); a thrown fault
    /// inside the bounded retry is <c>infra_entryId</c> → leave the slot intact (the tail REINJECTs);
    /// <c>true</c> is <c>completed</c>. An already-retired (<c>Guid.Empty</c>) or unparsable slot is inert and
    /// skipped (A18: retired slots carry no work).
    /// </para>
    /// </summary>
    private async Task RunRecoveryAsync(
        EntryStepDispatch d, Guid messageId, IDatabase db, int limit, CancellationToken ct)
    {
        // RECOV-01: HGETALL the slot array. Exhaust → REINJECT; END (no source delete — the input is intact).
        var read = await RetryLoop.ExecuteAsync(
            () => db.HashGetAllAsync(L2ProjectionKeys.MessageIndex(messageId)), limit, ct);
        if (!read.Succeeded)
        {
            await SendKeeper(BuildReinject(d), limit, ct);
            return;
        }

        // Build the temp list per slot (Claude's-Discretion representation: a local tuple list).
        var temp = new List<(RedisValue Slot, Guid EntryId, bool Completed, bool Infra)>();
        foreach (var entry in read.Value!)
        {
            // Skip already-retired (Guid.Empty) or unparsable slots — A18: retired slots are inert.
            if (!Guid.TryParse(entry.Value.ToString(), out var entryId) || entryId == Guid.Empty)
                continue;

            var exist = await RetryLoop.ExecuteAsync(
                () => db.KeyExistsAsync(L2ProjectionKeys.ExecutionData(entryId)), limit, ct);

            if (!exist.Succeeded)
                temp.Add((entry.Name, entryId, Completed: false, Infra: true));    // L2-fail → infra_entryId (leave slot)
            else if (exist.Value)
                temp.Add((entry.Name, entryId, Completed: true,  Infra: false));   // exists → completed
            // clean not-exist (exist.Value == false) → failed/DROP: NOT added (no send, no retire)
        }

        // Dispatch + send-before-retire (SLOT-03).
        foreach (var t in temp)
        {
            if (!t.Completed) continue;   // infra_entryId items: leave the slot intact, no send (handled in the tail)

            // D-03/Pitfall 4: recovery completed mints a FRESH exec (the slot holds only entryId, no exec persisted).
            await SendResult(BuildCompleted(d, NewId.NextGuid(), t.EntryId), limit, ct);   // SEND FIRST

            // Retire AFTER a confirmed send (SendResult throws on send-exhaust → reaching here == sent).
            var retire = await RetryLoop.ExecuteAsync(
                () => db.HashSetAsync(L2ProjectionKeys.MessageIndex(messageId), t.Slot, RetiredSlot), limit, ct);
            if (retire.Succeeded)
            {
                await RetryLoop.ExecuteAsync(
                    () => db.KeyExpireAsync(L2ProjectionKeys.MessageIndex(messageId), SlotTtl()), limit, ct);   // D-06 random[ttl,2×ttl] refresh
            }
            // Retire exhaust → "do nothing" (A18 line 192): the slot stays; a future replay re-sends (dup-tolerant, A16).
            // IN-01: the whole-HASH EXPIRE refresh (SlotTtl() = random[ExecutionDataTtl, 2×ExecutionDataTtl] — the index
            // outlives the data it points at, with jitter) runs ONLY inside the retire-succeeded branch above; on a
            // retire exhaust the existing HASH TTL is deliberately left intact (no path leaves a freshly-written HASH
            // without an EXPIRE — the HASH already carried a TTL from its forward-Post alloc write).
        }

        // RECOV-03 tail — REINJECT ⊻ source-delete mutual exclusion.
        var anyInfra = temp.Any(t => t.Infra);
        if (anyInfra)
        {
            await SendKeeper(BuildReinject(d), limit, ct);   // replay owns the lifecycle; do NOT delete the source
            return;
        }
        // D-06: NO source-step early-return — the unified tail's index DEL runs even for a source step
        // (ExecutionData(Guid.Empty) is a harmless absent operand).
        await DeleteTerminalAsync(d, messageId, db, limit, ct);
    }

    /// <summary>The A18 FORWARD pass (Pre → In → Post → inline source-delete tail). Reached only when
    /// <c>L2[messageId]</c> does NOT exist.</summary>
    private async Task RunForwardAsync(
        EntryStepDispatch d, Guid messageId, IDatabase db, int limit, TimeSpan executionDataTtl, CancellationToken ct)
    {
        // A19/FWD-03: the unified terminal tail is DeleteTerminalAsync(d, messageId, db, limit, ct) — invoked before
        // every non-REINJECT forward exit; it issues the atomic two-key DEL (data + origin index) and, on a source
        // step, still DELs the index (ExecutionData(Guid.Empty) is a harmless absent operand, D-06).

        // ---- PRE ----
        string validatedData;
        if (SourceStep.IsSource(d.EntryId))          // NEVER inline == Guid.Empty
        {
            validatedData = string.Empty;            // skip read
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
                await SendKeeper(BuildReinject(d), limit, ct);   // KeeperReinject; END — no source delete (input intact)
                return;                                          // FWD-01: REINJECT path returns WITHOUT the tail
            }
            validatedData = read.Value!;

            if (!ProcessorJsonSchemaValidator.TryValidate(context.InputDefinition, validatedData, out var inErrs))
            {
                await SendResult(BuildFailed(d, string.Join("; ", inErrs)), limit, ct);  // business StepFailed
                await DeleteTerminalAsync(d, messageId, db, limit, ct);   // read succeeded → tail runs
                return;
            }
        }

        // ---- IN ----
        List<ProcessItem> items;
        try { items = await processor.ExecuteAsync(validatedData, d.Payload, d.ExecutionId, ct); }
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
            await DeleteTerminalAsync(d, messageId, db, limit, ct); // read succeeded → tail runs
            return;
        }
        catch (Exception ex)                                        // unexpected ⇒ failed
        {
            await SendResult(BuildFailed(d, ex.Message), limit, ct);
            await DeleteTerminalAsync(d, messageId, db, limit, ct);
            return;
        }

        // ---- POST (forward, per item, in order) ----
        var slot = 0;                                               // completed-item ordinal ONLY (D-04 slot counter)
        var escalated = false;   // GATE-01: set true on a forward-Post INJECT; skips the cleanup tail (spec §4.3 final ¶)
        foreach (var item in items)
        {
            var outcome = item.Result;
            if (outcome == ProcessOutcome.Completed
                && !ProcessorJsonSchemaValidator.TryValidate(context.OutputDefinition, item.Data, out _))
                outcome = ProcessOutcome.Failed;                    // output-validation fail → business failed (A3, per-item)

            if (outcome == ProcessOutcome.Completed)
            {
                var entryId = NewId.NextGuid();                     // (1) allocate (unchanged)

                var write = await RetryLoop.ExecuteAsync(           // (2) ONE atomic index+data write (ATOMIC-01)
                    () => db.ScriptEvaluateAsync(AtomicForwardWrite,
                        new RedisKey[] { L2ProjectionKeys.MessageIndex(messageId), L2ProjectionKeys.ExecutionData(entryId) },
                        new RedisValue[]
                        {
                            slot,                                   // ARGV[1] slot ordinal (local counter, not atomic state)
                            entryId.ToString("D"),                  // ARGV[2]
                            item.Data,                              // ARGV[3]
                            (long)SlotTtl().TotalMilliseconds,      // ARGV[4] index TTL — computed in C#, random[ttl,2×ttl]
                            (long)executionDataTtl.TotalMilliseconds, // ARGV[5] data TTL — == ExecutionDataTtl
                        }),
                    limit, ct);
                if (!write.Succeeded)
                {
                    // NODROP-01: atomic-write exhausted → ONE INJECT (data in-hand). The exhaust covers BOTH a
                    // transient infra fault (index- OR data-failure that retry could not clear) AND a deterministic
                    // server-side script error (bad ARGV / Lua runtime error / value-too-large), which re-throws on
                    // every attempt and burns the budget. INJECT is the right safe terminal either way (no silent
                    // DROP — spec §10 bullet 1), but log the captured error so a deterministic defect is diagnosable
                    // rather than silent. The slot was claimed → consume it.
                    logger.LogWarning(write.Error,
                        "Atomic forward write exhausted; escalating to keeper INJECT (entryId={EntryId})", entryId);
                    await SendKeeper(BuildInject(d, item, entryId), limit, ct);
                    escalated = true;        // GATE-01: an item escalated this pass
                    slot++;
                    continue;
                }

                using (logger.BeginScope(new Dictionary<string, object>
                {
                    [ExecutionLogScope.ExecutionId] = item.ExecutionId.ToString(),   // author-minted (D-03/Pitfall 4)
                    [ExecutionLogScope.EntryId]     = entryId.ToString(),            // framework-minted
                }))
                {
                    await SendResult(BuildCompleted(d, item.ExecutionId, entryId), limit, ct);  // FWD-02 → orchestrator
                }
                slot++;                                            // completed → consume the slot
            }
            else // per-item business failed (author Failed OR output-validation fail) — no slot consumed
            {
                await SendResult(BuildFailed(d, "output failed schema validation"), limit, ct);  // one StepFailed; NOT abort
            }
        }

        // GATE-01 (spec §4.3 final ¶): run the cleanup tail ONLY if no item escalated. If any item escalated,
        // leave L2[messageId] + L2[inputEntryId] intact for the keeper to complete the escalated item and for a
        // later Recovery pass (redelivery) / index-TTL to reclaim — eliminating the processor/keeper race on the
        // index key. No KeyPersist on the skip path: the index keeps the TTL its atomic write set.
        if (!escalated)
            await DeleteTerminalAsync(d, messageId, db, limit, ct);    // A19/FWD-03 happy-path unified tail (two-key DEL)
    }

    /// <summary>A19/GC-01..03: the unified terminal tail. Atomically deletes BOTH the source data key and the
    /// origin allocation index in ONE multi-key DEL; on exhaustion best-effort PERSISTs the index (cancels its
    /// executionDataTtl EXPIRE) then escalates to a KeeperDelete carrying the messageId — regardless of the persist outcome
    /// (D-03). NO source-step early-return: ExecutionData(Guid.Empty) is a harmless absent operand (D-06).</summary>
    private async Task DeleteTerminalAsync(
        EntryStepDispatch d, Guid messageId, IDatabase db, int limit, CancellationToken ct)
    {
        var del = await RetryLoop.ExecuteAsync(
            () => db.KeyDeleteAsync(new RedisKey[]
            {
                L2ProjectionKeys.ExecutionData(d.EntryId),     // operand 1 (Guid.Empty → drop-on-absent no-op on a source step)
                L2ProjectionKeys.MessageIndex(messageId),      // operand 2 (the index — actively reclaimed)
            }), limit, ct);
        if (del.Succeeded) return;

        // D-03: best-effort persist (cancel the executionDataTtl EXPIRE) THEN escalate REGARDLESS of persist outcome.
        await RetryLoop.ExecuteAsync(() => db.KeyPersistAsync(L2ProjectionKeys.MessageIndex(messageId)), limit, ct);
        await SendKeeper(BuildDelete(d, messageId), limit, ct);
    }

    // ---- Send owners: every send wrapped in RetryLoop; send-exhaustion PROPAGATES (throw → broker redelivery, no _error). ----

    private async Task SendResult(IStepResult result, int limit, CancellationToken ct)
    {
        var ep = await sendProvider.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"));
        var sent = await RetryLoop.ExecuteAsync(
            async () => { await ep.Send((object)result, CancellationToken.None); return true; }, limit, ct);
        if (!sent.Succeeded) throw sent.Error!;   // propagate → throw → broker redelivery (no _error, Phase-53 D-01)

        // METRIC-05 / GAP-49-5 (D-10): count EVERY genuinely-sent step result, tagged ProcessorId PLUS the
        // terminal outcome (completed/failed/cancelled/processing). Placed AFTER the success guard so only a
        // confirmed send increments the counter (a propagated exhaustion above never reaches here).
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
        if (!sent.Succeeded) throw sent.Error!;   // propagate → throw → broker redelivery (Phase-53 D-01)
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

    private static KeeperDelete    BuildDelete(EntryStepDispatch d, Guid messageId) =>
        new(d.WorkflowId, d.StepId, d.ProcessorId) { CorrelationId = d.CorrelationId, ExecutionId = d.ExecutionId, EntryId = d.EntryId, MessageId = messageId };   // A1: inbound exec; A19: index id

    // INFRA-02 / Pitfall 1: BuildInject populates EntryId = the allocation just written,
    // Data = the raw-JSON output in-hand.
    private static KeeperInject    BuildInject(EntryStepDispatch d, ProcessItem item, Guid entryId) =>
        new(d.WorkflowId, d.StepId, d.ProcessorId)
        {
            CorrelationId = d.CorrelationId,
            ExecutionId   = item.ExecutionId,   // D-02/D-03: author-minted item exec
            EntryId       = entryId,            // the allocation written above
            Data          = item.Data,         // raw-JSON output, in-hand on the envelope
        };
}
