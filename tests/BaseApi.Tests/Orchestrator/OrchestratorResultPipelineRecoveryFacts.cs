using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestrator.Recovery;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// The GATE + RECOVERY paths of <see cref="OrchestratorResultPipeline"/> (Phase 71 — proves ORCV-01/05
/// hermetically):
/// <list type="bullet">
///   <item><description>ORCV-01: the gate branches on <c>exist L2[messageId]</c> (absent → FORWARD,
///   present → RECOVERY); a gate-op exhaust → exactly one OrchestratorReinject and NO cleanup DEL.</description></item>
///   <item><description>ORCV-05: RECOVERY parses each slot's JSON tuple and classifies on its newEntryId
///   (Pitfall 2) — data exists → re-send a StepCompleted carrying newEntryId + retire; clean not-exist → drop
///   (no retire); an L2 fault → leave the slot. The tail OrchestratorReinjects when any slot faulted, else runs
///   the two-key DEL. A retired (guid.empty) slot is skipped.</description></item>
/// </list>
/// </summary>
public sealed class OrchestratorResultPipelineRecoveryFacts
{
    private static OrchestratorResultPipeline Build(
        IConnectionMultiplexer redis, OrchestratorPipelineTestKit.CapturingSendProvider send) =>
        new(redis, send, OrchestratorPipelineTestKit.Advancement(),
            OrchestratorPipelineTestKit.Retry(3), OrchestratorPipelineTestKit.Recovery(300),
            OrchestratorPipelineTestKit.Metrics(), NullLogger<OrchestratorResultPipeline>.Instance);

    private static OrchestratorResultPipeline Build(
        IConnectionMultiplexer redis, ISendEndpointProvider send) =>
        new(redis, send, OrchestratorPipelineTestKit.Advancement(),
            OrchestratorPipelineTestKit.Retry(3), OrchestratorPipelineTestKit.Recovery(300),
            OrchestratorPipelineTestKit.Metrics(), NullLogger<OrchestratorResultPipeline>.Instance);

    private static StepProjection AnyStep() => OrchestratorPipelineTestKit.CompletedStep(Array.Empty<Guid>());

