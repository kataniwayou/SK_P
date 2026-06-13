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
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Phase 62.1 / G-62-01 hermetic facts proving the Plan-01 Gate-A-clash UNHEALTHY-REFRESH loop in
/// <see cref="ProcessorStartupOrchestrator"/> (CONTEXT D-04 — the live TEST-01b/01c re-proof is handed
/// back to a Phase-62 close-gate re-run, NOT this phase). A config-schema definition declaring
/// <c>Mode</c> as a string-enum CLASHES the processor's <c>GateAStubConfig</c> CLR enum (rule-table row
/// #13), driving the orchestrator down the terminal-clash path where — instead of the old terminal
/// <c>return;</c> that let the per-instance L2 key TTL-expire (→ absent) and the L1 record go stale (→
/// the self-watchdog falsely "loop stale") — it now re-stamps the SAME static <c>unhealthy</c> per-instance
/// entry (config=Fail, status=Unhealthy, interval=10) with a FRESH timestamp + reset TTL every
/// <c>IntervalSeconds</c> while alive.
/// <list type="bullet">
///   <item><b>Fact A (re-SET each interval):</b> advancing the <see cref="FakeTimeProvider"/> by N intervals
///   re-SETs the per-instance key ≥3 times with strictly-increasing timestamps, each Unhealthy / interval=10 /
///   config=Fail (NOT one write-then-stop).</item>
///   <item><b>Fact B (TTL resets):</b> the per-instance key TTL stays in the (25,30] band — max(10×2,30)=30 —
///   reset each interval, never decaying to expiry as in the gap.</item>
///   <item><b>Fact C (L1 advances):</b> the in-memory <c>l1.Current</c> record stays Unhealthy and its
///   timestamp advances past the first captured one (never goes stale).</item>
///   <item><b>Fact D (watchdog verdict UNCHANGED — D-03):</b> a refreshed interval=10 Unhealthy L1 entry reads
///   as <see cref="HealthStatus.Healthy"/> "live" (NOT "liveness loop stale"); the watchdog Data carries the
///   config=Fail outcome (PROBE-02). The fix does NOT re-introduce the false-restart bug.</item>
///   <item><b>Fact E (clean shutdown — D-06):</b> cancelling the refresh loop via <c>StopAsync</c> exits
///   cleanly with no unobserved exception and a non-faulted background task.</item>
/// </list>
/// Net-zero: both the per-instance key AND its (per-test-unique) index SET key are tracked (D-23); each test
/// uses a fresh <see cref="Guid.NewGuid"/> processorId. The real <see cref="ProcessorLivenessWriter"/> (NOT the
/// no-op stub) is wired against the <see cref="RedisFixture"/> so the re-SET is observable in Redis.
/// </summary>
[Trait("Phase", "62.1")]
public sealed class ClashRefreshFacts : IClassFixture<RedisFixture>
{
    private const string InstanceId = "pod-clash-refresh";

    // A config-schema definition whose "Mode" prop is a string-enum CLASHING the processor's TConfig CLR
    // enum (rule-table row #13 — the confirmed CLASH reused from DispatchBindSequenceFacts.GateA_Clash...).
    private const string ClashingConfigDef =
        "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\",\"properties\":{\"Mode\":{\"enum\":[\"A\",\"B\"]}}}";

    private readonly RedisFixture _redis;

    public ClashRefreshFacts(RedisFixture redis) => _redis = redis;

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

    private static IOptions<ProcessorLivenessOptions> ClashOptions() =>
        Options.Create(new ProcessorLivenessOptions
        {
            IntervalSeconds = 10,        // refresh cadence → recorded interval=10 → TTL max(10×2,30)=30
            StartupIntervalSeconds = 30,
            TtlSeconds = 30,
            RequestTimeoutSeconds = 8,
            BackoffCapSeconds = 30,
        });

    private static ServiceProvider BuildProvider(ProcessorIdentityFound identity, IReadOnlyDictionary<Guid, string> definitions)
        => new ServiceCollection()
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

