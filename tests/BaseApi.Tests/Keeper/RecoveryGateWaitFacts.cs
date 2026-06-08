using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Phase 46 Wave-0 RED stub for KEEP-09 / D-03: the recovery consumer base awaits IL2HealthGate
/// ONCE at Consume entry, blocking until the gate opens; on bound (~5-min linked-CTS) exhaustion it
/// throws a transient marker that the endpoint UseMessageRetry re-attempts. Deliberately RED now;
/// implemented green in plan 46-02/03. References NO not-yet-built production type so the test
/// project still compiles.
/// </summary>
public sealed class RecoveryGateWaitFacts
{
    [Fact]
    [Trait("Phase", "46")]
    public void GateWait_blocks_until_gate_opens()
        => Assert.Fail("Phase 46 Wave 0 stub — implemented in 46-02/46-03/46-04");

    [Fact]
    [Trait("Phase", "46")]
    public void GateWait_bound_exhaustion_throws_transient()
        => Assert.Fail("Phase 46 Wave 0 stub — implemented in 46-02/46-03/46-04");
}
