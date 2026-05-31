using BaseConsole.Core.DependencyInjection;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Orchestrator.Consumers;
using Orchestrator.Dispatch;
using Orchestrator.Hydration;
using Orchestrator.L1;
using Orchestrator.Scheduling;
using Quartz;

// Thin-shell composition root (ORCH-CON-01). Generic Host — Host.CreateApplicationBuilder, NOT
// WebApplication. The base library supplies all infra (observability, Redis soft-dep, embedded
// health, the MassTransit bus + correlation pipeline); this console supplies only its two
// consumers + the per-replica fan-out endpoint.
var builder = Host.CreateApplicationBuilder(args);

builder.AddBaseConsoleObservability(builder.Configuration);   // metrics-only OTel (no tracer — Pitfall 4)
builder.Services.AddBaseConsole(builder.Configuration);       // Redis soft-dep + embedded health

// D-06: a stable instance id per replica (fall back to a fresh GUID when unset). Captured by the
// closure below so BOTH consumers share the SAME instance id → one temporary/auto-delete fan-out
// queue "orchestrator-{instanceId}" (ORCH-CON-02): a fan-out broadcast, not competing-consumer
// load-balance.
var instanceId = builder.Configuration["Orchestrator:InstanceId"] ?? Guid.NewGuid().ToString("N");

builder.Services.AddBaseConsoleMessaging(builder.Configuration,
    x =>
    {
        x.AddConsumer<StartOrchestrationConsumer, StartOrchestrationConsumerDefinition>()
            .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
        x.AddConsumer<StopOrchestrationConsumer, StopOrchestrationConsumerDefinition>()
            .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
        // ORCH-RESULT-02: shared competing-consumer (NO InstanceId/Temporary) — the inverse of the
        // Start/Stop fan-out. ResultConsumerDefinition binds the stable "orchestrator-result" endpoint.
        x.AddConsumer<ResultConsumer, ResultConsumerDefinition>();
        // ORCH-GATE-01: register the in-memory message scheduler that backs UseScheduledRedelivery
        // (the gate-closed never-drop policy). Operates on IBusRegistrationConfigurator.
        x.AddInMemoryMessageScheduler();
    },
    // ORCH-GATE-01 (D-06 / Pitfall 3 / A2): the bus-factory half of the in-memory scheduler.
    // AddInMemoryMessageScheduler() registers the scheduler service, but UseScheduledRedelivery's
    // pipeline needs the scheduler wired into the bus factory via UseInMemoryScheduler() (the
    // RabbitMQ transport has no native message scheduler — the delayed-exchange plugin is not
    // installed). Routed through Plan 01's optional configureBus seam (base = infra firewall intact).
    configureBus: c => c.UseInMemoryScheduler());

// --- Runtime wiring (Phase 23 Plan 04): Quartz + L1 store + scheduler + lifecycle + hydration ---
builder.Services.AddQuartz();                                              // default MS-DI job factory + RAMJobStore
builder.Services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);
builder.Services.AddSingleton<IWorkflowL1Store, WorkflowL1Store>();
builder.Services.AddSingleton<WorkflowScheduler>();
builder.Services.AddSingleton<WorkflowLifecycle>();
builder.Services.AddSingleton<IStepDispatcher, StepDispatcher>();          // Plan 03 dispatch single-owner (result + fire share it)
builder.Services.AddSingleton<StepAdvancement>();                          // Plan 03 pure match helper (ResultConsumer dependency)
builder.Services.AddHostedService<HydrationBackgroundService>();           // D-13 — drives MarkReady (D-12)

// WorkflowScheduler injects a concrete IScheduler — resolve the hosted scheduler from the factory.
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<ISchedulerFactory>().GetScheduler().GetAwaiter().GetResult());

// TimeProvider for the scheduler/lifecycle cron math (idempotent — base library may not register it).
builder.Services.TryAddSingleton(TimeProvider.System);

// D-12: remove the base library's StartupCompletionService so MarkReady fires at hydration-complete,
// NOT bare host start. IStartupGate / StartupHealthCheck / the "self"/"live" check stay untouched.
foreach (var d in builder.Services
             .Where(d => d.ImplementationType == typeof(BaseConsole.Core.Health.StartupCompletionService))
             .ToList())
{
    builder.Services.Remove(d);
}

var host = builder.Build();
await host.RunAsync();
