using Quartz;

namespace Orchestrator.Scheduling;

/// <summary>
/// Quartz job stub — fleshed out in Task 3 (fire -> Send + liveness refresh + self-reschedule).
/// Created here so <see cref="WorkflowScheduler"/> can reference the job type for scheduling.
/// </summary>
[DisallowConcurrentExecution]
public sealed class WorkflowFireJob : IJob
{
    public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
}
