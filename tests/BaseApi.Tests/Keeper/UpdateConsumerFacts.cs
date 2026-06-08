using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Phase 46 Wave-0 RED stub for KEEP-04: the Keeper UPDATE state writes the validated data to the
/// L2 composite-backup key WITH the BackupOptions TTL (crash-backstop only), and ONLY once the
/// IL2HealthGate is open. Implemented green in plan 46-02/03; deliberately RED now so the
/// --filter "Phase=46" run discovers a target for the implementing task to turn green.
/// References NO not-yet-built production type so the test project still compiles.
/// </summary>
public sealed class UpdateConsumerFacts
{
    [Fact]
    [Trait("Phase", "46")]
    public void Update_writes_composite_with_TTL_only_when_gate_open()
        => Assert.Fail("Phase 46 Wave 0 stub — implemented in 46-02/46-03/46-04");
}
