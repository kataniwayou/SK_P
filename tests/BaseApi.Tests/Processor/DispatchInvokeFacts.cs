using BaseApi.Tests.Orchestrator;
using Messaging.Contracts.Projections;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// EXEC-04/06 invoke facts: the consumer invokes the <c>ProcessAsync</c> seam with the L2 input value
/// and the dispatch <c>Payload</c> as config, and mints a DISTINCT per-result <c>ExecutionId</c> for
/// each returned result. Output definition is null here (validation skipped) so each result is Completed.
/// </summary>
public sealed class DispatchInvokeFacts
{
    [Fact]
    public async Task Invokes_With_InputData_And_ConfigPayload()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid().ToString("D");
        const string inputJson = "{\"v\":42}";
        var redis = OrchestratorTestStubs.PresentL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = inputJson }, out _);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results("out"));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();
        var consumer = DispatchTestKit.Build(redis, context, processor, send);
        var dispatch = DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid(), payload: "{\"cfg\":7}");

        await consumer.Consume(OrchestratorTestStubs.Context(dispatch, ct));

        Assert.Equal(inputJson, processor.LastInputData);
        Assert.Equal("{\"cfg\":7}", processor.LastConfig);
    }

    [Fact]
    public async Task Mints_Distinct_ExecutionIds_Per_Result()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid().ToString("D");
        var redis = OrchestratorTestStubs.PresentL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out _);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results("a", "b"));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();
        var consumer = DispatchTestKit.Build(redis, context, processor, send);

        await consumer.Consume(OrchestratorTestStubs.Context(
            DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()), ct));

        Assert.Equal(2, send.Sent.Count);
        Assert.All(send.Sent, r => Assert.NotEqual(Guid.Empty, r.ExecutionId));
        Assert.NotEqual(send.Sent[0].ExecutionId, send.Sent[1].ExecutionId);     // distinct per-result mint
    }
}
