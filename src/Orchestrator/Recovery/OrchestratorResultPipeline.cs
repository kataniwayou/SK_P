using System.Text.Json;
using BaseConsole.Core.Resilience;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orchestrator.Configuration;
using Orchestrator.Dispatch;
using Orchestrator.Observability;
using StackExchange.Redis;

namespace Orchestrator.Recovery;

/// <summary>
/// ORCV-01..05 (Phase 71): the orchestrator result-consume gate → FORWARD/RECOVERY → gated cleanup pipeline —
/// a STRUCTURAL CLONE of <c>BaseProcessor.Core.Processing.ProcessorPipeline</c> (D-01: an independent class,
/// NOT a shared base), invoked from <see cref="Consumers.TypedResultConsumer{T}"/>. It reverses Phase 24.1's
/// L1-only result posture — L2 (the <c>L2[messageId]</c> allocation index) is back on the result path.
/// <para>
/// It mirrors the processor pipeline verbatim and diverges ONLY for the three locked domain differences:
/// <list type="number">
///   <item>D-02 heterogeneous slots — the index HASH field VALUE is a JSON tuple
///   <c>{ nextStepId, nextProcessorId, payload, newEntryId }</c> (the processor stores a bare entryId).
///   RECOVERY parses the JSON to reconstruct each per-slot re-send.</item>
///   <item>D-03 the single atomic FORWARD Lua <see cref="OrchestratorForwardWrite"/> COPIES an existing key
///   (origin → newEntryId, GET+SET form) instead of writing in-hand data. TTLs are computed in C# and passed
///   as ARGV (NO RNG inside Lua — the Phase-68 TEST-06 index/data anti-desync guard).</item>
///   <item>D-05 RECOVERY re-sends a reconstructed <see cref="StepCompleted"/> carrying the slot's
///   <c>newEntryId</c> (Pitfall 2: the existence check targets newEntryId, not the origin).</item>
/// </list>
/// </para>
/// <para>
/// <b>Delete invariant (ORCV-04/07):</b> the ONLY orchestrator-side deleter is the gated cleanup tail
/// <see cref="DeleteTerminalAsync"/> (a two-key atomic DEL of <c>L2[origin]</c> + <c>L2[messageId]</c>, run
/// only when no slot escalated this pass); FORWARD/RECOVERY escalation legs never delete. A delete exhaust
/// escalates out-of-band to a <c>KeeperDelete</c> (still the only deleting keeper state). <b>Resilience:</b>
/// every L2 op and every send is wrapped in <see cref="RetryLoop"/>; a send-exhaust PROPAGATES (throw → broker
/// redelivery, no <c>_error</c> — symmetric with the no-bus-retry orchestrator-result endpoint, Phase-53 D-01).
/// </para>
/// </summary>
public sealed class OrchestratorResultPipeline(
    IConnectionMultiplexer redis,
    ISendEndpointProvider sendProvider,
    StepAdvancement advancement,
    IOptions<RetryOptions> retryOptions,
    IOptions<OrchestratorRecoveryOptions> recoveryOptions,
    OrchestratorMetrics metrics,
    ILogger<OrchestratorResultPipeline> logger)
{
    /// <summary>The on-wire retired-slot sentinel (mirror ProcessorPipeline: a retired slot carries
    /// <c>Guid.Empty</c>). A shared <c>static readonly</c> keeps the production write byte-identical to the
    /// fact assertions (which hardcode <c>(RedisValue)Guid.Empty.ToString()</c>).</summary>
    private static readonly RedisValue RetiredSlot = Guid.Empty.ToString();

    /// <summary>ORCV-02/03 (D-03): the single atomic orchestrator FORWARD write — index slot HSET (= the D-02
    /// JSON tuple) + whole-HASH PEXPIRE + a COPY of the origin data key into the new key (GET+SET form,
    /// carrying the data TTL inline so the dest can never be left immortal — Pitfall 3), in ONE server-side op.
    /// A compile-time <c>const</c> with parameterized KEYS/ARGV (no orchestrator data concatenated into the
    /// script text → injection-safe, T-71-05). A concurrent reader/Recovery never observes a partial
    /// index-without-data state.
    /// <para>
    /// KEYS[1] = L2[messageId] (the index HASH); KEYS[2] = L2[newEntryId] (the copy dest);
    /// KEYS[3] = L2[originEntryId] (the copy source).
    /// ARGV[1] = slot ordinal; ARGV[2] = the JSON tuple; ARGV[3] = index TTL ms (random[ttl, 2×ttl]);
    /// ARGV[4] = data TTL ms (== ExecutionDataTtl). TTLs are computed in C# (no RNG inside Lua —
    /// the Phase-68 TEST-06 anti-desync guard).
    /// </para></summary>
    private const string OrchestratorForwardWrite = @"
        redis.call('HSET', KEYS[1], ARGV[1], ARGV[2])
        redis.call('PEXPIRE', KEYS[1], ARGV[3])
        local v = redis.call('GET', KEYS[3])
        if v then redis.call('SET', KEYS[2], v, 'PX', ARGV[4]) end
        return 1";

    /// <summary>The L2[messageId] index HASH whole-HASH TTL = <c>random[ExecutionDataTtl, 2×ExecutionDataTtl]</c>
    /// — derived from the SAME <see cref="OrchestratorRecoveryOptions.ExecutionDataTtlSeconds"/> as the data
    /// key (single source of truth → the index TTL can never DESYNC from the data TTL; the Phase-68 TEST-06
    /// root cause), with a floor == the data TTL and a 2× ceiling so the index STRICTLY OUTLIVES the data it
    /// points at (recovery headroom + jitter to avoid a synchronized expiry herd). Floored at 1s (a non-positive
    /// value would marshal to PEXPIRE/SET PX 0, a Redis server error).</summary>
    private TimeSpan SlotTtl()
    {
        var ttl = Math.Max(1, recoveryOptions.Value.ExecutionDataTtlSeconds);
        return TimeSpan.FromSeconds(Random.Shared.Next(ttl, 2 * ttl + 1));
    }

    /// <summary>ORCV-01..05: the entry point. Gates once on <c>exist L2[messageId]</c> (messageId = the inbound
    /// result's broker MessageId, NOT EntryId — Pitfall 1): absent → FORWARD, present → RECOVERY. A gate-op
    /// exhaustion routes to ONE OrchestratorReinject and ENDS the round trip with NO cleanup (the index/data
    /// are left intact for replay). The origin entryId = <c>m.EntryId</c> (the inbound result's real data key
    /// on a StepCompleted; Guid.Empty on Failed/Cancelled/Processing — a harmless absent copy/delete operand).</summary>
    public async Task RunAsync(
        IStepResult m, Guid messageId, StepOutcome outcome,
        StepProjection completed, IReadOnlyDictionary<Guid, StepProjection> steps, CancellationToken ct)
    {
        var db = redis.GetDatabase();
        var limit = retryOptions.Value.Limit;
        var executionDataTtl = TimeSpan.FromSeconds(Math.Max(1, recoveryOptions.Value.ExecutionDataTtlSeconds));

        // ORCV-01: branch on exist L2[messageId]. Exhaust → REINJECT; END (no cleanup, index/data intact).
        var exists = await RetryLoop.ExecuteAsync(
            () => db.KeyExistsAsync(L2ProjectionKeys.MessageIndex(messageId)), limit, ct);
        if (!exists.Succeeded)
        {
            await SendKeeper(BuildReinject(m), limit, ct);
            return;
        }

        if (exists.Value)
            await RunRecoveryAsync(m, messageId, db, limit, ct);
        else
            await RunForwardAsync(m, messageId, outcome, completed, steps, db, limit, executionDataTtl, ct);
    }

    /// <summary>ORCV-02/03/04: the FORWARD pass. Reached only when <c>L2[messageId]</c> does NOT exist.
    /// Iterates <see cref="StepAdvancement.SelectNext"/> (the SAME next-step source the consumer used); per
    /// next step mints a newEntryId, builds the D-02 JSON tuple, issues ONE atomic <see cref="OrchestratorForwardWrite"/>
    /// (index HSET + whole-hash PEXPIRE + origin→new data COPY), then dispatches an <see cref="EntryStepDispatch"/>
    /// to <c>queue:{nextProcessorId}</c> and retires the slot to guid.empty. An atomic-write exhaust → ONE
    /// OrchestratorInject + <c>escalated=true</c> (no dispatch/retire for that slot). The GATE-01 gated two-key
    /// cleanup tail runs ONLY if nothing escalated.</summary>
    private async Task RunForwardAsync(
        IStepResult m, Guid messageId, StepOutcome outcome,
        StepProjection completed, IReadOnlyDictionary<Guid, StepProjection> steps,
        IDatabase db, int limit, TimeSpan executionDataTtl, CancellationToken ct)
    {
        var originEntryId = m.EntryId;
        var slot = 0;
        var escalated = false;

        foreach (var (nextStepId, step) in advancement.SelectNext(outcome, completed, steps))
        {
            var newEntryId = NewId.NextGuid();

            // D-02 slot tuple — a plain anonymous object + default STJ (no converter; no [JsonPropertyName]).
            var tuple = JsonSerializer.Serialize(new
            {
                nextStepId,
                nextProcessorId = step.ProcessorId,
                payload = step.Payload,
                newEntryId,
            });

            // ORCV-02: ONE atomic index+data write (3 KEYS, TTLs as ARGV — no RNG in Lua).
            var write = await RetryLoop.ExecuteAsync(
                () => db.ScriptEvaluateAsync(OrchestratorForwardWrite,
                    new RedisKey[]
                    {
                        L2ProjectionKeys.MessageIndex(messageId),      // KEYS[1] — the index HASH
                        L2ProjectionKeys.ExecutionData(newEntryId),    // KEYS[2] — the copy dest
                        L2ProjectionKeys.ExecutionData(originEntryId), // KEYS[3] — the copy source
                    },
                    new RedisValue[]
                    {
                        slot,                                          // ARGV[1] slot ordinal
                        tuple,                                         // ARGV[2] the D-02 JSON tuple
                        (long)SlotTtl().TotalMilliseconds,             // ARGV[3] index TTL — random[ttl, 2×ttl]
                        (long)executionDataTtl.TotalMilliseconds,      // ARGV[4] data TTL — == ExecutionDataTtl
                    }),
                limit, ct);

            if (!write.Succeeded)
            {
                // ORCV-02 NODROP: atomic-write exhaust → ONE OrchestratorInject (the keeper completes the
                // copy + dispatches). No silent drop. The slot was claimed → consume it + escalate.
                logger.LogWarning(write.Error,
                    "Orchestrator atomic forward write exhausted; escalating to keeper INJECT (newEntryId={NewEntryId})", newEntryId);
                await SendKeeper(BuildInject(m, originEntryId, newEntryId, nextStepId, step.ProcessorId, step.Payload), limit, ct);
                escalated = true;
                slot++;
                continue;
            }

            // ORCV-04: dispatch downstream (send-before-retire), THEN retire the slot to guid.empty.
            await SendDispatch(m, nextStepId, step.ProcessorId, step.Payload, newEntryId, limit, ct);

            var retire = await RetryLoop.ExecuteAsync(
                () => db.HashSetAsync(L2ProjectionKeys.MessageIndex(messageId), slot, RetiredSlot), limit, ct);
            // Retire exhaust → leave the slot (a future replay re-sends; dup-tolerant) — mirror ProcessorPipeline.
            _ = retire;
            slot++;
        }

        // GATE-01 (ORCV-04): run the cleanup tail ONLY if nothing escalated. If any slot escalated, leave
        // L2[messageId] + L2[origin] intact for the keeper + a later Recovery pass / index-TTL to reclaim.
        if (!escalated)
            await DeleteTerminalAsync(m, messageId, db, limit, ct);
    }

    /// <summary>ORCV-05: the RECOVERY pass. Reached only when <c>L2[messageId]</c> EXISTS (a redelivery).
    /// HGETALL the slot array; per slot parse the D-02 JSON tuple (skip retired guid.empty / unparsable slots
    /// gracefully — T-71-06); the existence check targets the slot's <c>newEntryId</c> (Pitfall 2). 3-way
    /// classify: data exists → re-send a reconstructed <see cref="StepCompleted"/> carrying newEntryId BEFORE
    /// retiring the slot (send-before-retire); clean not-exist → drop, slot NOT retired; an L2 fault → leave
    /// the slot intact. Tail: OrchestratorReinject if any slot faulted, else the two-key DEL (mutual exclusion).</summary>
    private async Task RunRecoveryAsync(
        IStepResult m, Guid messageId, IDatabase db, int limit, CancellationToken ct)
    {
        // ORCV-05: HGETALL the slot array. Exhaust → REINJECT; END (no source delete — index/data intact).
        var read = await RetryLoop.ExecuteAsync(
            () => db.HashGetAllAsync(L2ProjectionKeys.MessageIndex(messageId)), limit, ct);
        if (!read.Succeeded)
        {
            await SendKeeper(BuildReinject(m), limit, ct);
            return;
        }

        var temp = new List<(RedisValue Slot, SlotTuple Tuple, bool Completed, bool Infra)>();
        foreach (var entry in read.Value!)
        {
            // Skip already-retired (Guid.Empty) or unparsable slots — T-71-06: a bad slot cannot abort the pass.
            if (!TryParseTuple(entry.Value, out var tuple) || tuple.NewEntryId == Guid.Empty)
                continue;

            // Pitfall 2: the existence check targets the slot's newEntryId (the copied-output key).
            var exist = await RetryLoop.ExecuteAsync(
                () => db.KeyExistsAsync(L2ProjectionKeys.ExecutionData(tuple.NewEntryId)), limit, ct);

            if (!exist.Succeeded)
                temp.Add((entry.Name, tuple, Completed: false, Infra: true));    // L2 fault → leave slot
            else if (exist.Value)
                temp.Add((entry.Name, tuple, Completed: true, Infra: false));    // exists → re-send
            // clean not-exist → drop: NOT added (no send, no retire)
        }

        foreach (var t in temp)
        {
            if (!t.Completed) continue;   // infra slots: leave intact, no send (handled in the tail)

            // ORCV-05: re-send a reconstructed StepCompleted carrying the slot's newEntryId (D-05). SEND FIRST.
            await SendResult(BuildResent(m, t.Tuple.NewEntryId), limit, ct);

            // Retire AFTER a confirmed send (SendResult throws on send-exhaust → reaching here == sent).
            var retire = await RetryLoop.ExecuteAsync(
                () => db.HashSetAsync(L2ProjectionKeys.MessageIndex(messageId), t.Slot, RetiredSlot), limit, ct);
            if (retire.Succeeded)
            {
                await RetryLoop.ExecuteAsync(
                    () => db.KeyExpireAsync(L2ProjectionKeys.MessageIndex(messageId), SlotTtl()), limit, ct);
            }
            // Retire exhaust → leave the slot; a future replay re-sends (dup-tolerant).
        }

        // ORCV-05 tail — REINJECT ⊻ two-key DEL mutual exclusion.
        if (temp.Any(t => t.Infra))
        {
            await SendKeeper(BuildReinject(m), limit, ct);   // replay owns the lifecycle; do NOT delete
            return;
        }
        await DeleteTerminalAsync(m, messageId, db, limit, ct);
    }

    /// <summary>ORCV-04/07: the unified terminal cleanup tail (copied verbatim from ProcessorPipeline). Atomically
    /// deletes BOTH the origin data key and the allocation index in ONE multi-key DEL; on exhaustion best-effort
    /// PERSISTs the index then escalates to a <see cref="KeeperDelete"/> carrying the messageId — regardless of
    /// the persist outcome. This is the ONLY orchestrator-side deleter; <c>KeeperDelete</c> stays the only
    /// deleting keeper state. <c>ExecutionData(Guid.Empty)</c> on a non-Completed origin is a harmless absent operand.</summary>
    private async Task DeleteTerminalAsync(
        IStepResult m, Guid messageId, IDatabase db, int limit, CancellationToken ct)
    {
        var del = await RetryLoop.ExecuteAsync(
            () => db.KeyDeleteAsync(new RedisKey[]
            {
                L2ProjectionKeys.ExecutionData(m.EntryId),     // operand 1 (Guid.Empty → drop-on-absent no-op)
                L2ProjectionKeys.MessageIndex(messageId),      // operand 2 (the index — actively reclaimed)
            }), limit, ct);
        if (del.Succeeded) return;

        await RetryLoop.ExecuteAsync(() => db.KeyPersistAsync(L2ProjectionKeys.MessageIndex(messageId)), limit, ct);
        await SendKeeper(BuildDelete(m, messageId), limit, ct);
    }

    // ---- Send owners: every send wrapped in RetryLoop; send-exhaustion PROPAGATES (throw → broker redelivery). ----

    private async Task SendResult(IStepResult result, int limit, CancellationToken ct)
    {
        var ep = await sendProvider.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"));
        var sent = await RetryLoop.ExecuteAsync(
            async () => { await ep.Send((object)result, CancellationToken.None); return true; }, limit, ct);
        if (!sent.Succeeded) throw sent.Error!;
    }

    private async Task SendDispatch(
        IStepResult m, Guid nextStepId, Guid nextProcessorId, string payload, Guid newEntryId, int limit, CancellationToken ct)
    {
        var dispatch = new EntryStepDispatch(m.WorkflowId, nextStepId, nextProcessorId, payload)
        {
            CorrelationId = m.CorrelationId,
            ExecutionId   = m.ExecutionId,
            EntryId       = newEntryId,
        };
        var ep = await sendProvider.GetSendEndpoint(new Uri($"queue:{nextProcessorId:D}"));
        var sent = await RetryLoop.ExecuteAsync(
            async () => { await ep.Send((object)dispatch, CancellationToken.None); return true; }, limit, ct);
        if (!sent.Succeeded) throw sent.Error!;
        metrics.DispatchSent.Add(1, new KeyValuePair<string, object?>("ProcessorId", nextProcessorId.ToString("D")));
    }

    private async Task SendKeeper(IKeeperRecoverable msg, int limit, CancellationToken ct)
    {
        var ep = await sendProvider.GetSendEndpoint(new Uri($"queue:{KeeperQueues.Recovery}"));
        var sent = await RetryLoop.ExecuteAsync(
            async () => { await ep.Send((object)msg, CancellationToken.None); return true; }, limit, ct);
        if (!sent.Succeeded) throw sent.Error!;
    }

    // ---- JSON slot-tuple parse (D-02 / Pitfall 2) ----

    /// <summary>The D-02 heterogeneous slot tuple — the index HASH field VALUE.</summary>
    private sealed record SlotTuple(Guid NextStepId, Guid NextProcessorId, string Payload, Guid NewEntryId);

    /// <summary>Tolerantly parse a slot value as the D-02 JSON tuple. A retired guid.empty sentinel, a null/empty
    /// value, or malformed JSON returns false (the caller skips it — T-71-06).</summary>
    private static bool TryParseTuple(RedisValue value, out SlotTuple tuple)
    {
        tuple = default!;
        if (value.IsNullOrEmpty) return false;
        var s = value.ToString();
        if (!s.StartsWith("{", StringComparison.Ordinal)) return false;   // a retired guid.empty sentinel is not JSON
        try
        {
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var newEntryId = root.TryGetProperty("newEntryId", out var ne) && ne.TryGetGuid(out var g) ? g : Guid.Empty;
            var nextStepId = root.TryGetProperty("nextStepId", out var ns) && ns.TryGetGuid(out var s1) ? s1 : Guid.Empty;
            var nextProcId = root.TryGetProperty("nextProcessorId", out var np) && np.TryGetGuid(out var p1) ? p1 : Guid.Empty;
            var payload = root.TryGetProperty("payload", out var pl) && pl.ValueKind == JsonValueKind.String ? pl.GetString()! : "";
            tuple = new SlotTuple(nextStepId, nextProcId, payload, newEntryId);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ---- Builders ----

    /// <summary>D-05: the recovery re-send — a StepCompleted carrying the slot's newEntryId (the copied output
    /// key), preserving the inbound result's correlation/execution lineage.</summary>
    private static StepCompleted BuildResent(IStepResult m, Guid newEntryId) =>
        new(m.WorkflowId, m.StepId, m.ProcessorId)
        {
            CorrelationId = m.CorrelationId,
            ExecutionId   = m.ExecutionId,
            EntryId       = newEntryId,
        };

    /// <summary>ORCV-01/05: the gate/recovery-fault escalation — an OrchestratorReinject carrying the inbound
    /// result's outcome discriminator + the matching union field (Failed/Cancelled), so the keeper's factory
    /// rebuilds the right IStepResult subtype and re-injects to queue:orchestrator-result.</summary>
    private static OrchestratorReinject BuildReinject(IStepResult m) =>
        new(m.WorkflowId, m.StepId, m.ProcessorId)
        {
            CorrelationId       = m.CorrelationId,
            ExecutionId         = m.ExecutionId,
            EntryId             = m.EntryId,
            Outcome             = OutcomeOf(m),
            ErrorMessage        = (m as StepFailed)?.ErrorMessage,
            CancellationMessage = (m as StepCancelled)?.CancellationMessage,
        };

    /// <summary>ORCV-02: the atomic-write-exhaust escalation — an OrchestratorInject carrying the copy operands
    /// (origin → new) + the downstream dispatch tuple, so the keeper completes the index+data copy and dispatches.</summary>
    private static OrchestratorInject BuildInject(
        IStepResult m, Guid originEntryId, Guid newEntryId, Guid nextStepId, Guid nextProcessorId, string payload) =>
        new(m.WorkflowId, m.StepId, m.ProcessorId)
        {
            CorrelationId   = m.CorrelationId,
            ExecutionId     = m.ExecutionId,
            EntryId         = newEntryId,      // the newEntryId to copy-into / dispatch with
            OriginEntryId   = originEntryId,   // the origin data key to copy FROM
            NextStepId      = nextStepId,
            NextProcessorId = nextProcessorId,
            Payload         = payload,
        };

    /// <summary>ORCV-04: the delete-exhaust escalation — the shared KeeperDelete (the only deleting keeper state).</summary>
    private static KeeperDelete BuildDelete(IStepResult m, Guid messageId) =>
        new(m.WorkflowId, m.StepId, m.ProcessorId)
        {
            CorrelationId = m.CorrelationId,
            ExecutionId   = m.ExecutionId,
            EntryId       = m.EntryId,
            MessageId     = messageId,
        };

    /// <summary>Maps the concrete inbound <see cref="IStepResult"/> record to its <see cref="StepOutcome"/>
    /// discriminator (the only allowed status branch — D-07).</summary>
    private static StepOutcome OutcomeOf(IStepResult m) => m switch
    {
        StepCompleted  => StepOutcome.Completed,
        StepFailed     => StepOutcome.Failed,
        StepCancelled  => StepOutcome.Cancelled,
        StepProcessing => StepOutcome.Processing,
        _              => StepOutcome.Failed,
    };
}
