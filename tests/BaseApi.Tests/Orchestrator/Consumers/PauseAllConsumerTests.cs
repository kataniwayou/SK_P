using System;
using System.Threading;
using System.Threading.Tasks;
using Messaging.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestrator.Consumers;
using Orchestrator.Scheduling;
using Quartz;
using Xunit;

namespace BaseApi.Tests.Orchestrator.Consumers;

/// <summary>
/// ORCH-02 global pause (D-01). <see cref="PauseAllConsumer"/> halts the cron for EVERY workflow on this
/// replica by calling the scheduler-wide <see cref="WorkflowScheduler.PauseAllAsync"/> (Quartz
/// <c>PauseAll</c>) — scheduler-wide, NOT per-job. A real <see cref="WorkflowScheduler"/> is built over an
/// NSubstitute <see cref="IScheduler"/> spy so the consumer's contract (it invokes native
/// <c>scheduler.PauseAll(...)</c>) is asserted directly; a duplicate delivery re-invokes it (idempotent
/// Quartz no-op, no exception). EVERY Quartz call carries <c>TestContext.Current.CancellationToken</c>.
/// </summary>
public sealed class PauseAllConsumerTests
{
    private static (PauseAllConsumer consumer, IScheduler spy) Build()
    {
        var spy = Substitute.For<IScheduler>();
        var workflowScheduler = new WorkflowScheduler(spy, TimeProvider.System);
        var consumer = new PauseAllConsumer(workflowScheduler, NullLogger<PauseAllConsumer>.Instance);
        return (consumer, spy);
    }

    [Fact]
    public async Task Consume_Calls_Scheduler_PauseAll()
    {
        var ct = TestContext.Current.CancellationToken;
        var (consumer, spy) = Build();

        await consumer.Consume(OrchestratorTestStubs.Context(new PauseAll { CorrelationId = Guid.NewGuid() }, ct));

        // Scheduler-wide pause: the native PauseAll() (pause ALL jobs) is invoked exactly once.
        await spy.Received(1).PauseAll(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Redelivery_Is_Idempotent_No_Op()
    {
        var ct = TestContext.Current.CancellationToken;
        var (consumer, spy) = Build();
        var msg = new PauseAll { CorrelationId = Guid.NewGuid() };

        // Duplicate delivery (serial replay at ConcurrentMessageLimit=1): no exception thrown.
        await consumer.Consume(OrchestratorTestStubs.Context(msg, ct));
        await consumer.Consume(OrchestratorTestStubs.Context(msg, ct));

        // Idempotent: PauseAll() invoked twice, the second a Quartz no-op (re-pause of paused groups).
        await spy.Received(2).PauseAll(Arg.Any<CancellationToken>());
    }
}
