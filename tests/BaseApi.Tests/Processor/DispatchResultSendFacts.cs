using BaseApi.Tests.Orchestrator;
using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;
using ExecutionResult = Messaging.Contracts.ExecutionResult;

namespace BaseApi.Tests.Processor;

/// <summary>
/// EXEC-07/08 send-discipline facts driven through the real in-memory harness Send pipeline: results
/// are sent one-by-one (individual messages) to <c>queue:orchestrator-result</c>; an empty result list
/// sends nothing and acks; a token-tripped cancellation sends one <see cref="StepOutcome.Cancelled"/>;
/// any other transform exception sends one <see cref="StepOutcome.Failed"/> carrying the message.
/// </summary>
public sealed class DispatchResultSendFacts
{
    private static IConnectionMultiplexer PresentInput(Guid entryId, out IDatabase db) =>
        OrchestratorTestStubs.PresentL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out db);

    [Fact]
    public async Task EmptyList_AcksWithNoMessage()
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

            Assert.False(await harness.Sent.Any<ExecutionResult>(ct));            // ack-only, no message
        }
        finally { await harness.Stop(ct); }
    }

    [Fact]
    public async Task Sends_One_By_One()
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

            Assert.True(await harness.Consumed.Any<ExecutionResult>(ct));
            var consumed = harness.Consumed.Select<ExecutionResult>(ct).ToList();
            Assert.Equal(3, consumed.Count);                                      // three individual messages
            Assert.All(consumed, c => Assert.Equal(StepOutcome.Completed, c.Context.Message.Outcome));
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

            Assert.True(await harness.Consumed.Any<ExecutionResult>(ct));
            var sent = Assert.Single(harness.Consumed.Select<ExecutionResult>(ct));
            Assert.Equal(StepOutcome.Failed, sent.Context.Message.Outcome);
            Assert.Contains("boom", sent.Context.Message.ErrorMessage);
        }
        finally { await harness.Stop(ct); }
    }
}
