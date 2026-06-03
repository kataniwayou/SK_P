using System.Text.Json;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestrator.Dispatch;
using Orchestrator.Hydration;
using Orchestrator.L1;
using Orchestrator.Scheduling;
using Quartz;
using Quartz.Impl;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Regression guard for the multi-step-hydration-cascade bug: <see cref="WorkflowLifecycle.HydrateAndScheduleAsync"/>
/// must load the FULL reachable step graph into L1 — not just <c>EntryStepIds</c> — so a series
/// (entry -> S2 -> S3 via <c>NextStepIds</c>) can advance through every step.
/// <para>
/// <b>Defect:</b> the original hydration built the L1 step map by iterating ONLY
/// <c>root.EntryStepIds</c>. Downstream steps existed in L2 but were never read into L1, so
/// <see cref="StepAdvancement.SelectNext"/>'s <c>TryGetValue</c> miss on every continuation edge
/// silently ended the cascade after the entry step (no error, balanced metrics). The fix BFS-walks
/// <c>NextStepIds</c> from the entry steps into the L1 map.
/// </para>
/// <para>
/// <b>Red/green:</b> the L1-map assertion below (S2 + S3 present, <c>Steps.Count == 3</c>) FAILS
/// against the old entry-only <c>foreach (var stepId in root.EntryStepIds)</c> hydration (only S1 in
/// the map) and PASSES with the BFS fix. The end-to-end <c>SelectNext</c> walk additionally proves the
/// observable consequence: with only S1 in L1, the S1->S2 edge is a graceful skip and the cascade dies.
/// </para>
/// </summary>
public sealed class MultiStepHydrationCascadeTests
{
    // ----- redis mux stub (mirrors HydrationTests.Mux) ------------------------------------------

    private static IConnectionMultiplexer Mux(IReadOnlyDictionary<string, string> values)
    {
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var key = ((RedisKey)ci[0]).ToString();
                return values.TryGetValue(key, out var v) ? (RedisValue)v : RedisValue.Null;
            });

        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    private static string SerializeRoot(Guid jobId, Guid entryStepId) =>
        JsonSerializer.Serialize(new WorkflowRootProjection(
            EntryStepIds: [entryStepId],
            Cron: "*/5 * * * *",
            JobId: jobId,
            Liveness: new LivenessProjection(DateTime.UtcNow, Interval: 0, Status: "active"),
            CorrelationId: Guid.NewGuid().ToString()));

    /// <summary>An Always-gated (EntryCondition 4) step pointing at <paramref name="nextStepIds"/>.</summary>
    private static string SerializeStep(Guid processorId, params Guid[] nextStepIds) =>
        JsonSerializer.Serialize(new StepProjection(
            EntryCondition: 4 /* Always */, ProcessorId: processorId, Payload: "{}", NextStepIds: [.. nextStepIds]));

    private static async Task<IScheduler> NewRamSchedulerAsync()
    {
        var props = new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"test-{Guid.NewGuid():N}",
        };
        var scheduler = await new StdSchedulerFactory(props).GetScheduler();
        await scheduler.Start();
        return scheduler;
    }

    private static WorkflowLifecycle NewLifecycle(IConnectionMultiplexer mux, IWorkflowL1Store store, IScheduler scheduler)
    {
        var workflowScheduler = new WorkflowScheduler(scheduler, TimeProvider.System);
        return new WorkflowLifecycle(
            mux, store, workflowScheduler, TimeProvider.System, NullLogger<WorkflowLifecycle>.Instance);
    }

    // ----- the regression --------------------------------------------------------------------

    [Fact]
    public async Task HydratesFullReachableGraph_NotJustEntryStep_SoSeriesAdvances()
    {
        var ct = TestContext.Current.CancellationToken;

        var wfId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var s1 = Guid.NewGuid(); // entry
        var s2 = Guid.NewGuid(); // downstream
        var s3 = Guid.NewGuid(); // terminal

        // S1 -> S2 -> S3 series. Only S1 is in EntryStepIds; S2/S3 are reachable ONLY via NextStepIds.
        var values = new Dictionary<string, string>
        {
            [L2ProjectionKeys.Root(wfId)] = SerializeRoot(jobId, s1),
            [L2ProjectionKeys.Step(wfId, s1)] = SerializeStep(Guid.NewGuid(), s2),
            [L2ProjectionKeys.Step(wfId, s2)] = SerializeStep(Guid.NewGuid(), s3),
            [L2ProjectionKeys.Step(wfId, s3)] = SerializeStep(Guid.NewGuid()), // terminal — no NextStepIds
        };

        var store = new WorkflowL1Store();
        var scheduler = await NewRamSchedulerAsync();
        try
        {
            var lifecycle = NewLifecycle(Mux(values), store, scheduler);
            await lifecycle.HydrateAndScheduleAsync(wfId, ct);

            Assert.True(store.TryGet(wfId, out var entry));

            // CORE REGRESSION ASSERTION: the FULL reachable graph is in L1, not just the entry step.
            // Entry-only hydration would leave Steps == { s1 } and these would fail.
            Assert.Equal(3, entry.Steps.Count);
            Assert.Contains(s1, entry.Steps.Keys);
            Assert.Contains(s2, entry.Steps.Keys);
            Assert.Contains(s3, entry.Steps.Keys);

            // OBSERVABLE CONSEQUENCE: SelectNext can now walk the whole series. Under entry-only
            // hydration the S1->S2 edge is a TryGetValue miss (graceful skip) and the cascade dies at S1.
            var advancement = new StepAdvancement();

            var afterS1 = advancement.SelectNext(StepOutcome.Completed, entry.Steps[s1], entry.Steps).ToList();
            Assert.Single(afterS1);
            Assert.Equal(s2, afterS1[0].stepId);

            var afterS2 = advancement.SelectNext(StepOutcome.Completed, entry.Steps[s2], entry.Steps).ToList();
            Assert.Single(afterS2);
            Assert.Equal(s3, afterS2[0].stepId);

            // S3 is terminal — no successors.
            var afterS3 = advancement.SelectNext(StepOutcome.Completed, entry.Steps[s3], entry.Steps).ToList();
            Assert.Empty(afterS3);
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }
}
