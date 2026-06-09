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
/// PIPE-02/03 — the Pre stage of <see cref="ProcessorPipeline"/>: a Guid.Empty source step skips the L2
/// read (empty validatedData, no end-delete); a Redis-faulting OR absent/empty L2 read (A2) after
/// exhaustion routes to <see cref="KeeperReinject"/> and skips end-delete (the input is left intact for the
/// keeper, T-44-08); an input-schema validation failure is a business <see cref="StepFailed"/> AND
/// end-delete still runs.
/// </summary>
public sealed class PipelinePreFacts
{
    private static ProcessorPipeline Build(
        IConnectionMultiplexer redis, IProcessorContext context, BaseProcessorBase processor,
        DispatchTestKit.CapturingSendProvider send) =>
        new(redis, context, processor, send, DispatchTestKit.Retry(3),
            DispatchTestKit.Metrics(), NullLogger<ProcessorPipeline>.Instance);

    [Fact]
    public async Task SourceStep_Skip_EmptyData_NoEndDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        var redis = DispatchTestKit.PresentReadWriteDeleteOkL2(new Dictionary<string, string>(), out var db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, context, processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId: Guid.Empty, correlationId: Guid.NewGuid()), ct);

        Assert.True(processor.Invoked);                                   // ran with empty validatedData
        Assert.Equal(string.Empty, processor.LastInputData);
        Assert.Empty(send.SentKeeper.OfType<KeeperReinject>());          // no REINJECT
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>());    // no end-delete (readSucceeded false)
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task ReadFault_Reinject()
    {
        var ct = TestContext.Current.CancellationToken;
        var redis = DispatchTestKit.ReadFaultL2(out var db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));
        var context = new FakeProcessorContext { InputDefinition = "{}", OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, context, processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId: Guid.NewGuid(), correlationId: Guid.NewGuid()), ct);

        Assert.Single(send.SentKeeper.OfType<KeeperReinject>());          // exactly one REINJECT
        Assert.False(processor.Invoked);                                 // never reached the In stage
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>());    // no end-delete on REINJECT
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task AbsentKey_Reinject()
    {
        var ct = TestContext.Current.CancellationToken;
        var redis = DispatchTestKit.AbsentReadL2(out var db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));
        var context = new FakeProcessorContext { InputDefinition = "{}", OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, context, processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId: Guid.NewGuid(), correlationId: Guid.NewGuid()), ct);

        // A2: absent/empty key unifies with a Redis fault → REINJECT (was a business StepFailed pre-Phase-44).
        Assert.Single(send.SentKeeper.OfType<KeeperReinject>());
        Assert.False(processor.Invoked);
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task InputInvalid_Failed()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var redis = DispatchTestKit.PresentReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out var db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));
        // present input "{}" fails a definition requiring "x" → business StepFailed BEFORE the In stage.
        var context = new FakeProcessorContext
        {
            InputDefinition = "{\"type\":\"object\",\"required\":[\"x\"]}",
            OutputDefinition = null,
        };
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, context, processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()), ct);

        var sent = Assert.Single(send.Sent);
        Assert.IsType<StepFailed>(sent);
        Assert.False(processor.Invoked);                                 // failed before the seam ran
        await db.Received().KeyDeleteAsync(L2ProjectionKeys.ExecutionData(entryId));  // end-delete STILL runs
        Assert.Empty(send.SentKeeper.OfType<KeeperDelete>());            // delete succeeded → no KeeperDelete
    }
}
