using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Processing;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;
using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;

namespace BaseApi.Tests.Processor;

/// <summary>
/// The A18 FORWARD pass of <see cref="ProcessorPipeline"/> (Phase 51 — proves FWD-01/02/03, SLOT-01/02,
/// INFRA-01/02 hermetically):
/// <list type="bullet">
///   <item><description>FWD-01: existence-check exhaust → <see cref="KeeperReinject"/> AND the source is NEVER deleted (input intact).</description></item>
///   <item><description>SLOT-01/02: a completed item writes the allocation index (<c>HashSetAsync(MessageIndex)</c>) BEFORE the data key (<c>StringSetAsync(ExecutionData)</c>).</description></item>
///   <item><description>INFRA-01: an allocation-write exhaust DROPS the item (no keeper send, no result, no data write).</description></item>
///   <item><description>INFRA-02: a data-write exhaust → <see cref="KeeperInject"/> carrying Data/DeleteEntryId/EntryId.</description></item>
///   <item><description>FWD-02: mixed completed + business-failed items each land on the right channel.</description></item>
///   <item><description>FWD-03: the happy-path tail deletes the source <c>L2[entryId]</c>; a tail exhaust → <see cref="KeeperDelete"/>.</description></item>
/// </list>
/// </summary>
public sealed class PipelineForwardFacts
{
    private const string Input = "{}";

    private static FakeProcessorContext Ctx() =>
        new() { InputDefinition = null, OutputDefinition = null };

    private static ProcessorPipeline Build(
        IConnectionMultiplexer redis, IProcessorContext context, BaseProcessorBase processor,
        DispatchTestKit.CapturingSendProvider send) =>
        new(redis, context, processor, send, DispatchTestKit.Retry(3), DispatchTestKit.Options(300),
            DispatchTestKit.Metrics(), NullLogger<ProcessorPipeline>.Instance);

