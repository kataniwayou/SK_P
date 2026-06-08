using Messaging.Contracts;
using Xunit;

namespace BaseApi.Tests.Contracts;

/// <summary>
/// Phase 43 Wave-0 RED Nyquist proof for SC-2 (D-07): the SINGLE shared source-step sentinel
/// predicate. Guid.Empty IS the source step; any other GUID is not. Every consumer must branch
/// off THIS predicate (never an ad-hoc == Guid.Empty).
///
/// References SourceStep, which does not exist until Plan 02 — deliberately RED until then.
/// </summary>
[Trait("Phase", "43")]
public sealed class SourceStepTests
{
    [Fact]
    public void IsSource_true_only_for_Guid_Empty()
    {
        Assert.True(SourceStep.IsSource(Guid.Empty));
        Assert.False(SourceStep.IsSource(Guid.NewGuid()));
    }
}
