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
/// FWD-03 — the inline source-delete TAIL of the forward pass in <see cref="ProcessorPipeline"/> (Phase 51:
/// the WR-01 <c>finally</c> is RETIRED). On the no-REINJECT happy path (happy, business-fail, In-exception)
/// the forward tail deletes <c>L2[entryId]</c> of the inbound dispatch with bounded retry; exhaust →
/// <see cref="KeeperDelete"/>. It is SKIPPED on the Pre-read-exhaust REINJECT path (input left intact) and
/// on a Guid.Empty source step.
/// </summary>
public sealed class PipelineEndDeleteFacts
{
    private const string Input = "{}";

    private static ProcessorPipeline Build(
        IConnectionMultiplexer redis, IProcessorContext context, BaseProcessorBase processor,
        DispatchTestKit.CapturingSendProvider send) =>
        new(redis, context, processor, send, DispatchTestKit.Retry(3), DispatchTestKit.Options(300),
            DispatchTestKit.Metrics(), NullLogger<ProcessorPipeline>.Instance);

    [Fact]
    public async Task EndDelete_RunsOnHappyPath()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.PresentReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out var db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, context, processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), messageId, ct);

        // A19/GC-01: ONE atomic two-key DEL whose operands contain BOTH the source data key and the index …
        await db.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey[]>(ks => ks.Length == 2
                && ks.Contains((RedisKey)L2ProjectionKeys.ExecutionData(entryId))
                && ks.Contains((RedisKey)L2ProjectionKeys.MessageIndex(messageId))),
            Arg.Any<CommandFlags>());
        // … AND zero scalar deletes (the GC-01 atomicity heart — never two scalar DELs).
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>());
        Assert.Empty(send.SentKeeper.OfType<KeeperDelete>());
    }

    [Fact]
    public async Task EndDelete_RunsOnBusinessFail()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var redis = DispatchTestKit.PresentReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out var db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));
        // input "{}" fails a definition requiring "x" → business fail before the In stage; end-delete still runs.
        var context = new FakeProcessorContext
        {
            InputDefinition = "{\"type\":\"object\",\"required\":[\"x\"]}",
            OutputDefinition = null,
        };
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, context, processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), Guid.NewGuid(), ct);

        await db.Received(1).KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task EndDelete_RunsOnInException()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var redis = DispatchTestKit.PresentReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out var db);
        var processor = new DispatchTestKit.FakeProcessor(new FailedException("x"));   // status exception
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, context, processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), Guid.NewGuid(), ct);

        await db.Received(1).KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task EndDelete_Skipped_OnReinject()
    {
        var ct = TestContext.Current.CancellationToken;
        var redis = DispatchTestKit.ReadFaultL2(out var db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));
        var context = new FakeProcessorContext { InputDefinition = "{}", OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, context, processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId: Guid.NewGuid(), correlationId: Guid.NewGuid()), Guid.NewGuid(), ct);

        Assert.Single(send.SentKeeper.OfType<ProcessorReinject>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>());                  // NEVER deleted on REINJECT
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());  // GC-02: index survives
    }

    [Fact]
    public async Task EndDelete_Skipped_OnSourceStep()
    {
        var ct = TestContext.Current.CancellationToken;
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.PresentReadWriteDeleteOkL2(new Dictionary<string, string>(), out var db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();

        // D-06: the source step (Guid.Empty entryId) now DELETES the index too — ExecutionData(Guid.Empty) is a
        // harmless drop-on-absent operand; the test completing without throwing proves the absent-operand no-op.
        await Build(redis, context, processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId: Guid.Empty, correlationId: Guid.NewGuid()), messageId, ct);

        // A19/GC-01/AC-3: ONE atomic two-key DEL containing the index + the Guid.Empty source data operand …
        await db.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey[]>(ks => ks.Length == 2
                && ks.Contains((RedisKey)L2ProjectionKeys.ExecutionData(Guid.Empty))
                && ks.Contains((RedisKey)L2ProjectionKeys.MessageIndex(messageId))),
            Arg.Any<CommandFlags>());
        // … and zero scalar deletes (atomicity heart).
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>());
    }

    [Fact]
    public async Task EndDelete_Exhaust_Delete()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadOkDeleteFaultL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out var db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, context, processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), messageId, ct);

        // AC-5: the array DEL exhausts → best-effort PERSIST the index (cancel its random TTL) …
        await db.Received(1).KeyPersistAsync((RedisKey)L2ProjectionKeys.MessageIndex(messageId), Arg.Any<CommandFlags>());
        // AC-6: … then exactly one escalated KeeperDelete carrying MessageId == messageId.
        var kd = Assert.Single(send.SentKeeper.OfType<KeeperDelete>());
        Assert.Equal(messageId, kd.MessageId);
    }

    [Fact]
    public async Task EndDelete_PersistExhaust_StillSendsKeeper()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        // D-03 fall-through: BOTH the array DEL and the best-effort persist THROW — the keeper must STILL be sent.
        var redis = DispatchTestKit.ReadOkDeleteAndPersistFaultL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out _);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, context, processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), messageId, ct);

        Assert.Single(send.SentKeeper.OfType<KeeperDelete>());   // persist-exhaust must not swallow the escalation
    }
}
