using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Orchestrator.Dispatch;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Pins the pure outcome->entry-condition match + NextStepIds traversal (SPEC req 3 / D-02):
/// <see cref="StepAdvancement.SelectNext"/> selects exactly the next steps whose
/// <c>EntryCondition</c> equals <c>(int)outcome</c> OR equals <c>Always</c>(4); <c>Never</c>(5) is
/// never selected for any outcome, and a dangling <c>NextStepIds</c> id (absent from the step map)
/// is silently skipped (T-24-06). Pure helper — instantiated directly, fed an in-memory step map,
/// NO harness / NO Redis (mirrors <c>CronIntervalTests</c>).
/// </summary>
public sealed class StepAdvancementTests
{
    private readonly StepAdvancement _sut = new();

    // One next-step id per EntryCondition value 0..5 (0=Processing, 1=Completed, 2=Failed,
    // 3=Cancelled, 4=Always, 5=Never). The "completed" step's NextStepIds points at all six.
    private static readonly Guid Cond0 = Guid.Parse("00000000-0000-0000-0000-000000000010");
    private static readonly Guid Cond1 = Guid.Parse("00000000-0000-0000-0000-000000000011");
    private static readonly Guid Cond2 = Guid.Parse("00000000-0000-0000-0000-000000000012");
    private static readonly Guid Cond3 = Guid.Parse("00000000-0000-0000-0000-000000000013");
    private static readonly Guid CondAlways = Guid.Parse("00000000-0000-0000-0000-000000000014");
    private static readonly Guid CondNever = Guid.Parse("00000000-0000-0000-0000-000000000015");
    private static readonly Guid Dangling = Guid.Parse("00000000-0000-0000-0000-0000000000ff");

    private static StepProjection Step(int entryCondition, params Guid[] nextStepIds) =>
        new(entryCondition, Guid.NewGuid(), "{}", new List<Guid>(nextStepIds));

    /// <summary>Builds the step map: one step per EntryCondition 0..5 (no onward edges).</summary>
    private static Dictionary<Guid, StepProjection> BuildMap() => new()
    {
        [Cond0] = Step(0),
        [Cond1] = Step(1),
        [Cond2] = Step(2),
        [Cond3] = Step(3),
        [CondAlways] = Step(4),
        [CondNever] = Step(5),
    };

    /// <summary>The completed step whose NextStepIds fans out to every condition (incl. a dangling id).</summary>
    private static StepProjection Completed() =>
        Step(99, Cond0, Cond1, Cond2, Cond3, CondAlways, CondNever, Dangling);

    [Theory]
    [InlineData(StepOutcome.Processing, 0)] // Processing -> EntryCondition 0 + Always(4)
    [InlineData(StepOutcome.Completed, 1)]  // Completed  -> EntryCondition 1 + Always(4)
    [InlineData(StepOutcome.Failed, 2)]     // Failed     -> EntryCondition 2 + Always(4)
    [InlineData(StepOutcome.Cancelled, 3)]  // Cancelled  -> EntryCondition 3 + Always(4)
    public void SelectsMatchingOutcomeConditionPlusAlways(StepOutcome outcome, int matchedCondition)
    {
        var map = BuildMap();

        var selected = _sut.SelectNext(outcome, Completed(), map).ToList();

        var selectedConditions = selected.Select(s => s.step.EntryCondition).OrderBy(c => c).ToList();
        Assert.Equal(new[] { matchedCondition, 4 }.OrderBy(c => c).ToList(), selectedConditions);
    }

    [Theory]
    [InlineData(StepOutcome.Processing)]
    [InlineData(StepOutcome.Completed)]
    [InlineData(StepOutcome.Failed)]
    [InlineData(StepOutcome.Cancelled)]
    public void NeverIsNeverSelected_ForAnyOutcome(StepOutcome outcome)
    {
        var map = BuildMap();

        var selected = _sut.SelectNext(outcome, Completed(), map).ToList();

        Assert.DoesNotContain(selected, s => s.step.EntryCondition == 5); // Never(5)
        Assert.DoesNotContain(selected, s => s.stepId == CondNever);
    }

    [Theory]
    [InlineData(StepOutcome.Processing, 1, 2, 3)] // when outcome=0, conditions 1/2/3 are NOT selected
    [InlineData(StepOutcome.Completed, 0, 2, 3)]
    [InlineData(StepOutcome.Failed, 0, 1, 3)]
    [InlineData(StepOutcome.Cancelled, 0, 1, 2)]
    public void NonMatchingOutcomeConditionsAreNotSelected(StepOutcome outcome, int a, int b, int c)
    {
        var map = BuildMap();

        var selectedConditions = _sut.SelectNext(outcome, Completed(), map)
            .Select(s => s.step.EntryCondition).ToHashSet();

        Assert.DoesNotContain(a, selectedConditions);
        Assert.DoesNotContain(b, selectedConditions);
        Assert.DoesNotContain(c, selectedConditions);
    }

    [Fact]
    public void DanglingNextStepId_IsSkipped_NoThrow()
    {
        var map = BuildMap(); // does NOT contain Dangling

        // Completed() includes Dangling in its NextStepIds; SelectNext must skip it, not throw.
        var selected = _sut.SelectNext(StepOutcome.Completed, Completed(), map).ToList();

        Assert.DoesNotContain(selected, s => s.stepId == Dangling);
        // sanity: the real matches (Completed=1, Always=4) still came through
        Assert.Contains(selected, s => s.stepId == Cond1);
        Assert.Contains(selected, s => s.stepId == CondAlways);
    }

    [Fact]
    public void HelperPerformsNoIo_TakesStepMapAsArgument()
    {
        // The signature itself proves no I/O: SelectNext takes the step map as an argument and
        // returns synchronously (IEnumerable, not Task). No Redis / store dependency is constructed.
        var map = BuildMap();
        var completed = Step(99, Cond1);

        var selected = _sut.SelectNext(StepOutcome.Completed, completed, map).ToList();

        Assert.Single(selected);
        Assert.Equal(Cond1, selected[0].stepId);
    }
}
