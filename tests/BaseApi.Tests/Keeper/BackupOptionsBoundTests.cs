using Keeper;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Phase 43 Wave-0 RED Nyquist proof for D-10: the composite-backup TTL knob defaults to 2 days.
/// Mirrors ProbeOptionsBoundTests (instantiate-defaults-and-assert-invariant).
///
/// References BackupOptions, which does not exist until Plan 02 — deliberately RED until then.
/// </summary>
[Trait("Phase", "43")]
public sealed class BackupOptionsBoundTests
{
    [Fact]
    public void BackupOptions_Default_TtlDays_Is_Two()
    {
        Assert.Equal(2, new BackupOptions().TtlDays);
    }
}
