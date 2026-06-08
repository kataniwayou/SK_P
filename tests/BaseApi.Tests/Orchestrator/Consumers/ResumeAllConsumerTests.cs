using Xunit;

namespace BaseApi.Tests.Orchestrator.Consumers;

// TODO(45-02): replace these RED stub bodies with real assertions against
// Orchestrator.Consumers.ResumeAllConsumer. Fakes the real body will need:
//   - a fake IWorkflowL1Store supplying the WorkflowIds snapshot to enumerate
//   - WorkflowLifecycle / WorkflowScheduler driving per-job ResumeAsync
//   - an IScheduler spy to assert per-job reschedule (NOT a native ResumeAll() burst)
// ORCH-02 — global resume (per-job, no herd):
//   - Consume enumerates the WorkflowIds snapshot and calls ResumeAsync for each
//   - a non-Paused trigger is ignored (TriggerState != Paused -> no-op, no spurious resume)
public sealed class ResumeAllConsumerTests
{
    [Fact]
    public void Consume_Enumerates_WorkflowIds_And_Calls_ResumeAsync_Each()
        => Assert.Fail("RED — 45-02 must implement Orchestrator.Consumers.ResumeAllConsumer");

    [Fact]
    public void Resume_Of_Non_Paused_Trigger_Is_Ignored()
        => Assert.Fail("RED — 45-02 must implement Orchestrator.Consumers.ResumeAllConsumer");
}
