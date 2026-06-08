using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Phase 46 Wave-0 RED stub for KEEP-08: the Keeper CLEANUP state deletes the redundant L2
/// composite-backup copy on the happy path (net-zero composite invariant). Deliberately RED now;
/// implemented green in plan 46-02/03. References NO not-yet-built production type so the test
/// project still compiles.
/// </summary>
public sealed class CleanupConsumerFacts
{
    [Fact]
    [Trait("Phase", "46")]
    public void Cleanup_deletes_composite_backup()
        => Assert.Fail("Phase 46 Wave 0 stub — implemented in 46-02/46-03/46-04");
}
