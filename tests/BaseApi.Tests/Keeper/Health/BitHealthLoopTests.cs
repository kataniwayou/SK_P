using Xunit;

namespace BaseApi.Tests.Keeper.Health;

// TODO(45-01): replace these RED stub bodies with real assertions against Keeper.Health.BitHealthLoop
// (the background BIT health probe loop) + Messaging.Contracts.PauseAll / ResumeAll.
// KEEP-01 — probe resilience:
//   - a RedisException reports unhealthy but the loop SURVIVES (next tick still runs)
//   - a non-Redis throw PROPAGATES (is NOT swallowed)
//   - the stoppingToken ends the loop cleanly
// KEEP-02 / SC#2 — edge-triggered global broadcast:
//   - healthy->unhealthy transition publishes PauseAll exactly ONCE
//   - unhealthy->healthy transition publishes ResumeAll exactly ONCE
//   - same-state ticks publish NOTHING (drive healthy,healthy,unhealthy,unhealthy,healthy
//     -> exactly 1 PauseAll + 1 ResumeAll, no duplicates)
// The real bodies will drive the loop via an injected probe fake + an ITestHarness (AddMassTransitTestHarness)
// to assert PauseAll/ResumeAll publish counts, using var ct = TestContext.Current.CancellationToken;
public sealed class BitHealthLoopTests
{
    [Fact]
    public void Probe_RedisException_Reports_Unhealthy_Loop_Survives()
        => Assert.Fail("RED — 45-01 must implement Keeper.Health.BitHealthLoop");

    [Fact]
    public void Probe_NonRedis_Throw_Propagates_Not_Swallowed()
        => Assert.Fail("RED — 45-01 must implement Keeper.Health.BitHealthLoop");

    [Fact]
    public void StoppingToken_Ends_Loop_Cleanly()
        => Assert.Fail("RED — 45-01 must implement Keeper.Health.BitHealthLoop");

    [Fact]
    public void Edge_Trigger_Publishes_PauseAll_Once_On_Healthy_To_Unhealthy()
        => Assert.Fail("RED — 45-01 must implement Keeper.Health.BitHealthLoop");

    [Fact]
    public void Edge_Trigger_Publishes_ResumeAll_Once_On_Unhealthy_To_Healthy()
        => Assert.Fail("RED — 45-01 must implement Keeper.Health.BitHealthLoop");

    [Fact]
    public void Same_State_Ticks_Publish_Nothing()
        => Assert.Fail("RED — 45-01 must implement Keeper.Health.BitHealthLoop");
}
