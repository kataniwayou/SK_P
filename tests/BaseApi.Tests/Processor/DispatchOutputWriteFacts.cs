using BaseApi.Tests.Composition;
using BaseApi.Tests.Orchestrator;
using Messaging.Contracts;
using Messaging.Contracts.Hashing;
using Messaging.Contracts.Projections;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// EXEC-05 output-write facts against the real localhost:6380 Redis (<see cref="RedisFixture"/>). Plan
/// 31-03 (req-3): a result that passes its <c>OutputDefinition</c> is content-addressed — its OutputData
/// is written at <c>L2[data(hash(blob))]</c> with the configured TTL (CONFIG-02) — and the consumer sends
/// ONE manifest result whose <c>EntryId</c> = hash of the JSON array of blob hashes (the manifest is also
/// written at <c>L2[data(manifestEntryId)]</c>). A result that FAILS its definition is a whole-dispatch
/// <see cref="StepOutcome.Failed"/> with an empty-string <c>EntryId</c> and writes NOTHING. Every written
/// key is <c>_redis.Track</c>'d for net-zero teardown (D-23).
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

        var dispatch = DispatchTestKit.Dispatch(entryId: "", correlationId: Guid.NewGuid());
        await consumer.Consume(OrchestratorTestStubs.Context(dispatch, ct));

        var sent = Assert.Single(send.Sent);
        Assert.Equal(StepOutcome.Completed, sent.Outcome);
        Assert.False(string.IsNullOrEmpty(sent.EntryId));

        // Plan 31-03: the blob is content-addressed at data[hash(blob)] (NOT the manifest entryId), and
        // the manifest itself is written at data[manifestEntryId] = sent.EntryId.
        var blobHash = MessageIdentity.HashBlob("out");
        var blobKey = L2ProjectionKeys.ExecutionData(blobHash);
        var manifestKey = L2ProjectionKeys.ExecutionData(sent.EntryId);
        var manifestJson = System.Text.Json.JsonSerializer.Serialize(new[] { blobHash });
        var pendingKey = L2ProjectionKeys.Flag(sent.H);                           // outbound Pending pre-write
        _redis.Track(blobKey);
        _redis.Track(manifestKey);
        _redis.Track(pendingKey);                                                 // net-zero teardown (D-23)

        var db = _redis.Multiplexer.GetDatabase();
        Assert.Equal("out", await db.StringGetAsync(blobKey));                    // OutputData written raw, content-addressed
        Assert.Equal(manifestJson, await db.StringGetAsync(manifestKey));         // manifest = ["<blobHash>"]
        Assert.Equal("Pending", await db.StringGetAsync(pendingKey));            // outbound flag[resultH]="Pending" seeded
        var remaining = await db.KeyTimeToLiveAsync(blobKey);
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
            DispatchTestKit.Dispatch(entryId: "", correlationId: Guid.NewGuid()), ct));

        var sent = Assert.Single(send.Sent);
        Assert.Equal(StepOutcome.Failed, sent.Outcome);
        Assert.Equal("", sent.EntryId);                                           // nothing minted -> nothing written
        Assert.NotNull(sent.ErrorMessage);
    }

    [Fact]
    public async Task AnyInvalidBlob_FailsWholeDispatch()
    {
        var ct = TestContext.Current.CancellationToken;
        // One valid output ("{\"x\":1}") + one invalid ("{}") vs a definition requiring "x". Plan 31-03:
        // a blob that fails output-schema validation is a WHOLE-DISPATCH business Failed (D-09) — the
        // consumer sends ONE Failed result with EntryId="" and does NOT emit a Completed manifest result.
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results("{\"x\":1}", "{}"));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = RequiresXDef };
        var send = new DispatchTestKit.CapturingSendProvider();
        var consumer = DispatchTestKit.Build(_redis.Multiplexer, context, processor, send);

        await consumer.Consume(OrchestratorTestStubs.Context(
            DispatchTestKit.Dispatch(entryId: "", correlationId: Guid.NewGuid()), ct));

        var sent = Assert.Single(send.Sent);
        Assert.Equal(StepOutcome.Failed, sent.Outcome);
        Assert.Equal("", sent.EntryId);                                           // Failed short-circuits the manifest
        Assert.Equal("", sent.H);                                                 // no outbound dedup identity on Failed
        Assert.NotNull(sent.ErrorMessage);

        // The first (valid) blob was content-addressed before the second failed — track it for teardown.
        _redis.Track(L2ProjectionKeys.ExecutionData(MessageIdentity.HashBlob("{\"x\":1}")));
    }

}
