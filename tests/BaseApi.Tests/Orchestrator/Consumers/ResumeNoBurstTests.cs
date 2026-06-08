using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Messaging.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestrator.Consumers;
using Orchestrator.Hydration;
using Orchestrator.L1;
using Orchestrator.Scheduling;
using Quartz;
using Xunit;

namespace BaseApi.Tests.Orchestrator.Consumers;

/// <summary>
/// THE load-bearing negative (ORCH-02 / D-02 / T-45-07 / Pitfall 2). <see cref="ResumeAllConsumer"/> must
/// resume PER-JOB and MUST NEVER call native <c>scheduler.ResumeAll()</c> — a global unpause would fire the
/// cross-workflow catch-up herd. Driven over an NSubstitute <see cref="IScheduler"/> spy (so the native
/// <c>ResumeAll</c> can be negatively asserted) whose <c>GetTriggerState</c> returns <c>Paused</c> (the
/// resume precondition) and whose <c>ScheduleJob</c> capture proves the fresh trigger's <c>StartAt</c> is
/// ≥ now (skip-to-next, no immediate refire). EVERY Quartz call carries
/// <c>TestContext.Current.CancellationToken</c>.
/// </summary>
public sealed class ResumeNoBurstTests
{
    private const string EveryFiveMinutes = "*/5 * * * *";

    private static (ResumeAllConsumer consumer, IScheduler spy, Guid jobId) Build(CancellationToken ct)
    {
        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var processorId = Guid.NewGuid();

        // L1 holds the one workflow the consumer will enumerate; the real WorkflowL1Store is hydrated from L2.
        var store = new WorkflowL1Store();

        var spy = Substitute.For<IScheduler>();
        // The trigger is Paused -> the resume guard proceeds to delete-stale + fresh-from-now reschedule.
        spy.GetTriggerState(Arg.Any<TriggerKey>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TriggerState.Paused));

        var workflowScheduler = new WorkflowScheduler(spy, TimeProvider.System);
        var values = OrchestratorTestStubs.RootWithStep(workflowId, jobId, stepId, processorId, EveryFiveMinutes);
        var mux = OrchestratorTestStubs.PresentL2(values, out _);
        var lifecycle = new WorkflowLifecycle(
            mux, store, workflowScheduler, TimeProvider.System, NullLogger<WorkflowLifecycle>.Instance);

        // Hydrate L1 (this also issues a ScheduleJob on the spy; the resume below issues the second one).
        lifecycle.HydrateAndScheduleAsync(workflowId, ct).GetAwaiter().GetResult();
        Assert.Contains(workflowId, store.WorkflowIds);

        var consumer = new ResumeAllConsumer(store, lifecycle, NullLogger<ResumeAllConsumer>.Instance);
        return (consumer, spy, jobId);
    }

    [Fact]
    public async Task Native_ResumeAll_Is_Never_Called()
    {
        var ct = TestContext.Current.CancellationToken;
        var (consumer, spy, _) = Build(ct);

        await consumer.Consume(OrchestratorTestStubs.Context(new ResumeAll { CorrelationId = Guid.NewGuid() }, ct));

        // The load-bearing negative: per-job reschedule only — never a native global unpause burst (T-45-07).
        await spy.DidNotReceive().ResumeAll(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resume_Reschedules_Fresh_From_Now_StartAt_Ge_Now()
    {
        var ct = TestContext.Current.CancellationToken;
        var (consumer, spy, _) = Build(ct);

        var before = DateTimeOffset.UtcNow;
        await consumer.Consume(OrchestratorTestStubs.Context(new ResumeAll { CorrelationId = Guid.NewGuid() }, ct));

        // Capture the trigger from the fresh (post-resume) ScheduleJob call and assert StartAt >= now (skip-to-next).
        var scheduled = spy.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IScheduler.ScheduleJob))
            .Select(c => c.GetArguments())
            .Where(args => args.Length >= 2 && args[0] is IJobDetail && args[1] is ITrigger)
            .Select(args => (ITrigger)args[1]!)
            .ToList();

        Assert.NotEmpty(scheduled);
        var freshTrigger = scheduled[^1]; // the resume's fresh-from-now trigger (last ScheduleJob)
        Assert.True(freshTrigger.StartTimeUtc >= before,
            "resumed trigger must start at-or-after now (fresh-from-now, no immediate refire)");
    }
}
