using System.Collections.Concurrent;
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
/// Loop B facts (CFG-03/04 / SCHEMA-01/02 / LIVE-04): after identity resolves, the orchestrator resolves
/// the definition for each NON-NULL input/output/CONFIG schema Id. Phase 57 D-12 LIFTS the D-05 carve-out
/// — the <c>ConfigSchemaId</c> IS now queried and its fetched definition stored on
/// <c>context.ConfigDefinition</c> (CFG-03); the unchanged Loop B retry body proves CFG-04 transient
/// retry for the config Id too (one leading NotFound per Id → Found). A null input schema Id still sends
/// NO request yet the processor still reaches Healthy (SCHEMA-02 / LIVE-04). A capturing schema responder
/// records every queried Id so the now-config-IS-queried invariant is asserted directly.
/// </summary>
public sealed class SchemaResolutionFacts
{
    /// <summary>Identity responder that replies Found immediately with caller-configured schema Ids.</summary>
    private sealed class FixedIdentityResponder(ProcessorIdentityFound identity) : IConsumer<GetProcessorBySourceHash>
    {
        public Task Consume(ConsumeContext<GetProcessorBySourceHash> context)
            => context.RespondAsync(identity);
    }

    /// <summary>
    /// Schema responder that CAPTURES every queried schema Id (proving never-config) and replies
    /// Found after the configured number of leading NotFound replies per Id.
    /// </summary>
    private sealed class CapturingSchemaResponder(SchemaCapture capture) : IConsumer<GetSchemaDefinition>
    {
        public async Task Consume(ConsumeContext<GetSchemaDefinition> context)
        {
            var id = context.Message.SchemaId;
            capture.QueriedIds.Add(id);
            if (capture.NextIsNotFound(id))
                await context.RespondAsync(new SchemaDefinitionNotFound(id));
            else
                await context.RespondAsync(new SchemaDefinitionFound($"def-for-{id:N}"));
        }
    }

    private sealed class SchemaCapture
    {
        public ConcurrentBag<Guid> QueriedIds { get; } = new();
        private readonly ConcurrentDictionary<Guid, int> _calls = new();

        /// <summary>One leading NotFound per Id, then Found (proves per-Id retry).</summary>
        public bool NextIsNotFound(Guid id) => _calls.AddOrUpdate(id, 1, (_, c) => c + 1) <= 1;
    }

