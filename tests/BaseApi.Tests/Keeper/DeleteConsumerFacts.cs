using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Phase 46 Wave-0 RED stub for KEEP-07: the Keeper DELETE state deletes the L2 execution-data key
/// (GC only). Deliberately RED now; implemented green in plan 46-02/03. References NO not-yet-built
/// production type so the test project still compiles.
/// </summary>
public sealed class DeleteConsumerFacts
{
    [Fact]
    [Trait("Phase", "46")]
    public void Delete_deletes_execution_data_key()
        => Assert.Fail("Phase 46 Wave 0 stub — implemented in 46-02/46-03/46-04");
}
