using BaseApi.Tests.Orchestrator;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// EXEC-04/06 invoke facts: the consumer invokes the <c>ProcessAsync</c> seam with the L2 input value
/// and the dispatch <c>Payload</c> as config. Phase 43 (D-03): straight-through — each result mints a
/// fresh <see cref="Guid"/> entryId, writes <c>L2[data(entryId)]</c>, and sends ONE
/// <see cref="StepCompleted"/>; N results send N StepCompleted records (no manifest collapse).
/// </summary>
public sealed class DispatchInvokeFacts
{
    [Fact]
    public async Task Invokes_With_InputData_And_ConfigPayload()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
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
    public async Task Sends_One_StepCompleted_Per_Result()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var redis = OrchestratorTestStubs.PresentL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out _);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results("a", "b"));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();
        var consumer = DispatchTestKit.Build(redis, context, processor, send);

        await consumer.Consume(OrchestratorTestStubs.Context(
            DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()), ct));

        // D-03 (straight-through): two blobs send TWO StepCompleted records, each carrying a fresh real Guid
        // EntryId (the L2 data key, D-06a) + a minted lineage ExecutionId. No manifest collapse.
        Assert.Equal(2, send.Sent.Count);
        foreach (var s in send.Sent)
        {
            var completed = Assert.IsType<StepCompleted>(s);
            Assert.NotEqual(Guid.Empty, completed.ExecutionId);
            Assert.NotEqual(Guid.Empty, completed.EntryId);   // real minted data key, not the empty sentinel
        }
        // Each result's minted EntryId is distinct (content keys are not collapsed).
        Assert.Equal(2, send.Sent.Cast<StepCompleted>().Select(c => c.EntryId).Distinct().Count());
    }
}
