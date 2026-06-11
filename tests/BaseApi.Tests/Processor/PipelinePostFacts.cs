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
            DispatchTestKit.SlotOptions(), DispatchTestKit.Metrics(), NullLogger<ProcessorPipeline>.Instance);

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

        // The Post write applies the bounded ExecutionDataTtl (CONFIG-02/D-17) so a terminal step's output
        // key self-expires (the close-gate redis net-zero invariant depends on this). Build() configures
        // Options(300), so every StringSetAsync call must carry a 300s expiry. Inspect the received calls
        // overload-agnostically (the expiry binds to either a TimeSpan? or an Expiration parameter).
        var expectedTtl = TimeSpan.FromSeconds(300);
        var setCalls = db.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IDatabase.StringSetAsync))
            .ToList();
        Assert.NotEmpty(setCalls);
        Assert.All(setCalls, c =>
        {
            var parameters = c.GetMethodInfo().GetParameters();
            var args = c.GetArguments();

            // TTL applied means: the TTL-bearing argument (a TimeSpan? expiry OR an Expiration) equals 300s.
            var tsIdx = Array.FindIndex(parameters, p => p.ParameterType == typeof(TimeSpan?));
            if (tsIdx >= 0)
                Assert.Equal(expectedTtl, (TimeSpan?)args[tsIdx]);   // TimeSpan? expiry = configured TTL

            var expIdx = Array.FindIndex(parameters, p => p.ParameterType.Name == "Expiration");
            if (expIdx >= 0)
                // The Expiration overload carries the same relative TTL (implicit TimeSpan→Expiration).
                Assert.Equal((Expiration)expectedTtl, (Expiration)args[expIdx]!);

            Assert.True(tsIdx >= 0 || expIdx >= 0,
                "StringSetAsync overload exposes no recognizable expiry parameter to assert the TTL on");
        });
    }

    [Fact]
    public async Task WriteFault_Inject()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = DispatchTestKit.PresentReadWriteFaultL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out _);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), messageId, ct);

        // Phase-50 (D-01): UPDATE/CLEANUP retired — a write-exhaust on a completed item is the sole keeper
        // send (KeeperInject, the infra route), with NO StepCompleted for that item.
        Assert.Single(send.SentKeeper.OfType<KeeperInject>());               // write-exhaust → KeeperInject
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
