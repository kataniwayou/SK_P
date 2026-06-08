using Xunit;

namespace BaseApi.Tests.Keeper.Health;

// TODO(45-01): replace these RED stub bodies with real assertions against Keeper.Health.IL2HealthGate / Keeper.Health.L2HealthGate.
// KEEP-03 — the asymmetric L2 health gate:
//   - starts CLOSED; WaitForOpenAsync blocks until Open() is called
//   - Open() completes any pending waiters
//   - Close() after Open() re-blocks subsequent waiters
//   - WaitForOpenAsync honors cancellation (OperationCanceledException)
//   - Open()/Close() are idempotent (repeat calls are no-ops, no throw)
// Each test below names the exact behavior; the real body will use
//   var ct = TestContext.Current.CancellationToken;
// to drive WaitForOpenAsync against the production gate once 45-01 lands it.
public sealed class L2HealthGateTests
{
    [Fact]
    public void Gate_Starts_Closed_WaitForOpenAsync_Blocks_Until_Open()
        => Assert.Fail("RED — 45-01 must implement Keeper.Health.L2HealthGate");

    [Fact]
    public void Open_Completes_The_Wait()
        => Assert.Fail("RED — 45-01 must implement Keeper.Health.L2HealthGate");

    [Fact]
    public void Close_After_Open_Re_Blocks()
        => Assert.Fail("RED — 45-01 must implement Keeper.Health.L2HealthGate");

    [Fact]
    public void WaitForOpenAsync_Throws_OperationCanceledException_On_Cancel()
        => Assert.Fail("RED — 45-01 must implement Keeper.Health.L2HealthGate");

    [Fact]
    public void Open_Is_Idempotent()
        => Assert.Fail("RED — 45-01 must implement Keeper.Health.L2HealthGate");

    [Fact]
    public void Close_Is_Idempotent()
        => Assert.Fail("RED — 45-01 must implement Keeper.Health.L2HealthGate");
}
