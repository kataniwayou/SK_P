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
/// PIPE-08 — the end-delete <c>finally</c> of <see cref="ProcessorPipeline"/>: over EVERY read-succeeded
/// path (happy, business-fail, In-exception) it deletes <c>L2[entryId]</c> of the inbound dispatch with
/// bounded retry; exhaust → <see cref="KeeperDelete"/>. It is SKIPPED on the REINJECT path and on a
/// Guid.Empty source step (both leave readSucceeded false).
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
        var redis = DispatchTestKit.PresentReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out var db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, context, processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), ct);

        await db.Received(1).KeyDeleteAsync(L2ProjectionKeys.ExecutionData(entryId));  // deletes the INBOUND key
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
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), ct);

        await db.Received(1).KeyDeleteAsync(L2ProjectionKeys.ExecutionData(entryId));
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
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), ct);

        await db.Received(1).KeyDeleteAsync(L2ProjectionKeys.ExecutionData(entryId));
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
            DispatchTestKit.Dispatch(entryId: Guid.NewGuid(), correlationId: Guid.NewGuid()), ct);

        Assert.Single(send.SentKeeper.OfType<KeeperReinject>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>());                  // NEVER deleted on REINJECT
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task EndDelete_Skipped_OnSourceStep()
    {
        var ct = TestContext.Current.CancellationToken;
        var redis = DispatchTestKit.PresentReadWriteDeleteOkL2(new Dictionary<string, string>(), out var db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, context, processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId: Guid.Empty, correlationId: Guid.NewGuid()), ct);

        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>());                  // NEVER deleted on Guid.Empty
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task EndDelete_Exhaust_Delete()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var redis = DispatchTestKit.ReadOkDeleteFaultL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out _);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, context, processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), ct);

        Assert.Single(send.SentKeeper.OfType<KeeperDelete>());   // delete-exhaust → exactly one KeeperDelete
    }
}