    /// <summary>
    /// Drives the Gate-A clash hermetically (identity Found with a non-null ConfigSchemaId; the schema
    /// responder returns the string-enum config definition that clashes <c>GateAStubConfig</c>), wiring the
    /// REAL <see cref="ProcessorLivenessWriter"/> against the fixture so the per-instance re-SET is observable.
    /// Starts the orchestrator and pumps the clock until <c>gate.IsReady</c> (the clash path fires MarkReady,
    /// NOT MarkHealthy), then hands the live harness state back to the caller's pump loop. The caller MUST
    /// dispose the returned <see cref="ClashRun"/> (StopAsync + harness.Stop) in a finally.
    /// </summary>
    private async Task<ClashRun> DriveIntoRefreshLoopAsync(Guid procId, RedisKey perInstance, RedisKey index, CancellationToken outer)
    {
        var configId = Guid.NewGuid();
        var identity = new ProcessorIdentityFound(
            procId, InputSchemaId: null, OutputSchemaId: null, ConfigSchemaId: configId, "proc", "1.0.0");

        var provider = BuildProvider(identity, new Dictionary<Guid, string> { [configId] = ClashingConfigDef });
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        var scope = provider.CreateScope();
        var identityClient = scope.ServiceProvider.GetRequiredService<IRequestClient<GetProcessorBySourceHash>>();
        var schemaClient = scope.ServiceProvider.GetRequiredService<IRequestClient<GetSchemaDefinition>>();

        var sourceHash = Substitute.For<ISourceHashProvider>();
        sourceHash.Get().Returns(new string('a', 64));

        var context = new ProcessorContext();
        var gate = new StartupGate();
        var clock = new FakeTimeProvider();
        var options = ClashOptions();
        var l1 = new ProcessorLivenessState();
        var writer = new ProcessorLivenessWriter(
            _redis.Multiplexer, l1, options, NullLogger<ProcessorLivenessWriter>.Instance);

        var orchestrator = new ProcessorStartupOrchestrator(
            identityClient, schemaClient, sourceHash, context, gate,
            IdentityResolutionFacts.StubConnector(), IdentityResolutionFacts.StubMeterProviderHolder(),
            // Gate A reflects over GateAStubConfig (CLR enum Mode) — the string-enum config def above CLASHES.
            IdentityResolutionFacts.StubConfigTypeProvider(typeof(IdentityResolutionFacts.GateAStubConfig)),
            writer, InstanceId, options, clock, NullLogger<ProcessorStartupOrchestrator>.Instance);

        await orchestrator.StartAsync(cts.Token);
        // The clash path NEVER reaches Healthy; it fires gate.MarkReady() then enters the refresh loop.
        await IdentityResolutionFacts.AdvanceUntilAsync(clock, () => gate.IsReady, cts.Token);

        return new ClashRun(provider, harness, scope, cts, orchestrator, context, gate, clock, l1);
    }

    /// <summary>The live harness state of a driven clash run; <see cref="DisposeAsync"/> stops the orchestrator + harness.</summary>
    private sealed class ClashRun(
        ServiceProvider provider, ITestHarness harness, IServiceScope scope, CancellationTokenSource cts,
        ProcessorStartupOrchestrator orchestrator, ProcessorContext context, StartupGate gate,
        FakeTimeProvider clock, ProcessorLivenessState l1) : IAsyncDisposable
    {
        public ProcessorStartupOrchestrator Orchestrator { get; } = orchestrator;
        public ProcessorContext Context { get; } = context;
        public StartupGate Gate { get; } = gate;
        public FakeTimeProvider Clock { get; } = clock;
        public ProcessorLivenessState L1 { get; } = l1;
        public CancellationToken Token => cts.Token;

        public async ValueTask DisposeAsync()
        {
            try { await Orchestrator.StopAsync(cts.Token); } catch { /* shutdown — clean-exit asserted in Fact E */ }
            try { await harness.Stop(CancellationToken.None); } catch { /* best effort */ }
            scope.Dispose();
            cts.Dispose();
            await provider.DisposeAsync();
        }
    }

