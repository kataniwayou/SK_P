using Xunit;

namespace BaseApi.Tests.Orchestrator.Consumers;

// TODO(45-02): replace these RED stub bodies with real assertions against
// Orchestrator.Consumers.PauseAllConsumer. Fakes the real body will need:
//   - an IScheduler spy (NSubstitute) to assert PauseAll() is invoked
//   - the consumer wired on an ITestHarness (AddMassTransitTestHarness) consuming Messaging.Contracts.PauseAll
// ORCH-02 — global pause:
//   - Consume calls the native scheduler PauseAll() (pause ALL jobs)
//   - redelivery is idempotent: PauseAll twice -> PauseAll() invoked twice, the second a no-op, no exception
public sealed class PauseAllConsumerTests
{
    [Fact]
    public void Consume_Calls_Scheduler_PauseAll()
        => Assert.Fail("RED — 45-02 must implement Orchestrator.Consumers.PauseAllConsumer");

    [Fact]
    public void Redelivery_Is_Idempotent_No_Op()
        => Assert.Fail("RED — 45-02 must implement Orchestrator.Consumers.PauseAllConsumer");
}
