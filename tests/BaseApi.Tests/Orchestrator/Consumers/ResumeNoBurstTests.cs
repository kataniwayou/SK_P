using Xunit;

namespace BaseApi.Tests.Orchestrator.Consumers;

// TODO(45-02): replace these RED stub bodies with real assertions against
// Orchestrator.Consumers.ResumeAllConsumer + WorkflowLifecycle.ResumeAsync. THE load-bearing negative.
// Fakes the real body will need:
//   - an IScheduler spy (NSubstitute) to assert scheduler.ResumeAll(...) is NEVER invoked
//   - the spy's ScheduleAsync capture to assert StartAt >= now (fresh-from-now reschedule)
// ORCH-02 — no thundering-herd on resume:
//   - the native scheduler ResumeAll(...) is NEVER called (per-job reschedule, not a global unpause burst)
//   - resume reschedules fresh-from-now: the captured StartAt >= now (skip-to-next, no immediate refire)
public sealed class ResumeNoBurstTests
{
    [Fact]
    public void Native_ResumeAll_Is_Never_Called()
        => Assert.Fail("RED — 45-02 must implement Orchestrator.Consumers.ResumeAllConsumer (no native ResumeAll burst)");

    [Fact]
    public void Resume_Reschedules_Fresh_From_Now_StartAt_Ge_Now()
        => Assert.Fail("RED — 45-02 must implement Orchestrator.Consumers.ResumeAllConsumer (fresh-from-now reschedule)");
}
