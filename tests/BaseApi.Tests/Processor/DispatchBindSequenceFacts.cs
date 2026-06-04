using BaseConsole.Core.Health;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Startup;
using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Microsoft.Extensions.DependencyInjection;
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
        public string? InputDefinition => _inner.InputDefinition;
        public string? OutputDefinition => _inner.OutputDefinition;
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
                identityClient, schemaClient, sourceHash, context, gate, connector, options,
                Options.Create(new Messaging.Contracts.Configuration.RetryOptions()), clock,
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
