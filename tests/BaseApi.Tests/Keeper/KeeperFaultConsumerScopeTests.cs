using Keeper.Consumers;
using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using ExecutionResult = Messaging.Contracts.ExecutionResult;   // disambiguate from MassTransit.ExecutionResult

namespace BaseApi.Tests.Keeper;

/// <summary>
/// KMET-04 / SC2 (hermetic): the two Keeper fault consumers — <see cref="FaultEntryStepDispatchConsumer"/>
/// and <see cref="FaultExecutionResultConsumer"/> — each double-unwrap <c>context.Message.Message</c> and
/// open a log scope carrying BOTH the manual <see cref="CorrelationKeys.LogScope"/> key (== the inner
/// message's CorrelationId) AND the 5 <see cref="ExecutionLogScope"/> execution-id keys.
/// <para>
/// This is the Phase-35 DELTA vs <c>ConsoleExecutionScopeFilterTests</c>: there the bus-wide filter scopes
/// the 5 exec ids but NO CorrelationId (Case A asserts its ABSENCE). Here — because a <see cref="Fault{T}"/>
/// envelope is NEITHER <see cref="IExecutionCorrelated"/> NOR <see cref="ICorrelated"/>, so the bus-wide
/// correlation filter cannot recover the propagated id and falls back to a fresh Guid — each consumer MUST
/// restore the CorrelationId itself. These tests PROVE it (the mandatory manual-CorrelationId-scope
/// correctness point, T-35-06).
/// </para>
/// The capturing-provider rig is cloned from <c>ConsoleExecutionScopeFilterTests.cs:65-113</c>. The
/// <see cref="Fault{T}"/> is published via a MassTransit message initializer (anonymous
/// <c>new { Message = inner }</c>) — a hand-rolled envelope does not satisfy the framework
/// <see cref="Fault{T}"/> interface (the proven Phase-33 spike approach). No RealStack trait — runs in the
/// fast hermetic suite.
/// </summary>
public sealed class KeeperFaultConsumerScopeTests
{
    // ── scope-capturing logger double (no new package) — cloned verbatim from the filter test ──────────
    private sealed class CapturingProvider : ILoggerProvider
    {
        public readonly List<Dictionary<string, object>> Scopes = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Scopes);
        public void Dispose() { }

        private sealed class CapturingLogger(List<Dictionary<string, object>> scopes) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull
            {
                if (state is IEnumerable<KeyValuePair<string, object>> kvps)
                    scopes.Add(new Dictionary<string, object>(kvps));
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
    }

    /// <summary>Builds an in-memory harness wiring the SUT fault consumer(s), with the capturing provider attached.</summary>
    private static ServiceProvider BuildHarness(CapturingProvider capturing, Action<IBusRegistrationConfigurator> addConsumers) =>
        new ServiceCollection()
            .AddLogging(b => b.AddProvider(capturing))
            .AddMassTransitTestHarness(x =>
            {
                addConsumers(x);
                x.UsingInMemory((ctx, cfg) => cfg.ConfigureEndpoints(ctx));
            })
            .BuildServiceProvider(true);

    /// <summary>The CorrelationId scope captured at consume time (the manual scope the consumer opens).</summary>
    private static Dictionary<string, object> CorrelationScope(CapturingProvider capturing) =>
        capturing.Scopes.FirstOrDefault(s => s.ContainsKey(CorrelationKeys.LogScope))
        ?? new Dictionary<string, object>();

    /// <summary>The execution-id scope captured at consume time (the BuildState scope).</summary>
    private static Dictionary<string, object> ExecutionScope(CapturingProvider capturing) =>
        capturing.Scopes.FirstOrDefault(s =>
            s.ContainsKey(ExecutionLogScope.WorkflowId) || s.ContainsKey(ExecutionLogScope.StepId)
            || s.ContainsKey(ExecutionLogScope.ProcessorId) || s.ContainsKey(ExecutionLogScope.ExecutionId)
            || s.ContainsKey(ExecutionLogScope.EntryId))
        ?? new Dictionary<string, object>();

