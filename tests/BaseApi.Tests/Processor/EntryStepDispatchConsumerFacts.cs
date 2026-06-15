using BaseProcessor.Core.Processing;
using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// The thin <see cref="EntryStepDispatchConsumer"/> shell (Phase 51, D-09/D-10): it reads the broker
/// <c>ctx.MessageId</c> (the slot-array branch key) and fail-fasts on null. A null MessageId is a contract
/// violation (MassTransit always sets it on Send/Publish) and must throw rather than synthesize a
/// <c>Guid.Empty</c> key that would collide recovery across messages (T-51-11).
/// </summary>
public sealed class EntryStepDispatchConsumerFacts
{
    [Fact]
    public async Task NullMessageId_Throws_InvalidOperationException()   // D-10 / T-51-11
    {
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };

        // A real-ish pipeline so the ctor is satisfied; it is NEVER reached — the null-MessageId guard fires
        // BEFORE RunAsync (the metric line, which reads context.Id, runs first and needs a non-null Id, which
        // FakeProcessorContext supplies by default).
        var redis = DispatchTestKit.ForwardOkL2(new Dictionary<string, string>(), out _);
        var processor = new DispatchTestKit.FakeProcessor(new List<ProcessItem>());
        var send = new DispatchTestKit.CapturingSendProvider();
        var pipeline = new ProcessorPipeline(
            redis, context, processor, send, DispatchTestKit.Retry(3), DispatchTestKit.Options(300),
            DispatchTestKit.Metrics(), NullLogger<ProcessorPipeline>.Instance);

        var consumer = new EntryStepDispatchConsumer(pipeline, context, DispatchTestKit.Metrics());

        var ctx = Substitute.For<ConsumeContext<EntryStepDispatch>>();
        ctx.MessageId.Returns((Guid?)null);
        ctx.Message.Returns(DispatchTestKit.Dispatch(Guid.NewGuid(), Guid.NewGuid()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => consumer.Consume(ctx));
    }
}
