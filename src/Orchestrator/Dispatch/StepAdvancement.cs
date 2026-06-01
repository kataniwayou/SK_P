using Messaging.Contracts;
using Messaging.Contracts.Projections;

namespace Orchestrator.Dispatch;

/// <summary>
/// The pure outcome->entry-condition match + <c>NextStepIds</c> traversal (SPEC req 3 / D-02).
/// Given a completed step's reported <see cref="StepOutcome"/> and the in-memory L1 step map, selects
/// exactly the next steps whose <c>EntryCondition</c> equals <c>(int)outcome</c> (0-3) OR equals
/// <see cref="Always"/> (4). <c>Never</c> (5) falls out of the predicate so a <c>Never</c>-gated step
/// can never be auto-advanced (T-24-07).
/// <para>
/// No I/O: the step map is passed in as an argument (no Redis, no store reference), so the match is a
/// pure function unit-testable without a harness. A <c>NextStepIds</c> id absent from the map is
/// SKIPPED via the <c>TryGetValue</c> guard — a dangling edge is a graceful business skip, never a
/// throw (T-24-06). <c>Always</c>/<c>Never</c> live here as orchestrator-side int constants; this type
/// does NOT reference <c>BaseApi.Service.StepEntryCondition</c> (Orchestrator references only
/// <c>Messaging.Contracts</c>).
/// </para>
/// </summary>
public sealed class StepAdvancement
{
    private const int Always = 4; // Never = 5 is never selected (it falls out of the predicate)

    /// <summary>
    /// Yields each <c>(stepId, step)</c> from <paramref name="completed"/>'s <c>NextStepIds</c> whose
    /// <c>EntryCondition</c> matches <paramref name="outcome"/> or is <see cref="Always"/>.
    /// <para>
    /// WR-02 (24.1 / D-24.1-06): a <c>null</c> (or empty) <c>NextStepIds</c> is the contract-defined
    /// TERMINAL step — it yields no successors (graceful), consistent with every sibling traversal
    /// site (the loader / cleanup BFS coalesce <c>NextStepIds</c> to an empty list). The lone
    /// unguarded site here previously NRE'd on the normal end-of-branch case and would dead-letter
    /// instead of acking; the <c>?? Enumerable.Empty&lt;Guid&gt;()</c> guard closes that.
    /// </para>
    /// </summary>
    public IEnumerable<(Guid stepId, StepProjection step)> SelectNext(
        StepOutcome outcome, StepProjection completed, IReadOnlyDictionary<Guid, StepProjection> steps)
    {
        foreach (var nextId in completed.NextStepIds ?? Enumerable.Empty<Guid>())
            if (steps.TryGetValue(nextId, out var next) &&
                (next.EntryCondition == (int)outcome || next.EntryCondition == Always))
                yield return (nextId, next);
    }
}
