using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Processing;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;
using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;

namespace BaseApi.Tests.Processor;

/// <summary>
/// PIPE-06/07 — the Post stage of <see cref="ProcessorPipeline"/>: per completed item, write
/// <c>L2[entryId]</c> with the bounded <c>ExecutionDataTtl</c> (CONFIG-02/D-17) → <see cref="StepCompleted"/>
/// carrying the framework entryId + author executionId. N completed items → N results. A write-exhaust
/// becomes failed(infra) → <see cref="KeeperInject"/> (no StepCompleted). A per-item business-failed
/// (author Failed OR output-validation fail) → one <see cref="StepFailed"/> and does NOT abort the batch (A3).
/// Phase-50 (D-01) removed the Model-B UPDATE/CLEANUP keeper sends + their composite backup key; the real
/// A18 slot-array forward/recovery pass is proven in Phase 51.
/// </summary>
public sealed class PipelinePostFacts
{
    private const string Input = "{}";

    private static FakeProcessorContext Ctx() =>
        new() { InputDefinition = null, OutputDefinition = null };

    private static ProcessorPipeline Build(
        IConnectionMultiplexer redis, IProcessorContext context, BaseProcessorBase processor,
        DispatchTestKit.CapturingSendProvider send) =>
        new(redis, context, processor, send, DispatchTestKit.Retry(3), DispatchTestKit.Options(300),
            DispatchTestKit.Metrics(), NullLogger<ProcessorPipeline>.Instance);

    [Fact]
    public async Task MultiItem_NCompleted_NResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.PresentReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out _);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("a", "b", "c"));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), messageId, ct);

        Assert.Equal(3, send.Sent.OfType<StepCompleted>().Count());      // 3 items → 3 StepCompleted (N→N)
    }

    [Fact]
    public async Task PostCompleted_WritesWithTtl_AndSendsStepCompleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.PresentReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out var db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), messageId, ct);

        // Phase-50 (D-01): the Model-B UPDATE/CLEANUP keeper sends are retired — a completed item now goes
        // straight write → StepCompleted with NO keeper send on the happy path.
        Assert.Empty(send.SentKeeper);
        Assert.Single(send.Sent.OfType<StepCompleted>());

        // Phase-69 (ATOMIC-01): the Post write is now ONE atomic ScriptEvaluateAsync; the bounded
        // ExecutionDataTtl (CONFIG-02/D-17) rides as the data-TTL ARGV (ARGV[5] / 0-based [4] = ms) so a
        // terminal step's output key self-expires (the close-gate redis net-zero invariant depends on this).
        // Build() configures Options(300) → data TTL must be 300_000ms.
        var argv = (RedisValue[])db.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IDatabase.ScriptEvaluateAsync)).GetArguments()[2]!;
        Assert.Equal(300_000L, (long)argv[4]);   // ARGV[5] data TTL ms == configured ExecutionDataTtl (300s)
    }

    [Fact]
    public async Task WriteFault_Inject()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        // Phase-69 (NODROP-01): the forward-Post write is one atomic ScriptEvaluateAsync — an atomic-write
        // exhaust is the sole keeper send (KeeperInject, the infra route), with NO StepCompleted for the item.
        var redis = DispatchTestKit.AtomicWriteFaultL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out _);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), messageId, ct);

        Assert.Single(send.SentKeeper.OfType<KeeperInject>());               // atomic-write exhaust → KeeperInject
        Assert.Single(send.SentKeeper);                                       // KeeperInject is the only keeper send
        Assert.Empty(send.Sent.OfType<StepCompleted>());                     // NO StepCompleted for that item
    }

    [Fact]
    public async Task CompletedCarriesIds()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.PresentReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out _);
        var authorExec = Guid.NewGuid();
        var processor = new DispatchTestKit.FakeProcessor(
            DispatchTestKit.Items(new ProcessItem(ProcessOutcome.Completed, "out", authorExec)));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), messageId, ct);

        var completed = Assert.Single(send.Sent.OfType<StepCompleted>());
        Assert.NotEqual(Guid.Empty, completed.EntryId);          // framework-minted real data key
        Assert.Equal(authorExec, completed.ExecutionId);         // author-minted item.ExecutionId provenance
    }

    [Fact]
    public async Task BusinessFailedItem_OneStepFailed_NoAbort()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.PresentReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out _);
        // [Completed, Failed] — the per-item business-failed emits one StepFailed and does NOT abort.
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items(
            new ProcessItem(ProcessOutcome.Completed, "ok", Guid.NewGuid()),
            new ProcessItem(ProcessOutcome.Failed, "bad", Guid.NewGuid())));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), messageId, ct);

        Assert.Single(send.Sent.OfType<StepCompleted>());        // the completed item still completed
        Assert.Single(send.Sent.OfType<StepFailed>());           // one StepFailed; batch NOT aborted (A3)
    }
}