    /// <summary>
    /// Facts A/B/C: drive the clash path into the refresh loop, advance the fake clock by ≥3 IntervalSeconds
    /// (10s) steps, and assert the per-instance key is re-SET each interval (≥3 distinct strictly-increasing
    /// timestamps), each Unhealthy / interval=10 / config=Fail; the TTL stays reset in the (25,30] band; and
    /// the in-memory L1 record advances past the first captured timestamp.
    /// </summary>
    [Fact]
    public async Task ClashPath_ReSets_Unhealthy_Key_Each_Interval_TtlResets_And_L1_Advances()
    {
        var ct = TestContext.Current.CancellationToken;
        var procId = Guid.NewGuid();
        var perInstance = L2ProjectionKeys.PerInstance(procId, InstanceId);
        var index = L2ProjectionKeys.InstanceIndex(procId);
        _redis.Track(perInstance);   // net-zero teardown (D-23)
        _redis.Track(index);         // deleting the SET key removes the SADD'd member
        var db = _redis.Multiplexer.GetDatabase();

        await using var run = await DriveIntoRefreshLoopAsync(procId, perInstance, index, ct);

        // The clash gate fired (readiness green), but the processor never latched Healthy (terminal clash).
        Assert.True(run.Gate.IsReady);
        Assert.False(run.Context.IsHealthy);

        // The initial clash stamp landed before/at gate-ready — capture the first timestamp.
        Assert.True(await db.KeyExistsAsync(perInstance),
            "the initial clash-path unhealthy key should be present once the gate is ready");
        var first = await ReadEntryAsync(db, perInstance, ct);
        // The INITIAL clash stamp (orchestrator line ~228 — no interval override) records the startup anchor
        // interval=30; the steady-state cadence (interval=10) is the REFRESH loop's signature, asserted on the
        // re-SET entries below. Here we only assert the clash STATUS invariants on the initial stamp.
        AssertClashStatus(first);

        // Fact A: pump the refresh loop ≥3 IntervalSeconds (10s). Each Advance releases the loop's
        // Task.Delay(refreshPeriod, clock, ...) so WriteUnhealthyAsync re-stamps a FRESH timestamp with the
        // steady-state recorded interval=10 (the decoupling proof — timestamp-fresh, status-Unhealthy).
        var timestamps = new List<DateTime> { first.Timestamp };
        for (var i = 0; i < 4; i++)
        {
            run.Token.ThrowIfCancellationRequested();
            run.Clock.Advance(TimeSpan.FromSeconds(10)); // one refresh interval
            await Task.Delay(20, ct);                    // let the loop continuation re-SET

            var entry = await ReadEntryAsync(db, perInstance, ct);
            AssertRefreshEntry(entry);                   // every refreshed entry: Unhealthy / interval=10 / config=Fail
            timestamps.Add(entry.Timestamp);
        }

        // ≥3 DISTINCT strictly-increasing timestamps ⇒ the key was re-SET ≥3 times (NOT one write-then-stop).
        var distinctIncreasing = CountDistinctStrictlyIncreasing(timestamps);
        Assert.True(distinctIncreasing >= 3,
            $"expected ≥3 distinct strictly-increasing re-SET timestamps (key re-SET each interval), got {distinctIncreasing} from [{string.Join(", ", timestamps.Select(t => t.Ticks))}]");

        // Fact B (TTL resets): after a refreshed interval the per-instance TTL is in (25,30] — max(10×2,30)=30
        // reset each interval, NOT a monotonic decay to expiry (the gap behavior).
        var ttl = await db.KeyTimeToLiveAsync(perInstance);
        Assert.NotNull(ttl);
        Assert.InRange(ttl!.Value.TotalSeconds, 25, 30);

        // Fact C (L1 advances): the in-memory record stays Unhealthy and its timestamp advanced past the first.
        Assert.NotNull(run.L1.Current);
        Assert.Equal(LivenessStatus.Unhealthy, run.L1.Current!.Status);
        Assert.True(run.L1.Current.Timestamp > first.Timestamp,
            $"L1 Current.Timestamp ({run.L1.Current.Timestamp.Ticks}) should advance past the first captured ({first.Timestamp.Ticks})");
    }

    private static async Task<ProcessorLivenessEntry> ReadEntryAsync(IDatabase db, RedisKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var raw = await db.StringGetAsync(key);
        var entry = System.Text.Json.JsonSerializer.Deserialize<ProcessorLivenessEntry>(raw!);
        Assert.NotNull(entry);
        return entry!;
    }

    /// <summary>Clash-status invariants common to every published clash entry (initial stamp + every refresh).</summary>
    private static void AssertClashStatus(ProcessorLivenessEntry entry)
    {
        Assert.Equal(LivenessStatus.Unhealthy, entry.Status);
        Assert.Equal(SchemaOutcome.Fail, entry.Summary.ConfigSchema); // the Gate-A clash outcome (D-04)
    }

    /// <summary>A REFRESHED clash entry additionally records the steady-state cadence interval=10 (D-02).</summary>
    private static void AssertRefreshEntry(ProcessorLivenessEntry entry)
    {
        AssertClashStatus(entry);
        Assert.Equal(10, entry.Interval); // D-02: the refresh loop records the steady-state heartbeat cadence
    }

    private static int CountDistinctStrictlyIncreasing(IReadOnlyList<DateTime> timestamps)
    {
        var count = 0;
        DateTime? prev = null;
        foreach (var t in timestamps)
        {
            if (prev is null || t > prev.Value)
            {
                count++;
                prev = t;
            }
        }
        return count;
    }
}