    [Fact]
    public async Task FaultEntryStepDispatch_Scope_Carries_CorrelationId_And_Five_Exec_Ids()
    {
        var ct = TestContext.Current.CancellationToken;
        var capturing = new CapturingProvider();

        var correlationId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var processorId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var entryId = Guid.NewGuid().ToString("D");

        var inner = new EntryStepDispatch(workflowId, stepId, processorId, "payload")
        {
            CorrelationId = correlationId,
            ExecutionId = executionId,
            EntryId = entryId,
            H = "abc123",
        };

        await using var provider = BuildHarness(capturing, x => x.AddConsumer<FaultEntryStepDispatchConsumer>());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            // Publish via message initializer — the framework materializes a real Fault<EntryStepDispatch>.
            await harness.Bus.Publish<Fault<EntryStepDispatch>>(new { Message = inner }, ct);
            Assert.True(await harness.Consumed.Any<Fault<EntryStepDispatch>>(ct));

            // The manual CorrelationId scope == inner.CorrelationId (the Phase-35 delta — bus-wide filter
            // cannot recover it from a Fault<T> envelope).
            var corr = CorrelationScope(capturing);
            Assert.Equal(correlationId.ToString(), corr[CorrelationKeys.LogScope]);

            // The 5-id execution scope via BuildState.
            var scope = ExecutionScope(capturing);
            Assert.Equal(5, scope.Count);
            Assert.Equal(workflowId.ToString(), scope[ExecutionLogScope.WorkflowId]);
            Assert.Equal(stepId.ToString(), scope[ExecutionLogScope.StepId]);
            Assert.Equal(processorId.ToString(), scope[ExecutionLogScope.ProcessorId]);
            Assert.Equal(executionId.ToString(), scope[ExecutionLogScope.ExecutionId]);
            Assert.Equal(entryId, scope[ExecutionLogScope.EntryId]);   // string verbatim
        }
        finally
        {
            await harness.Stop(ct);
        }
    }

    [Fact]
    public async Task FaultExecutionResult_Scope_Carries_CorrelationId_And_Five_Exec_Ids()
    {
        var ct = TestContext.Current.CancellationToken;
        var capturing = new CapturingProvider();

        var correlationId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var processorId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var entryId = Guid.NewGuid().ToString("D");

        var inner = new ExecutionResult(workflowId, stepId, processorId, StepOutcome.Failed)
        {
            CorrelationId = correlationId,
            ExecutionId = executionId,
            EntryId = entryId,
            H = "def456",
        };

        await using var provider = BuildHarness(capturing, x => x.AddConsumer<FaultExecutionResultConsumer>());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish<Fault<ExecutionResult>>(new { Message = inner }, ct);
            Assert.True(await harness.Consumed.Any<Fault<ExecutionResult>>(ct));

            var corr = CorrelationScope(capturing);
            Assert.Equal(correlationId.ToString(), corr[CorrelationKeys.LogScope]);

            var scope = ExecutionScope(capturing);
            Assert.Equal(5, scope.Count);
            Assert.Equal(workflowId.ToString(), scope[ExecutionLogScope.WorkflowId]);
            Assert.Equal(stepId.ToString(), scope[ExecutionLogScope.StepId]);
            Assert.Equal(processorId.ToString(), scope[ExecutionLogScope.ProcessorId]);
            Assert.Equal(executionId.ToString(), scope[ExecutionLogScope.ExecutionId]);
            Assert.Equal(entryId, scope[ExecutionLogScope.EntryId]);
        }
        finally
        {
            await harness.Stop(ct);
        }
    }

    [Fact]
    public async Task FaultEntryStepDispatch_GuidEmpty_And_EmptyEntryId_Are_Skipped_But_CorrelationId_Present()
    {
        var ct = TestContext.Current.CancellationToken;
        var capturing = new CapturingProvider();

        var correlationId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var processorId = Guid.NewGuid();

        // ExecutionId == Guid.Empty + EntryId == "" → BOTH skipped by BuildState; the 3 non-empty Guids stay.
        var inner = new EntryStepDispatch(workflowId, stepId, processorId, "payload")
        {
            CorrelationId = correlationId,
            ExecutionId = Guid.Empty,
            EntryId = "",
            H = "xyz789",
        };

        await using var provider = BuildHarness(capturing, x => x.AddConsumer<FaultEntryStepDispatchConsumer>());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish<Fault<EntryStepDispatch>>(new { Message = inner }, ct);
            Assert.True(await harness.Consumed.Any<Fault<EntryStepDispatch>>(ct));

            // CorrelationId still present (it is never subject to the BuildState skip rules).
            var corr = CorrelationScope(capturing);
            Assert.Equal(correlationId.ToString(), corr[CorrelationKeys.LogScope]);

            var scope = ExecutionScope(capturing);
            Assert.False(scope.ContainsKey(ExecutionLogScope.ExecutionId));   // Guid.Empty → absent
            Assert.False(scope.ContainsKey(ExecutionLogScope.EntryId));       // "" → absent
            Assert.Equal(3, scope.Count);                                     // the 3 non-empty Guids present
            Assert.Equal(workflowId.ToString(), scope[ExecutionLogScope.WorkflowId]);
            Assert.Equal(stepId.ToString(), scope[ExecutionLogScope.StepId]);
            Assert.Equal(processorId.ToString(), scope[ExecutionLogScope.ProcessorId]);
        }
        finally
        {
            await harness.Stop(ct);
        }
    }
}
