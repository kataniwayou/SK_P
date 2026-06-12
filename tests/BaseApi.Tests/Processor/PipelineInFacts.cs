using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Processing;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;

namespace BaseApi.Tests.Processor;

/// <summary>
/// PIPE-05 — the In stage of <see cref="ProcessorPipeline"/>: the author seam runs inside a try/catch; a
/// thrown <see cref="ProcessStatusException"/> maps by runtime type to exactly ONE matching Step* record
/// and aborts the batch (no Post, no Keeper item sends); an unexpected exception ⇒ <see cref="StepFailed"/>.
/// </summary>
public sealed class PipelineInFacts
{
    private const string Input = "{}";

    private static (ProcessorPipeline pipeline, DispatchTestKit.CapturingSendProvider send, Guid entryId)
        Build(BaseProcessorBase processor)
    {
        var entryId = Guid.NewGuid();
        var redis = DispatchTestKit.PresentReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out _);
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();
        var pipeline = new ProcessorPipeline(redis, context, processor, send, DispatchTestKit.Retry(3), DispatchTestKit.Options(300),
            DispatchTestKit.SlotOptions(), DispatchTestKit.Metrics(), NullLogger<ProcessorPipeline>.Instance);
        return (pipeline, send, entryId);
    }

    /// <summary>A trivial author config for the real-subclass deser-failure fact.</summary>
    private sealed record DeserConfig(string? Value) : ProcessorConfig;

    /// <summary>A REAL <see cref="BaseProcessor{DeserConfig}"/> (not a fake) so the framework actually
    /// runs <c>JsonSerializer.Deserialize</c> — a malformed payload throws inside ExecuteAsync before the
    /// transform body is ever reached.</summary>
    private sealed class RealDeserProcessor : BaseProcessor<DeserConfig>
    {
        protected override Task<List<ProcessItem>> ProcessAsync(
            string validatedData, DeserConfig? config, CancellationToken ct)
            => Task.FromResult(new List<ProcessItem>());   // never reached on a malformed payload
    }

    [Fact]
    public async Task MalformedPayload_DeserFailure_Emits_Single_StepFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (pipeline, send, entryId) = Build(new RealDeserProcessor());

        // "not json" → JsonException inside BaseProcessor<DeserConfig>.ExecuteAsync
        //            → ProcessorPipeline.cs:241 catch-all → exactly one StepFailed (D-03, Req 4a).
        await pipeline.RunAsync(DispatchTestKit.Dispatch(entryId, Guid.NewGuid(), "not json"), Guid.NewGuid(), ct);

        var sent = Assert.Single(send.Sent);               // exactly ONE result (no default, no crash)
        Assert.IsType<StepFailed>(sent);
        Assert.Empty(send.SentKeeper);                     // business StepFailed — NOT routed to Keeper (D-03)
    }

    [Fact]
    public async Task StatusException_Failed_AbortsBatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var processor = new DispatchTestKit.FakeProcessor(new FailedException("x"));
        var (pipeline, send, entryId) = Build(processor);

        await pipeline.RunAsync(DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), Guid.NewGuid(), ct);

        var sent = Assert.Single(send.Sent);
        var failed = Assert.IsType<StepFailed>(sent);
        Assert.Equal("x", failed.ErrorMessage);
        Assert.Empty(send.SentKeeper);                     // no Keeper item sends — batch aborted before Post
    }

    [Fact]
    public async Task StatusException_Cancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        var processor = new DispatchTestKit.FakeProcessor(new CancelledException("c"));
        var (pipeline, send, entryId) = Build(processor);

        await pipeline.RunAsync(DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), Guid.NewGuid(), ct);

        var sent = Assert.Single(send.Sent);
        var cancelled = Assert.IsType<StepCancelled>(sent);
        Assert.Equal("c", cancelled.CancellationMessage);
        Assert.Empty(send.SentKeeper);
    }

    [Fact]
    public async Task StatusException_Processing()
    {
        var ct = TestContext.Current.CancellationToken;
        var processor = new DispatchTestKit.FakeProcessor(new ProcessingException("p"));
        var (pipeline, send, entryId) = Build(processor);

        await pipeline.RunAsync(DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), Guid.NewGuid(), ct);

        var sent = Assert.Single(send.Sent);
        Assert.IsType<StepProcessing>(sent);               // message is logged only (no wire field, D-05)
        Assert.Empty(send.SentKeeper);
    }

    [Fact]
    public async Task UnexpectedException_Failed()
    {
        var ct = TestContext.Current.CancellationToken;
        var processor = new DispatchTestKit.FakeProcessor(new InvalidOperationException("boom"));
        var (pipeline, send, entryId) = Build(processor);

        await pipeline.RunAsync(DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), Guid.NewGuid(), ct);

        var sent = Assert.Single(send.Sent);
        var failed = Assert.IsType<StepFailed>(sent);      // unexpected ⇒ StepFailed
        Assert.Equal("boom", failed.ErrorMessage);
        Assert.Empty(send.SentKeeper);
    }
}
