using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using BaseConsole.Core.Messaging;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// SC2 / TEST-01 (recovery-paths half) — the RealStack proof of each of the THREE v5 Keeper recovery states
/// (REINJECT present/absent, INJECT, DELETE — the v4 UPDATE/CLEANUP states were retired in Phase 53),
/// driven by DIRECT-PUBLISH of the actual state contracts to the gate-open recovery queue
/// (<see cref="KeeperQueues.Recovery"/> = the sole surviving Keeper queue, D-05). Sibling to
/// <see cref="SampleRoundTripE2ETests"/> (SC1) — it REUSES that file's <c>RealStackWebAppFactory</c> host
/// overrides + net-zero teardown discipline (cloned below) but, instead of driving the organic
/// orchestrator → dispatch → processor round trip, it publishes <see cref="KeeperReinject"/> /
/// <see cref="KeeperInject"/> / <see cref="KeeperDelete"/> straight at <c>queue:keeper-recovery</c> and
/// asserts each state's deterministic L2 / re-inject / orchestrator-advance / dead-letter effect.
/// <para>
/// The three direct-publish proofs (effects read from the production recovery consumers):
/// </para>
/// <list type="number">
///   <item><b>REINJECT data-present</b> (<c>ReinjectConsumer</c>) — PRE-SEED <c>skp:data:{entryId}</c> so
///   STRLEN&gt;0 → the consumer re-injects a reconstructed <see cref="EntryStepDispatch"/> (carrying the
///   author <see cref="KeeperReinject.Payload"/>) to <c>queue:{ProcessorId:D}</c>. Asserted on that
///   origin-queue depth.</item>
///   <item><b>REINJECT data-gone</b> (<c>ReinjectConsumer</c>) — do NOT seed the data key (STRLEN==0) →
///   Phase 52 (D-06) makes this a BY-DESIGN silent drop (no throw, no send, no dead-letter; increments
///   <c>keeper_reinject_dropped</c>). Asserted on the origin queue staying EMPTY (nothing re-injected) and
///   the DLQ depth NOT incrementing — A18 "accepted silent losses".</item>
///   <item><b>INJECT</b> (<c>InjectConsumer</c>) — Phase 52 (KEEP-02) implements the A18 forward-only body:
///   write <c>L2[m.EntryId]=m.Data</c>, send a reconstructed <see cref="StepCompleted"/> to
///   <c>queue:orchestrator-result</c>, delete <c>m.DeleteEntryId</c>. Asserted on the data key being
///   written and the source key being deleted.</item>
///   <item><b>DELETE</b> (<c>DeleteConsumer</c>, A19 both-key) — <see cref="KeeperDelete"/> now carries a
///   <see cref="KeeperDelete.MessageId"/> (KeeperDelete.cs:13); the consumer deletes BOTH
///   <c>skp:data:{entryId}</c> AND the <c>skp:msg:{messageId}</c> allocation index in ONE atomic multi-key
///   <c>DEL</c> (DeleteConsumer.cs:19-24, GC-03). PRE-SEED BOTH keys → assert BOTH gone after the one DEL.</item>
/// </list>
/// <para>
/// In addition to the three direct-publish proofs, a SECOND <c>[Fact]</c> drives the ORGANIC recovery pass
/// end-to-end: pre-seed a populated slot-array index + a completed data key, publish an
/// <see cref="EntryStepDispatch"/> with a known broker <c>MessageId</c> at <c>queue:{procId:D}</c>, forcing
/// the live processor's <c>if exist L2[messageId]</c> recovery branch (ProcessorPipeline.cs:94-105) →
/// send-before-retire (SLOT-03, slot to <c>Guid.Empty</c>) → two-key DEL net-zero (RECOV-03 tail).
/// </para>
/// <para>
/// Net-zero (D-04 + D-07): EVERY minted key — the seeded <c>skp:data:*</c> + <c>skp:msg:*</c> index — is
/// registered into <c>factory.L2KeysToCleanup</c>, so a leak surfaces as a close-gate redis SHA mismatch
/// rather than a silent TTL pass. The A19 two-key DEL self-cleans the happy case; registration is the
/// belt-and-suspenders. The data-gone DLQ message is bounded (exactly one) and self-cleaning (drained in
/// teardown). Gate-open precondition: a healthy RealStack keeps the BIT loop
/// from <c>Stop()</c>ing the <c>keeper-recovery</c> endpoint (D-04/D-09, Phase 52). When the endpoint is
/// running, the three recovery consumers (REINJECT, INJECT, DELETE) process at entry with NO Consume-level
/// gate-wait — the per-<c>Consume</c> <c>gate.WaitForOpenAsync</c> was removed in Phase 52; gating is now at
/// the endpoint level via <c>Stop</c>/<c>Start</c>.
/// </para>
/// <para>
/// Tagged <c>Category=RealStack</c> + <c>Phase=55</c>: the hermetic filter (<c>Category!=RealStack</c>)
/// EXCLUDES these facts; they run only against the operator-gated live v5 stack (55-HUMAN-UAT.md). TEST-01
/// stays UNTICKED until that GREEN live run.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]
[Trait("Phase", "58")]
[Collection("Observability")]
public sealed class SC2RecoveryPathsE2ETests
{
    // The recovery consumer awaits the gate then runs its (millisecond) L2 op + Send; allow a generous
    // budget for broker round-trip + redis settle (mirrors SC1's OutputPollTimeoutMs).
    private const int EffectPollTimeoutMs = 120_000;

