using BaseApi.Tests.Composition;
using BaseConsole.Core.Health;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Liveness;
using BaseProcessor.Core.Startup;
using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// STATE-03 / LOOP-01 / D-01/02/03/04 inline-startup-write facts: the
/// <see cref="ProcessorStartupOrchestrator"/> writes an <c>unhealthy</c>
/// <see cref="ProcessorLivenessEntry"/> at EACH resolution iteration through the shared
/// <see cref="ProcessorLivenessWriter"/> against the real localhost:6380 Redis (<see cref="RedisFixture"/>),
/// so a starting / restarting replica is visible in L2 as <c>unhealthy</c> from the first post-identity
/// iteration (never absent). A schema responder that NEVER resolves the input schema parks the orchestrator
/// in Loop B, where it repeatedly rewrites the per-instance entry — the observation window for facts 1-4.
/// <list type="bullet">
///   <item><b>Fact 1 (STATE-03/LOOP-01):</b> PerInstance key present, <see cref="LivenessStatus.Unhealthy"/>, interval 30.</item>
///   <item><b>Fact 2 (D-04):</b> summary tracks progress — the unresolved input schema is <see cref="SchemaOutcome.Fail"/>.</item>
///   <item><b>Fact 3 (LOOP-04/D-15):</b> the index SET contains the instanceId (SADD).</item>
///   <item><b>Fact 4 (D-05):</b> the OLD flat <c>skp:{procId}</c> key is NOT written (its builder was deleted in Phase 61, D-11).</item>
///   <item><b>Fact 5 (LOOP-01 resilience):</b> a dead-Redis writer logs-and-continues — resolution still reaches Healthy.</item>
/// </list>
/// Net-zero: both the per-instance key AND its (per-test-unique) index SET key are tracked — deleting the
/// whole index SET removes the SADD'd member (D-23 known-key cleanup); each test uses a fresh processorId.
/// </summary>
[Trait("Phase", "60")]
public sealed class StartupUnhealthyWriteFacts : IClassFixture<RedisFixture>
{
    private const string InstanceId = "pod-startup";

    private readonly RedisFixture _redis;

    public StartupUnhealthyWriteFacts(RedisFixture redis) => _redis = redis;

    /// <summary>Identity responder that replies Found immediately with the caller-configured identity.</summary>
    private sealed class FixedIdentityResponder(ProcessorIdentityFound identity) : IConsumer<GetProcessorBySourceHash>
    {
        public Task Consume(ConsumeContext<GetProcessorBySourceHash> context)
            => context.RespondAsync(identity);
    }

    /// <summary>Schema responder that ALWAYS replies NotFound — the input schema never resolves, so the
    /// orchestrator parks in Loop B re-writing the unhealthy entry (the observation window).</summary>
    private sealed class NeverFoundSchemaResponder : IConsumer<GetSchemaDefinition>
    {
        public Task Consume(ConsumeContext<GetSchemaDefinition> context)
            => context.RespondAsync(new SchemaDefinitionNotFound(context.Message.SchemaId));
    }

