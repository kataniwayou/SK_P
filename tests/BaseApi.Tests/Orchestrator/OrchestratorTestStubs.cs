using System.Text.Json;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using NSubstitute;
using StackExchange.Redis;

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

    /// <summary>A ConsumeContext substitute carrying <paramref name="message"/> and a cancellation token.</summary>
    public static ConsumeContext<T> Context<T>(T message, CancellationToken ct)
        where T : class
    {
        var context = Substitute.For<ConsumeContext<T>>();
        context.Message.Returns(message);
        context.CancellationToken.Returns(ct);
        return context;
    }
}
