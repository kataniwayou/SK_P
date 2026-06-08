using BaseApi.Tests.Orchestrator;
using MassTransit.Testing;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// EXEC-07/08 send-discipline facts driven through the real in-memory harness Send pipeline. Phase 43
/// (D-03): the consumer is straight-through — one result = one <see cref="StepCompleted"/> sent to
/// <c>queue:orchestrator-result</c>; an EMPTY result list sends NOTHING (the orchestrator simply observes
/// no continuation and the dispatch acks). A token-tripped cancellation sends one
/// <see cref="StepCancelled"/>; any other transform exception sends one <see cref="StepFailed"/> carrying
/// the message.
/// </summary>
public sealed class DispatchResultSendFacts
{
    private static IConnectionMultiplexer PresentInput(Guid entryId, out IDatabase db) =>
        OrchestratorTestStubs.PresentL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out db);

    [Fact]
    public async Task EmptyResult_Sends_Nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
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

            // D-03: an empty result list emits NO Step* record (the orchestrator observes no continuation,
            // the dispatch is acked). Nothing reaches queue:orchestrator-result.
            Assert.False(await harness.Consumed.Any<StepCompleted>(ct));
            Assert.False(await harness.Consumed.Any<StepFailed>(ct));
        }
        finally { await harness.Stop(ct); }
    }

    [Fact]
    public async Task Multiple_Results_Send_One_StepCompleted_Each()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
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

            // D-03 (straight-through): three blobs send THREE StepCompleted records (no manifest collapse).
            Assert.True(await harness.Consumed.Any<StepCompleted>(ct));
            var consumed = harness.Consumed.Select<StepCompleted>(ct).ToList();
            Assert.Equal(3, consumed.Count);
        }
        finally { await harness.Stop(ct); }
    }

    [Fact]
    public async Task Cancelled_Always_Sent()
    {
        var entryId = Guid.NewGuid();
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

            Assert.True(await harness.Consumed.Any<StepCancelled>(TestContext.Current.CancellationToken));
            Assert.Single(harness.Consumed.Select<StepCancelled>(TestContext.Current.CancellationToken));
        }
        finally { await harness.Stop(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task CaughtException_Sends_Failed_With_Message()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
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

            Assert.True(await harness.Consumed.Any<StepFailed>(ct));
            var sent = Assert.Single(harness.Consumed.Select<StepFailed>(ct));
            Assert.Contains("boom", sent.Context.Message.ErrorMessage);
        }
        finally { await harness.Stop(ct); }
    }
}
