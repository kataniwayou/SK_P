using System;
using System.Threading;
using System.Threading.Tasks;
using Messaging.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
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
/// PAUSE-04 idempotency-under-serial-replay proof (Wave 0 RED). At <c>ConcurrentMessageLimit=1</c> the bus
/// delivers Pause/Resume serially; a duplicate (replayed) Pause→Resume must converge on the SAME end state
/// as a single round-trip (D-07). Wires <see cref="WorkflowL1Store"/> + <see cref="WorkflowScheduler"/> +
/// the new <c>PauseWorkflowConsumer</c>/<c>ResumeWorkflowConsumer</c> over a single real RAM scheduler
/// (mirroring StopConsumerLifecycleTests.Build), seeds one workflow into L1 + Quartz, then invokes Pause
/// TWICE and Resume TWICE serially. End state must be exactly one Normal trigger and exactly one Quartz job
/// (no orphans).
/// <para>
/// RED until Plan 02/03 create <c>PauseWorkflowConsumer</c>/<c>ResumeWorkflowConsumer</c> and the
/// <c>PauseWorkflow</c>/<c>ResumeWorkflow</c> contracts — failing ONLY because those production symbols are
/// absent (no harness errors). Unique <c>quartz.scheduler.instanceName = test-{Guid:N}</c> RAM store; EVERY
/// Quartz call passes <c>TestContext.Current.CancellationToken</c> (xUnit1051).
/// </para>
/// </summary>
public sealed class PauseResumeConsumerTests
{
    private const string EveryFiveMinutes = "*/5 * * * *";

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

    [Fact]
    public async Task PauseResumeIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var processorId = Guid.NewGuid();
        var values = OrchestratorTestStubs.RootWithStep(workflowId, jobId, stepId, processorId, EveryFiveMinutes);
        var mux = OrchestratorTestStubs.PresentL2(values, out _);

        var scheduler = await NewRamSchedulerAsync(ct);
        try
        {
            var store = new WorkflowL1Store();
            var workflowScheduler = new WorkflowScheduler(scheduler, TimeProvider.System);
            var lifecycle = new WorkflowLifecycle(
                mux, store, workflowScheduler, TimeProvider.System, NullLogger<WorkflowLifecycle>.Instance);

            // Seed one workflow into L1 + the Quartz scheduler exactly as a Start would (reads-only).
            await lifecycle.HydrateAndScheduleAsync(workflowId, ct);
            Assert.Single(await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), ct));

            var pause = new PauseWorkflowConsumer(lifecycle, NullLogger<PauseWorkflowConsumer>.Instance);
            var resume = new ResumeWorkflowConsumer(lifecycle, NullLogger<ResumeWorkflowConsumer>.Instance);

            // Serial replay (ConcurrentMessageLimit=1): Pause twice, then Resume twice, over the SAME scheduler.
            await pause.Consume(OrchestratorTestStubs.Context(new PauseWorkflow(workflowId, "h-val"), ct));
            await pause.Consume(OrchestratorTestStubs.Context(new PauseWorkflow(workflowId, "h-val"), ct));
            await resume.Consume(OrchestratorTestStubs.Context(new ResumeWorkflow(workflowId, "h-val"), ct));
            await resume.Consume(OrchestratorTestStubs.Context(new ResumeWorkflow(workflowId, "h-val"), ct));

            // Idempotent end state: exactly one Normal trigger, exactly one Quartz job (no orphans, D-07).
            var jobKeys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), ct);
            Assert.Single(jobKeys); // exactly one Quartz job (no orphans, D-07)
            var triggerKey = new TriggerKey(jobId.ToString("D"));
            Assert.Equal(TriggerState.Normal, await scheduler.GetTriggerState(triggerKey, ct));
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }
}
