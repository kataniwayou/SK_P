using BaseApi.Tests.Composition;
using BaseApi.Tests.Orchestrator;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// EXEC-05 output-write facts against the real localhost:6380 Redis (<see cref="RedisFixture"/>): a
/// result that passes its <c>OutputDefinition</c> mints a new entryId and writes its OutputData to
/// <c>L2[data(newEntryId)]</c> with the configured execution-data TTL (CONFIG-02); a result that fails
/// the definition is <see cref="StepOutcome.Failed"/> with <c>EntryId == Guid.Empty</c> and writes
/// NOTHING. Every minted key is <c>_redis.Track</c>'d for net-zero teardown (D-23).
/// <para>Uses a no-input source processor (empty definition + empty entryId) so the consumer skips the
/// L2 READ and exercises only the WRITE path on real Redis.</para>
/// </summary>
public sealed class DispatchOutputWriteFacts : IClassFixture<RedisFixture>
{
    private const string RequiresXDef = "{\"type\":\"object\",\"required\":[\"x\"]}";
    private readonly RedisFixture _redis;

    public DispatchOutputWriteFacts(RedisFixture redis) => _redis = redis;

    [Fact]
    public async Task Pass_Writes_L2_With_Ttl_And_Completed()
    {
        var ct = TestContext.Current.CancellationToken;
        const int ttl = 120;
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results("out"));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();
        var consumer = DispatchTestKit.Build(
            _redis.Multiplexer, context, processor, send, executionDataTtlSeconds: ttl);

        await consumer.Consume(OrchestratorTestStubs.Context(
            DispatchTestKit.Dispatch(entryId: Guid.Empty, correlationId: Guid.NewGuid()), ct));

        var sent = Assert.Single(send.Sent);
        Assert.Equal(StepOutcome.Completed, sent.Outcome);
        Assert.NotEqual(Guid.Empty, sent.EntryId);
        var key = L2ProjectionKeys.ExecutionData(sent.EntryId);
        _redis.Track(key);                                                        // net-zero teardown (D-23)

        var db = _redis.Multiplexer.GetDatabase();
        Assert.Equal("out", await db.StringGetAsync(key));                        // OutputData written raw
        var remaining = await db.KeyTimeToLiveAsync(key);
        Assert.NotNull(remaining);
        Assert.InRange(remaining!.Value.TotalSeconds, ttl - 5, ttl);             // CONFIG-02 TTL applied
    }

    [Fact]
    public async Task Fail_Writes_Nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        // Output "{}" fails a definition requiring "x" -> Failed, nothing written.
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results("{}"));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = RequiresXDef };
        var send = new DispatchTestKit.CapturingSendProvider();
        var consumer = DispatchTestKit.Build(_redis.Multiplexer, context, processor, send);

        await consumer.Consume(OrchestratorTestStubs.Context(
            DispatchTestKit.Dispatch(entryId: Guid.Empty, correlationId: Guid.NewGuid()), ct));

        var sent = Assert.Single(send.Sent);
        Assert.Equal(StepOutcome.Failed, sent.Outcome);
        Assert.Equal(Guid.Empty, sent.EntryId);                                   // nothing minted -> nothing written
        Assert.NotNull(sent.ErrorMessage);
    }

    [Fact]
    public async Task MixedBatch_Completed_And_Failed()
    {
        var ct = TestContext.Current.CancellationToken;
        // One valid output ("{\"x\":1}") + one invalid ("{}") vs a definition requiring "x".
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results("{\"x\":1}", "{}"));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = RequiresXDef };
        var send = new DispatchTestKit.CapturingSendProvider();
        var consumer = DispatchTestKit.Build(_redis.Multiplexer, context, processor, send);

        await consumer.Consume(OrchestratorTestStubs.Context(
            DispatchTestKit.Dispatch(entryId: Guid.Empty, correlationId: Guid.NewGuid()), ct));

        Assert.Equal(2, send.Sent.Count);
        var completed = Assert.Single(send.Sent, r => r.Outcome == StepOutcome.Completed);
        var failed = Assert.Single(send.Sent, r => r.Outcome == StepOutcome.Failed);

        Assert.NotEqual(Guid.Empty, completed.EntryId);
        var key = L2ProjectionKeys.ExecutionData(completed.EntryId);
        _redis.Track(key);                                                        // net-zero teardown (D-23)
        var db = _redis.Multiplexer.GetDatabase();
        Assert.Equal("{\"x\":1}", await db.StringGetAsync(key));                  // only the valid output written

        Assert.Equal(Guid.Empty, failed.EntryId);                                 // invalid -> nothing written
    }

}