    [Fact]
    public async Task LiveKeeperRecovery_AllThreeStates_ProduceTheirL2AndReinjectAndDeadLetterEffects()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var factory = new RealStackWebAppFactory();
        await factory.InitializeAsync();

        // D-05: resolve the bus and address the gate-open recovery queue by the CONST name (never the
        // literal queue string). In a healthy RealStack the BIT loop leaves the keeper-recovery endpoint
        // RUNNING, so the three recovery consumers (REINJECT, INJECT, DELETE) process at entry with no
        // Consume-level gate-wait (the per-Consume gate.WaitForOpenAsync was removed in Phase 52, D-04/D-09 —
        // gating is now endpoint Stop/Start).
        var bus = factory.Services.GetRequiredService<IBus>();
        var endpoint = await bus.GetSendEndpoint(new Uri($"queue:{KeeperQueues.Recovery}"));

        await using var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
        var db = mux.GetDatabase();

        // =========================================================================================
        // STATE 1 — REINJECT data-present → re-inject EntryStepDispatch to queue:{ProcessorId:D}
        // =========================================================================================
        {
            var wfId = Guid.NewGuid();
            var stepId = Guid.NewGuid();
            var procId = Guid.NewGuid();
            var entryId = Guid.NewGuid();

            // PRE-SEED skp:data:{entryId} so the consumer's STRLEN gate sees data PRESENT (>0) and
            // re-injects (rather than throwing the data-gone terminal). Register for net-zero teardown.
            var dataKey = L2ProjectionKeys.ExecutionData(entryId);
            await db.StringSetAsync(dataKey, "payload-bytes");
            factory.L2KeysToCleanup.Add(dataKey);

            await endpoint.Send(new KeeperReinject(wfId, stepId, procId)
            {
                CorrelationId = Guid.NewGuid(),
                ExecutionId = Guid.NewGuid(),
                EntryId = entryId,
                Payload = "step-config",
            }, ct);

            // EFFECT: ReinjectConsumer re-injects a reconstructed EntryStepDispatch to the origin queue
            // queue:{ProcessorId:D} (same target a direct dispatch uses). Assert that origin queue's depth
            // climbs to >=1 on the live broker (no consumer is bound to this fresh procId queue, so the
            // re-injected message parks there observably). Register the broker queue for teardown cleanup.
            var originQueue = procId.ToString("D");
            var depth = await PollForQueueDepthAsync(originQueue, minDepth: 1, ct);
            Assert.True(depth >= 1,
                $"REINJECT data-present: expected the re-injected EntryStepDispatch to land on " +
                $"queue:{originQueue}, but its depth stayed {depth}.");
            factory.BrokerQueuesToDelete.Add(originQueue);
        }

