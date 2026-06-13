using BaseConsole.Core.Health;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Startup;
using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// EXEC-01 bind-sequencing facts (D-02/D-03 — the load-bearing order) — the ONE test with no verbatim
/// in-repo analog (RESEARCH Pitfall 6). Drives <see cref="ProcessorStartupOrchestrator"/> to completion
/// with a fake <see cref="IReceiveEndpointConnector"/> and proves the runtime bind happens, its
/// <c>handle.Ready</c> is awaited, AND <c>MarkHealthy</c> is recorded STRICTLY AFTER the Ready-await —
/// i.e. the ordered event log is exactly <c>["connect", "ready", "markhealthy"]</c>. The queue name is
/// asserted to be the bare <c>Id.ToString("D")</c> (no <c>queue:</c> prefix). The real
/// <c>ConnectReceiveEndpoint</c>-against-RabbitMQ + Healthy-after-bind proof is deferred to the Phase 28
/// E2E (TEST-01); this proves the SEQUENCING only.
/// </summary>
public sealed class DispatchBindSequenceFacts
{
    /// <summary>
    /// A hand-rolled fake <see cref="IReceiveEndpointConnector"/> that records the queue name and the
    /// "connect"/"ready" events into a shared ordered log. <c>handle.Ready</c> resolves to a substituted
    /// <see cref="ReceiveEndpointReady"/> via a continuation that appends "ready" — so the await ordering
    /// relative to <c>MarkHealthy</c> is genuinely observable, not merely trusted from source order.
    /// </summary>
    private sealed class RecordingConnector(List<string> log) : IReceiveEndpointConnector
    {
        public string? BoundQueueName { get; private set; }

        public HostReceiveEndpointHandle ConnectReceiveEndpoint(string queueName, Action<IBusRegistrationContext, IReceiveEndpointConfigurator> configure)
        {
            BoundQueueName = queueName;
            log.Add("connect");
            return new RecordingHandle(log);
        }

        public HostReceiveEndpointHandle ConnectReceiveEndpoint(IEndpointDefinition definition, IEndpointNameFormatter? endpointNameFormatter, Action<IBusRegistrationContext, IReceiveEndpointConfigurator> configure)
            => throw new NotSupportedException("The orchestrator binds by bare queue name (D-02).");
    }

    /// <summary>
    /// A fake <see cref="HostReceiveEndpointHandle"/> whose <see cref="Ready"/> task appends "ready" to
    /// the shared log the moment its continuation runs — so awaiting it is observable in the event order.
    /// </summary>
    private sealed class RecordingHandle : HostReceiveEndpointHandle
    {
        public RecordingHandle(List<string> log)
        {
            var ready = Substitute.For<ReceiveEndpointReady>();
            Ready = Task.FromResult(ready).ContinueWith(t => { log.Add("ready"); return t.Result; });
        }

        public IReceiveEndpoint ReceiveEndpoint => Substitute.For<IReceiveEndpoint>();
        public Task<ReceiveEndpointReady> Ready { get; }
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// A <see cref="ProcessorContext"/> wrapper that appends "markhealthy" to the shared log the first
    /// time <see cref="MarkHealthy"/> is called, then defers to the real context so the orchestrator's
    /// completion gate (<c>IsHealthy</c>) flips for real.
    /// </summary>
    private sealed class RecordingContext(List<string> log) : IProcessorContext
    {
        private readonly ProcessorContext _inner = new();

        public Guid? Id => _inner.Id;
        public Guid? InputSchemaId => _inner.InputSchemaId;
        public Guid? OutputSchemaId => _inner.OutputSchemaId;
        public Guid? ConfigSchemaId => _inner.ConfigSchemaId;
        public string? Name => _inner.Name;
        public string? Version => _inner.Version;
        public string? InputDefinition => _inner.InputDefinition;
        public string? OutputDefinition => _inner.OutputDefinition;
        // Phase 57 Gate A (CFG-03) — proxy the new ConfigDefinition member so the orchestrator's Gate A
        // call site reads the fetched config definition through the recording context. RED until Plan 02/03
        // adds ConfigDefinition to ProcessorContext/IProcessorContext.
        public string? ConfigDefinition => _inner.ConfigDefinition;
        public bool IsHealthy => _inner.IsHealthy;
        public Task WhenHealthy => _inner.WhenHealthy;

