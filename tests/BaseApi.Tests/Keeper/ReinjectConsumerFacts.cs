using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Phase 46 Wave-0 RED stub for KEEP-05: the Keeper REINJECT state reads L2[entryId]; present →
/// re-injects a reconstructed EntryStepDispatch carrying the D-01 Payload to queue:{ProcessorId};
/// absent/empty → throws RecoveryDataGoneException (the deliberate data-gone terminal → skp-dlq-1).
/// Implemented green in plan 46-02/03; deliberately RED now. References NO not-yet-built production
/// type so the test project still compiles.
/// </summary>
public sealed class ReinjectConsumerFacts
{
    [Fact]
    [Trait("Phase", "46")]
    public void Reinject_present_sends_EntryStepDispatch_with_Payload()
        => Assert.Fail("Phase 46 Wave 0 stub — implemented in 46-02/46-03/46-04");

    [Fact]
    [Trait("Phase", "46")]
    public void Reinject_absent_throws_RecoveryDataGone()
        => Assert.Fail("Phase 46 Wave 0 stub — implemented in 46-02/46-03/46-04");
}