        // =========================================================================================
        // STATE 2 — REINJECT data-gone → Phase 52 (D-06) BY-DESIGN silent drop (no re-inject, no dead-letter)
        // =========================================================================================
        {
            var wfId = Guid.NewGuid();
            var stepId = Guid.NewGuid();
            var procId = Guid.NewGuid();
            var entryId = Guid.NewGuid();   // its skp:data:{entryId} is DELIBERATELY absent (STRLEN==0)

            // Read the DLQ depth BEFORE so we can assert it does NOT increment (data-gone is a drop, D-06).
            var dlqBefore = await ReadQueueDepthAsync(ConsolidatedErrorTransportFilter.Dlq1, ct);

            await endpoint.Send(new KeeperReinject(wfId, stepId, procId)
            {
                CorrelationId = Guid.NewGuid(),
                ExecutionId = Guid.NewGuid(),
                EntryId = entryId,
                Payload = "step-config",
            }, ct);

            // EFFECT (D-06): STRLEN==0 → silent drop — ack with no throw, no re-inject, no dead-letter, and
            // a keeper_reinject_dropped increment (observability only). Allow a settle window, then assert
            // the origin queue stayed EMPTY (nothing re-injected) and the DLQ depth did NOT climb.
            await Task.Delay(5_000, ct);
            var originQueue = procId.ToString("D");
            var originDepth = await ReadQueueDepthAsync(originQueue, ct);
            Assert.Equal(0, originDepth);   // dropped, not re-injected
            var dlqAfter = await ReadQueueDepthAsync(ConsolidatedErrorTransportFilter.Dlq1, ct);
            Assert.True(dlqAfter <= dlqBefore,
                $"REINJECT data-gone: expected a silent drop (no dead-letter), but " +
                $"{ConsolidatedErrorTransportFilter.Dlq1} depth climbed {dlqBefore} -> {dlqAfter}.");

            // WR-01: defensively register skp-dlq-1 for purge-on-teardown. Today the data-gone path is a
            // BY-DESIGN silent drop (D-06) so this purge is a no-op (nothing lands in the DLQ). But wiring it
            // here makes the teardown self-healing: if a future contract change makes data-gone throw →
            // dead-letter, the parked message is drained to net-zero locally instead of leaking to the
            // close gate's skp-dlq-1 depth==0 invariant (~50min later) with no test-local signal.
            factory.BrokerQueuesToPurge.Add(ConsolidatedErrorTransportFilter.Dlq1);
        }

        // =========================================================================================
        // STATE 3 — INJECT (Phase 52, KEEP-02) — A18 forward-only: write L2[m.EntryId]=m.Data, send a
        // reconstructed StepCompleted to queue:orchestrator-result, delete L2[m.DeleteEntryId].
        // =========================================================================================
        {
            var wfId = Guid.NewGuid();
            var stepId = Guid.NewGuid();
            var procId = Guid.NewGuid();
            var corr = Guid.NewGuid();
            var execId = Guid.NewGuid();
            var entryId = Guid.NewGuid();
            var deleteEntryId = Guid.NewGuid();

            // PRE-SEED the source key so its post-INJECT deletion is observable; register both keys for
            // net-zero teardown (the entryId write survives, the deleteEntryId source is removed by INJECT).
            var entryKey = L2ProjectionKeys.ExecutionData(entryId);
            var deleteKey = L2ProjectionKeys.ExecutionData(deleteEntryId);
            await db.StringSetAsync(deleteKey, "source-to-delete");
            factory.L2KeysToCleanup.Add(entryKey);
            factory.L2KeysToCleanup.Add(deleteKey);

            await endpoint.Send(new KeeperInject(wfId, stepId, procId)
            {
                CorrelationId = corr,
                ExecutionId = execId,
                EntryId = entryId,
                Data = "inject-payload",
                DeleteEntryId = deleteEntryId,
            }, ct);

            // EFFECT: the data key is written with m.Data, and the source key is deleted (the StepCompleted
            // send to queue:orchestrator-result is exercised end-to-end by SC1's round-trip).
            var written = await PollForKeyValueAsync(db, entryKey, "inject-payload", ct);
            Assert.True(written,
                $"INJECT: expected {entryKey} to be written with the injected Data, but it was not.");
            var sourceDeleted = await PollForKeyAbsentAsync(db, deleteKey, ct);
            Assert.True(sourceDeleted,
                $"INJECT: expected the source key {deleteKey} to be deleted after the send, but it remains.");
        }

