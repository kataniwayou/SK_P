using System.Reflection;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging;
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
/// Phase 32 req-8 (idempotency half) + D-13 guard for <see cref="FaultUnscheduleConsumer"/>: two
/// identical fault deliveries leave the SAME end state as one (job unscheduled, L1 kept, marker
/// untouched — the halt is naturally idempotent, NO CAS gate). And D-13 by construction: the consumer
/// takes NO Redis dependency — only <see cref="WorkflowLifecycle"/> + <see cref="ILogger{T}"/> — so it
/// CANNOT read/write a <c>flag[H]</c> key or the <c>skp:cancelled:*</c> marker (T-32-08c / Pitfall 4).
/// </summary>
[Trait("Category", "Hermetic")]
public sealed class FaultIdempotencyFacts
{
    private static async Task<IScheduler> NewRamSchedulerAsync(CancellationToken ct)
    {
        var props = new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"test-{Guid.NewGuid():N}",
        };
        var scheduler = await new StdSchedulerFactory(props).GetScheduler(ct);
        await scheduler.Start(ct);
        return scheduler;
    }

    private static ConsumeContext<Fault<EntryStepDispatch>> FaultContext(Guid workflowId, CancellationToken ct)
    {
        var dispatch = new EntryStepDispatch(workflowId, Guid.NewGuid(), Guid.NewGuid(), "{}");
        var fault = Substitute.For<Fault<EntryStepDispatch>>();
        fault.Message.Returns(dispatch);   // double-.Message: Fault<T>.Message IS the EntryStepDispatch
        return OrchestratorTestStubs.Context(fault, ct);
    }

    // ----- req-8: TWO identical fault deliveries leave the SAME end state as one (idempotent halt) -----

    [Fact]
    public async Task TwoIdenticalFaultDeliveries_LeaveSameEndStateAsOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        var scheduler = await NewRamSchedulerAsync(ct);
        try
        {
            var store = new WorkflowL1Store();
            var workflowScheduler = new WorkflowScheduler(scheduler, TimeProvider.System);
            // A db substitute purely to PROVE the path issues no flag[H]/marker write (asserted below).
            var db = Substitute.For<IDatabase>();
            db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);
            var mux = Substitute.For<IConnectionMultiplexer>();
            mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
            var lifecycle = new WorkflowLifecycle(
                mux, store, workflowScheduler, TimeProvider.System, NullLogger<WorkflowLifecycle>.Instance);
            var consumer = new FaultUnscheduleConsumer(lifecycle, NullLogger<FaultUnscheduleConsumer>.Instance);

            var entry = new WorkflowL1([Guid.NewGuid()], "*/5 * * * *", jobId, new Dictionary<Guid, StepProjection>())
            {
                Liveness = new LivenessProjection(DateTime.UtcNow, Interval: 300, Status: "active"),
            };
            store.Upsert(workflowId, entry);

            var jobKey = new JobKey(jobId.ToString("D"));
            await workflowScheduler.ScheduleAsync(workflowId, jobId, "*/5 * * * *", ct);
            Assert.True(await scheduler.CheckExists(jobKey, ct));

            var fault = FaultContext(workflowId, ct);

            // First delivery: job deleted, L1 kept.
            await consumer.Consume(fault);
            Assert.False(await scheduler.CheckExists(jobKey, ct));
            Assert.True(store.TryGet(workflowId, out _));

            // SECOND identical delivery: no throw, end state UNCHANGED (job still gone, L1 still kept).
            // DeleteJob on an absent job is idempotent — duplicate fault deliveries collapse naturally.
            await consumer.Consume(fault);
            Assert.False(await scheduler.CheckExists(jobKey, ct));
            Assert.Empty(await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), ct));
            Assert.True(store.TryGet(workflowId, out _));

            // D-13: across BOTH deliveries the fault path wrote NO flag[H] key and NO cancelled marker —
            // no StringSetAsync to ANY key, across every overload (the processor owns the marker, not this).
            await db.DidNotReceive().StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
            await db.DidNotReceive().StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>());
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }

    // ----- D-13 / T-32-08c: the consumer CANNOT touch flag[H] or the marker — no Redis dep by ctor -----

    [Fact]
    public void Consumer_TakesNoRedisDependency_OnlyLifecycleAndLogger()
    {
        // The cleanest D-13 proof: the FaultUnscheduleConsumer ctor has EXACTLY two params —
        // WorkflowLifecycle + ILogger<FaultUnscheduleConsumer> — and NO IConnectionMultiplexer / IDatabase.
        // With no Redis handle it cannot read/write flag[H] or skp:cancelled:* by construction.
        var ctors = typeof(FaultUnscheduleConsumer).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        var ctor = Assert.Single(ctors);

        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.Equal(2, paramTypes.Length);
        Assert.Contains(typeof(WorkflowLifecycle), paramTypes);
        Assert.Contains(typeof(ILogger<FaultUnscheduleConsumer>), paramTypes);

        // No Redis handle of any kind on the ctor (it CANNOT touch flag[H] / the marker).
        Assert.DoesNotContain(typeof(IConnectionMultiplexer), paramTypes);
        Assert.DoesNotContain(typeof(IDatabase), paramTypes);
    }
}
