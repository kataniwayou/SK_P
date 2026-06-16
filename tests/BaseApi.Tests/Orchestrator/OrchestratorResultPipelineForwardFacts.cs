using System.Text.Json;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestrator.Recovery;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// The FORWARD pass of <see cref="OrchestratorResultPipeline"/> (Phase 71 — proves ORCV-02/03/04 hermetically).
/// Reached only when <c>L2[messageId]</c> does NOT exist:
/// <list type="bullet">
///   <item><description>ORCV-02: a next step issues exactly ONE atomic <c>ScriptEvaluateAsync</c> over 3 KEYS
///   (index, copy-dest, copy-source); the Lua body HSETs the index slot, PEXPIREs the hash, and copies the
///   origin key via GET+SET (no RNG — TTLs ride as ARGV).</description></item>
///   <item><description>ORCV-03: the HSET value (ARGV[2]) is the JSON tuple { nextStepId, nextProcessorId,
///   payload, newEntryId }.</description></item>
///   <item><description>ORCV-04: a successful write dispatches an EntryStepDispatch to queue:{nextProcessorId}
///   then retires the slot to guid.empty; the gated two-key DEL runs on the no-escalation path and is SKIPPED
///   when a slot escalated.</description></item>
///   <item><description>ORCV-02 NODROP: an atomic-write exhaust → exactly one OrchestratorInject (no dispatch,
///   no StepCompleted for that slot).</description></item>
/// </list>
/// </summary>
public sealed class OrchestratorResultPipelineForwardFacts
{
    private static OrchestratorResultPipeline Build(
        IConnectionMultiplexer redis, OrchestratorPipelineTestKit.CapturingSendProvider send) =>
        new(redis, send, OrchestratorPipelineTestKit.Advancement(),
            OrchestratorPipelineTestKit.Retry(3), OrchestratorPipelineTestKit.Recovery(300),
            OrchestratorPipelineTestKit.Metrics(), NullLogger<OrchestratorResultPipeline>.Instance);

