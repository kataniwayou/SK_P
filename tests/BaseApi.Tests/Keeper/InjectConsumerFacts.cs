using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Phase 46 Wave-0 RED stub for KEEP-06: the Keeper INJECT state performs ordered ops —
/// read composite → new entryId → write L2[entryId] (NO TTL) → Send StepCompleted to the
/// orchestrator result queue → delete the composite copy. The implementing task must assert the
/// op order via Received.InOrder. Deliberately RED now. References NO not-yet-built production type
/// so the test project still compiles.
/// </summary>
public sealed class InjectConsumerFacts
{
    [Fact]
    [Trait("Phase", "46")]
    public void Inject_reads_then_writes_then_sends_then_deletes_in_order()
        => Assert.Fail("Phase 46 Wave 0 stub — implemented in 46-02/46-03/46-04");
}
