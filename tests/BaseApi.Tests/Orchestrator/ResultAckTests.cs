using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestrator.Consumers;
using Orchestrator.Dispatch;
using Orchestrator.L1;
using StackExchange.Redis;
using Xunit;
using ExecutionResult = Messaging.Contracts.ExecutionResult;   // disambiguate from MassTransit.ExecutionResult

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// ORCH-RESULT-ACK-01 goal-backward proof of the result-consume ack split (gate OPEN):
/// <list type="bullet">
///   <item>an unknown <c>(workflowId, stepId)</c> result acks (no throw, no dispatch);</item>
///   <item>a completed step with no matching next step acks (no throw, no dispatch);</item>
///   <item>NO L2/Redis read occurs on the result path (<c>db.DidNotReceive().StringGetAsync(...)</c>);</item>
///   <item>an injected infra fault on the broker <c>Send</c> propagates (does not ack-swallow).</item>
/// </list>
/// The consumer reads L1 only, so a directly-seeded <see cref="WorkflowL1Store"/> is the fixture; the
/// Redis mux stub exists purely to assert it is never touched (mirrors FireDispatchTests).
/// </summary>
public sealed class ResultAckTests
{
    private static StepProjection Step(int entryCondition, Guid processorId, string payload, params Guid[] nextStepIds) =>
        new(EntryCondition: entryCondition, ProcessorId: processorId, Payload: payload, NextStepIds: [.. nextStepIds]);

    private static void SeedWorkflow(
        WorkflowL1Store store, Guid workflowId, IReadOnlyDictionary<Guid, StepProjection> steps)
    {
        var entry = new WorkflowL1([], "*/5 * * * *", Guid.NewGuid(), steps)
        {
            Liveness = new LivenessProjection(DateTime.UtcNow, Interval: 300, Status: "active"),
        };
        store.Upsert(workflowId, entry);
    }

    private static ResultConsumer Build(WorkflowL1Store store, IStepDispatcher dispatcher)
        => Build(store, dispatcher, OrchestratorTestStubs.NoopRedis());

    private static ResultConsumer Build(WorkflowL1Store store, IStepDispatcher dispatcher, IConnectionMultiplexer redis)
    {
        // 24.1 / D-24.1-05: the boot gate is removed — ResultConsumer no longer takes an IStartupGate.
        // Plan 31-04: ResultConsumer now takes IConnectionMultiplexer for the effect-first dedup gate
        // (flag[m.H]) + the manifest read (data[m.EntryId]).
        return new ResultConsumer(
            store, new StepAdvancement(), dispatcher, redis, OrchestratorTestStubs.Metrics(),
            NullLogger<ResultConsumer>.Instance);
    }

    // ----- R5: an id ABSENT from L1 acks cleanly, lifecycle-agnostic (no throw, no _error) -------

