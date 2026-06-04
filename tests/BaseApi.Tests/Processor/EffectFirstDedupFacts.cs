using BaseApi.Tests.Orchestrator;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Hashing;
using Messaging.Contracts.Projections;
using NSubstitute;
using StackExchange.Redis;
using Xunit;
using ExecutionResult = Messaging.Contracts.ExecutionResult;

namespace BaseApi.Tests.Processor;

/// <summary>
/// req-3 (processor content-addressed write) + req-4 (processor-hop effect-first dedup) facts for
/// <see cref="BaseProcessor.Core.Processing.EntryStepDispatchConsumer"/> (Plan 31-03). These are hermetic
/// (NSubstitute <see cref="IDatabase"/>, no live Redis — default Category, no <c>RealStack</c> trait):
/// <list type="bullet">
///   <item><b>DropOnAck</b> — an inbound H already <c>"Ack"</c> produces NO effect: no blob write, no
///   outbound <c>flag[resultH]="Pending"</c> pre-write, no result Send (D-06).</item>
///   <item><b>EffectThenCas</b> — when the flag is not <c>"Ack"</c>, the consumer writes the blob, writes
///   the manifest, pre-writes <c>flag[resultH]="Pending"</c>, and Sends the result BEFORE flipping
///   <c>flag[dispatch.H]</c> Pending-&gt;Ack via <c>When.Exists</c> (effect-first, D-06/D-07).</item>
///   <item><b>OutboundPendingPreWrite</b> — the outbound result's <c>flag[resultH]="Pending"</c> (resultH
///   = ComputeH over the manifest EntryId) is written BEFORE the Send, seeding the orchestrator-hop
///   drop-on-Ack gate (Plan 04 flips it via <c>When.Exists</c>).</item>
///   <item><b>AckCas_UsesWhenExists</b> — the inbound flag flip is a SET XX (<c>When.Exists</c>).</item>
///   <item><b>CrashWindowResidual</b> — a redelivery whose flag never reached <c>"Ack"</c> re-produces the
///   SAME content-addressed data key and the SAME result H — a collapsed downstream DUPLICATE, never a
///   LOSS (D-06 effect-first tradeoff).</item>
/// </list>
/// </summary>
public sealed class EffectFirstDedupFacts
{
    private const string Output = "{\"v\":1}";

    // The data write + manifest write + the outbound Pending pre-write all bind the
    // StringSetAsync(RedisKey, RedisValue, Expiration, ValueCondition, CommandFlags) overload (the named
    // `expiry:` TimeSpan converts to Expiration). The inbound Ack flip binds the legacy
    // StringSetAsync(RedisKey, RedisValue, TimeSpan?, When, CommandFlags) overload (the named `when:`).
    private static Task<bool> ExpirySet(IDatabase db, Func<string, bool> keyMatch, string value) =>
        db.StringSetAsync(
            Arg.Is<RedisKey>(k => keyMatch(k.ToString())),
            Arg.Is<RedisValue>(v => v == value),
            Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>());

    private static Task<bool> WhenSet(IDatabase db, RedisKey key, string value, When when) =>
        db.StringSetAsync(
            Arg.Is<RedisKey>(k => k == key),
            Arg.Is<RedisValue>(v => v == value),
            Arg.Any<TimeSpan?>(), when, Arg.Any<CommandFlags>());

    /// <summary>Computes the manifest EntryId + outbound resultH for a single-blob result, test-side.</summary>
    private static (string ManifestEntryId, string ResultH) ExpectedManifest(EntryStepDispatch d, string blob)
    {
        var blobHash = MessageIdentity.HashBlob(blob);
        var manifestJson = System.Text.Json.JsonSerializer.Serialize(new[] { blobHash });
        var manifestEntryId = MessageIdentity.HashManifest(manifestJson);
        var resultH = MessageIdentity.ComputeH(d.CorrelationId, d.WorkflowId, d.StepId, d.ProcessorId, manifestEntryId);
        return (manifestEntryId, resultH);
    }