    private static ServiceProvider BuildProvider(ProcessorIdentityFound identity, SchemaCapture capture)
        => new ServiceCollection()
            .AddSingleton(capture)
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<FixedIdentityResponder>();
                x.AddConsumer<CapturingSchemaResponder>();
                x.AddRequestClient<GetProcessorBySourceHash>(new Uri("exchange:" + ProcessorQueues.IdentityQuery));
                x.AddRequestClient<GetSchemaDefinition>(new Uri("exchange:" + ProcessorQueues.SchemaQuery));
                x.UsingInMemory((ctx, cfg) =>
                {
                    cfg.ReceiveEndpoint(ProcessorQueues.IdentityQuery,
                        e => e.ConfigureConsumer<FixedIdentityResponder>(ctx));
                    cfg.ReceiveEndpoint(ProcessorQueues.SchemaQuery,
                        e => e.ConfigureConsumer<CapturingSchemaResponder>(ctx));
                });
            })
            // The FixedIdentityResponder needs the identity instance — register it as the consumer dependency.
            .AddSingleton(identity)
            .BuildServiceProvider(true);

    private static async Task<(ProcessorContext context, StartupGate gate)> RunOrchestratorAsync(
        ServiceProvider provider, CancellationToken ct)
    {
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        using var scope = provider.CreateScope();
        var identityClient = scope.ServiceProvider.GetRequiredService<IRequestClient<GetProcessorBySourceHash>>();
        var schemaClient = scope.ServiceProvider.GetRequiredService<IRequestClient<GetSchemaDefinition>>();

        var sourceHash = Substitute.For<ISourceHashProvider>();
        sourceHash.Get().Returns(new string('a', 64));

        var context = new ProcessorContext();
        var gate = new StartupGate();
        var clock = new FakeTimeProvider();
        var options = Options.Create(new ProcessorLivenessOptions
        {
            IntervalSeconds = 10,
            TtlSeconds = 30,
            RequestTimeoutSeconds = 8,
            BackoffCapSeconds = 30,
        });

        var orchestrator = new ProcessorStartupOrchestrator(
            identityClient, schemaClient, sourceHash, context, gate,
            IdentityResolutionFacts.StubConnector(), IdentityResolutionFacts.StubMeterProviderHolder(), options, clock,
            NullLogger<ProcessorStartupOrchestrator>.Instance);

        await orchestrator.StartAsync(ct);
        await IdentityResolutionFacts.AdvanceUntilAsync(clock, () => context.IsHealthy, ct);
        await orchestrator.StopAsync(ct);

        return (context, gate);
    }

    [Fact]
    public async Task LoopB_Resolves_Input_Output_And_Config()
    {
        var inputId = Guid.NewGuid();
        var outputId = Guid.NewGuid();
        var configId = Guid.NewGuid();
        var identity = new ProcessorIdentityFound(Guid.NewGuid(), inputId, outputId, configId, "proc", "1.0.0");
        var capture = new SchemaCapture();

        await using var provider = BuildProvider(identity, capture);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            var (context, gate) = await RunOrchestratorAsync(provider, cts.Token);

            // (a) input + output + config definitions all resolved (D-12 lifts the D-05 config carve-out).
            Assert.Equal($"def-for-{inputId:N}", context.InputDefinition);
            Assert.Equal($"def-for-{outputId:N}", context.OutputDefinition);
            // CFG-03 — the fetched config-schema definition is now stored on the context.
            Assert.Equal($"def-for-{configId:N}", context.ConfigDefinition);
            Assert.True(context.IsHealthy);
            Assert.True(gate.IsReady);

            // (b) the schema responder was queried for input + output AND the config Id (D-12, was D-05 never).
            var queried = capture.QueriedIds.ToList();
            Assert.Contains(inputId, queried);
            Assert.Contains(outputId, queried);
            Assert.Contains(configId, queried);
        }
        finally
        {
            await provider.GetRequiredService<ITestHarness>().Stop(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task LoopB_Null_Input_Skips_Request_And_Still_Reaches_Healthy()
    {
        var outputId = Guid.NewGuid();
        var configId = Guid.NewGuid();
        // InputSchemaId is null (source processor) — no request should be sent for it (SCHEMA-02).
        var identity = new ProcessorIdentityFound(Guid.NewGuid(), InputSchemaId: null, outputId, configId, "proc", "1.0.0");
        var capture = new SchemaCapture();

        await using var provider = BuildProvider(identity, capture);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            var (context, gate) = await RunOrchestratorAsync(provider, cts.Token);

            // Output still resolves; input definition stays null (skipped, no request sent).
            Assert.Null(context.InputDefinition);
            Assert.Equal($"def-for-{outputId:N}", context.OutputDefinition);
            // All-required-resolved with a skipped null is still Healthy (SCHEMA-02 / LIVE-04).
            Assert.True(context.IsHealthy);
            Assert.True(gate.IsReady);

            // Output + config are queried (D-12); the absent (null) input is NOT.
            var queried = capture.QueriedIds.ToList();
            Assert.Contains(outputId, queried);
            Assert.Contains(configId, queried);
            // CFG-03 — the config definition is resolved onto the context.
            Assert.Equal($"def-for-{configId:N}", context.ConfigDefinition);
        }
        finally
        {
            await provider.GetRequiredService<ITestHarness>().Stop(TestContext.Current.CancellationToken);
        }
    }
}
