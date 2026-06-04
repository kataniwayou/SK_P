using BaseApi.Tests.Orchestrator;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using NSubstitute;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// EXEC-02/03 input-resolution facts for <see cref="BaseProcessor.Core.Processing.EntryStepDispatchConsumer"/>:
/// input is read from <c>L2[data(entryId)]</c> and validated vs <c>InputDefinition</c> (the dispatch
/// <c>Payload</c> is config, never input). A required input that is missing/empty or fails its
/// definition yields a single <see cref="StepOutcome.Failed"/> BEFORE the transform runs; a no-input
/// source processor (empty definition + empty entryId) invokes with <c>inputData == ""</c> and no L2 read.
/// </summary>
public sealed class DispatchInputFacts
{
    private const string ObjectDef = "{\"type\":\"object\"}";
    private const string RequiresXDef = "{\"type\":\"object\",\"required\":[\"x\"]}";

    [Fact]
    public async Task MissingInput_NonEmptyDefinition_FailsBeforeProcessAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var redis = OrchestratorTestStubs.AbsentL2(out _);                       // L2 returns Null for the entryId
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results("out"));
        var context = new FakeProcessorContext { InputDefinition = ObjectDef };  // required input
        var send = new DispatchTestKit.CapturingSendProvider();
        var consumer = DispatchTestKit.Build(redis, context, processor, send);

        await consumer.Consume(OrchestratorTestStubs.Context(
            DispatchTestKit.Dispatch(entryId: Guid.NewGuid().ToString("D"), correlationId: Guid.NewGuid()), ct));

        var sent = Assert.Single(send.Sent);
        Assert.Equal(StepOutcome.Failed, sent.Outcome);
        Assert.False(processor.Invoked);                                          // ProcessAsync NEVER ran
    }

    [Fact]
    public async Task EmptyDefinition_EmptyEntryId_InvokesWithEmptyInput()
    {
        var ct = TestContext.Current.CancellationToken;
        var redis = OrchestratorTestStubs.AbsentL2(out var db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results("out"));
        var context = new FakeProcessorContext { InputDefinition = null };       // no-input source
        var send = new DispatchTestKit.CapturingSendProvider();
        var consumer = DispatchTestKit.Build(redis, context, processor, send);

        await consumer.Consume(OrchestratorTestStubs.Context(
            DispatchTestKit.Dispatch(entryId: "", correlationId: Guid.NewGuid()), ct));

        Assert.True(processor.Invoked);
        Assert.Equal(string.Empty, processor.LastInputData);                     // invoked with ""
        await db.DidNotReceive().StringGetAsync(Arg.Any<StackExchange.Redis.RedisKey>(), Arg.Any<StackExchange.Redis.CommandFlags>());
    }

    [Fact]
    public async Task PresentInput_PassingDefinition_InvokesWithL2Value()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid().ToString("D");
        const string inputJson = "{\"a\":1}";
        var redis = OrchestratorTestStubs.PresentL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = inputJson }, out _);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results("out"));
        var context = new FakeProcessorContext { InputDefinition = ObjectDef };
        var send = new DispatchTestKit.CapturingSendProvider();
        var consumer = DispatchTestKit.Build(redis, context, processor, send);
        var dispatch = DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid());

        await consumer.Consume(OrchestratorTestStubs.Context(dispatch, ct));

        Assert.True(processor.Invoked);
        Assert.Equal(inputJson, processor.LastInputData);                        // L2 value used as input
        Assert.Equal(dispatch.Payload, processor.LastConfig);                    // Payload used as config
    }

    [Fact]
    public async Task PresentInput_FailingDefinition_FailsBeforeProcessAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid().ToString("D");
        var redis = OrchestratorTestStubs.PresentL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{\"a\":1}" }, out _);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results("out"));
        var context = new FakeProcessorContext { InputDefinition = RequiresXDef }; // requires "x" — input lacks it
        var send = new DispatchTestKit.CapturingSendProvider();
        var consumer = DispatchTestKit.Build(redis, context, processor, send);

        await consumer.Consume(OrchestratorTestStubs.Context(
            DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()), ct));

        var sent = Assert.Single(send.Sent);
        Assert.Equal(StepOutcome.Failed, sent.Outcome);
        Assert.False(processor.Invoked);
    }
}
