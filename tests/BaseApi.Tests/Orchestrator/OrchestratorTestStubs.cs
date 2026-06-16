using System.Text.Json;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orchestrator.Configuration;
using Orchestrator.Dispatch;
using Orchestrator.Observability;
using Orchestrator.Recovery;
using StackExchange.Redis;
using System.Diagnostics.Metrics;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Shared Redis-multiplexer + ConsumeContext stubs for the Orchestrator lifecycle/ack tests
/// (recreating the helpers the seam-era StartStopConsumerAckTests carried before it was removed in
/// Plan 04). Used by Start/Stop lifecycle tests + the ack-semantics suite.
/// <list type="bullet">
///   <item><see cref="AbsentL2"/> — every <c>StringGetAsync</c> returns <see cref="RedisValue.Null"/>.</item>
///   <item><see cref="PresentL2"/> — registered keys resolve to their serialized projection value.</item>
///   <item><see cref="InfraFaultL2"/> — <c>StringGetAsync</c> throws a <see cref="RedisConnectionException"/>.</item>
///   <item><see cref="ParentIndexL2"/> — <c>SetMembersAsync(ParentIndex())</c> returns members +
///   registered <c>StringGetAsync</c> values (startup-hydration shape).</item>
/// </list>
/// </summary>
internal static class OrchestratorTestStubs
{
    /// <summary>A PresentL2 root + single entry-step value pair for <paramref name="workflowId"/>.</summary>
    public static IReadOnlyDictionary<string, string> RootWithStep(
        Guid workflowId, Guid jobId, Guid stepId, Guid processorId, string cron = "*/5 * * * *", string payload = "{}")
    {
        return new Dictionary<string, string>
        {
            [L2ProjectionKeys.Root(workflowId)] = JsonSerializer.Serialize(new WorkflowRootProjection(
                EntryStepIds: [stepId],
                Cron: cron,
                JobId: jobId,
                Liveness: new LivenessProjection(DateTime.UtcNow, Interval: 0, Status: "active"),
                CorrelationId: Guid.NewGuid().ToString())),
            [L2ProjectionKeys.Step(workflowId, stepId)] = JsonSerializer.Serialize(new StepProjection(
                EntryCondition: 0, ProcessorId: processorId, Payload: payload, NextStepIds: [])),
        };
    }

    /// <summary>StringGetAsync => RedisValue.Null for every key (workflow absent from L2).</summary>
    public static IConnectionMultiplexer AbsentL2(out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);
        return WrapMux(db);
    }

    /// <summary>StringGetAsync => the serialized value registered for that exact key (or Null).</summary>
    public static IConnectionMultiplexer PresentL2(IReadOnlyDictionary<string, string> values, out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var key = ((RedisKey)ci[0]).ToString();
                return values.TryGetValue(key, out var v) ? (RedisValue)v : RedisValue.Null;
            });
        return WrapMux(db);
    }

    /// <summary>StringGetAsync throws RedisConnectionException (infra fault — must propagate).</summary>
    public static IConnectionMultiplexer InfraFaultL2(out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns<Task<RedisValue>>(_ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stub: Redis unreachable"));
        return WrapMux(db);
    }

    /// <summary>
    /// SetMembersAsync(ParentIndex()) => the supplied members; StringGetAsync => registered values
    /// (the startup-hydration shape used by the corrupt-entry resilience test).
    /// </summary>
    public static IConnectionMultiplexer ParentIndexL2(
        IReadOnlyList<Guid> members, IReadOnlyDictionary<string, string> values, out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        var memberValues = members.Select(id => (RedisValue)id.ToString("D")).ToArray();
        db.SetMembersAsync(L2ProjectionKeys.ParentIndex(), Arg.Any<CommandFlags>()).Returns(memberValues);
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var key = ((RedisKey)ci[0]).ToString();
                return values.TryGetValue(key, out var v) ? (RedisValue)v : RedisValue.Null;
            });
        return WrapMux(db);
    }

    private static IConnectionMultiplexer WrapMux(IDatabase db)
    {
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    /// <summary>
    /// A real <see cref="OrchestratorMetrics"/> for hermetic tests — built from a live
    /// <see cref="IMeterFactory"/> (Plan 30-02 added the metrics ctor param to StepDispatcher +
    /// ResultConsumer). No collector is wired, so the increments are no-ops in-test; this just
    /// satisfies the non-null ctor dependency.
    /// </summary>
    public static OrchestratorMetrics Metrics()
    {
        var meterFactory = new ServiceCollection().AddMetrics().BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();
        return new OrchestratorMetrics(meterFactory);
    }

    /// <summary>A ConsumeContext substitute carrying <paramref name="message"/> and a cancellation token. A
    /// fresh non-null <c>MessageId</c> is stamped (Phase 71: the result pipeline's gate key) — pass an explicit
    /// <paramref name="messageId"/> to control it (e.g. to drive the gate via a redis fixture).</summary>
    public static ConsumeContext<T> Context<T>(T message, CancellationToken ct, Guid? messageId = null)
        where T : class
    {
        var context = Substitute.For<ConsumeContext<T>>();
        context.Message.Returns(message);
        context.CancellationToken.Returns(ct);
        context.MessageId.Returns(messageId ?? Guid.NewGuid());
        return context;
    }

    /// <summary>Phase 71: an <see cref="OrchestratorResultPipeline"/> over the given redis + send provider
    /// (default 3-retry / 300s data TTL). Used by the result-consume facts that now route dispatch through the
    /// L2-gated pipeline rather than a direct <c>IStepDispatcher</c>.</summary>
    public static OrchestratorResultPipeline Pipeline(IConnectionMultiplexer redis, ISendEndpointProvider send) =>
        new(redis, send, new StepAdvancement(),
            Options.Create(new RetryOptions { Limit = 3 }),
            Options.Create(new OrchestratorRecoveryOptions { ExecutionDataTtlSeconds = 300 }),
            Metrics(), NullLogger<OrchestratorResultPipeline>.Instance);

    /// <summary>Phase 71: a forward-OK redis mux — <c>KeyExistsAsync(MessageIndex)</c> FALSE → FORWARD branch;
    /// the single atomic <c>ScriptEvaluateAsync</c> + retire HSET + cleanup DEL succeed. The FORWARD pass then
    /// dispatches the downstream <see cref="EntryStepDispatch"/> via the send provider.</summary>
    public static IConnectionMultiplexer ForwardOkL2(out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(false);
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create(1));
        db.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns(2L);
        db.KeyPersistAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        return WrapMux(db);
    }
}