        public void SetIdentity(ProcessorIdentityFound identity) => _inner.SetIdentity(identity);
        public void SetDefinition(Guid schemaId, string definition) => _inner.SetDefinition(schemaId, definition);

        public void MarkHealthy()
        {
            log.Add("markhealthy");
            _inner.MarkHealthy();
        }
    }

    [Fact]
    public async Task Connect_Then_Ready_Then_MarkHealthy_In_Order()
    {
        var (log, connector, foundId) = await DriveOrchestratorToHealthy();

        // The bind happened, its Ready was awaited, and MarkHealthy ran STRICTLY after — in that order.
        Assert.Equal(new[] { "connect", "ready", "markhealthy" }, log);
        Assert.NotNull(connector.BoundQueueName);
    }

    [Fact]
    public async Task Binds_Bare_IdFormat_QueueName()
    {
        var (_, connector, foundId) = await DriveOrchestratorToHealthy();

        // The bound queue name is the bare Id "D" format — NO "queue:" prefix (the scheme is sender-only).
        Assert.Equal(foundId.ToString("D"), connector.BoundQueueName);
        Assert.DoesNotContain("queue:", connector.BoundQueueName!);
    }

    /// <summary>
    /// A list-recording <see cref="ILogger{T}"/> that captures every entry's level + rendered message so
    /// the Gate A clash fact can assert exactly one <see cref="LogLevel.Error"/> mentioning the processor
    /// id + config schema id + the clash property (D-10 single structured error log).
    /// </summary>
    private sealed class CapturingLogger : ILogger<ProcessorStartupOrchestrator>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>Identity responder that replies Found immediately with the caller-configured identity.</summary>
    private sealed class FixedIdentityResponder(ProcessorIdentityFound identity) : IConsumer<GetProcessorBySourceHash>
    {
        public Task Consume(ConsumeContext<GetProcessorBySourceHash> context)
            => context.RespondAsync(identity);
    }

    /// <summary>Schema responder that replies Found with a per-Id definition from the supplied map.</summary>
    private sealed class MappedSchemaResponder(IReadOnlyDictionary<Guid, string> definitions) : IConsumer<GetSchemaDefinition>
    {
        public Task Consume(ConsumeContext<GetSchemaDefinition> context)
        {
            var id = context.Message.SchemaId;
            var def = definitions.TryGetValue(id, out var d) ? d : "{\"type\":\"object\"}";
            return context.RespondAsync(new SchemaDefinitionFound(def));
        }
    }

    /// <summary>
    /// CFG-06 — a Gate A clash WITHHOLDS MarkHealthy + the bind. The bind/Ready/MarkHealthy ordered log is
    /// EMPTY (no "connect"/"ready"/"markhealthy" — none of the completion block ran), yet
    /// <c>gate.MarkReady</c> DID fire (<c>gate.IsReady</c> — readiness green, NO K8s crash-loop, Pitfall 1).
    /// The queue is never bound, the context never latches Healthy, and exactly one Error log records the clash.
    /// </summary>
    [Fact]
    public async Task GateA_Clash_Withholds_MarkHealthy_And_Bind()
    {
        var configId = Guid.NewGuid();
        // A config-schema definition whose "Mode" prop is a string-enum CLASHING the processor's TConfig
        // CLR enum (rule-table row #13 — confirmed CLASH by the Wave-0 spike).
        var clashingDef =
            "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\",\"properties\":{\"Mode\":{\"enum\":[\"A\",\"B\"]}}}";

        var (log, connector, context, logger, gate) = await DriveOrchestratorWithConfigSchema(
            configSchemaId: configId,
            definitions: new Dictionary<Guid, string> { [configId] = clashingDef });

        // The completion block never ran — NO connect/ready/markhealthy. (The "ready" log entry is the
        // RecordingHandle's await-Ready continuation, which only fires on the bind path; gate.MarkReady is a
        // separate StartupGate latch asserted directly below.)
        Assert.Empty(log);
        // gate.MarkReady DID fire — readiness goes green so K8s does NOT crash-loop (D-09, Pitfall 1, T-57-05).
        Assert.True(gate.IsReady);
        Assert.Null(connector.BoundQueueName);
        Assert.False(context.IsHealthy);

        // Exactly one Error log mentioning the processor id + config schema id + the clash property (D-10).
        var errors = logger.Entries.FindAll(e => e.Level == LogLevel.Error);
        Assert.Single(errors);
    }

