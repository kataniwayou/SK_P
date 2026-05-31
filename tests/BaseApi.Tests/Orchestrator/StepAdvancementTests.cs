using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Orchestrator.Dispatch;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Pins the pure outcome-to-entry-condition match + NextStepIds traversal (SPEC req 3 / D-02):
/// StepAdvancement.SelectNext selects exactly the next steps whose EntryCondition equals
/// (int)outcome OR equals Always(4); Never(5) is never selected for any outcome, and a dangling
/// NextStepIds id (absent from the step map) is silently skipped (T-24-06). Pure helper:
/// instantiated directly, fed an in-memory step map, NO harness / NO Redis (mirrors CronIntervalTests).
/// <para>
/// The map is built fresh per test with one next-step per EntryCondition value 0..5, keyed by a
/// deterministic id derived from the condition value (so a test can assert selection by stepId).
/// </para>
/// </summary>
public sealed class StepAdvancementTests
{
    private readonly StepAdvancement _sut = new();

    // Deterministic, distinct, non-empty stepId per condition value. Avoids the all-zero Guid for 0.
    private static Guid IdFor(int cond) => new($"11111111-1111-1111-1111-1111111111{cond:D2}");

    private const int Always = 4;
    private const int Never = 5;
    private static readonly Guid DanglingId = new("99999999-9999-9999-9999-999999999999");

    /// <summary>A next-step whose EntryCondition is the given value (distinct random ProcessorId, empty edges).</summary>
    private static StepProjection StepWithCondition(int entryCondition) =>
        new(entryCondition, Guid.NewGuid(), "{}", []);

    /// <summary>The step map: one step per EntryCondition 0..5, keyed by IdFor(condition).</summary>
    private static Dictionary<Guid, StepProjection> BuildMap()
    {
        var map = new Dictionary<Guid, StepProjection>();
        for (var cond = 0; cond <= 5; cond++)
            map[IdFor(cond)] = StepWithCondition(cond);
        return map;
    }

    /// <summary>The completed step whose NextStepIds fans out to every condition 0..5 + a dangling id.</summary>
    private static StepProjection Completed()
    {
        var next = new List<Guid>();
        for (var cond = 0; cond <= 5; cond++)
            next.Add(IdFor(cond));
        next.Add(DanglingId);
        return new StepProjection(EntryCondition: 7, ProcessorId: Guid.NewGuid(), Payload: "{}", NextStepIds: next);
    }

    [Theory]
    [InlineData(StepOutcome.Processing, 0)] // Processing -> EntryCondition 0 + Always(4)
    [InlineData(StepOutcome.Completed, 1)]  // Completed  -> EntryCondition 1 + Always(4)
    [InlineData(StepOutcome.Failed, 2)]     // Failed     -> EntryCondition 2 + Always(4)
    [InlineData(StepOutcome.Cancelled, 3)]  // Cancelled  -> EntryCondition 3 + Always(4)
    public void SelectsExactlyMatchingConditionPlusAlways(StepOutcome outcome, int matchedCondition)
    {
        var map = BuildMap();

        var selectedIds = _sut.SelectNext(outcome, Completed(), map)
            .Select(s => s.stepId)
            .ToHashSet();

        Assert.Equal(new HashSet<Guid> { IdFor(matchedCondition), IdFor(Always) }, selectedIds);
    }

    [Theory]
    [InlineData(StepOutcome.Processing)]
    [InlineData(StepOutcome.Completed)]
    [InlineData(StepOutcome.Failed)]
    [InlineData(StepOutcome.Cancelled)]
    public void NeverConditionStep_IsNeverSelected_ForAnyOutcome(StepOutcome outcome)
    {
        var map = BuildMap();

        var selected = _sut.SelectNext(outcome, Completed(), map).ToList();

        Assert.DoesNotContain(selected, s => s.stepId == IdFor(Never));
        Assert.DoesNotContain(selected, s => s.step.EntryCondition == Never);
    }

    [Theory]
    [InlineData(StepOutcome.Processing)]
    [InlineData(StepOutcome.Completed)]
    [InlineData(StepOutcome.Failed)]
    [InlineData(StepOutcome.Cancelled)]
    public void NonMatchingOutcomeConditionSteps_AreExcluded(StepOutcome outcome)
    {
        var map = BuildMap();

        var selectedIds = _sut.SelectNext(outcome, Completed(), map)
            .Select(s => s.stepId)
            .ToHashSet();

        // Every condition step in 0..3 other than the matched one is excluded (Always stays; Never never).
        for (var cond = 0; cond <= 3; cond++)
            if (cond != (int)outcome)
                Assert.DoesNotContain(IdFor(cond), selectedIds);
    }

    [Fact]
    public void DanglingNextStepId_IsSkipped_NoThrow()
    {
        var map = BuildMap(); // does NOT contain DanglingId

        var selectedIds = _sut.SelectNext(StepOutcome.Completed, Completed(), map)
            .Select(s => s.stepId)
            .ToHashSet();

        Assert.DoesNotContain(DanglingId, selectedIds);
        Assert.Contains(IdFor(1), selectedIds);      // matched (Completed == 1)
        Assert.Contains(IdFor(Always), selectedIds); // Always(4)
    }

    [Fact]
    public void HelperPerformsNoIo_TakesStepMapAsArgument()
    {
        // The signature proves no I/O: SelectNext takes the step map as an argument and returns
        // synchronously (IEnumerable, not Task). No Redis / store dependency is constructed.
        var map = BuildMap();
        var completed = new StepProjection(7, Guid.NewGuid(), "{}", [IdFor(1)]);

        var selected = _sut.SelectNext(StepOutcome.Completed, completed, map).ToList();

        Assert.Single(selected);
        Assert.Equal(IdFor(1), selected[0].stepId);
    }
}