    [Fact]
    public async Task ExistCheckFault_Reinject_NoSourceDelete()   // FWD-01
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        // existence-check (KeyExistsAsync) faults → exhaust → REINJECT; the source must never be deleted.
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci => ((RedisKey)ci[0]).ToString() == L2ProjectionKeys.ExecutionData(entryId) ? (RedisValue)Input : RedisValue.Null);
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns<Task<bool>>(_ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stub: exist-check unreachable"));
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(mux, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), messageId, ct);

        Assert.Single(send.SentKeeper.OfType<KeeperReinject>());                       // exactly one REINJECT
        Assert.False(processor.Invoked);                                               // never reached the In stage
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>());                  // input intact — never deleted
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Completed_AllocationBeforeData()   // SLOT-01/02
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ForwardOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out var db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), messageId, ct);

        // Allocation index (HashSet on L2[messageId]) MUST be written BEFORE the data key (StringSet on
        // L2[entryId]) — the entryId is framework-minted/unknown to the fact, so the ordering is inspected
        // overload-agnostically by method name over the recorded call sequence (the data-write StringSetAsync
        // binds to whichever expiry overload the compiler picks; matching by name avoids an overload mismatch).
        var calls = db.ReceivedCalls().ToList();
        var hashIdx = calls.FindIndex(c =>
            c.GetMethodInfo().Name == nameof(IDatabase.HashSetAsync)
            && ((RedisKey)c.GetArguments()[0]!).ToString() == L2ProjectionKeys.MessageIndex(messageId));
        var setIdx = calls.FindIndex(c =>
            c.GetMethodInfo().Name == nameof(IDatabase.StringSetAsync)
            && ((RedisKey)c.GetArguments()[0]!).ToString().StartsWith($"{L2ProjectionKeys.Prefix}data:", StringComparison.Ordinal));
        Assert.True(hashIdx >= 0, "allocation-index HashSetAsync(MessageIndex) was never written");
        Assert.True(setIdx >= 0, "data-key StringSetAsync(ExecutionData) was never written");
        Assert.True(hashIdx < setIdx, "SLOT-01/02: the allocation index must be written BEFORE the data key");

        // The whole-HASH EXPIRE (KeyExpireAsync on L2[messageId], the unified executionDataTtl) follows the
        // slot HashSet — proven overload-agnostically by name index ordering (the 2-arg call binds to the
        // ExpireWhen overload).
        var expireIdx = calls.FindIndex(c =>
            c.GetMethodInfo().Name == nameof(IDatabase.KeyExpireAsync)
            && ((RedisKey)c.GetArguments()[0]!).ToString() == L2ProjectionKeys.MessageIndex(messageId));
        Assert.True(expireIdx > hashIdx, "the whole-HASH unified TTL EXPIRE must follow the slot allocation write");

        // Received.InOrder over the two MessageIndex HASH ops (allocation HSET → whole-HASH expire) — both on
        // the known key. (StringSetAsync is asserted via the index ordering above because its 3-arg expiry
        // call binds to an overload whose exact signature is brittle to match in an InOrder spec.)
        Received.InOrder(() =>
        {
            db.HashSetAsync(L2ProjectionKeys.MessageIndex(messageId), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
        });

        Assert.Single(send.Sent.OfType<StepCompleted>());                              // completed → orchestrator
        Assert.Empty(send.SentKeeper);                                                 // no keeper send on happy path
    }

    [Fact]
    public async Task IndexTtl_IsRandom_BetweenDataTtl_And_2x_AndOutlivesData()   // Phase-68 TEST-06 desync guard
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ForwardOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out var db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));
        var send = new DispatchTestKit.CapturingSendProvider();

        // Build() configures Options(300) → data TTL = 300s, index TTL = random[300,600]s (SlotTtl()).
        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), messageId, ct);

        var dataTtl = TimeSpan.FromSeconds(300);
        var calls = db.ReceivedCalls().ToList();

        // KeyExpireAsync binds the TimeSpan? overload → TtlOf reads it directly (for the index RANGE check).
        // StringSetAsync binds the Expiration overload → TtlEquals compares via the implicit TimeSpan→Expiration
        // conversion (for the data EQUALITY check). Mirrors PipelinePostFacts.PostCompleted_WritesWithTtl.
        static TimeSpan? TtlOf(NSubstitute.Core.ICall call)
        {
            var p = call.GetMethodInfo().GetParameters();
            var a = call.GetArguments();
            var i = Array.FindIndex(p, x => x.ParameterType == typeof(TimeSpan?));
            return i >= 0 ? (TimeSpan?)a[i] : null;
        }
        static bool TtlEquals(NSubstitute.Core.ICall call, TimeSpan want)
        {
            var p = call.GetMethodInfo().GetParameters();
            var a = call.GetArguments();
            var i = Array.FindIndex(p, x => x.ParameterType == typeof(TimeSpan?));
            if (i >= 0) return (TimeSpan?)a[i] == want;
            var e = Array.FindIndex(p, x => x.ParameterType.Name == "Expiration");
            if (e >= 0) return Equals((Expiration)a[e]!, (Expiration)want);
            return false;
        }

        // (1) The index EXPIRE: KeyExpireAsync on L2[messageId] — applies SlotTtl() = random[ExecutionDataTtl, 2×].
        var expireCall = calls.Single(c =>
            c.GetMethodInfo().Name == nameof(IDatabase.KeyExpireAsync)
            && ((RedisKey)c.GetArguments()[0]!).ToString() == L2ProjectionKeys.MessageIndex(messageId));

        // (2) The data SET: StringSetAsync on L2[entryId] (data: prefix) — applies the bounded ExecutionDataTtl.
        var setCall = calls.Single(c =>
            c.GetMethodInfo().Name == nameof(IDatabase.StringSetAsync)
            && ((RedisKey)c.GetArguments()[0]!).ToString().StartsWith($"{L2ProjectionKeys.Prefix}data:", StringComparison.Ordinal));

        var indexTtl = TtlOf(expireCall);
        Assert.NotNull(indexTtl);   // KeyExpireAsync binds the TimeSpan? overload

        // Data key TTL == the configured ExecutionDataTtl (300s).
        Assert.True(TtlEquals(setCall, dataTtl), "data SET TTL must equal the configured ExecutionDataTtl (300s)");
        // Index TTL ∈ [ExecutionDataTtl, 2×ExecutionDataTtl] = [300,600]s — DERIVED from the same ExecutionDataTtl
        // (no separate knob → cannot desync; the Phase-68 TEST-06 root cause), with a floor == data TTL and a 2×
        // ceiling so the index STRICTLY OUTLIVES the data it points at (recovery headroom). Regression guard for the
        // Phase-68 TEST-06 index/data TTL relationship.
        Assert.InRange(indexTtl!.Value, dataTtl, TimeSpan.FromSeconds(600));
        Assert.True(indexTtl!.Value >= dataTtl, "index TTL must be >= data TTL (effect-once ordering + recovery headroom)");
    }

    [Fact]
    public async Task SlotWriteFault_Drop()   // INFRA-01
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ForwardSlotFaultL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out var db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));   // single completed item
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), messageId, ct);

        // infra_messageId → DROP: no keeper send, no result, and the data key was never written for the item.
        Assert.Empty(send.SentKeeper);
        Assert.Empty(send.Sent.OfType<StepCompleted>());
        await db.DidNotReceive().StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
            Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task DataWriteFault_Inject_WithIdSet()   // INFRA-02
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ForwardDataFaultL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out _);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));
        var send = new DispatchTestKit.CapturingSendProvider();
        var d = DispatchTestKit.Dispatch(entryId, Guid.NewGuid());

        await Build(redis, Ctx(), processor, send).RunAsync(d, messageId, ct);

        var inj = Assert.Single(send.SentKeeper.OfType<KeeperInject>());               // data-exhaust → INJECT
        Assert.NotEqual("", inj.Data);                                                 // raw-JSON output in-hand
        Assert.Equal(d.EntryId, inj.DeleteEntryId);                                    // source entryId to delete
        Assert.NotEqual(Guid.Empty, inj.EntryId);                                      // the allocation written
        Assert.Empty(send.Sent.OfType<StepCompleted>());                              // no StepCompleted for the item
    }

    [Fact]
    public async Task MixedItems_EachOnRightChannel()   // FWD-02
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ForwardOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out _);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items(
            new ProcessItem(ProcessOutcome.Completed, "ok", Guid.NewGuid()),
            new ProcessItem(ProcessOutcome.Failed, "bad", Guid.NewGuid())));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), messageId, ct);

        Assert.Single(send.Sent.OfType<StepCompleted>());                              // completed → orchestrator
        Assert.Single(send.Sent.OfType<StepFailed>());                                 // business-failed → orchestrator
        Assert.Empty(send.SentKeeper);                                                 // neither is infra
    }

    [Fact]
    public async Task HappyTail_DeletesSource()   // FWD-03 happy
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ForwardOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out var db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));
        var send = new DispatchTestKit.CapturingSendProvider();
        var d = DispatchTestKit.Dispatch(entryId, Guid.NewGuid());   // non-Guid.Empty source → tail armed

        await Build(redis, Ctx(), processor, send).RunAsync(d, messageId, ct);

        // A19/GC-01: ONE atomic two-key DEL containing BOTH the source data key and the index, zero scalar deletes.
        await db.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey[]>(ks => ks.Length == 2
                && ks.Contains((RedisKey)L2ProjectionKeys.ExecutionData(d.EntryId))
                && ks.Contains((RedisKey)L2ProjectionKeys.MessageIndex(messageId))),
            Arg.Any<CommandFlags>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>());
        Assert.Empty(send.SentKeeper.OfType<KeeperDelete>());                          // delete succeeded → no DELETE
    }

    [Fact]
    public async Task TailExhaust_Delete()   // FWD-03 exhaust
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ForwardDeleteFaultL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out _);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), messageId, ct);

        Assert.Single(send.SentKeeper.OfType<KeeperDelete>());                         // tail exhaust → one KeeperDelete
    }
}
