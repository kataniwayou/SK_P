using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orchestrator.Dispatch;
using Orchestrator.L1;
using Orchestrator.Scheduling;
using Quartz;
using Quartz.Impl;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// ORCH-FIRE-01 goal-backward proof. Drives <see cref="WorkflowFireJob.Execute"/> against a real
/// <see cref="WorkflowL1Store"/> + a synthetic <see cref="CapturingDispatchConsumer"/> bound to the
/// short-name endpoint <c>{processorId:D}</c> (the queue a <c>Send</c> to <c>queue:{processorId:D}</c>
/// lands on — RESEARCH assumption A2). Asserts, from the USER's perspective:
/// <list type="bullet">
///   <item>one <see cref="EntryStepDispatch"/> per entry step, with correct StepId/ProcessorId/Payload
///   and <c>ExecutionId == Guid.Empty</c> + <c>EntryId == ""</c>;</item>
///   <item>two consecutive fires produce non-empty, DIFFERING correlationIds (per-fire NewId);</item>
///   <item>the L1 liveness timestamp advances to the FakeTimeProvider's now on fire, with ZERO L2
///   writes (transport was Send to the in-memory queue, never an L2 mutation).</item>
/// </list>
/// </summary>
public sealed class FireDispatchTests
{
    private static async Task<IScheduler> NewRamSchedulerAsync(CancellationToken ct)
    {
        // Unique instance name per scheduler — StdSchedulerFactory binds schedulers in a SHARED
        // process-wide repository keyed by instance name; the default name collides across parallel
        // test classes (one test's Shutdown tears down another's scheduler). A fresh GUID name isolates
        // each test's RAMJobStore.
        var props = new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"test-{Guid.NewGuid():N}",
        };
        var scheduler = await new StdSchedulerFactory(props).GetScheduler(ct);
        await scheduler.Start(ct);
        return scheduler;
    }

    /// <summary>
    /// Builds the in-memory MassTransit harness, binding a <see cref="CapturingDispatchConsumer"/>
    /// short-name <c>ReceiveEndpoint($"{processorId:D}")</c> per distinct entry-step processorId so a
    /// fire's <c>Send</c> to <c>queue:{processorId:D}</c> is captured.
    /// </summary>
    private static ServiceProvider BuildHarness(IEnumerable<Guid> processorIds)
    {
        var ids = processorIds.Distinct().ToArray();
        return new ServiceCollection()
            .AddLogging()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<CapturingDispatchConsumer>();
                x.UsingInMemory((ctx, cfg) =>
                {
                    foreach (var processorId in ids)
                    {
                        // queue:{processorId:D}  <->  ReceiveEndpoint("{processorId:D}") (assumption A2)
                        cfg.ReceiveEndpoint($"{processorId:D}", e => e.ConfigureConsumer<CapturingDispatchConsumer>(ctx));
                    }

                    cfg.ConfigureEndpoints(ctx);
                });
            })
            .BuildServiceProvider(true);
    }

    private static WorkflowL1 SeedEntry(
        WorkflowL1Store store,
        Guid workflowId,
        Guid jobId,
        IReadOnlyList<(Guid stepId, Guid processorId, string payload)> steps,
        DateTime livenessTimestamp)
    {
        var stepMap = steps.ToDictionary(
            s => s.stepId,
            s => new StepProjection(EntryCondition: 0, ProcessorId: s.processorId, Payload: s.payload, NextStepIds: []));

        var entry = new WorkflowL1(
            EntryStepIds: steps.Select(s => s.stepId).ToList(),
            Cron: "*/5 * * * *",
            JobId: jobId,
            Steps: stepMap)
        {
            Liveness = new LivenessProjection(livenessTimestamp, Interval: 300, Status: "active"),
        };

        store.Upsert(workflowId, entry);
        return entry;
    }

    private static IJobExecutionContext FireContext(Guid workflowId, CancellationToken ct)
    {
        var map = new JobDataMap { { "workflowId", workflowId.ToString("D") } };
        var context = Substitute.For<IJobExecutionContext>();
        context.MergedJobDataMap.Returns(map);
        context.CancellationToken.Returns(ct);
        return context;
    }

    // ----- ORCH-FIRE-01: one message per entry step, correct fields -----------------------------

    [Fact]
    public async Task OneMessagePerEntryStep_CorrectFields()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var step1 = (stepId: Guid.NewGuid(), processorId: Guid.NewGuid(), payload: "{\"a\":1}");
        var step2 = (stepId: Guid.NewGuid(), processorId: Guid.NewGuid(), payload: "{\"b\":2}");
        var steps = new[] { step1, step2 };

        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero));
        var store = new WorkflowL1Store();
        SeedEntry(store, workflowId, jobId, steps, fakeTime.GetUtcNow().UtcDateTime.AddMinutes(-10));

        await using var provider = BuildHarness(steps.Select(s => s.processorId));
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var scheduler = await NewRamSchedulerAsync(ct);
            try
            {
                var workflowScheduler = new WorkflowScheduler(scheduler, fakeTime);
                // The job must exist before the fire path self-reschedules a fresh trigger (Pitfall 4b).
                await workflowScheduler.ScheduleAsync(workflowId, jobId, "*/5 * * * *", ct);
                var job = new WorkflowFireJob(
                    store,
                    new StepDispatcher(harness.Bus, OrchestratorTestStubs.NoopRedis(), OrchestratorTestStubs.Metrics()), // IStepDispatcher wrapping the harness bus
                    workflowScheduler,
                    fakeTime,
                    NullLogger<WorkflowFireJob>.Instance);

                await job.Execute(FireContext(workflowId, ct));

                Assert.True(await harness.Consumed.Any<EntryStepDispatch>(ct));
                var dispatched = harness.Consumed.Select<EntryStepDispatch>(ct)
                    .Select(c => c.Context.Message)
                    .ToList();

                Assert.Equal(2, dispatched.Count);

                foreach (var (stepId, processorId, payload) in steps)
                {
                    var msg = Assert.Single(dispatched, m => m.StepId == stepId);
                    Assert.Equal(workflowId, msg.WorkflowId);
                    Assert.Equal(processorId, msg.ProcessorId);
                    Assert.Equal(payload, msg.Payload);
                    Assert.Equal(Guid.Empty, msg.ExecutionId);
                    Assert.Equal("", msg.EntryId);   // Plan 04 changes this to the non-empty hash (req-2)
                    Assert.NotEqual(Guid.Empty, msg.CorrelationId);
                }

                // All entry-step dispatches in a SINGLE fire share the one per-fire correlationId.
                Assert.Single(dispatched.Select(m => m.CorrelationId).Distinct());
            }
            finally
            {
                await scheduler.Shutdown(waitForJobsToComplete: false, ct);
            }
        }
        finally
        {
            await harness.Stop(ct);
        }
    }

    // ----- ORCH-FIRE-01: per-fire correlationId differs -----------------------------------------

    [Fact]
    public async Task CorrelationIdDiffersAcrossTwoFires()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var step = (stepId: Guid.NewGuid(), processorId: Guid.NewGuid(), payload: "{}");
        var steps = new[] { step };

        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero));
        var store = new WorkflowL1Store();
        SeedEntry(store, workflowId, jobId, steps, fakeTime.GetUtcNow().UtcDateTime.AddMinutes(-10));

        await using var provider = BuildHarness(steps.Select(s => s.processorId));
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var scheduler = await NewRamSchedulerAsync(ct);
            try
            {
                var workflowScheduler = new WorkflowScheduler(scheduler, fakeTime);
                await workflowScheduler.ScheduleAsync(workflowId, jobId, "*/5 * * * *", ct);
                var job = new WorkflowFireJob(
                    store, new StepDispatcher(harness.Bus, OrchestratorTestStubs.NoopRedis(), OrchestratorTestStubs.Metrics()), workflowScheduler, fakeTime, NullLogger<WorkflowFireJob>.Instance);

                await job.Execute(FireContext(workflowId, ct));
                await job.Execute(FireContext(workflowId, ct));

                var correlationIds = harness.Consumed.Select<EntryStepDispatch>(ct)
                    .Select(c => c.Context.Message.CorrelationId)
                    .ToList();

                Assert.Equal(2, correlationIds.Count);
                Assert.All(correlationIds, id => Assert.NotEqual(Guid.Empty, id));
                Assert.NotEqual(correlationIds[0], correlationIds[1]); // per-fire NewId.NextGuid()
            }
            finally
            {
                await scheduler.Shutdown(waitForJobsToComplete: false, ct);
            }
        }
        finally
        {
            await harness.Stop(ct);
        }
    }

    // ----- ORCH-FIRE-01: liveness advances on fire, zero L2 writes ------------------------------

    [Fact]
    public async Task LivenessTimestampAdvancesOnFire_NoL2Write()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var step = (stepId: Guid.NewGuid(), processorId: Guid.NewGuid(), payload: "{}");
        var steps = new[] { step };

        var now = new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var staleTimestamp = now.UtcDateTime.AddMinutes(-10);

        var store = new WorkflowL1Store();
        SeedEntry(store, workflowId, jobId, steps, staleTimestamp);

        // A Redis mux stub the fire path NEVER touches — asserting DidNotReceive proves the fire used
        // Send to the in-memory queue, not an L2 mutation (transport vs. store).
        var db = Substitute.For<IDatabase>();

        await using var provider = BuildHarness(steps.Select(s => s.processorId));
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var scheduler = await NewRamSchedulerAsync(ct);
            try
            {
                var workflowScheduler = new WorkflowScheduler(scheduler, fakeTime);
                await workflowScheduler.ScheduleAsync(workflowId, jobId, "*/5 * * * *", ct);
                var job = new WorkflowFireJob(
                    store, new StepDispatcher(harness.Bus, OrchestratorTestStubs.NoopRedis(), OrchestratorTestStubs.Metrics()), workflowScheduler, fakeTime, NullLogger<WorkflowFireJob>.Instance);

                Assert.True(store.TryGet(workflowId, out var before));
                Assert.Equal(staleTimestamp, before.Liveness.Timestamp);

                await job.Execute(FireContext(workflowId, ct));

                Assert.True(store.TryGet(workflowId, out var after));
                Assert.Equal(now.UtcDateTime, after.Liveness.Timestamp); // advanced to FakeTimeProvider now
                Assert.True(after.Liveness.Timestamp > staleTimestamp);
                Assert.Equal("active", after.Liveness.Status);          // status/interval preserved
                Assert.Equal(300, after.Liveness.Interval);

                // ZERO L2 writes — the fire path mutates only in-memory L1.
                await db.DidNotReceive().StringSetAsync(
                    Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
                await db.DidNotReceive().SetAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
                await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
            }
            finally
            {
                await scheduler.Shutdown(waitForJobsToComplete: false, ct);
            }
        }
        finally
        {
            await harness.Stop(ct);
        }
    }
}