    [Fact]
    public async Task DropOnAck_ProducesNoEffect()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatch = new EntryStepDispatch(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "{\"cfg\":1}")
        {
            CorrelationId = Guid.NewGuid(),
            EntryId = "",
            H = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef",
        };
        var db = Substitute.For<IDatabase>();
        // The inbound flag is already "Ack" -> drop. Any other read returns Null (unused on this path).
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci => ((RedisKey)ci[0]).ToString() == L2ProjectionKeys.Flag(dispatch.H)
                ? (RedisValue)"Ack" : RedisValue.Null);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results(Output));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();
        var consumer = DispatchTestKit.Build(mux, context, processor, send);

        await consumer.Consume(OrchestratorTestStubs.Context(dispatch, ct));

        // NO effect: the transform never ran, NO data/manifest write, NO outbound Pending, NO Send.
        Assert.False(processor.Invoked);
        Assert.Empty(send.Sent);
        await db.DidNotReceive().StringSetAsync(
            Arg.Is<RedisKey>(k => k.ToString().StartsWith(L2ProjectionKeys.Prefix + "data:")),
            Arg.Any<RedisValue>(), Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>());
        await db.DidNotReceive().StringSetAsync(
            Arg.Is<RedisKey>(k => k.ToString().StartsWith(L2ProjectionKeys.Prefix + "flag:")),
            Arg.Any<RedisValue>(), Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Effect_Then_AckCas_InOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatch = new EntryStepDispatch(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "{\"cfg\":1}")
        {
            CorrelationId = Guid.NewGuid(),
            EntryId = "",
            H = "feedfacefeedfacefeedfacefeedfacefeedfacefeedfacefeedfacefeedface",
        };
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);   // flag not Ack
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results(Output));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();
        var consumer = DispatchTestKit.Build(mux, context, processor, send);

        var (_, resultH) = ExpectedManifest(dispatch, Output);
        var blobHash = MessageIdentity.HashBlob(Output);

        await consumer.Consume(OrchestratorTestStubs.Context(dispatch, ct));

        // The single manifest result was sent.
        var sent = Assert.Single(send.Sent);
        Assert.Equal(StepOutcome.Completed, sent.Outcome);
        Assert.Equal(resultH, sent.H);

        // Effect-first order: blob write -> outbound flag[resultH]="Pending" -> inbound flag[H]="Ack" CAS.
        // Inspect the recorded StringSetAsync calls in invocation order, matching by (key, value) across
        // ALL StringSetAsync overloads (the `expiry:` writes and the `when:` flip bind different overloads,
        // so a cross-overload Received.InOrder is fragile — order the captured calls directly instead).
        var setCalls = db.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IDatabase.StringSetAsync))
            .Select(c => c.GetArguments())
            .Where(a => a.Length > 1 && a[0] is RedisKey && a[1] is RedisValue)
            .Select(a => (Key: ((RedisKey)a[0]!).ToString(), Value: ((RedisValue)a[1]!).ToString()))
            .ToList();

        var iBlob    = setCalls.FindIndex(c => c.Key == L2ProjectionKeys.ExecutionData(blobHash) && c.Value == Output);
        var iPending = setCalls.FindIndex(c => c.Key == L2ProjectionKeys.Flag(resultH) && c.Value == "Pending");
        var iAck     = setCalls.FindIndex(c => c.Key == L2ProjectionKeys.Flag(dispatch.H) && c.Value == "Ack");

        Assert.True(iBlob >= 0, "blob write not observed");
        Assert.True(iPending >= 0, "outbound flag[resultH]=Pending pre-write not observed");
        Assert.True(iAck >= 0, "inbound flag[dispatch.H]=Ack flip not observed");
        Assert.True(iBlob < iPending, "blob write must precede the outbound Pending pre-write");
        Assert.True(iPending < iAck, "outbound Pending pre-write (before the Send) must precede the inbound Ack flip");

        // ...and the inbound flip is a SET XX (When.Exists), after the send.
        await WhenSet(db, L2ProjectionKeys.Flag(dispatch.H), "Ack", When.Exists);
    }

    [Fact]
    public async Task Outbound_Result_PreWrites_Pending_Before_Send()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatch = new EntryStepDispatch(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "{\"cfg\":1}")
        {
            CorrelationId = Guid.NewGuid(),
            EntryId = "",
            H = "0badc0de0badc0de0badc0de0badc0de0badc0de0badc0de0badc0de0badc0de",
        };
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results(Output));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        // A send provider that records, AT EACH Send, whether the outbound Pending pre-write already happened.
        var (_, resultH) = ExpectedManifest(dispatch, Output);
        var pendingSeenAtSend = new List<bool>();
        var sendProvider = new PreWriteProbeSendProvider(db, L2ProjectionKeys.Flag(resultH), pendingSeenAtSend);
        var consumer = DispatchTestKit.Build(mux, context, processor, sendProvider);

        await consumer.Consume(OrchestratorTestStubs.Context(dispatch, ct));

        // The outbound flag[resultH]="Pending" pre-write seeds the orchestrator-hop dedup (regression guard).
        await ExpirySet(db, k => k == L2ProjectionKeys.Flag(resultH), "Pending");
        // ...and it happened BEFORE the Send (the probe saw the Pending set already issued at Send time).
        Assert.Equal(new[] { true }, pendingSeenAtSend);
    }

    [Fact]
    public async Task AckCas_UsesWhenExists()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatch = new EntryStepDispatch(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "{\"cfg\":1}")
        {
            CorrelationId = Guid.NewGuid(),
            EntryId = "",
            H = "abad1deaabad1deaabad1deaabad1deaabad1deaabad1deaabad1deaabad1dea",
        };
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results(Output));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();
        var consumer = DispatchTestKit.Build(mux, context, processor, send);

        await consumer.Consume(OrchestratorTestStubs.Context(dispatch, ct));

        // The inbound flag flip is a SET XX on flag[dispatch.H] (When.Exists).
        await WhenSet(db, L2ProjectionKeys.Flag(dispatch.H), "Ack", When.Exists);
    }

    [Fact]
    public async Task CrashWindow_ReproducesCollapsedDuplicate()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatch = new EntryStepDispatch(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "{\"cfg\":1}")
        {
            CorrelationId = Guid.NewGuid(),
            EntryId = "",
            H = "c0ffeec0ffeec0ffeec0ffeec0ffeec0ffeec0ffeec0ffeec0ffeec0ffeec0ff",
        };
        var db = Substitute.For<IDatabase>();
        // The inbound flag NEVER reaches "Ack" (the crash window: the Pending->Ack flip was skipped/lost),
        // so both deliveries pass the drop-on-Ack gate and re-produce the effect.
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results(Output));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();
        var consumer = DispatchTestKit.Build(mux, context, processor, send);

        var (_, resultH) = ExpectedManifest(dispatch, Output);
        var blobHash = MessageIdentity.HashBlob(Output);

        await consumer.Consume(OrchestratorTestStubs.Context(dispatch, ct));   // delivery 1
        await consumer.Consume(OrchestratorTestStubs.Context(dispatch, ct));   // delivery 2 (redelivery)

        // BOTH deliveries re-produced the SAME content-addressed data key (idempotent overwrite) and the
        // SAME result H — a collapsed downstream DUPLICATE, never a LOSS.
        Assert.Equal(2, send.Sent.Count);
        Assert.All(send.Sent, r => Assert.Equal(resultH, r.H));
        await db.Received(2).StringSetAsync(
            Arg.Is<RedisKey>(k => k == L2ProjectionKeys.ExecutionData(blobHash)),
            Arg.Is<RedisValue>(v => v == Output),
            Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>());
    }

    /// <summary>
    /// A capturing send provider that, at each <c>Send</c>, probes whether the outbound
    /// <c>flag[resultH]="Pending"</c> StringSet was ALREADY issued on the substitute db — proving the
    /// pre-write precedes the Send without relying on cross-overload InOrder against the send endpoint.
    /// </summary>
    private sealed class PreWriteProbeSendProvider(IDatabase db, RedisKey pendingKey, List<bool> seen)
        : ISendEndpointProvider
    {
        public Task<ISendEndpoint> GetSendEndpoint(Uri address)
        {
            var endpoint = Substitute.For<ISendEndpoint>();
            endpoint.Send(Arg.Any<ExecutionResult>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    var pendingAlreadySet = db.ReceivedCalls().Any(c =>
                        c.GetMethodInfo().Name == nameof(IDatabase.StringSetAsync)
                        && c.GetArguments().Length > 1
                        && c.GetArguments()[0] is RedisKey k && k == pendingKey
                        && c.GetArguments()[1] is RedisValue v && v == "Pending");
                    seen.Add(pendingAlreadySet);
                    return Task.CompletedTask;
                });
            return Task.FromResult(endpoint);
        }

        public ConnectHandle ConnectSendObserver(ISendObserver observer) => throw new NotSupportedException();
    }
}
