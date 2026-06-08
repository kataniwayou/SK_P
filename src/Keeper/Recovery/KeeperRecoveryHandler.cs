using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Keeper.Observability;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Keeper.Recovery;

/// <summary>
/// KHARD-03 keystone — the SINGLE shared generic recovery body both Keeper fault consumers delegate to.
/// Collapses the two formerly verbatim-duplicate <c>Consume</c> bodies
/// (<c>FaultEntryStepDispatchConsumer</c>, <c>FaultExecutionResultConsumer</c>) into one place so future
/// recovery changes — and KHARD-01's cap (Plan 02) — land once, not twice. Injected sealed singleton
/// mirroring <see cref="L2ProbeRecovery"/> (primary-ctor, registered beside it in <c>Program.cs</c>).
/// <para>
/// The only per-type deltas are DATA, passed as params: the <c>fault_type</c> tag literal
/// (<c>faultTypeTag</c>) and the re-inject endpoint URI (<c>reinjectEndpoint</c>). Everything else — the
/// double-unwrap, the LOAD-BEARING manual CorrelationId scope + <see cref="ExecutionLogScope.BuildState"/>,
/// the intake Information log, <c>Publish(PauseWorkflow)</c>, the awaited probe loop, and both the Recovered
/// (re-inject + <c>Publish(ResumeWorkflow)</c>) and GaveUp (park the original <c>Fault&lt;T&gt;</c> to
/// keeper-dlq) branches — is byte-identical to the source consumers (T-40-01).
/// </para>
/// <para>
/// Security (T-40-02, carried verbatim from T-35-04/05): all ids, the derived <c>localKey</c>, and the
/// exception text are STRUCTURED PARAMS under fixed template holes / scope keys — never interpolated. Only <c>ExceptionType</c>
/// + <c>Message</c> are surfaced; the exception's stack frames are NOT logged at Information.
/// </para>
/// </summary>
public sealed class KeeperRecoveryHandler(
    ILogger<KeeperRecoveryHandler> logger, L2ProbeRecovery recovery, KeeperMetrics metrics,
    IConnectionMultiplexer redis, IOptions<ProbeOptions> opts)   // KHARD-01: cap deps (mirrors L2ProbeRecovery)
{
    /// <summary>
    /// WR-01 (T-40-05): atomic INCR + conditional PEXPIRE for the per-H recover-attempt counter. INCR the
    /// key; iff the result is 1 (the key was just created) PEXPIRE it with ARGV[1] ms; return the count.
    /// Run as a single Redis op so the key can NEVER exist without a TTL (no INCR-then-EXPIRE crash window);
    /// the PEXPIRE is gated on first-create so later increments do NOT clobber the TTL (no-clobber, net-zero).
    /// </summary>
    private const string IncrWithTtl =
        "local n = redis.call('INCR', KEYS[1]) " +
        "if n == 1 then redis.call('PEXPIRE', KEYS[1], ARGV[1]) end " +
        "return n";

    /// <summary>D-A3: the counter-key TTL window in milliseconds (300s — matches the flag/data window).
    /// Passed as ARGV[1] to <see cref="IncrWithTtl"/> so the TTL value lives in exactly one place.</summary>
    private const long CounterTtlMillis = 300_000;

    /// <summary>
    /// The shared recovery body. <typeparamref name="T"/> is the verbatim inner faulted message
    /// (<see cref="IExecutionCorrelated"/>), read through the generic bound. D-14 (DARK): the wire
    /// content-hash <c>H</c> was removed from the interface in v4.0.0, so this body now derives a LOCAL
    /// identity key from the four-tuple (<see cref="L2ProjectionKeys.CompositeBackup"/>) instead of reading
    /// <c>inner.H</c>; the path is present-and-compiling but dormant (its retirement is Phases 47/48).
    /// </summary>
    /// <param name="context">The <see cref="Fault{T}"/> consume context.</param>
    /// <param name="faultTypeTag">The closed-enum fault_type literal (<see cref="KeeperMetricTags.FaultTypeDispatch"/> | <see cref="KeeperMetricTags.FaultTypeResult"/>).</param>
    /// <param name="reinjectEndpoint">The per-type re-inject endpoint URI selector (<c>inner =&gt; queue:{ProcessorId:D}</c> | <c>queue:{OrchestratorQueues.Result}</c>).</param>
    /// <param name="ct">The consume cancellation token (the consumer passes <c>context.CancellationToken</c>).</param>
    public async Task HandleAsync<T>(
        ConsumeContext<Fault<T>> context,
        string faultTypeTag,
        Func<T, Uri> reinjectEndpoint,
        CancellationToken ct)
        where T : class, IExecutionCorrelated
    {
        var inner = context.Message.Message;   // double .Message — verbatim inner IExecutionCorrelated
        var ex    = context.Message.Exceptions is { Length: > 0 } exs ? exs[0] : null;

        // D-14 (DARK path): the wire content-hash `H` was removed from IExecutionCorrelated in v4.0.0
        // (RETIRE-01). The reactive recovery path stays present-and-compiling but goes dormant — wherever it
        // formerly keyed off `inner.H` it now derives a LOCAL identity string from the four-tuple, which is
        // exactly the new CompositeBackup key shape. No wire H is re-introduced; this is purely an internal
        // probe-attempts / pause-key slot (T-43-10 accepted — no auth/dedup decision depends on it here).
        var localKey = L2ProjectionKeys.CompositeBackup(
            inner.CorrelationId, inner.WorkflowId, inner.ProcessorId, inner.ExecutionId);

        // KMET-02/03: intake — start the intake→terminal timer and count the consumed fault. ProcessorId is
        // the only bounded id label (no workflowId). fault_type is this consumer's closed enum (passed in).
        var procId = inner.ProcessorId.ToString("D");
        var sw     = Stopwatch.StartNew();
        metrics.FaultConsumed.Add(1, KeeperMetricTags.FaultTags(faultTypeTag, procId));

        using (logger.BeginScope(new Dictionary<string, object> { [CorrelationKeys.LogScope] = inner.CorrelationId.ToString() }))
        using (logger.BeginScope(ExecutionLogScope.BuildState(inner)))
        {
            logger.LogInformation(
                "Keeper fault intake: {FaultType} for H={H} — {ExceptionType}: {ExceptionMessage}",
                typeof(T).Name, localKey, ex?.ExceptionType, ex?.Message);
        }

        // PAUSE-01 (D-03): publish PauseWorkflow at intake — BEFORE probing — so the orchestrator pauses
        // the workflow's Quartz job and the L2 outage stops spreading. Publish (NOT Send) so message-type
        // binding fans the signal out to the orchestrator's per-replica endpoint; CorrelationId carried.
        await context.Publish(
            new PauseWorkflow(inner.WorkflowId, localKey) { CorrelationId = inner.CorrelationId },
            ct);
        metrics.WorkflowPaused.Add(1, new KeyValuePair<string, object?>(KeeperMetricTags.ProcessorId, procId)); // workflow-scoped (no fault_type)

        // PROBE-01/05: the bounded probe loop is AWAITED inside Consume — the broker delivery stays
        // un-acked until the loop exits, so ack-after-loop is automatic (no early Task.CompletedTask).
        var outcome = await recovery.RunAsync(inner.EntryId, localKey, procId, ct);
        if (outcome == ProbeOutcome.Recovered)
        {
            // KHARD-01 (D-A1/D-A3): bound the OUTER recover→reinject cycle per H. A persistent fault that the
            // probe keeps "recovering" but the receiver keeps re-faulting would otherwise flood the stack. The
            // atomic INCR is race-free across the 2 keeper replicas; any crossing increment (n >= cap+1) parks
            // — retry-safe (WR-03): a park after a failed Send is re-attempted, never silently dropped.
            var db  = redis.GetDatabase();
            var key = (RedisKey)L2ProjectionKeys.KeeperRecoverAttempts(localKey);
            // WR-01 (T-40-05): atomic INCR + PEXPIRE-NX in ONE round-trip. The previous INCR-then-EXPIRE pair
            // had a crash window between the two ops that could leave the counter key with NO TTL → permanent
            // leak. The Lua eval makes the key BORN with a TTL the moment it first exists (n==1); the PEXPIRE
            // is conditional on first-create so later increments NEVER clobber the TTL — exactly the prior
            // ExpireWhen.HasNoExpiry no-clobber semantics, now indivisible (no leak window, net-zero skp: growth).
            var n   = (long)await db.ScriptEvaluateAsync(
                IncrWithTtl,
                new[] { key },
                new RedisValue[] { CounterTtlMillis });
            var cap = opts.Value.RecoverAttemptCap;
            if (n >= cap + 1)
            {
                // WR-03 (T-40-06): retry-safe cap-park. Treat ANY crossing (n >= cap+1), not just the exact
                // n == cap+1, as "must be parked". Rationale: if the Send below throws (broker hiccup), the
                // exception propagates BEFORE the DEL, MassTransit's Immediate(N) re-delivers, and the retry's
                // INCR yields cap+2 — under the old strict n==cap+1 gate that retry took a silent non-parking
                // return, dropping the Fault<T> envelope. With n >= cap+1 the retry still parks. keeper-dlq is
                // terminal and a duplicate park under the rare 2-replica race is drained by the Plan-03
                // stably-empty loop, so park-idempotency (not single-winner) is the correct contract here.
                var capDlq = await context.GetSendEndpoint(new Uri($"queue:{KeeperQueues.DeadLetter}"));
                await capDlq.Send(context.Message, ct);   // if this throws → propagate → Immediate(N) retry re-parks (DEL below NOT reached)
                // Metrics recorded only AFTER the Send is confirmed durable (a failed Send never counts a park).
                metrics.DlqPushed.Add(1,
                    new KeyValuePair<string, object?>(KeeperMetricTags.Reason, KeeperMetricTags.ReasonRecoverCap),
                    new KeyValuePair<string, object?>(KeeperMetricTags.FaultType, faultTypeTag),
                    new KeyValuePair<string, object?>(KeeperMetricTags.ProcessorId, procId));
                metrics.RecoveryDuration.Record(sw.Elapsed.TotalSeconds,
                    new KeyValuePair<string, object?>(KeeperMetricTags.Outcome, KeeperMetricTags.OutcomeGaveUp),
                    new KeyValuePair<string, object?>(KeeperMetricTags.ProcessorId, procId));
                await db.KeyDeleteAsync(key);   // DEL only AFTER a SUCCESSFUL park — converges, no counter leak
                return;   // parked: never reinject past cap
            }

            // PROBE-03 (spike-proven, GATE_EXIT=0): re-inject the VERBATIM inner to its origin endpoint.
            // Send (NOT Publish), same H. PROBE-06: NO Keeper-side dedup — the receiver's surviving Phase-31
            // flag[H] gate collapses any duplicate re-inject. Origin endpoint is the per-type delta.
            var endpoint = await context.GetSendEndpoint(reinjectEndpoint(inner));
            await endpoint.Send(inner, ct);

            // PAUSE-05 (D-04): resume on recovery — AFTER the re-inject, still inside the Recovered branch.
            await context.Publish(
                new ResumeWorkflow(inner.WorkflowId, localKey) { CorrelationId = inner.CorrelationId },
                ct);

            // KMET-02/03: recovered terminal — resume + recovered counters + the intake→terminal duration
            // (seconds, NOT milliseconds — the buckets are seconds).
            metrics.WorkflowResumed.Add(1, new KeyValuePair<string, object?>(KeeperMetricTags.ProcessorId, procId));
            metrics.Recovered.Add(1, KeeperMetricTags.FaultTags(faultTypeTag, procId));
            metrics.RecoveryDuration.Record(sw.Elapsed.TotalSeconds,
                new KeyValuePair<string, object?>(KeeperMetricTags.Outcome, KeeperMetricTags.OutcomeRecovered),
                new KeyValuePair<string, object?>(KeeperMetricTags.ProcessorId, procId));
        }
        else
        {
            // PROBE-04 (D-09/D-10): park the ORIGINAL Fault<T> envelope (carries Exceptions[] for triage) to
            // keeper-dlq, then return → ack. A fault in THIS Send is infra → Immediate(N) → DLQ-1 (consistent).
            var dlq = await context.GetSendEndpoint(new Uri($"queue:{KeeperQueues.DeadLetter}"));
            await dlq.Send(context.Message, ct);

            // KMET-02/03: gave-up terminal — dlq counter (reason+fault_type+ProcessorId) + the duration.
            metrics.DlqPushed.Add(1,
                new KeyValuePair<string, object?>(KeeperMetricTags.Reason, KeeperMetricTags.ReasonProbeExhausted),
                new KeyValuePair<string, object?>(KeeperMetricTags.FaultType, faultTypeTag),
                new KeyValuePair<string, object?>(KeeperMetricTags.ProcessorId, procId));
            metrics.RecoveryDuration.Record(sw.Elapsed.TotalSeconds,
                new KeyValuePair<string, object?>(KeeperMetricTags.Outcome, KeeperMetricTags.OutcomeGaveUp),
                new KeyValuePair<string, object?>(KeeperMetricTags.ProcessorId, procId));
        }
    }
}
