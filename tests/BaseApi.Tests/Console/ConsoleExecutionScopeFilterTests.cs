using BaseConsole.Core.Messaging;
using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BaseApi.Tests.Console;

/// <summary>
/// LOG-02 / LOG-03: the bus-wide <see cref="InboundExecutionScopeConsumeFilter{T}"/> opens a MEL log
/// scope carrying the five execution ids under the <see cref="ExecutionLogScope"/> keys for an
/// <see cref="IExecutionCorrelated"/> message, skipping any <c>Guid.Empty</c> value (D-03), and is a
/// no-op for non-IExecutionCorrelated messages. It NEVER scopes CorrelationId (D-01 — that stays
/// owned by the unchanged correlation filter).
/// <list type="bullet">
///   <item>(a) all five ids non-empty → the captured scope dict has exactly the five keys with the
///         expected <c>.ToString()</c> values; NO "CorrelationId" key from THIS filter.</item>
///   <item>(b) one id == Guid.Empty (ExecutionId) → that key is ABSENT, the other four present.</item>
///   <item>(c) non-IExecutionCorrelated → consumed without throwing; no execution-scope keys.</item>
/// </list>
/// The execution filter has NO <c>ICorrelationAccessor</c> (D-01), so this test captures the scope
/// state directly via a tiny scope-capturing <see cref="ILoggerProvider"/> double (D-07 / RESEARCH A2)
/// rather than through the accessor the correlation test uses. No new NuGet package.
/// </summary>
public sealed class ConsoleExecutionScopeFilterTests
{
    /// <summary>A minimal <see cref="IExecutionCorrelated"/> probe message — carries the five execution ids.</summary>
    public sealed record ExecProbeMessage(
        Guid CorrelationId, Guid WorkflowId, Guid StepId, Guid ProcessorId, Guid ExecutionId, string EntryId)
        : IExecutionCorrelated;

    /// <summary>A non-IExecutionCorrelated message — the filter must pass it through untouched (case c).</summary>
    public sealed record PlainExecMessage(string Text);

    /// <summary>
    /// Probe consumer: logs ONCE inside Consume so the inbound filter's scope is open when the
    /// capturing logger records the scope state (the filter wraps <c>next.Send</c> in the scope, so the
    /// consumer body runs INSIDE the scope).
    /// </summary>
    public sealed class ExecProbeConsumer(ILogger<ExecProbeConsumer> logger) : IConsumer<ExecProbeMessage>
    {
        public Task Consume(ConsumeContext<ExecProbeMessage> context)
        {
            logger.LogInformation("exec-probe consumed");
            return Task.CompletedTask;
        }
    }

    /// <summary>Consumer for the non-correlated message — must run to completion (filter tolerates it).</summary>
    public sealed class PlainExecConsumer(ILogger<PlainExecConsumer> logger) : IConsumer<PlainExecMessage>
    {
        public Task Consume(ConsumeContext<PlainExecMessage> context)
        {
            logger.LogInformation("plain consumed");
            return Task.CompletedTask;
        }
    }

    // ── scope-capturing logger double (~no new package) ──────────────────────────────────────────
    // Records every BeginScope state that is an IEnumerable<KeyValuePair<string,object>> (which is the
    // shape a Dictionary<string,object> presents to MEL), so the test can assert on the execution-id keys.

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

    /// <summary>Builds an in-memory harness wiring the SUT execution filter, with the capturing provider attached.</summary>
    private static ServiceProvider BuildHarness(CapturingProvider capturing, Action<IBusRegistrationConfigurator> addConsumers) =>
        new ServiceCollection()
            .AddLogging(b => b.AddProvider(capturing))
            .AddMassTransitTestHarness(x =>
            {
                addConsumers(x);
                x.UsingInMemory((ctx, cfg) =>
                {
                    cfg.UseConsumeFilter(typeof(InboundExecutionScopeConsumeFilter<>), ctx);  // SUT
                    cfg.ConfigureEndpoints(ctx);
                });
            })
            .BuildServiceProvider(true);

    /// <summary>The execution-id scope captured at consume time (the one carrying any ExecutionLogScope key).</summary>
    private static Dictionary<string, object> ExecutionScope(CapturingProvider capturing) =>
        capturing.Scopes.FirstOrDefault(s =>
            s.ContainsKey(ExecutionLogScope.WorkflowId) || s.ContainsKey(ExecutionLogScope.StepId)
            || s.ContainsKey(ExecutionLogScope.ProcessorId) || s.ContainsKey(ExecutionLogScope.ExecutionId)
            || s.ContainsKey(ExecutionLogScope.EntryId))
        ?? new Dictionary<string, object>();

