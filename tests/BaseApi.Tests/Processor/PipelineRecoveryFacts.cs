using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Processing;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;
using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;

namespace BaseApi.Tests.Processor;

/// <summary>
/// The A18 RECOVERY pass of <see cref="ProcessorPipeline"/> (Phase 51 — proves RECOV-01/02/03, SLOT-03, D-03
/// hermetically). Reached only when <c>L2[messageId]</c> EXISTS (a redelivery):
/// <list type="bullet">
///   <item><description>RECOV-01: an HGETALL exhaust → <see cref="KeeperReinject"/> AND the source is NEVER deleted (input intact).</description></item>
///   <item><description>RECOV-02: a mixed HASH (exists / clean-absent / fault) → exactly one re-sent <see cref="StepCompleted"/> + exactly one retire HashSet(Guid.Empty); the absent entry sends/retires nothing; the fault entry leaves its slot intact (no retire).</description></item>
///   <item><description>SLOT-03 send-before-retire: a completed entry retires its slot to Guid.Empty AFTER the re-send; a send-fail leaves the slot un-retired (no retire HashSet).</description></item>
///   <item><description>D-03: the re-sent <see cref="StepCompleted"/> carries a FRESH exec (≠ Guid.Empty and ≠ the inbound dispatch exec).</description></item>
///   <item><description>RECOV-03 mutual exclusion: any infra_entryId → REINJECT and the source is NOT deleted; an all-clear HASH → the source IS deleted.</description></item>
/// </list>
/// </summary>
public sealed class PipelineRecoveryFacts
{
    private static FakeProcessorContext Ctx() =>
        new() { InputDefinition = null, OutputDefinition = null };

    private static ProcessorPipeline Build(
        IConnectionMultiplexer redis, IProcessorContext context, BaseProcessorBase processor,
        DispatchTestKit.CapturingSendProvider send) =>
        new(redis, context, processor, send, DispatchTestKit.Retry(3), DispatchTestKit.Options(300),
            DispatchTestKit.Metrics(), NullLogger<ProcessorPipeline>.Instance);

    private static ProcessorPipeline Build(
        IConnectionMultiplexer redis, IProcessorContext context, BaseProcessorBase processor,
        ISendEndpointProvider send) =>
        new(redis, context, processor, send, DispatchTestKit.Retry(3), DispatchTestKit.Options(300),
            DispatchTestKit.Metrics(), NullLogger<ProcessorPipeline>.Instance);

    // The In stage is never reached on the recovery branch (the dispatcher routes to RunRecoveryAsync before
    // the processor seam), so an empty-item processor double is fine for every recovery fact.
    private static DispatchTestKit.FakeProcessor NoopProcessor() =>
        new(new List<ProcessItem>());