    [Fact]
    public async Task ResultForIdAbsentFromL1_AcksGracefully_NoThrow_NoDispatch()
    {
        // 24.1 R5 / D-24.1-05 (supersedes D-06 / ORCH-GATE-01): with the boot gate REMOVED, L1 is the
        // SOLE arbiter. A result for an id absent from L1 — unknown / stopped-drained / not-yet-hydrated,
        // uniformly — is the DEFINED graceful business-ack outcome: log + return, NEVER throw and NEVER
        // route to _error. (There is no gate; nothing distinguishes "boot window" from "unknown" — both
        // are an L1 miss.) This proves the lifecycle-agnostic graceful-miss path.
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = Substitute.For<IStepDispatcher>();
        var store = new WorkflowL1Store(); // empty — the id is absent from L1 regardless of lifecycle

        var consumer = Build(store, dispatcher);
        var result = new ExecutionResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), StepOutcome.Completed)
        {
            CorrelationId = Guid.NewGuid(),
        };

        // No throw (clean business-ack) — the message is consumed, not redelivered, not DLQ'd.
        await consumer.Consume(OrchestratorTestStubs.Context(result, ct));

        await dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ----- unknown (workflowId, stepId) -> ack (no throw, no dispatch) ---------------------------

    [Fact]
    public async Task UnknownWorkflowStep_Acks_NoThrow_NoDispatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = Substitute.For<IStepDispatcher>();
        var store = new WorkflowL1Store(); // empty — workflow absent

        var consumer = Build(store, dispatcher);
        var result = new ExecutionResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), StepOutcome.Completed)
        {
            CorrelationId = Guid.NewGuid(),
        };

        // No throw (clean ack).
        await consumer.Consume(OrchestratorTestStubs.Context(result, ct));

        await dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ----- completed step with NO matching next step -> ack (no throw, no dispatch) --------------

    [Fact]
    public async Task NoMatchingNextStep_Acks_NoThrow_NoDispatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = Substitute.For<IStepDispatcher>();

        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();

        // A Completed result, but the only next step is Failed(2)-gated — no match.
        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Failed, Guid.NewGuid(), "{}"),
        };
        var store = new WorkflowL1Store();
        SeedWorkflow(store, workflowId, steps);

        var consumer = Build(store, dispatcher);
        var result = new ExecutionResult(workflowId, completedStepId, Guid.NewGuid(), StepOutcome.Completed)
        {
            CorrelationId = Guid.NewGuid(),
        };

        await consumer.Consume(OrchestratorTestStubs.Context(result, ct));

        await dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ----- result path reads L2 for dedup + manifest (Phase 31 supersedes the L1-only invariant) ----

    [Fact]
    public async Task ResultPath_ReadsDedupFlag_And_UnbundlesManifest_ThenDispatches()
    {
        // Phase 31-04 (D-06/D-08): the orchestrator hop is no longer L1-only — it reads flag[m.H] for the
        // effect-first dedup gate AND data[m.EntryId] for the manifest. A one-item manifest x one matched
        // successor dispatches exactly ONE continuation carrying the ITEM EntryId, then flips flag[m.H]->Ack.
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = new RecordingDispatcher();

        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();
        var nextProcessorId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Completed, nextProcessorId, "{}"),
        };
        var store = new WorkflowL1Store();
        SeedWorkflow(store, workflowId, steps);

        var manifestEntryId = Guid.NewGuid().ToString("D");
        var itemEntryId = "aa" + Guid.NewGuid().ToString("N");
        var manifestJson = System.Text.Json.JsonSerializer.Serialize(new[] { itemEntryId });
        var resultH = "ddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddffff";

        // flag[resultH] not "Ack" (Null -> pass the gate); data[manifestEntryId] -> the one-item manifest.
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var key = ((RedisKey)ci[0]).ToString();
                return key == L2ProjectionKeys.ExecutionData(manifestEntryId) ? (RedisValue)manifestJson : RedisValue.Null;
            });
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        var consumer = Build(store, dispatcher, mux);
        var result = new ExecutionResult(workflowId, completedStepId, Guid.NewGuid(), StepOutcome.Completed)
        {
            CorrelationId = Guid.NewGuid(),
            EntryId = manifestEntryId,
            H = resultH,
        };

        await consumer.Consume(OrchestratorTestStubs.Context(result, ct));

        // ONE continuation dispatched carrying the manifest ITEM EntryId + the matched successor's ids.
        var dispatched = Assert.Single(dispatcher.Calls);
        Assert.Equal(workflowId, dispatched.WorkflowId);
        Assert.Equal(nextStepId, dispatched.StepId);
        Assert.Equal(nextProcessorId, dispatched.ProcessorId);
        Assert.Equal(itemEntryId, dispatched.EntryId);
        Assert.NotEqual(Guid.Empty, dispatched.ExecutionId); // regenerated lineage

        // The dedup gate read flag[m.H], then flipped it Pending->Ack via a SET XX (When.Exists). Inspect
        // the recorded calls directly (robust to StringSetAsync overload binding — see EffectFirstDedupFacts).
        // The `when: When.Exists` SET binds the modern StringSetAsync(RedisKey, RedisValue, Expiration,
        // ValueCondition, CommandFlags) overload — When.Exists surfaces as a ValueCondition that renders
        // "XX" (SET XX). Match by (key, value, "XX") across whichever overload was bound (robust like
        // EffectFirstDedupFacts).
        var flagKey = L2ProjectionKeys.Flag(resultH);
        Assert.Contains(db.ReceivedCalls(), c =>
            c.GetMethodInfo().Name == nameof(IDatabase.StringGetAsync)
            && c.GetArguments().Length > 0 && c.GetArguments()[0] is RedisKey gk && gk.ToString() == flagKey);
        Assert.Contains(db.ReceivedCalls(), c =>
            c.GetMethodInfo().Name == nameof(IDatabase.StringSetAsync)
            && c.GetArguments().Length > 1
            && c.GetArguments()[0] is RedisKey sk && sk.ToString() == flagKey
            && c.GetArguments()[1] is RedisValue sv && sv.ToString() == "Ack"
            // SET XX surfaces as a ValueCondition("XX") on the (RedisKey,RedisValue,Expiration,ValueCondition,
            // CommandFlags) overload, OR as a When.Exists enum on the keepTtl
            // (RedisKey,RedisValue,TimeSpan?,bool,When,CommandFlags) overload the flip now binds (Phase 31
            // keepTtl preserves the sender TTL). Accept either so the assertion is overload-robust.
            && c.GetArguments().Any(a => (a is ValueCondition vc && vc.ToString() == "XX") || (a is When w && w == When.Exists)));
    }

    // ----- an inbound result whose flag[m.H] is already "Ack" is dropped (no dispatch, no flip) -------

    [Fact]
    public async Task ResultWithFlagAlreadyAck_IsDropped_NoDispatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = Substitute.For<IStepDispatcher>();

        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Completed, Guid.NewGuid(), "{}"),
        };
        var store = new WorkflowL1Store();
        SeedWorkflow(store, workflowId, steps);

        var resultH = "ac4eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci => ((RedisKey)ci[0]).ToString() == L2ProjectionKeys.Flag(resultH) ? (RedisValue)"Ack" : RedisValue.Null);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        var consumer = Build(store, dispatcher, mux);
        var result = new ExecutionResult(workflowId, completedStepId, Guid.NewGuid(), StepOutcome.Completed)
        {
            CorrelationId = Guid.NewGuid(),
            EntryId = Guid.NewGuid().ToString("N"),
            H = resultH,
        };

        // Drop-on-Ack: no throw, NO dispatch, NO manifest read.
        await consumer.Consume(OrchestratorTestStubs.Context(result, ct));

        await dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ----- injected infra fault on Send propagates (does not ack-swallow) ------------------------

    [Fact]
    public async Task InfraFaultOnSend_Propagates()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = Substitute.For<IStepDispatcher>();
        dispatcher.DispatchAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new MassTransitException("stub: broker Send fault"));

        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Completed, Guid.NewGuid(), "{}"),
        };
        var store = new WorkflowL1Store();
        SeedWorkflow(store, workflowId, steps);

        // A one-item manifest so the fan-out actually issues a dispatch (which then throws the infra fault).
        var manifestEntryId = Guid.NewGuid().ToString("D");
        var manifestJson = System.Text.Json.JsonSerializer.Serialize(new[] { "aa" + Guid.NewGuid().ToString("N") });
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci => ((RedisKey)ci[0]).ToString() == L2ProjectionKeys.ExecutionData(manifestEntryId)
                ? (RedisValue)manifestJson : RedisValue.Null);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        var consumer = Build(store, dispatcher, mux);
        var result = new ExecutionResult(workflowId, completedStepId, Guid.NewGuid(), StepOutcome.Completed)
        {
            CorrelationId = Guid.NewGuid(),
            EntryId = manifestEntryId,
            H = "1234" + new string('0', 60),
        };

        // Infra fault must NOT be ack-swallowed — it propagates so the bounded retry -> _error.
        await Assert.ThrowsAsync<MassTransitException>(
            () => consumer.Consume(OrchestratorTestStubs.Context(result, ct)));
    }

    /// <summary>
    /// A concrete recording <see cref="IStepDispatcher"/> — captures each <c>DispatchAsync</c> call's
    /// args so the fan-out assertions are deterministic (avoids NSubstitute matcher fragility when the
    /// same captured call is asserted with mixed concrete/Arg matchers).
    /// </summary>
    private sealed class RecordingDispatcher : IStepDispatcher
    {
        public sealed record Call(Guid WorkflowId, Guid StepId, Guid ProcessorId, string Payload,
            Guid CorrelationId, Guid ExecutionId, string EntryId);

        public List<Call> Calls { get; } = [];

        public Task DispatchAsync(Guid workflowId, Guid stepId, Guid processorId, string payload,
            Guid correlationId, Guid executionId, string entryId, CancellationToken ct)
        {
            Calls.Add(new Call(workflowId, stepId, processorId, payload, correlationId, executionId, entryId));
            return Task.CompletedTask;
        }
    }
}