        // =========================================================================================
        // STATE 4 — DELETE (A19 both-key, v5): KeeperDelete carries a MessageId; DeleteConsumer deletes
        // BOTH skp:data:{entryId} AND the skp:msg:{messageId} allocation index in ONE atomic multi-key DEL
        // (DeleteConsumer.cs:19-24, GC-03). The v4 source-only DELETE (data key only, no MessageId) is gone.
        // =========================================================================================
        {
            var wfId = Guid.NewGuid();
            var stepId = Guid.NewGuid();
            var procId = Guid.NewGuid();
            var entryId = NewId.NextGuid();
            var messageId = NewId.NextGuid();

            // PRE-SEED BOTH operands of the both-key DEL: the execution-data key AND a one-slot allocation
            // index HASH (skp:msg:{messageId} → {0: entryId}). Register BOTH for net-zero teardown
            // (belt-and-suspenders, D-07 — the DEL self-cleans the happy case below).
            var dataKey = L2ProjectionKeys.ExecutionData(entryId);    // skp:data:{entryId:D}
            var indexKey = L2ProjectionKeys.MessageIndex(messageId);  // skp:msg:{messageId:D}
            await db.StringSetAsync(dataKey, "to-be-deleted");
            await db.HashSetAsync(indexKey, 0, entryId.ToString("D"));
            factory.L2KeysToCleanup.Add(dataKey);
            factory.L2KeysToCleanup.Add(indexKey);

            await endpoint.Send(new KeeperDelete(wfId, stepId, procId)
            {
                CorrelationId = Guid.NewGuid(),
                ExecutionId = Guid.NewGuid(),
                EntryId = entryId,
                MessageId = messageId,   // v5 delta: MessageId carried (KeeperDelete.cs:13) → both-key DEL
            }, ct);

            // EFFECT: DeleteConsumer issues ONE KeyDeleteAsync(new[]{ ExecutionData(entryId), MessageIndex(messageId) }).
            // Assert BOTH operands go ABSENT — the A19 two-key DEL reclaimed both, not just the data key.
            Assert.True(await PollForKeyAbsentAsync(db, dataKey, ct),
                $"DELETE: expected {dataKey} (skp:data) to be deleted by the two-key DEL, but it still exists.");
            Assert.True(await PollForKeyAbsentAsync(db, indexKey, ct),
                $"DELETE: expected {indexKey} (skp:msg index) to be deleted by the two-key DEL, but it still exists.");
        }
    }

    // The live processor-sample container resolves identity + binds + MarkHealthy after the DB row is
    // seeded (compose start_period + identity-resolve latency); allow a generous budget (mirrors SC1).
    private const int LivenessPollTimeoutMs = 90_000;

    /// <summary>
    /// ORGANIC recovery-pass proof (D-03, Open-Question 2 option (b)) — drives the live processor's
    /// <c>if exist L2[messageId]</c> recovery branch (ProcessorPipeline.cs:94-105) end-to-end, NOT via a
    /// direct keeper-recovery publish. Pre-seeds a populated slot-array index (one COMPLETED slot) + its
    /// completed data key, then publishes an <see cref="EntryStepDispatch"/> to the live processor's OWN
    /// queue (<c>queue:{procId:D}</c>) with the broker <c>MessageId</c> set to that messageId — the
    /// <c>EntryStepDispatchConsumer.cs:42</c> slot-array branch key. The live processor takes the recovery
    /// branch: HGETALL → re-send the completed step (a FRESH exec, SLOT-03 send-before-retire) BEFORE
    /// retiring the slot → all-clear → the RECOV-03 two-key DEL tail (net-zero).
    /// <para>
    /// Asserts (Redis-observable, deterministic):
    /// (1) the slot is RETIRED to <c>Guid.Empty</c> in the index HASH — a slot retires ONLY after a
    ///     CONFIRMED send (SLOT-03), so observing the retire IS the proof the completed step was re-sent to
    ///     queue:orchestrator-result (no NEW data key is expected — recovery re-sends the EXISTING entryId);
    /// (2) the RECOV-03 terminal two-key DEL actively reclaims the slot-array index <c>skp:msg:{messageId}</c>
    ///     (operand 2) — assert it goes ABSENT (the A19 active reclaim, not a TTL race). The dispatch's own
    ///     <c>ExecutionData(d.EntryId)</c> is operand 1 (here a harmless absent no-op — the source sentinel
    ///     <c>Guid.Empty</c>); the slot's COMPLETED data key <c>skp:data:{entryId}</c> is a downstream-consumed
    ///     output, NOT a recovery-tail operand, so it is teardown-cleaned (L2KeysToCleanup), not asserted here.
    /// </para>
    /// <para>
    /// Per D-03 discretion this test does NOT stop redis, so it stays in <c>[Collection("Observability")]</c>
    /// (NOT the serial outage collection). Uses the SC1 truthful liveness gate (no synthetic seed) so the
    /// dispatch reaches a REAL container bound to <c>queue:{procId:D}</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task LiveOrganicRecovery_PreSeededSlotArray_ReSendsCompletedThenRetiresThenTwoKeyDelete()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var factory = new RealStackWebAppFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        // Truthful liveness gate (SC1 idiom): register the GENUINE embedded SourceHash as the Processor DB
        // row + step, then POLL the REAL container's skp:{procId:D} Healthy heartbeat so the dispatch we Send
        // reaches a container actually bound to queue:{procId:D}.
        var hash = typeof(global::Processor.Sample.SampleProcessor).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "SourceHash").Value!;
        var procId = await SeedProcessorAsync(client, hash, ct);
        var stepId = await SeedStepAsync(client, procId, ct);
        await PollForHealthyLivenessAsync(procId, ct);

        var bus = factory.Services.GetRequiredService<IBus>();
        // Drive the recovery branch by dispatching to the processor's OWN queue (the same target a direct
        // dispatch / ReinjectConsumer re-injection uses) — NOT keeper-recovery.
        var dispatchEndpoint = await bus.GetSendEndpoint(new Uri($"queue:{procId:D}"));

        await using var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
        var db = mux.GetDatabase();

        var messageId = NewId.NextGuid();
        var entryId = NewId.NextGuid();
        var indexKey = L2ProjectionKeys.MessageIndex(messageId);   // skp:msg:{messageId:D} (the recovery branch key)
        var dataKey = L2ProjectionKeys.ExecutionData(entryId);     // skp:data:{entryId:D}

        // Pre-seed a populated slot array (one COMPLETED slot at index 0) + the completed data key. This is
        // the L2[messageId]-EXISTS precondition that forces the recovery branch (vs. the forward pass).
        await db.HashSetAsync(indexKey, 0, entryId.ToString("D"));
        await db.StringSetAsync(dataKey, "completed-step-data");
        factory.L2KeysToCleanup.Add(indexKey);   // belt-and-suspenders net-zero (D-07; the two-key DEL self-cleans)
        factory.L2KeysToCleanup.Add(dataKey);

        // Fire the recovery branch: Send EntryStepDispatch to queue:{procId:D} with the broker MessageId set
        // to messageId. EntryStepDispatchConsumer.cs:42 reads ctx.MessageId as the slot-array branch key
        // (null is a contract violation). The MassTransit pipe-callback overload sets it on the SendContext.
        var dispatch = new EntryStepDispatch(procId, stepId, procId, "organic-recovery-config")
        {
            CorrelationId = Guid.NewGuid(),
        };
        await dispatchEndpoint.Send(dispatch, ctx => ctx.MessageId = messageId, ct);

        // EFFECT (1) send-before-retire (SLOT-03): the slot is retired to Guid.Empty ONLY after the completed
        // step is re-sent to queue:orchestrator-result. Poll the HASH slot until it reads Guid.Empty.
        Assert.True(await PollForHashSlotRetiredAsync(db, indexKey, 0, ct),
            $"ORGANIC recovery: expected slot 0 of {indexKey} to be retired to Guid.Empty after the " +
            $"send-before-retire (SLOT-03), proving the completed step was re-sent — it never retired.");

        // EFFECT (2) RECOV-03 all-clear tail — the two-key DEL actively reclaims the slot-array INDEX (net-zero).
        // The terminal tail (DeleteTerminalAsync, ProcessorPipeline.cs:310-315) DELs the pair
        // [ExecutionData(d.EntryId), MessageIndex(messageId)]. Here d.EntryId is the source sentinel Guid.Empty
        // (the EntryStepDispatch default), so operand 1 is a HARMLESS ABSENT no-op (D-06) and the load-bearing
        // reclaim is operand 2 — the index skp:msg:{messageId}. Assert IT goes ABSENT: that is the A19 ACTIVE
        // reclaim (not a TTL race). The slot's COMPLETED data key skp:data:{entryId} is deliberately NOT asserted
        // gone — by A18 design it is a downstream-consumed OUTPUT (the re-sent StepCompleted carries entryId; a
        // later step's terminal tail / its bounded TTL reclaims it), NOT a recovery-tail operand. It is registered
        // in L2KeysToCleanup (above) for net-zero teardown, so it never leaks to the close gate.
        Assert.True(await PollForKeyAbsentAsync(db, indexKey, ct),
            $"ORGANIC recovery: expected {indexKey} (skp:msg index) gone after the two-key DEL net-zero tail " +
            $"(operand 2 of DeleteTerminalAsync — the A19 active index reclaim).");
    }

    // ---- Liveness poll (cloned from SC1): wait for the REAL container's skp:{procId:D} Healthy heartbeat ----

    private static async Task PollForHealthyLivenessAsync(Guid procId, CancellationToken ct)
    {
        await using var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
        var db = mux.GetDatabase();
        var key = L2ProjectionKeys.Processor(procId);

        var deadline = DateTime.UtcNow.AddMilliseconds(LivenessPollTimeoutMs);
        var delay = 500;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var raw = await db.StringGetAsync(key);
            if (!raw.IsNullOrEmpty)
            {
                var projection = JsonSerializer.Deserialize<ProcessorProjection>(raw!);
                if (projection?.Liveness is { } live)
                {
                    var age = DateTime.UtcNow - live.Timestamp.ToUniversalTime();
                    var staleAfter = TimeSpan.FromSeconds(Math.Max(live.Interval, 1) * 3);
                    if (age <= staleAfter)
                    {
                        return; // the REAL container is Healthy — the dispatch will reach its bound queue.
                    }
                }
            }

            await Task.Delay(Math.Min(delay, 2_000), ct);
            delay = Math.Min(delay * 2, 2_000);
        }

        Assert.Fail(
            $"The processor-sample container never wrote a fresh Healthy liveness key {key} within " +
            $"{LivenessPollTimeoutMs}ms. Either the container is down, or its embedded SourceHash diverges " +
            $"from the host-built hash registered as the DB row. Ensure the full compose stack incl. " +
            $"processor-sample is up healthy.");
    }

    /// <summary>Poll the int-slot of a slot-array index HASH until it reads <c>Guid.Empty</c> (the
    /// <c>RetiredSlot</c> sentinel written send-before-retire, SLOT-03).</summary>
    private static async Task<bool> PollForHashSlotRetiredAsync(IDatabase db, string indexKey, int slot, CancellationToken ct)
    {
        var empty = Guid.Empty.ToString("D");
        var deadline = DateTime.UtcNow.AddMilliseconds(EffectPollTimeoutMs);
        var delay = 500;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var value = await db.HashGetAsync(indexKey, slot);
            if (value.HasValue && string.Equals(value.ToString(), empty, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            // The HASH may already be gone (the two-key DEL raced ahead of this poll) — that ALSO proves the
            // slot was retired then the all-clear tail fired; treat an absent key as retired.
            if (!await db.KeyExistsAsync(indexKey))
            {
                return true;
            }

            await Task.Delay(Math.Min(delay, 2_000), ct);
            delay = Math.Min(delay * 2, 2_000);
        }

        return false;
    }

    // ---- HTTP seeding helpers (Processor -> Step), cloned from SC1RoundTripE2ETests ----------------

    private static async Task<Guid> SeedProcessorAsync(HttpClient client, string sourceHash, CancellationToken ct)
    {
        // GET-or-create (idempotent): the genuine embedded hash is FIXED and guarded by
        // uq_processor_source_hash, so reuse the existing row (the one the live container heartbeats against)
        // and only create on a fresh DB.
        var lookup = await client.GetAsync($"/api/v1/processors/by-source-hash/{sourceHash}", ct);
        if (lookup.StatusCode == HttpStatusCode.OK)
        {
            var existing = await lookup.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);
            return existing!.Id;
        }

        var dto = new ProcessorCreateDto(
            Name: $"sample-proc-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            SourceHash: sourceHash,
            InputSchemaId: null,
            OutputSchemaId: null,
            ConfigSchemaId: null);
        var resp = await client.PostAsJsonAsync("/api/v1/processors", dto, ct);
        resp.EnsureSuccessStatusCode();
        var proc = await resp.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);
        return proc!.Id;
    }

    private static async Task<Guid> SeedStepAsync(HttpClient client, Guid processorId, CancellationToken ct)
    {
        var dto = new StepCreateDto(
            Name: $"sample-step-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            ProcessorId: processorId,
            NextStepIds: null,
            EntryCondition: StepEntryCondition.Always);
        var resp = await client.PostAsJsonAsync("/api/v1/steps", dto, ct);
        resp.EnsureSuccessStatusCode();
        var step = await resp.Content.ReadFromJsonAsync<StepReadDto>(cancellationToken: ct);
        return step!.Id;
    }

    // ---- L2 polls (mirror SampleRoundTripE2ETests' scan/poll shapes) -------------------------------

    private static async Task<bool> PollForKeyAbsentAsync(IDatabase db, string key, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(EffectPollTimeoutMs);
        var delay = 500;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (!await db.KeyExistsAsync(key))
            {
                return true;
            }

            await Task.Delay(Math.Min(delay, 2_000), ct);
            delay = Math.Min(delay * 2, 2_000);
        }

        return false;
    }

    private static async Task<bool> PollForKeyValueAsync(IDatabase db, string key, string expected, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(EffectPollTimeoutMs);
        var delay = 500;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var value = await db.StringGetAsync(key);
            if (value.HasValue && value.ToString() == expected)
            {
                return true;
            }

            await Task.Delay(Math.Min(delay, 2_000), ct);
            delay = Math.Min(delay * 2, 2_000);
        }

        return false;
    }

    // ---- Broker queue-depth helpers (live RabbitMQ via docker exec rabbitmqctl) --------------------
    // Mirrors the RecoveryDeadLetterFacts depth-assertion idiom adapted to the RealStack: the live
    // consolidated transport really lands the data-gone give-up in skp-dlq-1, and the re-inject really
    // lands an EntryStepDispatch on queue:{procId:D}. Read depth off the live broker.

    private static async Task<long> PollForQueueDepthAsync(string queue, long minDepth, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(EffectPollTimeoutMs);
        var delay = 1_000;
        long depth = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            depth = await ReadQueueDepthAsync(queue, ct);
            if (depth >= minDepth)
            {
                return depth;
            }

            await Task.Delay(Math.Min(delay, 3_000), ct);
            delay = Math.Min(delay * 2, 3_000);
        }

        return depth;
    }

    /// <summary>
    /// Read the message count of a single broker queue via
    /// <c>docker exec sk-rabbitmq rabbitmqctl -q list_queues name messages</c>, matching the row whose name
    /// equals <paramref name="queue"/>. Returns 0 when the queue does not exist yet (no row).
    /// </summary>
    private static async Task<long> ReadQueueDepthAsync(string queue, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "exec", "sk-rabbitmq", "rabbitmqctl", "-q", "list_queues", "name", "messages" })
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start 'docker exec sk-rabbitmq rabbitmqctl'.");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // rabbitmqctl -q emits TAB-separated "name<TAB>messages" rows.
            var cols = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (cols.Length >= 2 && string.Equals(cols[0], queue, StringComparison.Ordinal)
                && long.TryParse(cols[1], out var count))
            {
                return count;
            }
        }

        return 0;   // queue not present (or empty) → depth 0.
    }

    /// <summary>Purge a broker queue via <c>docker exec sk-rabbitmq rabbitmqctl purge_queue {queue}</c>.</summary>
    private static async Task PurgeQueueAsync(string queue)
    {
        await RunRabbitCtlAsync(new[] { "purge_queue", queue });
    }

    /// <summary>Delete a broker queue via <c>docker exec sk-rabbitmq rabbitmqctl delete_queue {queue}</c>.</summary>
    private static async Task DeleteQueueAsync(string queue)
    {
        await RunRabbitCtlAsync(new[] { "delete_queue", queue });
    }

    private static async Task RunRabbitCtlAsync(string[] ctlArgs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add("sk-rabbitmq");
        psi.ArgumentList.Add("rabbitmqctl");
        psi.ArgumentList.Add("-q");
        foreach (var arg in ctlArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            return;   // best-effort teardown — a docker hiccup must not fail an otherwise-green E2E.
        }
        _ = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
    }

    private const string HostRedis = "localhost:6380,abortConnect=false,connectTimeout=5000";

    /// <summary>
    /// Points the in-process WebApi at the REAL host stack (RMQ localhost:5673, Redis localhost:6380,
    /// Postgres localhost:5433, otel localhost:4317) and drains net-zero teardown in
    /// <see cref="DisposeAsync"/>. CLONED from <see cref="SampleRoundTripE2ETests"/>'s
    /// <c>RealStackWebAppFactory</c> — the env-var-in-ctor host overrides + L2KeysToCleanup /
    /// ParentIndexMembersToSrem discipline are identical, EXTENDED with broker-queue teardown
    /// (<see cref="BrokerQueuesToPurge"/> / <see cref="BrokerQueuesToDelete"/>) so the bounded data-gone
    /// DLQ message + the parked re-inject queue are cleaned to net-zero before the close gate's
    /// skp-dlq-1 depth==0 + rabbitmq name-SHA invariants.
    /// </summary>
    private sealed class RealStackWebAppFactory : Composition.Phase8WebAppFactory
    {
        private readonly Dictionary<string, string?> _prior = new();

        public RealStackWebAppFactory()
            : base(
                skipPostgresFixture: true,
                connectionStringOverride: HostPostgres,
                skipRedisFixture: true,
                redisConnectionStringOverride: HostRedis)
        {
            try
            {
                Set("RabbitMq__Host", "localhost");
                Set("RabbitMq__Port", "5673");
                Set("RabbitMq__Username", "guest");
                Set("RabbitMq__Password", "guest");

                Set("ConnectionStrings__Redis", HostRedis);
                Set("ConnectionStrings__Postgres", HostPostgres);

                Set("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317");
            }
            catch
            {
                Restore();
                throw;
            }
        }

        // IN-04: Redis connection string is the outer-class HostRedis (same file, private const is
        // visible to this nested class) — single source of truth, no shadowing HostRedisFull duplicate.
        private const string HostPostgres =
            "Host=localhost;Port=5433;Database=stepsdb;Username=postgres;Password=postgres;Maximum Pool Size=20;Timeout=15";

        private void Set(string key, string value)
        {
            _prior[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }

        private void Restore()
        {
            foreach (var kv in _prior)
            {
                Environment.SetEnvironmentVariable(kv.Key, kv.Value);
            }
        }

        /// <summary>
        /// L2 keys (production "skp:" prefix) the test registers for deletion on teardown — EVERY minted
        /// key, INCLUDING the composite backup whose 2-day TTL cannot be waited out (D-07). Drained in
        /// <see cref="DisposeAsync"/> so the close-gate <c>redis-cli --scan</c> net-zero invariant holds.
        /// </summary>
        public List<RedisKey> L2KeysToCleanup { get; } = new();

        /// <summary>Shared <c>skp:</c> parent-index members this test SADDed to SREM on teardown.</summary>
        public List<RedisValue> ParentIndexMembersToSrem { get; } = new();

        /// <summary>Broker queues to PURGE on teardown (the bounded data-gone skp-dlq-1 message) so the
        /// close gate's depth==0 holds.</summary>
        public List<string> BrokerQueuesToPurge { get; } = new();

        /// <summary>Broker queues to DELETE on teardown (the per-procId re-inject queue the test created by
        /// re-injecting to a fresh queue:{procId:D}) so the close gate's rabbitmq name-SHA holds.</summary>
        public List<string> BrokerQueuesToDelete { get; } = new();

        public override async ValueTask DisposeAsync()
        {
            if (L2KeysToCleanup.Count > 0 || ParentIndexMembersToSrem.Count > 0)
            {
                await using var cleanupMux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
                var db = cleanupMux.GetDatabase();
                if (L2KeysToCleanup.Count > 0)
                {
                    await db.KeyDeleteAsync(L2KeysToCleanup.ToArray());
                }
                if (ParentIndexMembersToSrem.Count > 0)
                {
                    await db.SetRemoveAsync(L2ProjectionKeys.ParentIndex(), ParentIndexMembersToSrem.ToArray());
                }
            }

            foreach (var q in BrokerQueuesToPurge)
            {
                await PurgeQueueAsync(q);
            }
            foreach (var q in BrokerQueuesToDelete)
            {
                await DeleteQueueAsync(q);
            }

            Restore();
            await base.DisposeAsync();
        }
    }
}
