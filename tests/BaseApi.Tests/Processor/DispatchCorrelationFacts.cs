using BaseApi.Tests.Orchestrator;
using MassTransit.Testing;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ExecutionResult = Messaging.Contracts.ExecutionResult;

namespace BaseApi.Tests.Processor;

/// <summary>
/// EXEC-10 correlation fact: the dispatch BODY <c>CorrelationId</c> flows onto the emitted
/// <see cref="ExecutionResult"/> (mirrors <c>StepDispatcher</c> threading the correlation id through).
/// Plan 31-03 (req-3): N result blobs collapse into ONE manifest result, so the correlation id flows onto
/// that single result.
/// </summary>
public sealed class DispatchCorrelationFacts
{
    [Fact]
    public async Task Body_CorrelationId_Flows_To_The_Manifest_Result()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid().ToString("D");
        var correlationId = Guid.NewGuid();
        var redis = OrchestratorTestStubs.PresentL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out _);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results("a", "b"));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };

        await using var provider = DispatchTestKit.BuildResultHarness();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var consumer = DispatchTestKit.Build(redis, context, processor, harness.Bus);
            await consumer.Consume(OrchestratorTestStubs.Context(
                DispatchTestKit.Dispatch(entryId, correlationId), ct));

            Assert.True(await harness.Consumed.Any<ExecutionResult>(ct));
            var sent = Assert.Single(harness.Consumed.Select<ExecutionResult>(ct));   // ONE manifest result
            Assert.Equal(correlationId, sent.Context.Message.CorrelationId);
        }
        finally { await harness.Stop(ct); }
    }
}
