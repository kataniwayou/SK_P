using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Phase 46 Wave-0 RED stub for ORCH-01: the TypedResultConsumer&lt;T&gt; family advances workflow
/// steps off per-item StepCompleted/StepFailed/StepCancelled/StepProcessing via its Outcome knob and
/// StepAdvancement.SelectNext — no status if/switch. A Keeper-INJECT'd StepCompleted is processed
/// byte-indistinguishably from a direct processor completion. Deliberately RED now; implemented
/// green in plan 46-04. References NO not-yet-built production type so the test project still compiles.
/// </summary>
public sealed class TypedResultConsumerFacts
{
    [Fact]
    [Trait("Phase", "46")]
    public void TypedResultConsumer_advances_via_SelectNext_outcome()
        => Assert.Fail("Phase 46 Wave 0 stub — implemented in 46-02/46-03/46-04");

    [Fact]
    [Trait("Phase", "46")]
    public void Injected_StepCompleted_indistinguishable_from_direct()
        => Assert.Fail("Phase 46 Wave 0 stub — implemented in 46-02/46-03/46-04");
}
