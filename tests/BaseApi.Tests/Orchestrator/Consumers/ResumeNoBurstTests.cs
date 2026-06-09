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
/// THE load-bearing no-herd contract (ORCH-02 / D-02 / GAP-49-2 / D-08 Option A / T-49-01).
/// <see cref="ResumeAllConsumer"/> resumes PER-JOB first (delete-stale + fresh-from-now reschedule, no
/// immediate refire), THEN calls ONE group-level <c>scheduler.ResumeAll()</c> to clear Quartz's
/// <c>pausedTriggerGroups</c> so post-recovery workflows are born <c>Normal</c> again. The binding guarantee
/// is now "no immediate-refire herd," NOT "no group-level resume call ever": (a) the group-level resume runs
/// strictly AFTER all per-job reschedules, and (b) EVERY resulting fresh trigger has <c>StartAt &gt;= now</c>.
/// Driven over an NSubstitute <see cref="IScheduler"/> spy (so the ordering + the single <c>ResumeAll</c>
/// can be asserted via <c>ReceivedCalls()</c>) whose <c>GetTriggerState</c> returns
/// <c>Paused</c> (the resume precondition) and whose <c>ScheduleJob</c> captures prove each fresh trigger's
/// <c>StartAt</c> is ≥ now (skip-to-next, no immediate refire). EVERY Quartz call carries
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

        var consumer = new ResumeAllConsumer(
            store, lifecycle, workflowScheduler, NullLogger<ResumeAllConsumer>.Instance);
        return (consumer, spy, jobId);
    }

    [Fact]
    public async Task Group_Resume_Runs_After_Per_Job_Reschedules()
    {
        var ct = TestContext.Current.CancellationToken;
        var (consumer, spy, _) = Build(ct);

        // Build() already issued a hydration-time ScheduleJob on the spy. Baseline the call count so the
        // ordering assertion below inspects ONLY the Consume-time timeline — otherwise the hydration
        // ScheduleJob would count as a "per-job reschedule" and the ordering could pass even if the resume
        // path issued no reschedule at all (WR-01 / IN-01).
        var callsBefore = spy.ReceivedCalls().Count();

        await consumer.Consume(OrchestratorTestStubs.Context(new ResumeAll { CorrelationId = Guid.NewGuid() }, ct));

        // The load-bearing ORDERING (T-49-01): every per-job fresh reschedule (ScheduleJob) must occur BEFORE
        // the single group-level clear (ResumeAll). Walk ONLY the Consume-time received-call timeline in order.
        var calls = spy.ReceivedCalls().Skip(callsBefore).ToList();
        var methodNames = calls.Select(c => c.GetMethodInfo().Name).ToList();

        var lastScheduleJobIndex = methodNames.FindLastIndex(n => n == nameof(IScheduler.ScheduleJob));
        var resumeAllIndex = methodNames.FindIndex(n => n == nameof(IScheduler.ResumeAll));

        Assert.True(lastScheduleJobIndex >= 0, "expected at least one per-job ScheduleJob (fresh reschedule)");
        Assert.True(resumeAllIndex >= 0, "expected the group-level ResumeAll() clear");
        Assert.True(resumeAllIndex > lastScheduleJobIndex,
            "group-level ResumeAll MUST run AFTER the last per-job ScheduleJob (no stale paused trigger to herd)");

        // Exactly one group-level clear (one ResumeAll message -> one pausedTriggerGroups clear).
        await spy.Received(1).ResumeAll(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resume_Reschedules_Fresh_From_Now_StartAt_Ge_Now()
    {
        var ct = TestContext.Current.CancellationToken;
        var (consumer, spy, _) = Build(ct);

        // Build() already issued a hydration-time ScheduleJob (StartAt is also future, so it would pass the
        // assertion vacuously). Baseline the call count so we inspect ONLY the resume-path reschedules made
        // during Consume — otherwise a broken ResumeAsync that skips its ScheduleAsync would still pass on the
        // leftover hydration trigger alone (WR-01).
        var callsBefore = spy.ReceivedCalls().Count();

        var before = DateTimeOffset.UtcNow;
        await consumer.Consume(OrchestratorTestStubs.Context(new ResumeAll { CorrelationId = Guid.NewGuid() }, ct));

        // Capture EVERY fresh ScheduleJob trigger issued DURING Consume and assert each StartAt >= now (no
        // immediate refire on ANY reschedule — the no-herd guarantee across all per-job resumes).
        var scheduled = spy.ReceivedCalls()
            .Skip(callsBefore)
            .Where(c => c.GetMethodInfo().Name == nameof(IScheduler.ScheduleJob))
            .Select(c => c.GetArguments())
            .Where(args => args.Length >= 2 && args[0] is IJobDetail && args[1] is ITrigger)
            .Select(args => (ITrigger)args[1]!)
            .ToList();

        Assert.NotEmpty(scheduled);
        Assert.All(scheduled, t => Assert.True(t.StartTimeUtc >= before,
            "every resumed trigger must start at-or-after now (fresh-from-now, no immediate refire)"));
    }
}
