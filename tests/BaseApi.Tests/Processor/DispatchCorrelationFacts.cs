using BaseApi.Tests.Orchestrator;
using MassTransit.Testing;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// EXEC-10 correlation fact: the dispatch BODY <c>CorrelationId</c> flows onto each emitted
/// <see cref="StepCompleted"/> (mirrors <c>StepDispatcher</c> threading the correlation id through).
/// Phase 43 (D-03): straight-through — N result blobs send N StepCompleted records, each carrying the
/// dispatch's correlation id.
/// </summary>
public sealed class DispatchCorrelationFacts
{
    [Fact]
    public async Task Body_CorrelationId_Flows_To_Each_Result()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
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

            Assert.True(await harness.Consumed.Any<StepCompleted>(ct));
            var sent = harness.Consumed.Select<StepCompleted>(ct).Select(c => c.Context.Message).ToList();
            Assert.Equal(2, sent.Count);                                    // two results -> two StepCompleted
            Assert.All(sent, m => Assert.Equal(correlationId, m.CorrelationId));
        }
        finally { await harness.Stop(ct); }
    }
}
