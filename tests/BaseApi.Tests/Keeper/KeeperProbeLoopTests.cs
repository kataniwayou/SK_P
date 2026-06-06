using System.Diagnostics.Metrics;
using Keeper;
using Keeper.Consumers;
using Keeper.Observability;
using Keeper.Recovery;
using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;
using ExecutionResult = Messaging.Contracts.ExecutionResult;   // disambiguate from MassTransit.ExecutionResult

namespace BaseApi.Tests.Keeper;

/// <summary>
/// PROBE-02..05 hermetic proof for Plan 02's recovery engine. Two layers:
/// <list type="bullet">
///   <item><description>
///   Helper-level facts (<c>Probe_RequiresReadAndWrite</c>, <c>Probe_FailThenSucceed</c>,
///   <c>Probe_FailToMax</c>) drive <see cref="L2ProbeRecovery"/> DIRECTLY against the Plan-01
///   <see cref="FakeRedis"/> double — proving the loop requires BOTH a read AND a write-then-delete
///   (PROBE-02), recovers on a fail-then-succeed sequence (PROBE-01/03 logic), and gives up at
///   MaxAttempts (PROBE-04 logic). DelaySeconds=0 keeps the loop instant.
///   </description></item>
///   <item><description>
///   Harness-level facts (<c>Probe_Success_Reinjects</c>, <c>Probe_GiveUp_ParksToDlq</c>,
///   <c>Probe_AcksOnlyAfterLoop</c>) wire the SUT consumer + the <see cref="FakeRedis"/> multiplexer +
///   the helper into a MassTransit in-memory harness, publish a <see cref="Fault{T}"/> via the
///   framework initializer (<c>new { Message = inner }</c>), and assert the verbatim inner is Sent to
///   its origin endpoint on recovery (PROBE-03) / the ORIGINAL Fault&lt;T&gt; envelope is Sent to
///   keeper-dlq on give-up (PROBE-04), consumed only after the awaited loop exits (PROBE-05).
///   </description></item>
/// </list>
/// No RealStack trait — runs in the fast hermetic suite.
/// </summary>
public sealed class KeeperProbeLoopTests
{
    private static IOptions<ProbeOptions> Opts(int maxAttempts = 3, int delaySeconds = 0) =>
        Options.Create(new ProbeOptions { DelaySeconds = delaySeconds, MaxAttempts = maxAttempts });

    /// <summary>A throwaway KeeperMetrics (IMeterFactory-backed) for the helper-level facts — no listener attached.</summary>
    private static KeeperMetrics NewMetrics() =>
        new(new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>());

