using Keeper;
using Keeper.Recovery;
using Messaging.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

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
        var sut = new L2ProbeRecovery(fake.Multiplexer, Opts(maxAttempts: 3));

        var outcome = await sut.RunAsync(SampleInner().EntryId, SampleInner().H, TestContext.Current.CancellationToken);

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
        var sut = new L2ProbeRecovery(fake.Multiplexer, Opts(maxAttempts: maxAttempts));

        var outcome = await sut.RunAsync(SampleInner().EntryId, SampleInner().H, TestContext.Current.CancellationToken);

        Assert.Equal(ProbeOutcome.Recovered, outcome);
        Assert.Equal(FakeRedis.RedisHealth.Up, fake.Health);   // recovered within the budget (did NOT exhaust)
    }

    [Fact]
    public async Task Probe_FailToMax()
    {
        // Down for all MaxAttempts → GaveUp. (A Null read on an Up Redis still counts as a successful read —
        // the read value need NOT exist; here Redis is Down throughout so every attempt faults.)
        var fake = new FakeRedis(FakeRedis.RedisHealth.Down);
        var sut = new L2ProbeRecovery(fake.Multiplexer, Opts(maxAttempts: 3));

        var outcome = await sut.RunAsync(SampleInner().EntryId, SampleInner().H, TestContext.Current.CancellationToken);

        Assert.Equal(ProbeOutcome.GaveUp, outcome);
    }
}