    [Fact]
    public async Task GateExhaust_OneReinject_NoCleanup()   // ORCV-01
    {
        var ct = TestContext.Current.CancellationToken;
        var messageId = Guid.NewGuid();
        var redis = OrchestratorPipelineTestKit.GateFaultL2(out var db);
        var send = new OrchestratorPipelineTestKit.CapturingSendProvider();
        var m = OrchestratorPipelineTestKit.Completed(Guid.NewGuid());

        await Build(redis, send).RunAsync(m, messageId, StepOutcome.Completed, AnyStep(),
            OrchestratorPipelineTestKit.Steps(), ct);

        Assert.Single(send.SentKeeper.OfType<OrchestratorReinject>());
        // No cleanup: neither DEL overload, no HGETALL, no atomic write.
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task HGetAllExhaust_OneReinject_NoCleanup()   // ORCV-05 (HGETALL fault)
    {
        var ct = TestContext.Current.CancellationToken;
        var messageId = Guid.NewGuid();
        var redis = OrchestratorPipelineTestKit.RecoveryHGetAllFaultL2(out var db);
        var send = new OrchestratorPipelineTestKit.CapturingSendProvider();
        var m = OrchestratorPipelineTestKit.Completed(Guid.NewGuid());

        await Build(redis, send).RunAsync(m, messageId, StepOutcome.Completed, AnyStep(),
            OrchestratorPipelineTestKit.Steps(), ct);

        Assert.Single(send.SentKeeper.OfType<OrchestratorReinject>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task MixedSlots_OneResent_OneRetire_FaultLeavesSlot_Reinject_NoDelete()   // ORCV-05 with-fault
    {
        var ct = TestContext.Current.CancellationToken;
        var existsNew = Guid.NewGuid();   // slot 0: data exists → re-send + retire
        var absentNew = Guid.NewGuid();   // slot 1: clean not-exist → drop (no send, no retire)
        var faultNew  = Guid.NewGuid();   // slot 2: per-slot exist faults → L2 fault → leave slot, REINJECT in tail
        var messageId = Guid.NewGuid();
        var slots = OrchestratorPipelineTestKit.Slots(
            OrchestratorPipelineTestKit.SlotJson(existsNew),
            OrchestratorPipelineTestKit.SlotJson(absentNew),
            OrchestratorPipelineTestKit.SlotJson(faultNew));
        var redis = OrchestratorPipelineTestKit.RecoveryL2(
            messageId, slots,
            newEntryExists: new Dictionary<Guid, bool> { [existsNew] = true, [absentNew] = false },
            faultEntries: new[] { faultNew }, out var db);
        var send = new OrchestratorPipelineTestKit.CapturingSendProvider();
        var m = OrchestratorPipelineTestKit.Completed(Guid.NewGuid());

        await Build(redis, send).RunAsync(m, messageId, StepOutcome.Completed, AnyStep(),
            OrchestratorPipelineTestKit.Steps(), ct);

        // ORCV-05: exactly ONE re-sent StepCompleted carrying the exists slot's newEntryId.
        var resent = Assert.Single(send.Sent.OfType<StepCompleted>());
        Assert.Equal(existsNew, resent.EntryId);
        // exactly ONE retire HashSet(Guid.Empty) — only the completed slot is retired.
        await db.Received(1).HashSetAsync(
            L2ProjectionKeys.MessageIndex(messageId), Arg.Any<RedisValue>(),
            (RedisValue)Guid.Empty.ToString(), Arg.Any<When>(), Arg.Any<CommandFlags>());

        // fault slot → REINJECT and the source is NOT deleted (mutual exclusion).
        Assert.Single(send.SentKeeper.OfType<OrchestratorReinject>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task AllClear_DeletesSource_NoReinject()   // ORCV-05 no-fault
    {
        var ct = TestContext.Current.CancellationToken;
        var existsNew = Guid.NewGuid();
        var absentNew = Guid.NewGuid();
        var originEntryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var slots = OrchestratorPipelineTestKit.Slots(
            OrchestratorPipelineTestKit.SlotJson(existsNew),
            OrchestratorPipelineTestKit.SlotJson(absentNew));
        var redis = OrchestratorPipelineTestKit.RecoveryL2(
            messageId, slots,
            newEntryExists: new Dictionary<Guid, bool> { [existsNew] = true, [absentNew] = false },
            faultEntries: Array.Empty<Guid>(), out var db);
        var send = new OrchestratorPipelineTestKit.CapturingSendProvider();
        var m = OrchestratorPipelineTestKit.Completed(originEntryId);

        await Build(redis, send).RunAsync(m, messageId, StepOutcome.Completed, AnyStep(),
            OrchestratorPipelineTestKit.Steps(), ct);

        // no fault → ONE two-key DEL of [data:origin, index]; no REINJECT.
        await db.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey[]>(ks => ks.Length == 2
                && ks.Contains((RedisKey)L2ProjectionKeys.ExecutionData(originEntryId))
                && ks.Contains((RedisKey)L2ProjectionKeys.MessageIndex(messageId))),
            Arg.Any<CommandFlags>());
        Assert.Empty(send.SentKeeper.OfType<OrchestratorReinject>());
    }

    [Fact]
    public async Task RetiredSlot_Skipped()   // ORCV-05 (guid.empty slot is inert)
    {
        var ct = TestContext.Current.CancellationToken;
        var existsNew = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var slots = OrchestratorPipelineTestKit.Slots(
            OrchestratorPipelineTestKit.RetiredSlot,                      // slot 0: retired → skipped
            OrchestratorPipelineTestKit.SlotJson(existsNew));            // slot 1: exists → re-send
        var redis = OrchestratorPipelineTestKit.RecoveryL2(
            messageId, slots,
            newEntryExists: new Dictionary<Guid, bool> { [existsNew] = true },
            faultEntries: Array.Empty<Guid>(), out _);
        var send = new OrchestratorPipelineTestKit.CapturingSendProvider();
        var m = OrchestratorPipelineTestKit.Completed(Guid.NewGuid());

        await Build(redis, send).RunAsync(m, messageId, StepOutcome.Completed, AnyStep(),
            OrchestratorPipelineTestKit.Steps(), ct);

        // The retired slot contributes nothing; exactly one re-send from the live slot.
        var resent = Assert.Single(send.Sent.OfType<StepCompleted>());
        Assert.Equal(existsNew, resent.EntryId);
    }

    [Fact]
    public async Task SendBeforeRetire_SendFail_LeavesSlot()   // ORCV-05 send-before-retire
    {
        var ct = TestContext.Current.CancellationToken;
        var existsNew = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var slots = OrchestratorPipelineTestKit.Slots(OrchestratorPipelineTestKit.SlotJson(existsNew));
        var redis = OrchestratorPipelineTestKit.RecoveryL2(
            messageId, slots,
            newEntryExists: new Dictionary<Guid, bool> { [existsNew] = true },
            faultEntries: Array.Empty<Guid>(), out var db);
        var send = new OrchestratorPipelineTestKit.ResultSendFailProvider();   // the re-send THROWS
        var m = OrchestratorPipelineTestKit.Completed(Guid.NewGuid());

        // SendResult exhausts and PROPAGATES — the slot must NOT have been retired (send-before-retire).
        await Assert.ThrowsAsync<RedisConnectionException>(() =>
            Build(redis, send).RunAsync(m, messageId, StepOutcome.Completed, AnyStep(),
                OrchestratorPipelineTestKit.Steps(), ct));

        await db.DidNotReceive().HashSetAsync(
            L2ProjectionKeys.MessageIndex(messageId), Arg.Any<RedisValue>(),
            (RedisValue)Guid.Empty.ToString(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }
}