    [Fact]
    public async Task HGetAllFault_Reinject_NoSourceDelete()   // RECOV-01
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.RecoveryHGetAllFaultL2(messageId, out var db);
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), NoopProcessor(), send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), messageId, ct);

        Assert.Single(send.SentKeeper.OfType<KeeperReinject>());                       // HGETALL exhaust → one REINJECT
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>());                  // input intact — never deleted
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());  // GC-02: index survives
    }

    [Fact]
    public async Task MixedSlots_OneResent_OneRetire_FaultLeavesSlot_NoSourceDelete()   // RECOV-02 + RECOV-03 with-infra
    {
        var ct = TestContext.Current.CancellationToken;
        var existsId = Guid.NewGuid();   // slot 0: data exists → completed → re-send + retire
        var absentId = Guid.NewGuid();   // slot 1: clean not-exist → drop (no send, no retire)
        var faultId  = Guid.NewGuid();   // slot 2: per-entry exist faults → infra_entryId → leave slot, REINJECT in tail
        var messageId = Guid.NewGuid();
        var slots = DispatchTestKit.Slots(existsId, absentId, faultId);
        var redis = DispatchTestKit.RecoveryL2(
            messageId, slots,
            entryExists: new Dictionary<Guid, bool> { [existsId] = true, [absentId] = false },
            faultEntries: new[] { faultId }, out var db);
        var send = new DispatchTestKit.CapturingSendProvider();
        var d = DispatchTestKit.Dispatch(Guid.NewGuid(), Guid.NewGuid());   // non-Guid.Empty source

        await Build(redis, Ctx(), NoopProcessor(), send).RunAsync(d, messageId, ct);

        // RECOV-02: exactly ONE re-sent StepCompleted (the exists entry); absent + fault produce none.
        Assert.Single(send.Sent.OfType<StepCompleted>());
        // exactly ONE retire HashSet(Guid.Empty) — only the completed entry's slot is retired.
        await db.Received(1).HashSetAsync(
            L2ProjectionKeys.MessageIndex(messageId), Arg.Any<RedisValue>(),
            (RedisValue)Guid.Empty.ToString(), Arg.Any<When>(), Arg.Any<CommandFlags>());

        // RECOV-03 with-infra: the fault entry → REINJECT and the source is NOT deleted (mutual exclusion).
        Assert.Single(send.SentKeeper.OfType<KeeperReinject>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());  // GC-02: index survives — no array DEL
    }

    [Fact]
    public async Task ResentCompleted_CarriesFreshExec()   // D-03
    {
        var ct = TestContext.Current.CancellationToken;
        var existsId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var slots = DispatchTestKit.Slots(existsId);
        var redis = DispatchTestKit.RecoveryL2(
            messageId, slots,
            entryExists: new Dictionary<Guid, bool> { [existsId] = true },
            faultEntries: Array.Empty<Guid>(), out _);
        var send = new DispatchTestKit.CapturingSendProvider();
        var d = DispatchTestKit.Dispatch(Guid.NewGuid(), Guid.NewGuid());

        await Build(redis, Ctx(), NoopProcessor(), send).RunAsync(d, messageId, ct);

        var sc = Assert.IsType<StepCompleted>(Assert.Single(send.Sent));
        Assert.NotEqual(Guid.Empty, sc.ExecutionId);          // a real minted exec
        Assert.NotEqual(d.ExecutionId, sc.ExecutionId);       // D-03/Pitfall 4: FRESH, not the inbound dispatch exec
    }

    [Fact]
    public async Task AllClear_DeletesSource()   // RECOV-03 no-infra
    {
        var ct = TestContext.Current.CancellationToken;
        var existsId = Guid.NewGuid();
        var absentId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var slots = DispatchTestKit.Slots(existsId, absentId);
        var redis = DispatchTestKit.RecoveryL2(
            messageId, slots,
            entryExists: new Dictionary<Guid, bool> { [existsId] = true, [absentId] = false },
            faultEntries: Array.Empty<Guid>(), out var db);
        var send = new DispatchTestKit.CapturingSendProvider();
        var d = DispatchTestKit.Dispatch(Guid.NewGuid(), Guid.NewGuid());   // non-Guid.Empty source → delete armed

        await Build(redis, Ctx(), NoopProcessor(), send).RunAsync(d, messageId, ct);

        // A19/GC-01/AC-2: the all-clear path issues ONE atomic two-key DEL containing BOTH the source data key
        // and the index — and zero scalar deletes (atomicity heart); no REINJECT.
        await db.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey[]>(ks => ks.Length == 2
                && ks.Contains((RedisKey)L2ProjectionKeys.ExecutionData(d.EntryId))
                && ks.Contains((RedisKey)L2ProjectionKeys.MessageIndex(messageId))),
            Arg.Any<CommandFlags>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>());
        Assert.Empty(send.SentKeeper.OfType<KeeperReinject>());
    }

    [Fact]
    public async Task SendBeforeRetire_SendFail_LeavesSlot()   // SLOT-03 (T-51-08)
    {
        var ct = TestContext.Current.CancellationToken;
        var existsId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var slots = DispatchTestKit.Slots(existsId);
        var redis = DispatchTestKit.RecoveryAllCompletedL2(messageId, slots, out var db);
        var send = new DispatchTestKit.ResultSendFailProvider();   // the completed re-send THROWS
        var d = DispatchTestKit.Dispatch(Guid.NewGuid(), Guid.NewGuid());

        // SendResult exhausts and PROPAGATES (D-10) — the slot must NOT have been retired (send-before-retire).
        await Assert.ThrowsAsync<RedisConnectionException>(() =>
            Build(redis, Ctx(), NoopProcessor(), send).RunAsync(d, messageId, ct));

        await db.DidNotReceive().HashSetAsync(
            L2ProjectionKeys.MessageIndex(messageId), Arg.Any<RedisValue>(),
            (RedisValue)Guid.Empty.ToString(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }
}
