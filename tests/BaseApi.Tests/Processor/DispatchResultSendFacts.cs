using BaseApi.Tests.Orchestrator;
using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Messaging.Contracts.Hashing;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;
using ExecutionResult = Messaging.Contracts.ExecutionResult;

namespace BaseApi.Tests.Processor;

/// <summary>
/// EXEC-07/08 send-discipline facts driven through the real in-memory harness Send pipeline. Plan 31-03
/// (req-3): the consumer collapses N result blobs into ONE content-addressed manifest result, so it sends
/// exactly ONE <see cref="ExecutionResult"/> to <c>queue:orchestrator-result</c> on the Completed path —
/// INCLUDING the empty-result case, which sends a terminal <c>"[]"</c> manifest result (so the
/// orchestrator observes-and-terminates and acks, Pitfall 4). A token-tripped cancellation sends one
/// <see cref="StepOutcome.Cancelled"/>; any other transform exception sends one
/// <see cref="StepOutcome.Failed"/> carrying the message.
/// </summary>
public sealed class DispatchResultSendFacts
{
    private static IConnectionMultiplexer PresentInput(string entryId, out IDatabase db) =>
        OrchestratorTestStubs.PresentL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out db);

    [Fact]
    public async Task EmptyResult_Sends_One_Terminal_Manifest_Result()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid().ToString("D");
        var redis = PresentInput(entryId, out _);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results()); // empty list
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };

        await using var provider = DispatchTestKit.BuildResultHarness();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var consumer = DispatchTestKit.Build(redis, context, processor, harness.Bus);
            await consumer.Consume(OrchestratorTestStubs.Context(
                DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()), ct));

            // Plan 31-03 (req-3, Pitfall 4): an empty result still SENDS one terminal "[]" manifest result
            // (EntryId = hash("[]"), non-empty H) so the orchestrator observes-and-terminates and acks.
            Assert.True(await harness.Consumed.Any<ExecutionResult>(ct));
            var sent = Assert.Single(harness.Consumed.Select<ExecutionResult>(ct));
            Assert.Equal(StepOutcome.Completed, sent.Context.Message.Outcome);
            var emptyManifest = MessageIdentity.HashManifest(
                System.Text.Json.JsonSerializer.Serialize(Array.Empty<string>()));
            Assert.Equal(emptyManifest, sent.Context.Message.EntryId);
            Assert.False(string.IsNullOrEmpty(sent.Context.Message.H));
        }
        finally { await harness.Stop(ct); }
    }

    [Fact]
    public async Task Multiple_Results_Collapse_Into_One_Manifest_Send()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid().ToString("D");
        var redis = PresentInput(entryId, out _);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results("a", "b", "c"));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };

        await using var provider = DispatchTestKit.BuildResultHarness();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var consumer = DispatchTestKit.Build(redis, context, processor, harness.Bus);
            await consumer.Consume(OrchestratorTestStubs.Context(
                DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()), ct));

            // Plan 31-03 (req-3): three blobs collapse into ONE manifest result whose EntryId = hash of the
            // JSON array of the three blob hashes.
            Assert.True(await harness.Consumed.Any<ExecutionResult>(ct));
            var consumed = harness.Consumed.Select<ExecutionResult>(ct).ToList();
            var sent = Assert.Single(consumed);                                   // ONE manifest message
            Assert.Equal(StepOutcome.Completed, sent.Context.Message.Outcome);
            var manifest = MessageIdentity.HashManifest(System.Text.Json.JsonSerializer.Serialize(
                new[] { MessageIdentity.HashBlob("a"), MessageIdentity.HashBlob("b"), MessageIdentity.HashBlob("c") }));
            Assert.Equal(manifest, sent.Context.Message.EntryId);
        }
        finally { await harness.Stop(ct); }
    }

    [Fact]
    public async Task Cancelled_Always_Sent()
    {
        var entryId = Guid.NewGuid().ToString("D");
        var redis = PresentInput(entryId, out _);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var processor = new DispatchTestKit.FakeProcessor(new OperationCanceledException(cts.Token));
        var context = new FakeProcessorContext { InputDefinition = null };

        await using var provider = DispatchTestKit.BuildResultHarness();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var consumer = DispatchTestKit.Build(redis, context, processor, harness.Bus);
            await consumer.Consume(OrchestratorTestStubs.Context(
                DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()), cts.Token));

            Assert.True(await harness.Consumed.Any<ExecutionResult>(TestContext.Current.CancellationToken));
            var sent = Assert.Single(harness.Consumed.Select<ExecutionResult>(TestContext.Current.CancellationToken));
            Assert.Equal(StepOutcome.Cancelled, sent.Context.Message.Outcome);
        }
        finally { await harness.Stop(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task CaughtException_Sends_Failed_With_Message()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid().ToString("D");
        var redis = PresentInput(entryId, out _);
        var processor = new DispatchTestKit.FakeProcessor(new InvalidOperationException("boom"));
        var context = new FakeProcessorContext { InputDefinition = null };

        await using var provider = DispatchTestKit.BuildResultHarness();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var consumer = DispatchTestKit.Build(redis, context, processor, harness.Bus);
            await consumer.Consume(OrchestratorTestStubs.Context(
                DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()), ct));

            Assert.True(await harness.Consumed.Any<ExecutionResult>(ct));
            var sent = Assert.Single(harness.Consumed.Select<ExecutionResult>(ct));
            Assert.Equal(StepOutcome.Failed, sent.Context.Message.Outcome);
            Assert.Contains("boom", sent.Context.Message.ErrorMessage);
        }
        finally { await harness.Stop(ct); }
    }
}
