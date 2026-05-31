using System.Text.Json;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestrator.Hydration;
using Orchestrator.L1;
using Orchestrator.Scheduling;
using Quartz;
using Quartz.Impl;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Proves ORCH-STARTUP-01 + ORCH-ACK-01 for <see cref="WorkflowLifecycle.HydrateAndScheduleAsync"/>
/// against a real <see cref="WorkflowL1Store"/> and a real Quartz RAMJobStore scheduler:
/// <list type="bullet">
///   <item>N parent-index workflows hydrate into L1 (workflow + step entries only — no processor key,
///   no parent-index key); <c>store.WorkflowIds</c> contains exactly the N workflow GUIDs.</item>
///   <item>A corrupt (malformed-JSON) root is skipped; the other N-1 hydrate and no exception escapes
///   (host-stays-up proxy, ORCH-ACK-01).</item>
/// </list>
/// </summary>
public sealed class HydrationTests
{
    // ----- redis mux stub -----------------------------------------------------------------------

    /// <summary>
    /// Builds a multiplexer whose <c>SetMembersAsync(ParentIndex())</c> returns the supplied workflow
    /// GUIDs, and whose <c>StringGetAsync(Root/Step)</c> returns the serialized value registered for
    /// that exact key (or <see cref="RedisValue.Null"/> if absent).
    /// </summary>
    private static IConnectionMultiplexer Mux(
        IReadOnlyList<Guid> parentIndexIds,
        IReadOnlyDictionary<string, string> values)
    {
        var db = Substitute.For<IDatabase>();

        var members = parentIndexIds.Select(id => (RedisValue)id.ToString("D")).ToArray();
        db.SetMembersAsync(L2ProjectionKeys.ParentIndex(), Arg.Any<CommandFlags>()).Returns(members);

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

    private static string SerializeRoot(Guid jobId, Guid stepId) =>
        JsonSerializer.Serialize(new WorkflowRootProjection(
            EntryStepIds: [stepId],
            Cron: "*/5 * * * *",
            JobId: jobId,
            Liveness: new LivenessProjection(DateTime.UtcNow, Interval: 0, Status: "active"),
            CorrelationId: Guid.NewGuid().ToString()));

    private static string SerializeStep(Guid processorId) =>
        JsonSerializer.Serialize(new StepProjection(
            EntryCondition: 0,
            ProcessorId: processorId,
            Payload: "{}",
            NextStepIds: []));

    private static async Task<IScheduler> NewRamSchedulerAsync()
    {
        // Unique instance name — StdSchedulerFactory binds schedulers in a SHARED process-wide
        // repository keyed by instance name; the default name collides across parallel test classes
        // (Plan 05 added several scheduler-using classes). A fresh GUID name isolates each scheduler.
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

    // ----- ORCH-STARTUP-01: N hydrate, no processor/parent-index key ----------------------------

    [Fact]
    public async Task HydratesAllParentIndexWorkflows_NoProcessorOrParentIndexKey()
    {
        var ct = TestContext.Current.CancellationToken;
        const int n = 3;

        var ids = Enumerable.Range(0, n).Select(_ => Guid.NewGuid()).ToList();
        var values = new Dictionary<string, string>();
        var stepIds = new Dictionary<Guid, Guid>();
        foreach (var wfId in ids)
        {
            var jobId = Guid.NewGuid();
            var stepId = Guid.NewGuid();
            stepIds[wfId] = stepId;
            values[L2ProjectionKeys.Root(wfId)] = SerializeRoot(jobId, stepId);
            values[L2ProjectionKeys.Step(wfId, stepId)] = SerializeStep(Guid.NewGuid());
        }

        var store = new WorkflowL1Store();
        var scheduler = await NewRamSchedulerAsync();
        try
        {
            var lifecycle = NewLifecycle(Mux(ids, values), store, scheduler);
            foreach (var wfId in ids)
            {
                await lifecycle.HydrateAndScheduleAsync(wfId, ct);
            }

            Assert.Equal(n, store.Count);

            // L1 holds ONLY the workflow GUIDs — no parent-index key, no processor key.
            Assert.Equal(ids.OrderBy(x => x), store.WorkflowIds.OrderBy(x => x));

            // Each workflow carries its entry step in the L1 step map.
            foreach (var wfId in ids)
            {
                Assert.True(store.TryGet(wfId, out var entry));
                Assert.Contains(stepIds[wfId], entry.Steps.Keys);
            }
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }

    // ----- ORCH-ACK-01: corrupt entry skipped, others hydrate -----------------------------------

    [Fact]
    public async Task CorruptEntrySkipped_OthersHydrate()
    {
        var ct = TestContext.Current.CancellationToken;
        const int n = 3;

        var ids = Enumerable.Range(0, n).Select(_ => Guid.NewGuid()).ToList();
        var values = new Dictionary<string, string>();
        for (var i = 0; i < ids.Count; i++)
        {
            var wfId = ids[i];
            if (i == 1)
            {
                // Malformed root JSON — business skip (ORCH-ACK-01).
                values[L2ProjectionKeys.Root(wfId)] = "{ this is not valid json";
                continue;
            }

            var jobId = Guid.NewGuid();
            var stepId = Guid.NewGuid();
            values[L2ProjectionKeys.Root(wfId)] = SerializeRoot(jobId, stepId);
            values[L2ProjectionKeys.Step(wfId, stepId)] = SerializeStep(Guid.NewGuid());
        }

        var store = new WorkflowL1Store();
        var scheduler = await NewRamSchedulerAsync();
        try
        {
            var lifecycle = NewLifecycle(Mux(ids, values), store, scheduler);

            // Drive each id exactly as HydrationBackgroundService would, with the same business guard.
            foreach (var wfId in ids)
            {
                try
                {
                    await lifecycle.HydrateAndScheduleAsync(wfId, ct);
                }
                catch (Exception ex) when (WorkflowLifecycle.IsBusiness(ex))
                {
                    // skip — host stays up
                }
            }

            Assert.Equal(n - 1, store.Count); // the malformed entry was skipped
            Assert.DoesNotContain(ids[1], store.WorkflowIds);
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }
}
