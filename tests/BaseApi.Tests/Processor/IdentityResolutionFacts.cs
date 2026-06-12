using BaseConsole.Core.Health;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Observability;
using BaseProcessor.Core.Startup;
using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Loop A facts (IDENT-04 / RPC-04 / T-26-05): the startup orchestrator resolves identity by
/// SourceHash via <c>IRequestClient&lt;GetProcessorBySourceHash&gt;</c> against the Wave 0 harness,
/// retrying past leading NotFound responses (boot-before-register tolerated) until a Found arrives,
/// then populating the <see cref="IProcessorContext"/>. A <see cref="FakeTimeProvider"/> advances the
/// bounded-backoff delays so the test never sleeps in real time; a CancellationTokenSource timeout
/// fails a hang fast.
/// </summary>
public sealed class IdentityResolutionFacts
{
    [Fact]
    public async Task LoopA_Retries_Past_NotFound_Then_Resolves_Identity()
    {
        var foundId = Guid.NewGuid();
        var sequence = new ProcessorTestHarness.ResponderSequence
        {
            // NotFound -> NotFound -> Found: prove retry happens before resolution (RPC-04 / T-26-05).
            IdentityNotFoundCount = 2,
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
            // IRequestClient<T> is SCOPED — resolve from a scope (Wave 0 correction).
            using var scope = provider.CreateScope();
            var identityClient = scope.ServiceProvider.GetRequiredService<IRequestClient<GetProcessorBySourceHash>>();
            var schemaClient = scope.ServiceProvider.GetRequiredService<IRequestClient<GetSchemaDefinition>>();

            // Stub the SourceHash to a known 64-hex hash (D-13) — no real assembly attribute needed.
            var sourceHash = Substitute.For<ISourceHashProvider>();
            sourceHash.Get().Returns(new string('a', 64));

            var context = new ProcessorContext();
            var gate = new StartupGate();
            var fakeClock = new FakeTimeProvider();
            var options = Options.Create(new ProcessorLivenessOptions
            {
                IntervalSeconds = 10,
                TtlSeconds = 30,
                RequestTimeoutSeconds = 8,
                BackoffCapSeconds = 30,
            });

            var orchestrator = new ProcessorStartupOrchestrator(
                identityClient, schemaClient, sourceHash, context, gate, StubConnector(),
                StubMeterProviderHolder(), StubConfigTypeProvider(), options, fakeClock,
                NullLogger<ProcessorStartupOrchestrator>.Instance);

            // Drive the orchestrator. The two leading NotFound replies trigger two backoff delays
            // (1s then 2s) on the FakeTimeProvider; advance the clock so Task.Delay completes without
            // real sleeping. Identity carries null schema Ids so Loop B is a no-op (immediate Healthy).
            await orchestrator.StartAsync(cts.Token); // returns once ExecuteAsync first yields at the request await
            await AdvanceUntilAsync(fakeClock, () => context.IsHealthy, cts.Token);
            await orchestrator.StopAsync(cts.Token);

            // (a) identity populated to the Found value.
            Assert.Equal(foundId, context.Id);
            Assert.True(context.IsHealthy);
            // (b) the responder was called >= 3 times (2 NotFound + 1 Found) — retry happened.
            Assert.True(GetIdentityCallCount(sequence) >= 3,
                $"expected >= 3 identity calls, got {GetIdentityCallCount(sequence)}");
            // Completion drives the startup gate (D-02).
            Assert.True(gate.IsReady);
        }
        finally
        {
            await harness.Stop(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Pumps the <see cref="FakeTimeProvider"/> forward in coarse steps until <paramref name="done"/>
    /// is satisfied (the orchestrator has resolved + marked Healthy) or the linked token cancels.
    /// Each advance releases any pending <c>Task.Delay</c> so the unbounded-backoff loop progresses
    /// without real-time sleeping.
    /// </summary>
    internal static async Task AdvanceUntilAsync(FakeTimeProvider clock, Func<bool> done, CancellationToken ct)
    {
        while (!done())
        {
            ct.ThrowIfCancellationRequested();
            clock.Advance(TimeSpan.FromSeconds(60)); // > BackoffCap so any pending delay fires
            await Task.Delay(20, ct); // let the orchestrator's continuation run on the thread pool
        }
    }

    /// <summary>
    /// A no-op <see cref="IReceiveEndpointConnector"/> for the Loop A/B resolution facts (which do not
    /// assert on the bind): <c>ConnectReceiveEndpoint</c> returns a handle whose <c>Ready</c> is already
    /// completed, so the orchestrator's completion block (bind -&gt; await Ready -&gt; MarkHealthy, D-03)
    /// flows straight through to Healthy. The bind ORDERING itself is proven by DispatchBindSequenceFacts.
    /// </summary>
    internal static IReceiveEndpointConnector StubConnector()
    {
        var handle = Substitute.For<HostReceiveEndpointHandle>();
        handle.Ready.Returns(Task.FromResult(Substitute.For<ReceiveEndpointReady>()));

        var connector = Substitute.For<IReceiveEndpointConnector>();
        connector
            .ConnectReceiveEndpoint(Arg.Any<string>(), Arg.Any<Action<IBusRegistrationContext, IReceiveEndpointConfigurator>>())
            .Returns(handle);
        return connector;
    }

    /// <summary>
    /// A real <see cref="MeterProviderHolder"/> over a minimal hermetic host MeterProvider (provider #1
    /// with the placeholder resource). The orchestrator's Loop-A swap fires the production
    /// <c>SwapTo</c>/<c>Build</c> path: building provider #2 does NOT open a live OTLP connection (the
    /// gRPC channel is lazy), and <c>ForceFlush</c>/<c>Dispose</c> on #1 are safe with no collector.
    /// Shared by every test that drives the orchestrator past identity-resolve (MLBL-03 / Plan 38-03).
    /// </summary>
    /// <summary>
    /// A stub <see cref="IConfigTypeProvider"/> returning the supplied <paramref name="configType"/> (or
    /// <see cref="GateAStubConfig"/> by default) so the orchestrator's Gate A has a concrete <c>TConfig</c>
    /// to reflect over WITHOUT a real <c>BaseProcessor&lt;TConfig&gt;</c> DI registration. Mirrors the
    /// <see cref="StubConnector"/>/<see cref="StubMeterProviderHolder"/> seam pattern.
    /// </summary>
    internal static IConfigTypeProvider StubConfigTypeProvider(Type? configType = null)
    {
        var provider = Substitute.For<IConfigTypeProvider>();
        provider.Get().Returns(configType ?? typeof(GateAStubConfig));
        return provider;
    }

    /// <summary>The enum whose name-vs-number STJ binding is rule-table row #13 (spike-CONFIRMED CLASH).</summary>
    internal enum GateAMode { A, B }

    /// <summary>
    /// A minimal author-config analog for Gate A facts: its <c>Mode</c> is a CLR enum, so a config-schema
    /// declaring <c>Mode</c> as a string-enum (<c>{"enum":["A","B"]}</c>) CLASHES (row #13). A schema with
    /// no <c>Mode</c> property (or a string <c>Mode</c>) is covered.
    /// </summary>
    internal sealed record GateAStubConfig(GateAMode Mode) : ProcessorConfig;

    internal static MeterProviderHolder StubMeterProviderHolder()
    {
        var hostProvider = Sdk.CreateMeterProviderBuilder()
            .ConfigureResource(r => r
                .AddService(serviceName: "processor-sample_3.5.0", serviceVersion: "3.5.0")
                .AddAttributes(new[] { new KeyValuePair<string, object>("service.instance.id", "test-instance") }))
            .AddMeter(BaseProcessor.Core.Observability.ProcessorMetrics.MeterName)
            .Build();
        return new MeterProviderHolder(hostProvider, "test-instance", "3.5.0");
    }

    private static int GetIdentityCallCount(ProcessorTestHarness.ResponderSequence sequence)
    {
        // The sequence increments its internal counter on each reply; read it back via reflection
        // (the field is private) so the assertion proves retry without changing the Wave 0 harness.
        var field = typeof(ProcessorTestHarness.ResponderSequence)
            .GetField("_identityNotFoundCalls", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (int)field!.GetValue(sequence)!;
    }
}