    /// <summary>
    /// CFG-07 — a NULL ConfigSchemaId SKIPS Gate A entirely: the processor binds + reaches Healthy on the
    /// normal pass path (ordered log <c>["connect","ready","markhealthy"]</c>). RED until Gate A wiring lands.
    /// </summary>
    [Fact]
    public async Task GateA_NullConfigSchemaId_Skips_And_Reaches_Healthy()
    {
        var (log, connector, context, _, _) = await DriveOrchestratorWithConfigSchema(
            configSchemaId: null,
            definitions: new Dictionary<Guid, string>());

        Assert.Equal(new[] { "connect", "ready", "markhealthy" }, log);
        Assert.NotNull(connector.BoundQueueName);
        Assert.True(context.IsHealthy);
    }

    /// <summary>
    /// Builds an in-memory harness whose identity responds Found immediately with the supplied
    /// <paramref name="configSchemaId"/> (input/output null), and whose schema responder returns the
    /// definitions in <paramref name="definitions"/>. Drives the orchestrator with the recording connector
    /// + recording context + a <see cref="CapturingLogger"/> and returns the ordered event log, the
    /// connector, the recording context, and the logger so Gate A pass/clash/skip is fully observable.
    /// </summary>
    private static async Task<(List<string> Log, RecordingConnector Connector, RecordingContext Context, CapturingLogger Logger, StartupGate Gate)>
        DriveOrchestratorWithConfigSchema(Guid? configSchemaId, IReadOnlyDictionary<Guid, string> definitions)
    {
        var foundId = Guid.NewGuid();
        var identity = new ProcessorIdentityFound(
            foundId, InputSchemaId: null, OutputSchemaId: null, ConfigSchemaId: configSchemaId, "proc", "1.0.0");

        await using var provider = new ServiceCollection()
            .AddSingleton(identity)
            .AddSingleton(definitions)
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<FixedIdentityResponder>();
                x.AddConsumer<MappedSchemaResponder>();
                x.AddRequestClient<GetProcessorBySourceHash>(new Uri("exchange:" + ProcessorQueues.IdentityQuery));
                x.AddRequestClient<GetSchemaDefinition>(new Uri("exchange:" + ProcessorQueues.SchemaQuery));
                x.UsingInMemory((ctx, cfg) =>
                {
                    cfg.ReceiveEndpoint(ProcessorQueues.IdentityQuery,
                        e => e.ConfigureConsumer<FixedIdentityResponder>(ctx));
                    cfg.ReceiveEndpoint(ProcessorQueues.SchemaQuery,
                        e => e.ConfigureConsumer<MappedSchemaResponder>(ctx));
                });
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            using var scope = provider.CreateScope();
            var identityClient = scope.ServiceProvider.GetRequiredService<IRequestClient<GetProcessorBySourceHash>>();
            var schemaClient = scope.ServiceProvider.GetRequiredService<IRequestClient<GetSchemaDefinition>>();

            var sourceHash = Substitute.For<ISourceHashProvider>();
            sourceHash.Get().Returns(new string('a', 64));

            var log = new List<string>();
            var connector = new RecordingConnector(log);
            var context = new RecordingContext(log);
            var gate = new StartupGate();
            var clock = new FakeTimeProvider();
            var logger = new CapturingLogger();
            var options = Options.Create(new ProcessorLivenessOptions
            {
                IntervalSeconds = 10,
                TtlSeconds = 30,
                RequestTimeoutSeconds = 8,
                BackoffCapSeconds = 30,
                ExecutionDataTtlSeconds = 3600,
            });

            var orchestrator = new ProcessorStartupOrchestrator(
                identityClient, schemaClient, sourceHash, context, gate, connector,
                IdentityResolutionFacts.StubMeterProviderHolder(),
                // Gate A reflects over GateAStubConfig (its Mode is a CLR enum) — so a config schema declaring
                // Mode as a string-enum CLASHES (row #13); a null config def or a schema without Mode is covered.
                IdentityResolutionFacts.StubConfigTypeProvider(typeof(IdentityResolutionFacts.GateAStubConfig)),
                IdentityResolutionFacts.StubLivenessWriter(), "pod-test",
                options, clock, logger);

            await orchestrator.StartAsync(cts.Token);
            // On the clash path the context never latches Healthy, so wait on the gate (fires on all paths).
            await IdentityResolutionFacts.AdvanceUntilAsync(clock, () => gate.IsReady, cts.Token);
            await orchestrator.StopAsync(cts.Token);

            return (log, connector, context, logger, gate);
        }
        finally
        {
            await harness.Stop(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Builds the Wave 0 in-memory harness (identity responds Found immediately with null schema Ids so
    /// Loop B is a no-op), constructs the orchestrator with the recording connector + recording context,
    /// drives it to Healthy on a <see cref="FakeTimeProvider"/>, and returns the ordered event log.
    /// </summary>
    private static async Task<(List<string> Log, RecordingConnector Connector, Guid FoundId)> DriveOrchestratorToHealthy()
    {
        var foundId = Guid.NewGuid();
        var sequence = new ProcessorTestHarness.ResponderSequence
        {
            IdentityNotFoundCount = 0,   // Found immediately — fastest path to the completion block under test.
            SchemaNotFoundCount = 0,
            FoundProcessorId = foundId,
        };

        await using var provider = ProcessorTestHarness.BuildProvider(sequence);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            using var scope = provider.CreateScope();
            var identityClient = scope.ServiceProvider.GetRequiredService<IRequestClient<GetProcessorBySourceHash>>();
            var schemaClient = scope.ServiceProvider.GetRequiredService<IRequestClient<GetSchemaDefinition>>();

            var sourceHash = Substitute.For<ISourceHashProvider>();
            sourceHash.Get().Returns(new string('a', 64));

            var log = new List<string>();
            var connector = new RecordingConnector(log);
            var context = new RecordingContext(log);
            var gate = new StartupGate();
            var clock = new FakeTimeProvider();
            var options = Options.Create(new ProcessorLivenessOptions
            {
                IntervalSeconds = 10,
                TtlSeconds = 30,
                RequestTimeoutSeconds = 8,
                BackoffCapSeconds = 30,
                ExecutionDataTtlSeconds = 3600,
            });

            var orchestrator = new ProcessorStartupOrchestrator(
                identityClient, schemaClient, sourceHash, context, gate, connector,
                IdentityResolutionFacts.StubMeterProviderHolder(), IdentityResolutionFacts.StubConfigTypeProvider(),
                IdentityResolutionFacts.StubLivenessWriter(), "pod-test",
                options, clock,
                NullLogger<ProcessorStartupOrchestrator>.Instance);

            await orchestrator.StartAsync(cts.Token);
            await IdentityResolutionFacts.AdvanceUntilAsync(clock, () => context.IsHealthy, cts.Token);
            await orchestrator.StopAsync(cts.Token);

            Assert.True(context.IsHealthy);
            Assert.True(gate.IsReady);
            return (log, connector, foundId);
        }
        finally
        {
            await harness.Stop(TestContext.Current.CancellationToken);
        }
    }
}
