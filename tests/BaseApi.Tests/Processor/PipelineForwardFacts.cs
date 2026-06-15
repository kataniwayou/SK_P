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
/// The A18 FORWARD pass of <see cref="ProcessorPipeline"/> (Phase 51/69 — proves FWD-01/02/03, ATOMIC-01,
/// NODROP-01 hermetically):
/// <list type="bullet">
///   <item><description>FWD-01: existence-check exhaust → <see cref="KeeperReinject"/> AND the source is NEVER deleted (input intact).</description></item>
///   <item><description>ATOMIC-01: a completed item issues ONE atomic <c>ScriptEvaluateAsync</c> whose body HSETs the index slot BEFORE SET-ing the data key; the index/data TTLs ride as script ARGV (Phase-68 TEST-06 desync guard).</description></item>
///   <item><description>NODROP-01 (Phase 69): an atomic-write exhaust → ONE <see cref="KeeperInject"/> carrying Data/DeleteEntryId/EntryId — no silent DROP path remains (spec §10 bullet 1).</description></item>
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

        // ATOMIC-01: the forward-Post write is now ONE atomic Lua ScriptEvaluateAsync. The HSET-before-SET
        // ordering is INTERNAL to the script (strictly stronger than two ordered C# calls). Inspect the single
        // script call's KEYS[] and its body: it must carry KEYS[0]=MessageIndex(messageId) + KEYS[1]=a data:
        // key, and HSET the index slot before SET-ing the data key.
        var call = db.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IDatabase.ScriptEvaluateAsync));
        var args = call.GetArguments();
        var script = (string)args[0]!;
        var keys = (RedisKey[])args[1]!;

        // NOTE: the Lua call-name tokens are single-quoted ('HSET', 'SET'); match the quoted token so the
        // 'HSET' call name is distinguished from the 'SET' call name (a bare "SET" would match inside "HSET").
        // The data-write SET is the LAST quoted SET in the script.
        Assert.True(script.IndexOf("'HSET'", StringComparison.Ordinal) < script.LastIndexOf("'SET'", StringComparison.Ordinal),
            "the atomic script must HSET the index slot before SET-ing the data key");
        Assert.Equal(L2ProjectionKeys.MessageIndex(messageId), keys[0].ToString());
        Assert.StartsWith($"{L2ProjectionKeys.Prefix}data:", keys[1].ToString(), StringComparison.Ordinal);

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

        // ATOMIC-01: the TTLs are now ARGV on the SINGLE atomic ScriptEvaluateAsync — index TTL ms = ARGV[4]
        // (0-based [3]), data TTL ms = ARGV[5] (0-based [4]). Both are computed in C# and passed as ms (no RNG
        // in Lua), so the Phase-68 TEST-06 index/data desync guard reduces to inspecting the script ARGV.
        var args = db.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IDatabase.ScriptEvaluateAsync)).GetArguments();
        var argv = (RedisValue[])args[2]!;
        var indexTtlMs = (long)argv[3];   // ARGV[4] (0-based [3]) — SlotTtl() random[ExecutionDataTtl, 2×]
        var dataTtlMs  = (long)argv[4];   // ARGV[5] (0-based [4]) — == ExecutionDataTtl

        // Data key TTL == the configured ExecutionDataTtl (300s = 300_000ms).
        Assert.Equal(300_000L, dataTtlMs);
        // Index TTL ∈ [ExecutionDataTtl, 2×ExecutionDataTtl] = [300_000, 600_000]ms — DERIVED from the same
        // ExecutionDataTtl (no separate knob → cannot desync; the Phase-68 TEST-06 root cause), with a floor ==
        // data TTL and a 2× ceiling so the index STRICTLY OUTLIVES the data it points at (recovery headroom).
        Assert.InRange(indexTtlMs, 300_000L, 600_000L);
        Assert.True(indexTtlMs >= dataTtlMs, "index TTL must be >= data TTL (effect-once ordering + recovery headroom)");
    }

    [Fact]
    public async Task AtomicWriteFault_Inject()   // NODROP-01 (replaces SlotWriteFault_Drop + DataWriteFault_Inject_WithIdSet)
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.AtomicWriteFaultL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out _);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));   // single completed item
        var send = new DispatchTestKit.CapturingSendProvider();
        var d = DispatchTestKit.Dispatch(entryId, Guid.NewGuid());

        await Build(redis, Ctx(), processor, send).RunAsync(d, messageId, ct);

        // spec §10: the atomic-write exhaust (former index- AND data-failure modes, now one write) → ONE INJECT
        // (was a silent DROP). NO drop path remains; the dropped item never completes.
        var inj = Assert.Single(send.SentKeeper.OfType<KeeperInject>());
        Assert.NotEqual("", inj.Data);                                                 // raw-JSON output in-hand
        Assert.Equal(d.EntryId, inj.DeleteEntryId);                                    // source entryId to delete
        Assert.NotEqual(Guid.Empty, inj.EntryId);                                      // the allocation minted
        Assert.Empty(send.Sent.OfType<StepCompleted>());                              // dropped item never completes
    }

    [Fact]
    public async Task EscalatedItem_SkipsCleanup()   // GATE-01 (spec §4.3 final ¶)
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.AtomicWriteFaultL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out var db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));   // one completed item → its write faults
        var send = new DispatchTestKit.CapturingSendProvider();
        var d = DispatchTestKit.Dispatch(entryId, Guid.NewGuid());   // non-Guid.Empty source

        await Build(redis, Ctx(), processor, send).RunAsync(d, messageId, ct);

        Assert.Single(send.SentKeeper.OfType<KeeperInject>());                  // the item escalated
        // GATE-01: an item escalated → the forward cleanup tail (atomic two-key DEL) MUST NOT run.
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
        Assert.Empty(send.SentKeeper.OfType<KeeperDelete>());                   // no tail → no DELETE escalation either
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