    private static ServiceProvider BuildProvider(ProcessorIdentityFound identity)
        => new ServiceCollection()
            .AddSingleton(identity)
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<FixedIdentityResponder>();
                x.AddConsumer<NeverFoundSchemaResponder>();
                x.AddRequestClient<GetProcessorBySourceHash>(new Uri("exchange:" + ProcessorQueues.IdentityQuery));
                x.AddRequestClient<GetSchemaDefinition>(new Uri("exchange:" + ProcessorQueues.SchemaQuery));
                x.UsingInMemory((ctx, cfg) =>
                {
                    cfg.ReceiveEndpoint(ProcessorQueues.IdentityQuery,
                        e => e.ConfigureConsumer<FixedIdentityResponder>(ctx));
                    cfg.ReceiveEndpoint(ProcessorQueues.SchemaQuery,
                        e => e.ConfigureConsumer<NeverFoundSchemaResponder>(ctx));
                });
            })
            .BuildServiceProvider(true);

    private static IOptions<ProcessorLivenessOptions> StartupOptions() =>
        Options.Create(new ProcessorLivenessOptions
        {
            IntervalSeconds = 10,
            StartupIntervalSeconds = 30,
            TtlSeconds = 30,
            RequestTimeoutSeconds = 8,
            BackoffCapSeconds = 30,
        });

    /// <summary>
    /// Facts 1-4: drive the orchestrator to its first post-identity Loop-B iteration (the input schema never
    /// resolves), GET the per-instance entry while it is parked there, and assert it is present + Unhealthy +
    /// interval 30, the unresolved input summary field is Fail, the index SET carries the instanceId, and the
    /// OLD flat Processor key was never written.
    /// </summary>
    [Fact]
    public async Task Startup_Writes_Unhealthy_PerInstance_With_Summary_Progress_Sadd_And_No_Old_Key()
    {
        var ct = TestContext.Current.CancellationToken;
        var procId = Guid.NewGuid();
        var inputSchemaId = Guid.NewGuid();
        var perInstance = L2ProjectionKeys.PerInstance(procId, InstanceId);
        var index = L2ProjectionKeys.InstanceIndex(procId);
        _redis.Track(perInstance);                 // net-zero teardown (D-23)
        _redis.Track(index);                       // deleting the SET key removes the SADD'd member
        var legacyFlatKey = $"{L2ProjectionKeys.Prefix}{procId}"; // Phase 61 D-11: builder deleted — inline the old shape
        _redis.Track(legacyFlatKey);                       // belt-and-braces: track the OLD key (must stay absent)
        var db = _redis.Multiplexer.GetDatabase();

        // Identity Found immediately with a NON-NULL input schema id (output/config null) — Loop B queries the
        // input schema, the responder always NotFounds it, so the orchestrator parks in Loop B writing unhealthy.
        var identity = new ProcessorIdentityFound(
            procId, InputSchemaId: inputSchemaId, OutputSchemaId: null, ConfigSchemaId: null, "proc", "1.0.0");

        await using var provider = BuildProvider(identity);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            using var scope = provider.CreateScope();
            var identityClient = scope.ServiceProvider.GetRequiredService<IRequestClient<GetProcessorBySourceHash>>();
            var schemaClient = scope.ServiceProvider.GetRequiredService<IRequestClient<GetSchemaDefinition>>();

            var sourceHash = Substitute.For<ISourceHashProvider>();
            sourceHash.Get().Returns(new string('a', 64));

            var context = new ProcessorContext();
            var gate = new StartupGate();
            var clock = new FakeTimeProvider();
            var options = StartupOptions();
            var l1 = new ProcessorLivenessState();
            var writer = new ProcessorLivenessWriter(
                _redis.Multiplexer, l1, options, NullLogger<ProcessorLivenessWriter>.Instance);

            var orchestrator = new ProcessorStartupOrchestrator(
                identityClient, schemaClient, sourceHash, context, gate,
                IdentityResolutionFacts.StubConnector(), IdentityResolutionFacts.StubMeterProviderHolder(),
                IdentityResolutionFacts.StubConfigTypeProvider(), writer, InstanceId,
                options, clock, NullLogger<ProcessorStartupOrchestrator>.Instance);

            await orchestrator.StartAsync(cts.Token);

            // Pump the fake clock so the backoff Task.Delays fire; wait until the per-instance key lands (the
            // first post-identity Loop-B write). The orchestrator never reaches Healthy (input never resolves).
            var landed = false;
            for (var attempt = 0; attempt < 200 && !landed; attempt++)
            {
                cts.Token.ThrowIfCancellationRequested();
                clock.Advance(TimeSpan.FromSeconds(60)); // > BackoffCap so any pending delay fires
                await Task.Delay(20, cts.Token);
                landed = await db.KeyExistsAsync(perInstance);
            }
            Assert.True(landed, "the per-instance unhealthy key should land from the first post-identity iteration");

            // Fact 1 (STATE-03/LOOP-01): present + Unhealthy + interval 30 (StartupIntervalSeconds).
            var raw = await db.StringGetAsync(perInstance);
            var entry = System.Text.Json.JsonSerializer.Deserialize<ProcessorLivenessEntry>(raw!);
            Assert.NotNull(entry);
            Assert.Equal(LivenessStatus.Unhealthy, entry!.Status);
            Assert.Equal(30, entry.Interval);

            // Fact 2 (D-04): summary tracks per-schema progress — the unresolved input schema is Fail; the
            // null output/config schemas are null-is-skip => Success.
            Assert.Equal(SchemaOutcome.Fail, entry.Summary.InputSchema);
            Assert.Equal(SchemaOutcome.Success, entry.Summary.OutputSchema);
            Assert.Equal(SchemaOutcome.Success, entry.Summary.ConfigSchema);

            // Fact 3 (LOOP-04/D-15): the index SET contains the instanceId (SADD on the first write).
            var members = await db.SetMembersAsync(index);
            Assert.Contains(InstanceId, members.Select(m => (string)m!));

            // Fact 4 (D-05): the OLD flat Processor key is NEVER written by the orchestrator.
            // Phase 61 D-11: the L2ProjectionKeys.Processor builder was deleted with the legacy contract;
            // the old shape is inlined so this absence regression still holds.
            Assert.False(await db.KeyExistsAsync(legacyFlatKey));

            // L1 mirrors L2 (L1-01 / D-09): the in-memory holder carries the same unhealthy record.
            Assert.NotNull(l1.Current);
            Assert.Equal(LivenessStatus.Unhealthy, l1.Current!.Status);

            await orchestrator.StopAsync(cts.Token);
        }
        finally
        {
            await harness.Stop(ct);
        }
    }

    /// <summary>
    /// Fact 5 (LOOP-01 resilience / T-60-13): the startup write rides a writer pointing at a DEAD Redis whose
    /// <c>GetDatabase()</c> throws. The writer's log-and-continue swallows the fault, so identity + (all-null)
    /// schema resolution still reaches Healthy and NO exception escapes — a dead Redis must not crash the host
    /// or abort resolution.
    /// </summary>
    [Fact]
    public async Task Dead_Redis_Startup_Write_Still_Reaches_Healthy()
    {
        var ct = TestContext.Current.CancellationToken;

        // All-null schemas (source processor) => Loop B is a no-op => fastest path to Healthy.
        var sequence = new ProcessorTestHarness.ResponderSequence
        {
            IdentityNotFoundCount = 0,
            SchemaNotFoundCount = 0,
            FoundProcessorId = Guid.NewGuid(),
        };

        await using var provider = ProcessorTestHarness.BuildProvider(sequence);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            using var scope = provider.CreateScope();
            var identityClient = scope.ServiceProvider.GetRequiredService<IRequestClient<GetProcessorBySourceHash>>();
            var schemaClient = scope.ServiceProvider.GetRequiredService<IRequestClient<GetSchemaDefinition>>();

            var sourceHash = Substitute.For<ISourceHashProvider>();
            sourceHash.Get().Returns(new string('a', 64));

            var context = new ProcessorContext();
            var gate = new StartupGate();
            var clock = new FakeTimeProvider();
            var options = StartupOptions();

            // Dead Redis: GetDatabase() throws — the writer's catch swallows it (log-and-continue).
            var deadRedis = Substitute.For<IConnectionMultiplexer>();
            deadRedis.GetDatabase(Arg.Any<int>(), Arg.Any<object>())
                .Returns(_ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "dead"));
            var writer = new ProcessorLivenessWriter(
                deadRedis, new ProcessorLivenessState(), options, NullLogger<ProcessorLivenessWriter>.Instance);

            var orchestrator = new ProcessorStartupOrchestrator(
                identityClient, schemaClient, sourceHash, context, gate,
                IdentityResolutionFacts.StubConnector(), IdentityResolutionFacts.StubMeterProviderHolder(),
                IdentityResolutionFacts.StubConfigTypeProvider(), writer, InstanceId,
                options, clock, NullLogger<ProcessorStartupOrchestrator>.Instance);

            await orchestrator.StartAsync(cts.Token);
            await IdentityResolutionFacts.AdvanceUntilAsync(clock, () => context.IsHealthy, cts.Token);
            await orchestrator.StopAsync(cts.Token);

            // Resolution reached Healthy despite the dead-Redis startup write (no exception escaped).
            Assert.True(context.IsHealthy);
            Assert.True(gate.IsReady);
        }
        finally
        {
            await harness.Stop(ct);
        }
    }
}
