using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestrator.Consumers;
using Orchestrator.Hydration;
using Orchestrator.L1;
using Orchestrator.Scheduling;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Phase 32 req-4 / D-06 goal-backward proof of <see cref="FaultUnscheduleConsumer"/>: the
/// MassTransit-auto-published <c>Fault&lt;EntryStepDispatch&gt;</c> drives a keep-L1 unschedule of the
/// Quartz job for the WorkflowId extracted via the double <c>.Message</c> (proven to round-trip by the
/// Plan-01 FaultConsumerBindingFacts). The schedule-owning replica (workflow PRESENT in L1) deletes the
/// job; a workflow ABSENT from L1 (a non-owning replica / unknown workflow) is a no-throw no-op.
/// Real RAM Quartz scheduler + real <see cref="WorkflowL1Store"/> + real <see cref="WorkflowLifecycle"/>
/// (mirrors StopConsumerLifecycleTests) — the unschedule effect is observed via <c>CheckExists</c>.
/// </summary>
[Trait("Category", "Hermetic")]
public sealed class FaultUnscheduleFacts
{
    private static async Task<IScheduler> NewRamSchedulerAsync(CancellationToken ct)
    {
        // Unique instance name per scheduler — StdSchedulerFactory binds schedulers in a SHARED
        // process-wide repository keyed by instance name; a fresh GUID isolates each test's RAMJobStore.
        var props = new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"test-{Guid.NewGuid():N}",
        };
        var scheduler = await new StdSchedulerFactory(props).GetScheduler(ct);
        await scheduler.Start(ct);
        return scheduler;
    }

    private static (FaultUnscheduleConsumer consumer, WorkflowL1Store store, WorkflowScheduler workflowScheduler) Build(
        IScheduler scheduler)
    {
        var store = new WorkflowL1Store();
        var workflowScheduler = new WorkflowScheduler(scheduler, TimeProvider.System);
        // The fault consumer never reads L2 — a benign no-op multiplexer just satisfies the lifecycle ctor.
        var lifecycle = new WorkflowLifecycle(
            OrchestratorTestStubs.NoopRedis(), store, workflowScheduler, TimeProvider.System,
            NullLogger<WorkflowLifecycle>.Instance);
        var consumer = new FaultUnscheduleConsumer(lifecycle, NullLogger<FaultUnscheduleConsumer>.Instance);
        return (consumer, store, workflowScheduler);
    }

    /// <summary>Build a substitute <c>ConsumeContext&lt;Fault&lt;EntryStepDispatch&gt;&gt;</c> whose
    /// <c>Message.Message.WorkflowId</c> resolves to <paramref name="workflowId"/> — exactly the
    /// double-<c>.Message</c> extraction the consumer relies on.</summary>
    private static ConsumeContext<Fault<EntryStepDispatch>> FaultContext(Guid workflowId, CancellationToken ct)
    {
        var dispatch = new EntryStepDispatch(workflowId, Guid.NewGuid(), Guid.NewGuid(), "{}");
        var fault = Substitute.For<Fault<EntryStepDispatch>>();
        fault.Message.Returns(dispatch);   // Fault<T>.Message IS the original EntryStepDispatch instance
        return OrchestratorTestStubs.Context(fault, ct);
    }

    private static void SeedScheduled(WorkflowL1Store store, Guid workflowId, Guid jobId)
    {
        var entry = new WorkflowL1([Guid.NewGuid()], "*/5 * * * *", jobId,
            new Dictionary<Guid, StepProjection>())
        {
            Liveness = new LivenessProjection(DateTime.UtcNow, Interval: 300, Status: "active"),
        };
        store.Upsert(workflowId, entry);
    }

    // ----- req-4: workflow PRESENT in L1 -> the schedule-owning replica unschedules the Quartz job -----

    [Fact]
    public async Task FaultForWorkflowPresentInL1_UnschedulesTheJob_KeepsL1()
    {
        var ct = TestContext.Current.CancellationToken;
        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        var scheduler = await NewRamSchedulerAsync(ct);
        try
        {
            var (consumer, store, workflowScheduler) = Build(scheduler);
            SeedScheduled(store, workflowId, jobId);

            // Schedule the workflow's job exactly as a fire would (jobId-addressed JobKey via the real
            // WorkflowScheduler — builds the production WorkflowFireJob).
            var jobKey = new JobKey(jobId.ToString("D"));
            await workflowScheduler.ScheduleAsync(workflowId, jobId, "*/5 * * * *", ct);
            Assert.True(await scheduler.CheckExists(jobKey, ct));

            await consumer.Consume(FaultContext(workflowId, ct));

            // The schedule-owning replica deleted the job (jobId resolved from THIS workflow's L1 entry),
            // but the L1 entry REMAINS (keep-L1 — UnscheduleOnlyAsync never Removes).
            Assert.False(await scheduler.CheckExists(jobKey, ct));
            Assert.Empty(await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), ct));
            Assert.True(store.TryGet(workflowId, out _));
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }

    // ----- req-4: workflow ABSENT from L1 -> no throw, no unschedule (non-owning replica no-op) --------

    [Fact]
    public async Task FaultForWorkflowAbsentFromL1_NoThrow_NoUnschedule()
    {
        var ct = TestContext.Current.CancellationToken;
        var workflowId = Guid.NewGuid();   // never seeded into L1
        var otherJobId = Guid.NewGuid();

        var scheduler = await NewRamSchedulerAsync(ct);
        try
        {
            var (consumer, _, workflowScheduler) = Build(scheduler);   // empty store — workflow absent from L1

            // A DIFFERENT replica's job exists; the absent-L1 fault must NOT touch it (no jobId to resolve).
            var otherKey = new JobKey(otherJobId.ToString("D"));
            await workflowScheduler.ScheduleAsync(Guid.NewGuid(), otherJobId, "*/5 * * * *", ct);

            // No throw — absent-from-L1 is the defined business no-op inside UnscheduleOnlyAsync.
            await consumer.Consume(FaultContext(workflowId, ct));

            // The unrelated job is untouched (the consumer unscheduled nothing).
            Assert.True(await scheduler.CheckExists(otherKey, ct));
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }

    // ----- req-4: the unschedule is keyed on context.Message.Message.WorkflowId (correct workflow) -----

    [Fact]
    public async Task FaultUnschedulesOnlyTheWorkflowCarriedInFaultMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var targetWorkflowId = Guid.NewGuid();
        var targetJobId = Guid.NewGuid();
        var otherWorkflowId = Guid.NewGuid();
        var otherJobId = Guid.NewGuid();

        var scheduler = await NewRamSchedulerAsync(ct);
        try
        {
            var (consumer, store, workflowScheduler) = Build(scheduler);
            SeedScheduled(store, targetWorkflowId, targetJobId);
            SeedScheduled(store, otherWorkflowId, otherJobId);

            await workflowScheduler.ScheduleAsync(targetWorkflowId, targetJobId, "*/5 * * * *", ct);
            await workflowScheduler.ScheduleAsync(otherWorkflowId, otherJobId, "*/5 * * * *", ct);

            // The fault carries ONLY targetWorkflowId (context.Message.Message.WorkflowId).
            await consumer.Consume(FaultContext(targetWorkflowId, ct));

            // Only the target workflow's job (resolved by its L1 JobId) was deleted; the other survives.
            Assert.False(await scheduler.CheckExists(new JobKey(targetJobId.ToString("D")), ct));
            Assert.True(await scheduler.CheckExists(new JobKey(otherJobId.ToString("D")), ct));
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }
}