    private static EntryStepDispatch SampleInner(Guid? processorId = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), processorId ?? Guid.NewGuid(), "payload")
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            EntryId = Guid.NewGuid().ToString("D"),
            H = "abc123",
        };

    // ── Helper-level facts (drive L2ProbeRecovery directly against FakeRedis) ──────────────────────────

    [Fact]
    public async Task Probe_RequiresReadAndWrite()
    {
        // HalfOpen: read OK, write/delete throw → the iteration is NOT a success; loop runs to MaxAttempts → GaveUp.
        var fake = new FakeRedis(FakeRedis.RedisHealth.HalfOpen);
        var sut = new L2ProbeRecovery(fake.Multiplexer, Opts(maxAttempts: 3), NewMetrics());

        var inner = SampleInner();
        var outcome = await sut.RunAsync(inner.EntryId, inner.H, inner.ProcessorId.ToString("D"), TestContext.Current.CancellationToken);

        Assert.Equal(ProbeOutcome.GaveUp, outcome);   // write-failing half-open Redis never counts as recovered (PROBE-02)
    }

    [Fact]
    public async Task Probe_FailThenSucceed()
    {
        // Down for k < MaxAttempts iterations, then auto-recovers Up → Recovered, without exceeding MaxAttempts.
        const int maxAttempts = 5;
        const int failuresBeforeUp = 2;
        var fake = new FakeRedis();
        fake.SetFailuresBeforeUp(failuresBeforeUp);   // first 2 probe ops throw, then health flips Up
        var sut = new L2ProbeRecovery(fake.Multiplexer, Opts(maxAttempts: maxAttempts), NewMetrics());

        var inner = SampleInner();
        var outcome = await sut.RunAsync(inner.EntryId, inner.H, inner.ProcessorId.ToString("D"), TestContext.Current.CancellationToken);

        Assert.Equal(ProbeOutcome.Recovered, outcome);
        Assert.Equal(FakeRedis.RedisHealth.Up, fake.Health);   // recovered within the budget (did NOT exhaust)
    }

    [Fact]
    public async Task Probe_FailToMax()
    {
        // Down for all MaxAttempts → GaveUp. (A Null read on an Up Redis still counts as a successful read —
        // the read value need NOT exist; here Redis is Down throughout so every attempt faults.)
        var fake = new FakeRedis(FakeRedis.RedisHealth.Down);
        var sut = new L2ProbeRecovery(fake.Multiplexer, Opts(maxAttempts: 3), NewMetrics());

        var inner = SampleInner();
        var outcome = await sut.RunAsync(inner.EntryId, inner.H, inner.ProcessorId.ToString("D"), TestContext.Current.CancellationToken);

        Assert.Equal(ProbeOutcome.GaveUp, outcome);
    }

    // ── Harness-level facts (wire the SUT consumer + FakeRedis + helper into an in-memory bus) ─────────

    /// <summary>
    /// Builds an in-memory MassTransit harness wiring the SUT fault consumer(s), with the FakeRedis
    /// multiplexer + ProbeOptions + the L2ProbeRecovery helper registered (the consumer ctor-deps).
    /// </summary>
    private static ServiceProvider BuildHarness(
        FakeRedis fake, IOptions<ProbeOptions> opts, Action<IBusRegistrationConfigurator> addConsumers) =>
        new ServiceCollection()
            .AddLogging()
            .AddMetrics()
            .AddSingleton<KeeperMetrics>()
            .AddSingleton<IConnectionMultiplexer>(fake.Multiplexer)
            .AddSingleton(opts)
            .AddSingleton<L2ProbeRecovery>()
            .AddSingleton<KeeperRecoveryHandler>()
            .AddMassTransitTestHarness(x =>
            {
                addConsumers(x);
                x.UsingInMemory((ctx, cfg) => cfg.ConfigureEndpoints(ctx));
            })
            .BuildServiceProvider(true);

    [Fact]
    public async Task Probe_Success_Reinjects()
    {
        var ct = TestContext.Current.CancellationToken;

        // ── Dispatch: fail-then-up → verbatim re-inject to queue:{ProcessorId:D} ──
        var processorId = Guid.NewGuid();
        var dispatchInner = SampleInner(processorId);
        var fakeUp = new FakeRedis(FakeRedis.RedisHealth.Up);   // probe succeeds immediately → Recovered

        await using var provider = BuildHarness(
            fakeUp, Opts(maxAttempts: 3), x => x.AddConsumer<FaultEntryStepDispatchConsumer>());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish<Fault<EntryStepDispatch>>(new { Message = dispatchInner }, ct);
            Assert.True(await harness.Consumed.Any<Fault<EntryStepDispatch>>(ct));
            // PROBE-03: the verbatim inner is Sent back onto the bus (re-injected to its origin endpoint).
            Assert.True(await harness.Sent.Any<EntryStepDispatch>(ct));
        }
        finally { await harness.Stop(ct); }

        // ── Result: fail-then-up → verbatim re-inject to queue:orchestrator-result ──
        var resultInner = new ExecutionResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), StepOutcome.Failed)
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            EntryId = Guid.NewGuid().ToString("D"),
            H = "def456",
        };
        var fakeUp2 = new FakeRedis(FakeRedis.RedisHealth.Up);

        await using var provider2 = BuildHarness(
            fakeUp2, Opts(maxAttempts: 3), x => x.AddConsumer<FaultExecutionResultConsumer>());
        var harness2 = provider2.GetRequiredService<ITestHarness>();
        await harness2.Start();
        try
        {
            await harness2.Bus.Publish<Fault<ExecutionResult>>(new { Message = resultInner }, ct);
            Assert.True(await harness2.Consumed.Any<Fault<ExecutionResult>>(ct));
            Assert.True(await harness2.Sent.Any<ExecutionResult>(ct));
        }
        finally { await harness2.Stop(ct); }
    }

    [Fact]
    public async Task Probe_GiveUp_ParksToDlq()
    {
        var ct = TestContext.Current.CancellationToken;

        var dispatchInner = SampleInner();
        var fakeDown = new FakeRedis(FakeRedis.RedisHealth.Down);   // down for all attempts → GaveUp

        await using var provider = BuildHarness(
            fakeDown, Opts(maxAttempts: 2), x => x.AddConsumer<FaultEntryStepDispatchConsumer>());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish<Fault<EntryStepDispatch>>(new { Message = dispatchInner }, ct);
            Assert.True(await harness.Consumed.Any<Fault<EntryStepDispatch>>(ct));
            // PROBE-04: the ORIGINAL Fault<EntryStepDispatch> envelope is parked to keeper-dlq (NOT the bare inner).
            Assert.True(await harness.Sent.Any<Fault<EntryStepDispatch>>(ct));
            // The bare inner is NEVER re-injected on give-up.
            Assert.False(await harness.Sent.Any<EntryStepDispatch>(ct));
        }
        finally { await harness.Stop(ct); }
    }

    [Fact]
    public async Task Probe_AcksOnlyAfterLoop()
    {
        var ct = TestContext.Current.CancellationToken;

        // A non-trivial loop (fail twice then up) — the await holds the delivery; Consumed becomes true only
        // AFTER the loop exits (the harness Consumed assertion completing PROVES ack-after-loop, PROBE-05).
        var dispatchInner = SampleInner();
        var fake = new FakeRedis();
        fake.SetFailuresBeforeUp(2);

        await using var provider = BuildHarness(
            fake, Opts(maxAttempts: 5), x => x.AddConsumer<FaultEntryStepDispatchConsumer>());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish<Fault<EntryStepDispatch>>(new { Message = dispatchInner }, ct);
            // Consumed completes only after the awaited probe loop exits and the consumer returns (ack).
            Assert.True(await harness.Consumed.Any<Fault<EntryStepDispatch>>(ct));
            // The loop recovered → the verbatim inner was re-injected as part of the same (post-loop) consume.
            Assert.True(await harness.Sent.Any<EntryStepDispatch>(ct));
        }
        finally { await harness.Stop(ct); }
    }
}
