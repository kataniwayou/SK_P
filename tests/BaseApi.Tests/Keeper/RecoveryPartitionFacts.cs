using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Phase 46 Wave-0 RED stub for KEEP-09: the keeper-recovery endpoint partitions per-key on the
/// IKeeperRecoverable 4-tuple (corr:wf:ProcessorId:executionId), deliberately EXCLUDING StepId
/// (D-12). Deliberately RED now; implemented green in plan 46-02/03. References NO not-yet-built
/// production type so the test project still compiles.
/// </summary>
public sealed class RecoveryPartitionFacts
{
    [Fact]
    [Trait("Phase", "46")]
    public void Partition_key_is_four_tuple_excluding_StepId()
        => Assert.Fail("Phase 46 Wave 0 stub — implemented in 46-02/46-03/46-04");
}
