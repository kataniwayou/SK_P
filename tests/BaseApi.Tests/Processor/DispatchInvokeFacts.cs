using BaseApi.Tests.Orchestrator;
using Messaging.Contracts;
using Messaging.Contracts.Hashing;
using Messaging.Contracts.Projections;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// EXEC-04/06 invoke facts: the consumer invokes the <c>ProcessAsync</c> seam with the L2 input value
/// and the dispatch <c>Payload</c> as config. Plan 31-03 collapses N result blobs into ONE
/// content-addressed manifest result (req-3), so the per-result mint is no longer observable on the wire;
/// instead the single result carries the manifest EntryId = hash(JSON array of blob hashes).
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
    public async Task Collapses_Multiple_Results_Into_One_Manifest_Result()
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

        // Plan 31-03 (req-3): two blobs collapse into ONE manifest result whose EntryId = hash of the
        // JSON array of the two blob hashes (content-addressed, deterministic), with a non-empty H.
        var sent = Assert.Single(send.Sent);
        Assert.Equal(StepOutcome.Completed, sent.Outcome);
        Assert.NotEqual(Guid.Empty, sent.ExecutionId);

        var manifestJson = System.Text.Json.JsonSerializer.Serialize(
            new[] { MessageIdentity.HashBlob("a"), MessageIdentity.HashBlob("b") });
        Assert.Equal(MessageIdentity.HashManifest(manifestJson), sent.EntryId);
        Assert.False(string.IsNullOrEmpty(sent.H));                              // outbound dedup identity carried
    }
}
