using BaseApi.Tests.Orchestrator;
using BaseProcessor.Core.Processing;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// LOG-04 / LOG-01: on the Completed path, <see cref="EntryStepDispatchConsumer"/> opens a nested
/// <c>BeginScope</c> carrying the MINTED <c>ExecutionId</c> + output <c>EntryId</c> around the L2 write +
/// result-build. The scoped values are the SAME minted ids the sent <see cref="Messaging.Contracts.ExecutionResult"/>
/// reports, and each key appears EXACTLY ONCE on the wrapped LogRecord (L2/A4 — the inbound dispatch
/// carries <c>Guid.Empty</c>, which the outer execution-scope filter skips, so the inner nested scope is
/// the only writer of these two keys). The early Failed/Cancelled paths are NOT wrapped (Pitfall 2).
/// </summary>
public sealed class EntryStepDispatchScopeTests
{
    // ── scope-capturing logger double (mirrors ConsoleExecutionScopeFilterTests; no new package) ──
    private sealed class CapturingLogger : ILogger<EntryStepDispatchConsumer>
    {
        public List<Dictionary<string, object>> Scopes { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            if (state is IEnumerable<KeyValuePair<string, object>> kvps)
                Scopes.Add(new Dictionary<string, object>(kvps));
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) { }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>The nested scope captured on the write/send path (the one carrying ExecutionId or EntryId).</summary>
    private static Dictionary<string, object> NestedScope(CapturingLogger logger) =>
        logger.Scopes.FirstOrDefault(s =>
            s.ContainsKey(ExecutionLogScope.ExecutionId) || s.ContainsKey(ExecutionLogScope.EntryId))
        ?? new Dictionary<string, object>();

    /// <summary>A non-faulting Redis multiplexer whose StringSetAsync (output write) is a no-op success.</summary>
    private static IConnectionMultiplexer WritableRedis()
    {
        var db = Substitute.For<IDatabase>();   // unstubbed StringSetAsync returns a completed Task (no-op)
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    [Fact]
    public async Task Completed_Path_Scopes_The_Minted_ExecutionId_And_EntryId_Once_Per_Key()
    {
        var ct = TestContext.Current.CancellationToken;

        // No-input source processor (InputDefinition null + entryId "") → skip the L2 READ and
        // exercise the Completed write/send path. One result passes a null OutputDefinition (skip validate).
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results("out"));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new DispatchTestKit.CapturingSendProvider();
        var logger = new CapturingLogger();

        var consumer = new EntryStepDispatchConsumer(
            WritableRedis(), context, processor, DispatchTestKit.Options(300), send,
            DispatchTestKit.Metrics(), logger);

        await consumer.Consume(OrchestratorTestStubs.Context(
            DispatchTestKit.Dispatch(entryId: Guid.Empty, correlationId: Guid.NewGuid()), ct));

        // D-03/D-06a: the per-result write opens a nested scope carrying the MINTED lineage ExecutionId +
        // the freshly minted Guid EntryId (the real L2 data key). One result = one StepCompleted carrying
        // that same Guid EntryId + ExecutionId (straight-through — no manifest, the scoped ids ARE the
        // sent record's ids).
        var sent = Assert.Single(send.Sent);
        var completed = Assert.IsType<StepCompleted>(sent);
        Assert.NotEqual(Guid.Empty, completed.ExecutionId);
        Assert.NotEqual(Guid.Empty, completed.EntryId);

        var scope = NestedScope(logger);

        // Exactly the two keys, each present exactly once.
        Assert.Equal(2, scope.Count);
        Assert.True(scope.ContainsKey(ExecutionLogScope.ExecutionId));
        Assert.True(scope.ContainsKey(ExecutionLogScope.EntryId));
        Assert.True(Guid.TryParse((string)scope[ExecutionLogScope.ExecutionId], out var scopedExec)
            && scopedExec != Guid.Empty);                                                  // a real lineage Guid
        // D-06a: the scoped EntryId is the minted Guid data key (.ToString()), == the sent record's EntryId.
        Assert.Equal(completed.EntryId.ToString(), scope[ExecutionLogScope.EntryId]);
        Assert.Equal(completed.ExecutionId.ToString(), scope[ExecutionLogScope.ExecutionId]);

        // One entry per key: only ONE captured scope carries these execution keys (no duplicate nesting).
        var carriers = logger.Scopes.Count(s =>
            s.ContainsKey(ExecutionLogScope.ExecutionId) || s.ContainsKey(ExecutionLogScope.EntryId));
        Assert.Equal(1, carriers);
    }

    [Fact]
    public async Task Failed_Path_Opens_No_Nested_ExecutionId_EntryId_Scope()
    {
        var ct = TestContext.Current.CancellationToken;

        // Output "{}" fails a definition requiring "x" → Failed: EntryId stays "", nothing
        // written, and the nested ExecutionId/EntryId scope must NOT open on this path (Pitfall 2).
        const string requiresX = "{\"type\":\"object\",\"required\":[\"x\"]}";
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results("{}"));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = requiresX };
        var send = new DispatchTestKit.CapturingSendProvider();
        var logger = new CapturingLogger();

        var consumer = new EntryStepDispatchConsumer(
            WritableRedis(), context, processor, DispatchTestKit.Options(300), send,
            DispatchTestKit.Metrics(), logger);

        await consumer.Consume(OrchestratorTestStubs.Context(
            DispatchTestKit.Dispatch(entryId: Guid.Empty, correlationId: Guid.NewGuid()), ct));

        var sent = Assert.Single(send.Sent);
        Assert.IsType<StepFailed>(sent);

        // The Failed branch builds the result OUTSIDE the nested scope — no ExecutionId/EntryId scope opened.
        Assert.Empty(NestedScope(logger));
    }
}
