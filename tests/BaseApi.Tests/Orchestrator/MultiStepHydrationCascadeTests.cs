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
        SerializeRootMulti(jobId, entryStepId);

    /// <summary>Root projection with one OR MORE entry steps (each the head of a separate pipeline).</summary>
    private static string SerializeRootMulti(Guid jobId, params Guid[] entryStepIds) =>
        JsonSerializer.Serialize(new WorkflowRootProjection(
            EntryStepIds: [.. entryStepIds],
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

    /// <summary>
    /// Regression guard for the two-separated-pipelines topology: ONE workflow with TWO entry steps,
    /// each the head of an independent series (pipeline A: a1->a2->a3; pipeline B: b1->b2->b3->b4), one
    /// schedule, NOT merged. Hydration must seed the BFS from ALL <c>EntryStepIds</c> so BOTH full graphs
    /// land in L1, and advancement must walk each pipeline independently with no cross-pipeline edge.
    /// <para>
    /// <b>Red/green:</b> entry-only hydration would leave <c>Steps</c> == { a1, b1 } (count 2), so the
    /// <c>Steps.Count == 7</c> assertion FAILS on the old code and PASSES with the BFS fix. The disjoint /
    /// no-cross-edge assertions prove the two pipelines stay SEPARATED (no accidental merge).
    /// </para>
    /// </summary>
    [Fact]
    public async Task HydratesMultipleEntryPipelines_Separated_NoMerge()
    {
        var ct = TestContext.Current.CancellationToken;

        var wfId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        // Pipeline A: a1 (entry) -> a2 -> a3 (terminal)
        Guid a1 = Guid.NewGuid(), a2 = Guid.NewGuid(), a3 = Guid.NewGuid();
        // Pipeline B: b1 (entry) -> b2 -> b3 -> b4 (terminal)
        Guid b1 = Guid.NewGuid(), b2 = Guid.NewGuid(), b3 = Guid.NewGuid(), b4 = Guid.NewGuid();

        // TWO entry steps; downstream steps reachable ONLY via NextStepIds (never in EntryStepIds).
        var values = new Dictionary<string, string>
        {
            [L2ProjectionKeys.Root(wfId)] = SerializeRootMulti(jobId, a1, b1),
            [L2ProjectionKeys.Step(wfId, a1)] = SerializeStep(Guid.NewGuid(), a2),
            [L2ProjectionKeys.Step(wfId, a2)] = SerializeStep(Guid.NewGuid(), a3),
            [L2ProjectionKeys.Step(wfId, a3)] = SerializeStep(Guid.NewGuid()),
            [L2ProjectionKeys.Step(wfId, b1)] = SerializeStep(Guid.NewGuid(), b2),
            [L2ProjectionKeys.Step(wfId, b2)] = SerializeStep(Guid.NewGuid(), b3),
            [L2ProjectionKeys.Step(wfId, b3)] = SerializeStep(Guid.NewGuid(), b4),
            [L2ProjectionKeys.Step(wfId, b4)] = SerializeStep(Guid.NewGuid()),
        };

        var store = new WorkflowL1Store();
        var scheduler = await NewRamSchedulerAsync();
        try
        {
            var lifecycle = NewLifecycle(Mux(values), store, scheduler);
            await lifecycle.HydrateAndScheduleAsync(wfId, ct);

            Assert.True(store.TryGet(wfId, out var entry));

            // CORE REGRESSION: BOTH full pipelines hydrated from the TWO entry steps (3 + 4 = 7 steps).
            // Entry-only hydration would leave Steps == { a1, b1 } (count 2) and this would fail.
            Assert.Equal(7, entry.Steps.Count);
            foreach (var id in new[] { a1, a2, a3, b1, b2, b3, b4 })
                Assert.Contains(id, entry.Steps.Keys);

            var adv = new StepAdvancement();

            // Pipeline A walks independently to its own terminal.
            Assert.Equal(a2, adv.SelectNext(StepOutcome.Completed, entry.Steps[a1], entry.Steps).Single().stepId);
            Assert.Equal(a3, adv.SelectNext(StepOutcome.Completed, entry.Steps[a2], entry.Steps).Single().stepId);
            Assert.Empty(adv.SelectNext(StepOutcome.Completed, entry.Steps[a3], entry.Steps));

            // Pipeline B walks independently to its own terminal.
            Assert.Equal(b2, adv.SelectNext(StepOutcome.Completed, entry.Steps[b1], entry.Steps).Single().stepId);
            Assert.Equal(b3, adv.SelectNext(StepOutcome.Completed, entry.Steps[b2], entry.Steps).Single().stepId);
            Assert.Equal(b4, adv.SelectNext(StepOutcome.Completed, entry.Steps[b3], entry.Steps).Single().stepId);
            Assert.Empty(adv.SelectNext(StepOutcome.Completed, entry.Steps[b4], entry.Steps));

            // SEPARATION: the two pipelines are disjoint and no step advances across the boundary.
            var pipelineA = new HashSet<Guid> { a1, a2, a3 };
            var pipelineB = new HashSet<Guid> { b1, b2, b3, b4 };
            Assert.Empty(pipelineA.Intersect(pipelineB));
            foreach (var id in pipelineA)
                foreach (var (next, _) in adv.SelectNext(StepOutcome.Completed, entry.Steps[id], entry.Steps))
                    Assert.DoesNotContain(next, pipelineB);
            foreach (var id in pipelineB)
                foreach (var (next, _) in adv.SelectNext(StepOutcome.Completed, entry.Steps[id], entry.Steps))
                    Assert.DoesNotContain(next, pipelineA);
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }

    /// <summary>
    /// Regression guard for the FAN-OUT topology: a single step with MULTIPLE <c>NextStepIds</c>
    /// (<c>s2 -> { bMain, bSide }</c>), each branch ending in its own terminal. Distinct from the
    /// linear-chain facts above — it locks two behaviors the linear tests cannot exercise:
    /// (1) BFS hydration follows EVERY outgoing edge of a node (not just the first), so BOTH branches'
    /// steps land in L1; (2) <see cref="StepAdvancement.SelectNext"/> returns MULTIPLE successors for
    /// the fan-out node (the orchestrator dispatches one continuation per match).
    /// <para>
    /// <b>Red/green:</b> entry-only hydration leaves <c>Steps == { s1 }</c> (count 1) → fails; a
    /// first-edge-only BFS would drop <c>bSide</c>/<c>bSideTerm</c> (count 4, <c>SelectNext(s2)</c>
    /// yields 1) → fails; the full BFS fix loads all 6 and <c>SelectNext(s2)</c> yields both → passes.
    /// </para>
    /// </summary>
    [Fact]
    public async Task HydratesFanOutBranch_BothSuccessorsAndBothTerminals()
    {
        var ct = TestContext.Current.CancellationToken;

        var wfId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var s1 = Guid.NewGuid();        // entry
        var s2 = Guid.NewGuid();        // FAN-OUT node -> two branches
        var bMain = Guid.NewGuid();     // branch 1 head
        var bMainTerm = Guid.NewGuid(); // branch 1 terminal
        var bSide = Guid.NewGuid();     // branch 2 head
        var bSideTerm = Guid.NewGuid(); // branch 2 terminal

        // s1 -> s2 -> { bMain -> bMainTerm , bSide -> bSideTerm }. s2 has TWO NextStepIds (fan-out);
        // every downstream step is reachable ONLY via NextStepIds (never in EntryStepIds).
        var values = new Dictionary<string, string>
        {
            [L2ProjectionKeys.Root(wfId)] = SerializeRootMulti(jobId, s1),
            [L2ProjectionKeys.Step(wfId, s1)] = SerializeStep(Guid.NewGuid(), s2),
            [L2ProjectionKeys.Step(wfId, s2)] = SerializeStep(Guid.NewGuid(), bMain, bSide), // fan-out
            [L2ProjectionKeys.Step(wfId, bMain)] = SerializeStep(Guid.NewGuid(), bMainTerm),
            [L2ProjectionKeys.Step(wfId, bMainTerm)] = SerializeStep(Guid.NewGuid()),
            [L2ProjectionKeys.Step(wfId, bSide)] = SerializeStep(Guid.NewGuid(), bSideTerm),
            [L2ProjectionKeys.Step(wfId, bSideTerm)] = SerializeStep(Guid.NewGuid()),
        };

        var store = new WorkflowL1Store();
        var scheduler = await NewRamSchedulerAsync();
        try
        {
            var lifecycle = NewLifecycle(Mux(values), store, scheduler);
            await lifecycle.HydrateAndScheduleAsync(wfId, ct);

            Assert.True(store.TryGet(wfId, out var entry));

            // (1) BFS followed BOTH edges off the fan-out node — all 6 reachable steps in L1.
            Assert.Equal(6, entry.Steps.Count);
            foreach (var id in new[] { s1, s2, bMain, bMainTerm, bSide, bSideTerm })
                Assert.Contains(id, entry.Steps.Keys);

            var adv = new StepAdvancement();

            // s1 -> s2 (single successor).
            Assert.Equal(s2, adv.SelectNext(StepOutcome.Completed, entry.Steps[s1], entry.Steps).Single().stepId);

            // (2) FAN-OUT: s2 yields BOTH successors (the multi-successor case the linear facts never hit).
            var afterS2 = adv.SelectNext(StepOutcome.Completed, entry.Steps[s2], entry.Steps)
                .Select(n => n.stepId).ToHashSet();
            Assert.Equal(2, afterS2.Count);
            Assert.Contains(bMain, afterS2);
            Assert.Contains(bSide, afterS2);

            // Each branch walks independently to its own terminal.
            Assert.Equal(bMainTerm, adv.SelectNext(StepOutcome.Completed, entry.Steps[bMain], entry.Steps).Single().stepId);
            Assert.Equal(bSideTerm, adv.SelectNext(StepOutcome.Completed, entry.Steps[bSide], entry.Steps).Single().stepId);

            // Both leaves terminate (no successors).
            Assert.Empty(adv.SelectNext(StepOutcome.Completed, entry.Steps[bMainTerm], entry.Steps));
            Assert.Empty(adv.SelectNext(StepOutcome.Completed, entry.Steps[bSideTerm], entry.Steps));
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }

    /// <summary>
    /// Behavior lock for the MERGE (diamond / fan-in) topology: a node reachable from two predecessors
    /// (<c>e -> { p1, p2 }; p1 -> m; p2 -> m; m -> mTerm</c>). Two INTENDED properties are asserted:
    /// <list type="number">
    /// <item><b>Hydration loads the merge node exactly once</b> — BFS reaches <c>m</c> from both
    /// <c>p1</c> and <c>p2</c>, but the <c>seen</c> guard enqueues it once, so L1 holds 5 distinct steps
    /// (no infinite loop, no duplicate L1 entry on the diamond).</item>
    /// <item><b>NO join/barrier — each predecessor independently advances to the merge node.</b>
    /// <c>SelectNext(p1)</c> AND <c>SelectNext(p2)</c> EACH yield <c>m</c>, so at runtime the orchestrator
    /// dispatches <c>m</c> (and re-runs its whole subtree) ONCE PER COMPLETED PREDECESSOR. This duplicate
    /// execution on convergence is the REQUESTED/intended behavior (confirmed against the live ES proof:
    /// the merge node + subtree executed twice per trigger). There is deliberately no fan-in dedup; this
    /// test guards that intent so a future change can't silently add a join barrier.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task MergeNode_LoadedOnce_ButEachPredecessorIndependentlyAdvancesToIt()
    {
        var ct = TestContext.Current.CancellationToken;

        var wfId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var e = Guid.NewGuid();      // entry, fans out to p1 + p2
        var p1 = Guid.NewGuid();     // predecessor 1 of the merge node
        var p2 = Guid.NewGuid();     // predecessor 2 of the merge node
        var m = Guid.NewGuid();      // MERGE node — two incoming edges (p1, p2)
        var mTerm = Guid.NewGuid();  // terminal after the merge node

        var values = new Dictionary<string, string>
        {
            [L2ProjectionKeys.Root(wfId)] = SerializeRootMulti(jobId, e),
            [L2ProjectionKeys.Step(wfId, e)] = SerializeStep(Guid.NewGuid(), p1, p2), // fan-out
            [L2ProjectionKeys.Step(wfId, p1)] = SerializeStep(Guid.NewGuid(), m),     // p1 -> m
            [L2ProjectionKeys.Step(wfId, p2)] = SerializeStep(Guid.NewGuid(), m),     // p2 -> m (merge)
            [L2ProjectionKeys.Step(wfId, m)] = SerializeStep(Guid.NewGuid(), mTerm),
            [L2ProjectionKeys.Step(wfId, mTerm)] = SerializeStep(Guid.NewGuid()),
        };

        var store = new WorkflowL1Store();
        var scheduler = await NewRamSchedulerAsync();
        try
        {
            var lifecycle = NewLifecycle(Mux(values), store, scheduler);
            await lifecycle.HydrateAndScheduleAsync(wfId, ct);

            Assert.True(store.TryGet(wfId, out var entry));

            // (1) Diamond hydrated WITHOUT duplication/loop — m loaded exactly once → 5 distinct steps.
            Assert.Equal(5, entry.Steps.Count);
            foreach (var id in new[] { e, p1, p2, m, mTerm })
                Assert.Contains(id, entry.Steps.Keys);

            var adv = new StepAdvancement();

            // (2) NO JOIN: each predecessor independently advances to the merge node — so at runtime
            // m is dispatched once per completed predecessor (intended duplicate execution).
            Assert.Equal(m, adv.SelectNext(StepOutcome.Completed, entry.Steps[p1], entry.Steps).Single().stepId);
            Assert.Equal(m, adv.SelectNext(StepOutcome.Completed, entry.Steps[p2], entry.Steps).Single().stepId);

            // The merge node continues to its terminal; terminal has no successors.
            Assert.Equal(mTerm, adv.SelectNext(StepOutcome.Completed, entry.Steps[m], entry.Steps).Single().stepId);
            Assert.Empty(adv.SelectNext(StepOutcome.Completed, entry.Steps[mTerm], entry.Steps));
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }
}