    [Fact]
    public async Task Case_A_All_Five_Ids_Scoped_With_Expected_Values_And_No_CorrelationId_Key()
    {
        var ct = TestContext.Current.CancellationToken;
        var capturing = new CapturingProvider();

        var workflowId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var processorId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var entryId = Guid.NewGuid().ToString("D");

        await using var provider = BuildHarness(capturing, x => x.AddConsumer<ExecProbeConsumer>());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(
                new ExecProbeMessage(Guid.NewGuid(), workflowId, stepId, processorId, executionId, entryId), ct);
            Assert.True(await harness.Consumed.Any<ExecProbeMessage>(ct));

            var scope = ExecutionScope(capturing);
            // Exactly the five execution-id keys — and NO CorrelationId key from THIS filter (D-01).
            Assert.Equal(5, scope.Count);
            Assert.Equal(workflowId.ToString(), scope[ExecutionLogScope.WorkflowId]);
            Assert.Equal(stepId.ToString(), scope[ExecutionLogScope.StepId]);
            Assert.Equal(processorId.ToString(), scope[ExecutionLogScope.ProcessorId]);
            Assert.Equal(executionId.ToString(), scope[ExecutionLogScope.ExecutionId]);
            Assert.Equal(entryId, scope[ExecutionLogScope.EntryId]);   // string EntryId stored verbatim (no .ToString())
            Assert.False(scope.ContainsKey("CorrelationId"));   // D-01: execution filter never scopes CorrelationId
        }
        finally
        {
            await harness.Stop(ct);
        }
    }

    [Fact]
    public async Task Case_B_Guid_Empty_Id_Is_Skipped()
    {
        var ct = TestContext.Current.CancellationToken;
        var capturing = new CapturingProvider();

        var workflowId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var processorId = Guid.NewGuid();
        var entryId = Guid.NewGuid().ToString("D");

        await using var provider = BuildHarness(capturing, x => x.AddConsumer<ExecProbeConsumer>());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            // ExecutionId == Guid.Empty → its scope key must be ABSENT (D-03 / LOG-03). A non-empty
            // string EntryId IS present (the EntryId empty-string skip is proven in Case D below).
            await harness.Bus.Publish(
                new ExecProbeMessage(Guid.NewGuid(), workflowId, stepId, processorId, Guid.Empty, entryId), ct);
            Assert.True(await harness.Consumed.Any<ExecProbeMessage>(ct));

            var scope = ExecutionScope(capturing);
            Assert.False(scope.ContainsKey(ExecutionLogScope.ExecutionId));   // Guid.Empty → no entry
            Assert.Equal(4, scope.Count);                                     // the other four present
            Assert.Equal(workflowId.ToString(), scope[ExecutionLogScope.WorkflowId]);
            Assert.Equal(stepId.ToString(), scope[ExecutionLogScope.StepId]);
            Assert.Equal(processorId.ToString(), scope[ExecutionLogScope.ProcessorId]);
            Assert.Equal(entryId, scope[ExecutionLogScope.EntryId]);
        }
        finally
        {
            await harness.Stop(ct);
        }
    }

    [Fact]
    public async Task Case_C_Non_ExecutionCorrelated_Passes_Through_With_No_Scope()
    {
        var ct = TestContext.Current.CancellationToken;
        var capturing = new CapturingProvider();

        await using var provider = BuildHarness(capturing, x => x.AddConsumer<PlainExecConsumer>());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            // A message that does NOT implement IExecutionCorrelated must be consumed without throwing
            // and must NOT open any execution-scope (D-03 pass-through no-op).
            await harness.Bus.Publish(new PlainExecMessage("no execution ids here"), ct);
            Assert.True(await harness.Consumed.Any<PlainExecMessage>(ct));

            var scope = ExecutionScope(capturing);
            Assert.Empty(scope);   // no execution-scope keys captured
        }
        finally
        {
            await harness.Stop(ct);
        }
    }

    [Fact]
    public async Task Case_D_Empty_String_EntryId_Is_Skipped()
    {
        var ct = TestContext.Current.CancellationToken;
        var capturing = new CapturingProvider();

        var workflowId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var processorId = Guid.NewGuid();
        var executionId = Guid.NewGuid();

        await using var provider = BuildHarness(capturing, x => x.AddConsumer<ExecProbeConsumer>());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            // EntryId == "" (the new empty sentinel, was Guid.Empty) → its scope key must be ABSENT
            // (Phase 31 string guard: !string.IsNullOrEmpty(ec.EntryId)). The other four ids are present.
            await harness.Bus.Publish(
                new ExecProbeMessage(Guid.NewGuid(), workflowId, stepId, processorId, executionId, ""), ct);
            Assert.True(await harness.Consumed.Any<ExecProbeMessage>(ct));

            var scope = ExecutionScope(capturing);
            Assert.False(scope.ContainsKey(ExecutionLogScope.EntryId));   // "" → no entry
            Assert.Equal(4, scope.Count);                                 // the other four present
            Assert.Equal(executionId.ToString(), scope[ExecutionLogScope.ExecutionId]);
        }
        finally
        {
            await harness.Stop(ct);
        }
    }
}
