using System.Text.Json;
using BaseConsole.Core.Health;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestrator.Consumers;
using Orchestrator.Hydration;
using Orchestrator.L1;
using Orchestrator.Scheduling;
using Quartz;
using Quartz.Impl;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// ORCH-ACK-01 goal-backward proof of the consume ack split:
/// <list type="bullet">
///   <item>an absent-workflow Start AND Stop complete without throwing (clean ack — no <c>_error</c>);</item>
///   <item>a Redis-unreachable consume PROPAGATES (does not ack-swallow), so the bounded retry pipeline
///   can route to <c>_error</c>;</item>
///   <item>a corrupt startup entry is skipped while the rest hydrate (host stays up, store Count == 1).</item>
/// </list>
/// </summary>
public sealed class AckSemanticsTests
{
    private static async Task<IScheduler> NewRamSchedulerAsync(CancellationToken ct)
    {
        // Unique instance name per scheduler — StdSchedulerFactory binds schedulers in a SHARED
        // process-wide repository keyed by instance name; the default name collides across parallel
        // test classes. A fresh GUID name isolates each test's RAMJobStore.
        var props = new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"test-{Guid.NewGuid():N}",
        };
        var scheduler = await new StdSchedulerFactory(props).GetScheduler(ct);
        await scheduler.Start(ct);
        return scheduler;
    }

    private static (StartOrchestrationConsumer start, StopOrchestrationConsumer stop, WorkflowL1Store store, WorkflowLifecycle lifecycle) Build(
        IConnectionMultiplexer mux, IScheduler scheduler)
    {
        // 24.1 / D-24.1-05: the boot gate is removed — Start/Stop consumers no longer take an IStartupGate.
        var store = new WorkflowL1Store();
        var workflowScheduler = new WorkflowScheduler(scheduler, TimeProvider.System);
        var lifecycle = new WorkflowLifecycle(
            mux, store, workflowScheduler, TimeProvider.System, NullLogger<WorkflowLifecycle>.Instance);
        var start = new StartOrchestrationConsumer(lifecycle, NullLogger<StartOrchestrationConsumer>.Instance);
        var stop = new StopOrchestrationConsumer(lifecycle, NullLogger<StopOrchestrationConsumer>.Instance);
        return (start, stop, store, lifecycle);
    }

    // ----- ORCH-ACK-01: absent workflow -> Start acks (no throw) --------------------------------

    [Fact]
    public async Task AbsentWorkflow_Start_Acks_NoThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        var absentWf = Guid.NewGuid();
        var mux = OrchestratorTestStubs.AbsentL2(out _);

        var scheduler = await NewRamSchedulerAsync(ct);
        try
        {
            var (start, _, store, _) = Build(mux, scheduler);

            // Business absent-root path returns (ack) — no _error.
            await start.Consume(OrchestratorTestStubs.Context(new StartOrchestration([absentWf]), ct));

            Assert.Equal(0, store.Count); // nothing hydrated; no throw
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }

    // ----- ORCH-ACK-01: absent workflow -> Stop acks (no throw) ---------------------------------

    [Fact]
    public async Task AbsentWorkflow_Stop_Acks_NoThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        var absentWf = Guid.NewGuid();
        var mux = OrchestratorTestStubs.AbsentL2(out _);

        var scheduler = await NewRamSchedulerAsync(ct);
        try
        {
            var (_, stop, store, _) = Build(mux, scheduler);

            // Teardown of an unknown workflow is a no-op ack.
            await stop.Consume(OrchestratorTestStubs.Context(new StopOrchestration([absentWf]), ct));

            Assert.Equal(0, store.Count);
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }

    // ----- ORCH-ACK-01: Redis unreachable -> consume PROPAGATES (does not ack-swallow) ----------

    [Fact]
    public async Task RedisUnreachable_Consume_Propagates()
    {
        var ct = TestContext.Current.CancellationToken;
        var wf = Guid.NewGuid();
        var mux = OrchestratorTestStubs.InfraFaultL2(out _);

        var scheduler = await NewRamSchedulerAsync(ct);
        try
        {
            var (start, _, _, _) = Build(mux, scheduler);

            // Infra fault must NOT be ack-swallowed — it propagates so the bounded retry -> _error.
            await Assert.ThrowsAsync<RedisConnectionException>(
                () => start.Consume(OrchestratorTestStubs.Context(new StartOrchestration([wf]), ct)));
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }

    // ----- ORCH-ACK-01: corrupt startup entry skipped, rest hydrate, host stays up --------------

    [Fact]
    public async Task StartupCorruptEntry_HydratesRest_HostStaysUp()
    {
        var ct = TestContext.Current.CancellationToken;

        var wfGood = Guid.NewGuid();
        var wfCorrupt = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var stepId = Guid.NewGuid();

        var values = new Dictionary<string, string>
        {
            [L2ProjectionKeys.Root(wfGood)] = JsonSerializer.Serialize(new WorkflowRootProjection(
                EntryStepIds: [stepId], Cron: "*/5 * * * *", JobId: jobId,
                Liveness: new LivenessProjection(DateTime.UtcNow, 0, "active"),
                CorrelationId: Guid.NewGuid().ToString())),
            [L2ProjectionKeys.Step(wfGood, stepId)] = JsonSerializer.Serialize(new StepProjection(
                EntryCondition: 0, ProcessorId: Guid.NewGuid(), Payload: "{}", NextStepIds: [])),
            [L2ProjectionKeys.Root(wfCorrupt)] = "{ this is not valid json", // malformed -> business skip
        };
        var members = new[] { wfGood, wfCorrupt };
        var mux = OrchestratorTestStubs.ParentIndexL2(members, values, out var db);

        var store = new WorkflowL1Store();
        var gate = new StartupGate();
        var scheduler = await NewRamSchedulerAsync(ct);
        try
        {
            var workflowScheduler = new WorkflowScheduler(scheduler, TimeProvider.System);
            var lifecycle = new WorkflowLifecycle(
                mux, store, workflowScheduler, TimeProvider.System, NullLogger<WorkflowLifecycle>.Instance);
            var hydration = new HydrationBackgroundService(
                mux, lifecycle, gate, NullLogger<HydrationBackgroundService>.Instance);

            // StartAsync drives ExecuteAsync: SMEMBERS -> hydrate each -> MarkReady. The corrupt entry
            // is caught by the per-workflow business guard; the host (this unit) stays up.
            await hydration.StartAsync(ct);
            await hydration.StopAsync(ct);

            Assert.Equal(1, store.Count);                 // only the good workflow hydrated
            Assert.True(store.TryGet(wfGood, out _));
            Assert.False(store.TryGet(wfCorrupt, out _));
            Assert.True(gate.IsReady);                    // hydration completed -> gate opened, no crash
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }
}
