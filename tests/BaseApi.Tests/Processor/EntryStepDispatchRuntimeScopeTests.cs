using BaseConsole.Core.Messaging;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Processing;
using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using Xunit;
using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;

namespace BaseApi.Tests.Processor;

/// <summary>
/// LOG-04 (real-stack gap): proves the <see cref="EntryStepDispatchConsumer"/> Completed path emits a
/// LogRecord that — once the bus-wide <see cref="InboundExecutionScopeConsumeFilter{T}"/> outer scope is
/// applied — carries ALL FIVE execution-id scope keys (WorkflowId/StepId/ProcessorId from the inbound
/// dispatch via the outer filter, plus the minted ExecutionId/EntryId from the consumer's nested scope).
/// <para>
/// Crucially, this binds the consumer the SAME way production does (<c>ProcessorStartupOrchestrator.cs:149</c>):
/// via <see cref="IReceiveEndpointConnector.ConnectReceiveEndpoint"/> at RUNTIME (after bus start), NOT via
/// <c>AddConsumer</c> + bus-configured endpoint. The bus-factory <c>UseConsumeFilter</c> is registered
/// exactly as <c>AddBaseConsoleMessaging</c> registers it. This closes the 29-02 gap — that test only proved
/// the filter for a bus-CONFIGURED consumer, never a runtime-connected one. If the bus-factory consume
/// filter does NOT reach the runtime endpoint, the WorkflowId/StepId/ProcessorId keys would be ABSENT and
/// this test fails — making the filter-reach question empirically answered rather than assumed.
/// </para>
/// </summary>
public sealed class EntryStepDispatchRuntimeScopeTests
{
    // ── log-record capturing provider: records each LogRecord's flattened scope state (mirrors how OTel
    //    IncludeScopes flattens scope dictionaries onto the record) so the test asserts on the EMITTED line,
    //    not merely on an opened scope. No new NuGet package. ──
    //    It implements ISupportExternalScope so MEL's LoggerFactory injects ONE shared
    //    IExternalScopeProvider across ALL loggers (the SAME mechanism OTel IncludeScopes uses) —
    //    so a scope opened by the bus-wide filter's ILogger is visible when the consumer's ILogger
    //    emits the line, exactly as in production. ──
    private sealed class RecordCapturingProvider : ILoggerProvider, ISupportExternalScope
    {
        public readonly List<(Dictionary<string, object> Scopes, string Message)> Records = new();
        private readonly object _gate = new();
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
        public ILogger CreateLogger(string categoryName) => new RecordLogger(this);
        public void Dispose() { }

        private void Capture(string message)
        {
            // Flatten every active scope from the SHARED provider, outer→inner so inner overrides on
            // collision — the same flattening OTel IncludeScopes performs onto the LogRecord's attributes.
            var flat = new Dictionary<string, object>();
            _scopes.ForEachScope((state, _) =>
            {
                if (state is IEnumerable<KeyValuePair<string, object>> kvps)
                    foreach (var kvp in kvps)
                        flat[kvp.Key] = kvp.Value;
            }, (object?)null);
            lock (_gate) Records.Add((flat, message));
        }

