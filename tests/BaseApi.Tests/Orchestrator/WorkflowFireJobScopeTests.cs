using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orchestrator.Dispatch;
using Orchestrator.L1;
using Orchestrator.Scheduling;
using Quartz;
using Quartz.Impl;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// LOG-05 / LOG-01 / D-06: <see cref="WorkflowFireJob.Execute"/> runs OUTSIDE the consume pipeline
/// (it is a Quartz job), so neither the correlation filter nor the execution-scope filter ever sees it.
/// The job must therefore open its OWN explicit <c>BeginScope</c> — AFTER the per-fire
/// <c>correlationId = NewId.NextGuid()</c> mint (the point where both ids are known) — carrying
/// CorrelationId (via <see cref="CorrelationKeys.LogScope"/>, the ONE place the job owns it) and the
/// parsed WorkflowId (via <see cref="ExecutionLogScope.WorkflowId"/>). The early returns (unparseable
/// workflowId / workflow absent) fire BEFORE the mint and must NOT be wrapped (Pattern 6).
///
/// <para>This drives the real <see cref="WorkflowFireJob"/> with a real <see cref="WorkflowL1Store"/>,
/// an in-memory MassTransit harness behind <see cref="StepDispatcher"/>, and a scope-capturing
/// <see cref="ILogger{T}"/> double (mirrors <c>EntryStepDispatchScopeTests</c>). It asserts a log line
/// emitted on the post-mint path carries a captured scope with CorrelationId present (any non-empty
/// Guid string — minted per fire) AND WorkflowId == the known input workflowId.</para>
/// </summary>
public sealed class WorkflowFireJobScopeTests
{
    // ── scope-capturing logger double (mirrors EntryStepDispatchScopeTests; no new package) ──
    private sealed class CapturingLogger : ILogger<WorkflowFireJob>
    {
        public List<Dictionary<string, object>> Scopes { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            if (state is IEnumerable<KeyValuePair<string, object>> kvps)
                Scopes.Add(new Dictionary<string, object>(kvps));
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) { }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>The post-mint scope captured on the fire path (the one carrying CorrelationId or WorkflowId).</summary>
    private static Dictionary<string, object> FireScope(CapturingLogger logger) =>
        logger.Scopes.FirstOrDefault(s =>
            s.ContainsKey(CorrelationKeys.LogScope) || s.ContainsKey(ExecutionLogScope.WorkflowId))
        ?? new Dictionary<string, object>();

    private static async Task<IScheduler> NewRamSchedulerAsync(CancellationToken ct)
    {
        // Unique instance name — StdSchedulerFactory binds schedulers in a SHARED process-wide
        // repository keyed by instance name; a fresh GUID isolates this test's RAMJobStore.
        var props = new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"test-{Guid.NewGuid():N}",
        };
        var scheduler = await new StdSchedulerFactory(props).GetScheduler(ct);
        await scheduler.Start(ct);
        return scheduler;
    }

    private static ServiceProvider BuildHarness(Guid processorId) =>
        new ServiceCollection()
            .AddLogging()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<CapturingDispatchConsumer>();
                x.UsingInMemory((ctx, cfg) =>
                {
                    // queue:{processorId:D} <-> ReceiveEndpoint("{processorId:D}")
                    cfg.ReceiveEndpoint($"{processorId:D}", e => e.ConfigureConsumer<CapturingDispatchConsumer>(ctx));
                    cfg.ConfigureEndpoints(ctx);
                });
            })
            .BuildServiceProvider(true);

    private static void SeedEntry(
        WorkflowL1Store store, Guid workflowId, Guid jobId, Guid stepId, Guid processorId, DateTime livenessTimestamp)
    {
        var stepMap = new Dictionary<Guid, StepProjection>
        {
            [stepId] = new StepProjection(EntryCondition: 0, ProcessorId: processorId, Payload: "{}", NextStepIds: []),
        };
        var entry = new WorkflowL1(
            EntryStepIds: [stepId],
            Cron: "*/5 * * * *",
            JobId: jobId,
            Steps: stepMap)
        {
            Liveness = new LivenessProjection(livenessTimestamp, Interval: 300, Status: "active"),
        };
        store.Upsert(workflowId, entry);
    }

    private static IJobExecutionContext FireContext(Guid workflowId, CancellationToken ct)
    {
        var map = new JobDataMap { { "workflowId", workflowId.ToString("D") } };
        var context = Substitute.For<IJobExecutionContext>();
        context.MergedJobDataMap.Returns(map);
        context.CancellationToken.Returns(ct);
        return context;
    }

    [Fact]
    public async Task PostMint_FireLogs_Carry_CorrelationId_And_WorkflowId_Scope()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var processorId = Guid.NewGuid();

        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero));
        var store = new WorkflowL1Store();
        SeedEntry(store, workflowId, jobId, stepId, processorId, fakeTime.GetUtcNow().UtcDateTime.AddMinutes(-10));

        await using var provider = BuildHarness(processorId);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var scheduler = await NewRamSchedulerAsync(ct);
            try
            {
                var workflowScheduler = new WorkflowScheduler(scheduler, fakeTime);
                // The job must exist before the fire path self-reschedules a fresh trigger.
                await workflowScheduler.ScheduleAsync(workflowId, jobId, "*/5 * * * *", ct);

                var logger = new CapturingLogger();
                var job = new WorkflowFireJob(
                    store, new StepDispatcher(harness.Bus, OrchestratorTestStubs.NoopRedis(), OrchestratorTestStubs.Metrics()), workflowScheduler, fakeTime, logger);

                await job.Execute(FireContext(workflowId, ct));

                var scope = FireScope(logger);

                // Both keys present on the post-mint scope.
                Assert.True(scope.ContainsKey(CorrelationKeys.LogScope), "CorrelationId scope key must be present");
                Assert.True(scope.ContainsKey(ExecutionLogScope.WorkflowId), "WorkflowId scope key must be present");

                // WorkflowId == the deterministic input value.
                Assert.Equal(workflowId.ToString(), scope[ExecutionLogScope.WorkflowId]);

                // CorrelationId is minted per fire — assert presence + non-empty Guid shape, not a fixed value.
                var corr = (string)scope[CorrelationKeys.LogScope];
                Assert.True(Guid.TryParse(corr, out var corrGuid), "CorrelationId scope value must be a Guid string");
                Assert.NotEqual(Guid.Empty, corrGuid);
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