    [Fact]
    public async Task SingleNextStep_OneAtomicWrite_TupleHSet_DispatchToQueue_RetiresSlot()   // ORCV-02/03/04
    {
        var ct = TestContext.Current.CancellationToken;
        var originEntryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();
        var nextProcessorId = Guid.NewGuid();
        var redis = OrchestratorPipelineTestKit.ForwardOkL2(out var db);
        var send = new OrchestratorPipelineTestKit.CapturingSendProvider();

        var m = OrchestratorPipelineTestKit.Completed(originEntryId);
        var completed = OrchestratorPipelineTestKit.CompletedStep(new[] { nextStepId });
        var steps = OrchestratorPipelineTestKit.Steps((nextStepId, nextProcessorId, "{\"k\":1}"));

        await Build(redis, send).RunAsync(m, messageId, StepOutcome.Completed, completed, steps, ct);

        // ORCV-02: exactly ONE atomic ScriptEvaluateAsync; its KEYS are [index, data:new, data:origin].
        var call = db.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IDatabase.ScriptEvaluateAsync));
        var args = call.GetArguments();
        var script = (string)args[0]!;
        var keys = (RedisKey[])args[1]!;
        var argv = (RedisValue[])args[2]!;

        Assert.Equal(L2ProjectionKeys.MessageIndex(messageId), keys[0].ToString());
        Assert.StartsWith($"{L2ProjectionKeys.Prefix}data:", keys[1].ToString(), StringComparison.Ordinal);
        Assert.Equal(L2ProjectionKeys.ExecutionData(originEntryId), keys[2].ToString());
        // GET+SET copy form, no RNG in Lua, HSET before the copy SET.
        Assert.Contains("'GET'", script);
        Assert.DoesNotContain("TIME", script);
        Assert.True(script.IndexOf("'HSET'", StringComparison.Ordinal) < script.LastIndexOf("'SET'", StringComparison.Ordinal),
            "the atomic script must HSET the index slot before SET-ing the copied data key");

        // ORCV-03: ARGV[2] (0-based [1]) is the JSON tuple carrying nextStepId/nextProcessorId/payload/newEntryId.
        using var doc = JsonDocument.Parse(argv[1].ToString());
        var root = doc.RootElement;
        Assert.Equal(nextStepId, root.GetProperty("nextStepId").GetGuid());
        Assert.Equal(nextProcessorId, root.GetProperty("nextProcessorId").GetGuid());
        Assert.Equal("{\"k\":1}", root.GetProperty("payload").GetString());
        var newEntryId = root.GetProperty("newEntryId").GetGuid();
        Assert.NotEqual(Guid.Empty, newEntryId);
        // The copy-dest key (KEYS[2]) targets the SAME minted newEntryId.
        Assert.Equal(L2ProjectionKeys.ExecutionData(newEntryId), keys[1].ToString());

        // ORCV-04: an EntryStepDispatch went to queue:{nextProcessorId} carrying the newEntryId.
        var (uri, dispatch) = Assert.Single(send.SentDispatch);
        Assert.Equal($"queue:{nextProcessorId:D}", uri.ToString());
        Assert.Equal(nextProcessorId, dispatch.ProcessorId);
        Assert.Equal(nextStepId, dispatch.StepId);
        Assert.Equal(newEntryId, dispatch.EntryId);

        // ORCV-04: the slot was retired to guid.empty.
        await db.Received(1).HashSetAsync(
            L2ProjectionKeys.MessageIndex(messageId), Arg.Any<RedisValue>(),
            (RedisValue)Guid.Empty.ToString(), Arg.Any<When>(), Arg.Any<CommandFlags>());

        // No escalation → the gated two-key cleanup DEL ran.
        await db.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey[]>(ks => ks.Length == 2
                && ks.Contains((RedisKey)L2ProjectionKeys.ExecutionData(originEntryId))
                && ks.Contains((RedisKey)L2ProjectionKeys.MessageIndex(messageId))),
            Arg.Any<CommandFlags>());
        Assert.Empty(send.SentKeeper);
    }

    [Fact]
    public async Task AtomicWriteExhaust_OneInject_NoDispatch_NoCleanup()   // ORCV-02 NODROP + GATE-01
    {
        var ct = TestContext.Current.CancellationToken;
        var originEntryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();
        var nextProcessorId = Guid.NewGuid();
        var redis = OrchestratorPipelineTestKit.AtomicWriteFaultL2(out var db);
        var send = new OrchestratorPipelineTestKit.CapturingSendProvider();

        var m = OrchestratorPipelineTestKit.Completed(originEntryId);
        var completed = OrchestratorPipelineTestKit.CompletedStep(new[] { nextStepId });
        var steps = OrchestratorPipelineTestKit.Steps((nextStepId, nextProcessorId, "{}"));

        await Build(redis, send).RunAsync(m, messageId, StepOutcome.Completed, completed, steps, ct);

        // ORCV-02 NODROP: exactly one OrchestratorInject; it carries the copy operands + dispatch tuple.
        var inj = Assert.Single(send.SentKeeper.OfType<OrchestratorInject>());
        Assert.Equal(originEntryId, inj.OriginEntryId);
        Assert.NotEqual(Guid.Empty, inj.EntryId);              // the minted newEntryId
        Assert.Equal(nextStepId, inj.NextStepId);
        Assert.Equal(nextProcessorId, inj.NextProcessorId);

        // No dispatch, no StepCompleted for the escalated slot.
        Assert.Empty(send.SentDispatch);
        Assert.Empty(send.Sent);

        // GATE-01: a slot escalated → the cleanup two-key DEL MUST NOT run; no KeeperDelete either.
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
        Assert.Empty(send.SentKeeper.OfType<KeeperDelete>());
    }

    [Fact]
    public async Task NoNextSteps_NoWrite_CleanupRuns()   // ORCV-04 (terminal step: gated DEL still runs)
    {
        var ct = TestContext.Current.CancellationToken;
        var originEntryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var redis = OrchestratorPipelineTestKit.ForwardOkL2(out var db);
        var send = new OrchestratorPipelineTestKit.CapturingSendProvider();

        var m = OrchestratorPipelineTestKit.Completed(originEntryId);
        var completed = OrchestratorPipelineTestKit.CompletedStep(Array.Empty<Guid>());   // terminal — no successors
        var steps = OrchestratorPipelineTestKit.Steps();

        await Build(redis, send).RunAsync(m, messageId, StepOutcome.Completed, completed, steps, ct);

        // No next steps → no atomic write, no dispatch — but the gated cleanup tail still reclaims the index+origin.
        Assert.Empty(send.SentDispatch);
        await db.DidNotReceive().ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>());
        await db.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey[]>(ks => ks.Length == 2
                && ks.Contains((RedisKey)L2ProjectionKeys.ExecutionData(originEntryId))
                && ks.Contains((RedisKey)L2ProjectionKeys.MessageIndex(messageId))),
            Arg.Any<CommandFlags>());
    }
}