        private sealed class RecordLogger(RecordCapturingProvider owner) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull
                => owner._scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => owner.Capture(formatter(state, exception));
        }
    }

    /// <summary>Records the StepCompleted the consumer Sends to queue:orchestrator-result (D-03).</summary>
    private sealed class RecordingResultConsumer(List<StepCompleted> received) : IConsumer<StepCompleted>
    {
        public Task Consume(ConsumeContext<StepCompleted> context)
        {
            lock (received) received.Add(context.Message);
            return Task.CompletedTask;
        }
    }

    /// <summary>A non-faulting Redis multiplexer whose StringSetAsync (output write) is a no-op success.</summary>
    private static IConnectionMultiplexer WritableRedis()
    {
        var db = Substitute.For<IDatabase>();   // unstubbed StringSetAsync returns a completed Task (no-op)
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    [Fact]
    public async Task RuntimeConnected_Completed_Path_Emits_Log_Carrying_All_Five_Scope_Keys()
    {
        var ct = TestContext.Current.CancellationToken;
        var capturing = new RecordCapturingProvider();
        var received = new List<StepCompleted>();

        var workflowId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var processorId = Guid.NewGuid();

        // No-input source processor (InputDefinition null + entryId Guid.Empty) → skip the L2 READ and
        // exercise the Completed write/send path. One result passes a null OutputDefinition (skip validate).
        var processorContext = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results("out"));

        await using var provider = new ServiceCollection()
            .AddLogging(b => b.AddProvider(capturing))
            // The collaborators EntryStepDispatchConsumer needs from DI when bound via ConfigureConsumer.
            .AddSingleton<IConnectionMultiplexer>(_ => WritableRedis())
            .AddSingleton<IProcessorContext>(processorContext)
            .AddSingleton<BaseProcessorBase>(processor)
            .AddSingleton<IOptions<ProcessorLivenessOptions>>(DispatchTestKit.Options(300))
            // METRIC-05: the consumer now depends on ProcessorMetrics (real IMeterFactory; no-op in-test).
            .AddSingleton(DispatchTestKit.Metrics())
            // The bus-wide InboundCorrelationConsumeFilter needs the accessor (as AddBaseConsoleMessaging registers it).
            .AddSingleton<ICorrelationAccessor, AsyncLocalCorrelationAccessor>()
            .AddSingleton(new RecordingResultConsumer(received))
            .AddMassTransitTestHarness(x =>
            {
                // The result endpoint so the consumer's Send to queue:orchestrator-result lands somewhere.
                x.AddConsumer<RecordingResultConsumer>();
                // The dispatch consumer is NOT bus-configured here — it is connected at RUNTIME below,
                // exactly like ProcessorStartupOrchestrator.cs:149. Register it for DI resolution only,
                // suppressing its conventional endpoint so the ONLY path it runs through is the runtime
                // ConnectReceiveEndpoint below (true production mirror).
                x.AddConsumer<EntryStepDispatchConsumer>()
                    .Endpoint(e => e.ConfigureConsumeTopology = false);
                x.UsingInMemory((busCtx, cfg) =>
                {
                    // Mirror AddBaseConsoleMessaging: both inbound filters bus-wide (open-generic).
                    cfg.UseConsumeFilter(typeof(InboundCorrelationConsumeFilter<>), busCtx);
                    cfg.UseConsumeFilter(typeof(InboundExecutionScopeConsumeFilter<>), busCtx);
                    cfg.ReceiveEndpoint(OrchestratorQueues.Result,
                        e => e.ConfigureConsumer<RecordingResultConsumer>(busCtx));
                    // Deliberately DO NOT call ConfigureEndpoints — the dispatch endpoint is runtime-connected.
                });
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            // RUNTIME bind — the exact production shape (ProcessorStartupOrchestrator.cs:149-153):
            // IReceiveEndpointConnector.ConnectReceiveEndpoint after the bus is started.
            var connector = provider.GetRequiredService<IReceiveEndpointConnector>();
            var bus = provider.GetRequiredService<IBus>();
            var registrationContext = provider.GetRequiredService<IBusRegistrationContext>();
            var queueName = Guid.NewGuid().ToString("D");
            var handle = connector.ConnectReceiveEndpoint(queueName, (busCtx, cfg) =>
            {
                cfg.UseMessageRetry(r => r.Immediate(3));
                cfg.ConfigureConsumer<EntryStepDispatchConsumer>(registrationContext);
            });
            await handle.Ready;

            // Send a dispatch carrying the three inbound ids (ExecutionId Guid.Empty / EntryId Guid.Empty —
            // the source-step sentinel; the real Guid EntryId is minted by the consumer per result, D-04/D-06a).
            var dispatch = new EntryStepDispatch(workflowId, stepId, processorId, "{\"cfg\":1}")
            {
                CorrelationId = Guid.NewGuid(),
                EntryId = Guid.Empty,
            };
            var endpoint = await bus.GetSendEndpoint(new Uri($"queue:{queueName}"));
            await endpoint.Send(dispatch, ct);

            // The dispatch must be consumed by the runtime-connected endpoint (proves Send routed to it).
            Assert.True(await harness.Consumed.Any<EntryStepDispatch>(ct), "dispatch was not consumed");

            // Surface any consumer fault for diagnosis.
            var dispatchCtx = await harness.Consumed.SelectAsync<EntryStepDispatch>(ct).FirstOrDefault();
            Assert.True(dispatchCtx is not null && dispatchCtx.Exception is null,
                "dispatch consumer faulted: " + dispatchCtx?.Exception);

            // The round-trip must produce the Completed StepCompleted (proves the Completed path ran).
            Assert.True(await harness.Consumed.Any<StepCompleted>(ct), "no StepCompleted produced");

            // Wait until the result reaches the recording consumer (proves the Completed path ran).
            for (var i = 0; i < 100; i++)
            {
                lock (received)
                    if (received.Count > 0) break;
                await Task.Delay(50, ct);
            }
            StepCompleted sent;
            lock (received)
            {
                Assert.NotEmpty(received);
                sent = received[0];
            }

            // The Completed-path LogRecord: the one carrying the minted ExecutionId + EntryId (nested scope).
            // It MUST also carry WorkflowId/StepId/ProcessorId from the outer execution-scope filter — proving
            // the bus-wide UseConsumeFilter reaches the RUNTIME-connected endpoint.
            (Dictionary<string, object> Scopes, string Message)? completed = null;
            for (var i = 0; i < 50 && completed is null; i++)
            {
                completed = capturing.Records
                    .Where(r => r.Scopes.ContainsKey(ExecutionLogScope.ExecutionId)
                                && r.Scopes.ContainsKey(ExecutionLogScope.EntryId))
                    .Cast<(Dictionary<string, object>, string)?>()
                    .FirstOrDefault();
                if (completed is null) await Task.Delay(50, ct);
            }

            Assert.NotNull(completed);
            var completedRecord = completed!.Value.Scopes;

            // All five execution-id keys present on the emitted Completed-path line.
            Assert.Equal(workflowId.ToString(), completedRecord[ExecutionLogScope.WorkflowId]);
            Assert.Equal(stepId.ToString(), completedRecord[ExecutionLogScope.StepId]);
            Assert.Equal(processorId.ToString(), completedRecord[ExecutionLogScope.ProcessorId]);
            Assert.True(completedRecord.ContainsKey(ExecutionLogScope.ExecutionId));
            Assert.True(completedRecord.ContainsKey(ExecutionLogScope.EntryId));

            // D-03/D-06a: the nested scope carries the MINTED lineage ExecutionId + the minted Guid EntryId
            // (the real L2 data key) — straight-through, so the scoped ids EQUAL the sent StepCompleted's ids.
            Assert.NotEqual(Guid.Empty, sent.ExecutionId);
            Assert.NotEqual(Guid.Empty, sent.EntryId);
            Assert.True(Guid.TryParse((string)completedRecord[ExecutionLogScope.ExecutionId], out var scopedExec)
                && scopedExec != Guid.Empty);
            Assert.Equal(sent.EntryId.ToString(), completedRecord[ExecutionLogScope.EntryId]);
            Assert.Equal(sent.ExecutionId.ToString(), completedRecord[ExecutionLogScope.ExecutionId]);

            // T-18-04: the execution ids are reported ONLY as scope values — never interpolated into the
            // rendered message text. The line references CorrelationId (template), not any execution id.
            var message = completed.Value.Message;
            Assert.DoesNotContain(workflowId.ToString(), message);
            Assert.DoesNotContain(stepId.ToString(), message);
            Assert.DoesNotContain(processorId.ToString(), message);
            Assert.DoesNotContain((string)completedRecord[ExecutionLogScope.ExecutionId], message);
            Assert.DoesNotContain((string)completedRecord[ExecutionLogScope.EntryId], message);
        }
        finally
        {
            await harness.Stop(ct);
        }
    }
}
