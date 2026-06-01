using BaseApi.Tests.Orchestrator;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// EXEC-09 / D-15 business-ack vs infra-throw split for
/// <see cref="BaseProcessor.Core.Processing.EntryStepDispatchConsumer"/> (mirrors the orchestrator's
/// <c>AckSemanticsTests</c>):
/// <list type="bullet">
///   <item>an L2 OUTPUT-WRITE fault PROPAGATES (never lose output) — the dispatch retries;</item>
///   <item>an L2 INPUT-READ fault PROPAGATES;</item>
///   <item>a business failure (missing required input) does NOT throw — it sends a Failed and acks.</item>
/// </list>
/// </summary>
public sealed class DispatchAckSemanticsFacts
{
    [Fact]
    public async Task OutputWriteFault_Propagates()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        // Input read succeeds; the StringSetAsync (output write) throws RedisConnectionException.
        var redis = DispatchTestKit.PresentReadWriteFaultL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out _);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results("out"));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();
        var consumer = DispatchTestKit.Build(redis, context, processor, send);

        // Infra: NOT business-acked — propagates so the bounded Immediate(3) retry can route to _error.
        await Assert.ThrowsAsync<RedisConnectionException>(() => consumer.Consume(
            OrchestratorTestStubs.Context(DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()), ct)));
    }

    [Fact]
    public async Task InputReadFault_Propagates()
    {
        var ct = TestContext.Current.CancellationToken;
        var redis = OrchestratorTestStubs.InfraFaultL2(out _);                    // StringGetAsync throws
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results("out"));
        var context = new FakeProcessorContext { InputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();
        var consumer = DispatchTestKit.Build(redis, context, processor, send);

        await Assert.ThrowsAsync<RedisConnectionException>(() => consumer.Consume(
            OrchestratorTestStubs.Context(
                DispatchTestKit.Dispatch(entryId: Guid.NewGuid(), correlationId: Guid.NewGuid()), ct)));
    }

    [Fact]
    public async Task BusinessFailure_DoesNotThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        var redis = OrchestratorTestStubs.AbsentL2(out _);                        // required input missing
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results("out"));
        var context = new FakeProcessorContext { InputDefinition = "{\"type\":\"object\"}" };
        var send = new DispatchTestKit.CapturingSendProvider();
        var consumer = DispatchTestKit.Build(redis, context, processor, send);

        // Business: does NOT throw — sends a Failed and returns (ack).
        await consumer.Consume(OrchestratorTestStubs.Context(
            DispatchTestKit.Dispatch(entryId: Guid.NewGuid(), correlationId: Guid.NewGuid()), ct));

        var sent = Assert.Single(send.Sent);
        Assert.Equal(StepOutcome.Failed, sent.Outcome);
        Assert.False(processor.Invoked);
    }
}
