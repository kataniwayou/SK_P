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
/// PIPE-06/07 — the Post stage of <see cref="ProcessorPipeline"/>: per completed item, UPDATE → write
/// <c>L2[entryId]</c> with NO TTL → CLEANUP on write-success → <see cref="StepCompleted"/> carrying the
/// framework entryId + author executionId. N completed items → N results. A write-exhaust becomes
/// failed(infra) → <see cref="KeeperInject"/> (no StepCompleted, no CLEANUP). A per-item business-failed
/// (author Failed OR output-validation fail) → one <see cref="StepFailed"/> and does NOT abort the batch (A3).
/// </summary>
public sealed class PipelinePostFacts
{
    private const string Input = "{}";

    private static FakeProcessorContext Ctx() =>
        new() { InputDefinition = null, OutputDefinition = null };

    private static ProcessorPipeline Build(
        IConnectionMultiplexer redis, IProcessorContext context, BaseProcessorBase processor,
        DispatchTestKit.CapturingSendProvider send) =>
        new(redis, context, processor, send, DispatchTestKit.Retry(3),
            NullLogger<ProcessorPipeline>.Instance);

    [Fact]
    public async Task MultiItem_NCompleted_NResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var redis = DispatchTestKit.PresentReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out _);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("a", "b", "c"));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), ct);

        Assert.Equal(3, send.Sent.OfType<StepCompleted>().Count());      // 3 items → 3 StepCompleted (N→N)
    }

    [Fact]
    public async Task PostCompleted_UpdateCleanup()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var redis = DispatchTestKit.PresentReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out var db);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), ct);

        var update = Assert.Single(send.SentKeeper.OfType<KeeperUpdate>());
        var cleanup = Assert.Single(send.SentKeeper.OfType<KeeperCleanup>());
        // UPDATE precedes CLEANUP (UPDATE → write → CLEANUP order).
        Assert.True(send.SentKeeper.IndexOf(update) < send.SentKeeper.IndexOf(cleanup));
        Assert.Single(send.Sent.OfType<StepCompleted>());

        // The Post write dropped the TTL: every StringSetAsync call carries a NULL TimeSpan? expiry argument
        // (no-TTL — design §16/64). Inspect the received calls overload-agnostically (the 2-arg call site
        // binds the TimeSpan? expiry parameter to its default null).
        var setCalls = db.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IDatabase.StringSetAsync))
            .ToList();
        Assert.NotEmpty(setCalls);
        Assert.All(setCalls, c =>
        {
            var parameters = c.GetMethodInfo().GetParameters();
            var args = c.GetArguments();

            // No-TTL means: any TTL-bearing argument (a TimeSpan? expiry OR an Expiration) is null/None.
            // The 2-arg call site defaults whichever expiry parameter the bound overload exposes.
            var tsIdx = Array.FindIndex(parameters, p => p.ParameterType == typeof(TimeSpan?));
            if (tsIdx >= 0)
                Assert.Null(args[tsIdx]);   // TimeSpan? expiry defaulted to null

            var expIdx = Array.FindIndex(parameters, p => p.ParameterType.Name == "Expiration");
            if (expIdx >= 0)
                // Expiration default is the "no expiry" None value — its ToString() is "(none)" / default.
                Assert.Equal(default, (Expiration)args[expIdx]!);

            Assert.True(tsIdx >= 0 || expIdx >= 0,
                "StringSetAsync overload exposes no recognizable expiry parameter to assert no-TTL on");
        });
    }

    [Fact]
    public async Task WriteFault_Inject()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var redis = DispatchTestKit.PresentReadWriteFaultL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out _);
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items("out"));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), ct);

        var update = Assert.Single(send.SentKeeper.OfType<KeeperUpdate>());
        var inject = Assert.Single(send.SentKeeper.OfType<KeeperInject>());   // write-exhaust → KeeperInject
        Assert.True(send.SentKeeper.IndexOf(update) < send.SentKeeper.IndexOf(inject));  // UPDATE then INJECT
        Assert.Empty(send.SentKeeper.OfType<KeeperCleanup>());               // NO cleanup on write-fault
        Assert.Empty(send.Sent.OfType<StepCompleted>());                     // NO StepCompleted for that item
    }

    [Fact]
    public async Task CompletedCarriesIds()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var redis = DispatchTestKit.PresentReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out _);
        var authorExec = Guid.NewGuid();
        var processor = new DispatchTestKit.FakeProcessor(
            DispatchTestKit.Items(new ProcessItem(ProcessOutcome.Completed, "out", authorExec)));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), ct);

        var completed = Assert.Single(send.Sent.OfType<StepCompleted>());
        Assert.NotEqual(Guid.Empty, completed.EntryId);          // framework-minted real data key
        Assert.Equal(authorExec, completed.ExecutionId);         // author-minted item.ExecutionId provenance
    }

    [Fact]
    public async Task BusinessFailedItem_OneStepFailed_NoAbort()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryId = Guid.NewGuid();
        var redis = DispatchTestKit.PresentReadWriteDeleteOkL2(
            new Dictionary<string, string> { [L2ProjectionKeys.ExecutionData(entryId)] = Input }, out _);
        // [Completed, Failed] — the per-item business-failed emits one StepFailed and does NOT abort.
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Items(
            new ProcessItem(ProcessOutcome.Completed, "ok", Guid.NewGuid()),
            new ProcessItem(ProcessOutcome.Failed, "bad", Guid.NewGuid())));
        var send = new DispatchTestKit.CapturingSendProvider();

        await Build(redis, Ctx(), processor, send).RunAsync(
            DispatchTestKit.Dispatch(entryId, Guid.NewGuid()), ct);

        Assert.Single(send.Sent.OfType<StepCompleted>());        // the completed item still completed
        Assert.Single(send.Sent.OfType<StepFailed>());           // one StepFailed; batch NOT aborted (A3)
    }
}
