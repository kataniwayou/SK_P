using BaseConsole.Core.DependencyInjection;
using MassTransit;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Orchestrator.Consumers;
using Orchestrator.Dispatch;
using Orchestrator.Hydration;
using Orchestrator.L1;
using Orchestrator.Observability;
using Orchestrator.Scheduling;
using OpenTelemetry.Metrics;   // ConfigureOpenTelemetryMeterProvider (via OpenTelemetry.Extensions.Hosting)
using Quartz;

// Thin-shell composition root (ORCH-CON-01). Generic Host — Host.CreateApplicationBuilder, NOT
// WebApplication. The base library supplies all infra (observability, Redis soft-dep, embedded
// health, the MassTransit bus + correlation pipeline); this console supplies only its two
// consumers + the per-replica fan-out endpoint.
var builder = Host.CreateApplicationBuilder(args);

builder.AddBaseConsoleObservability(builder.Configuration);   // metrics-only OTel (no tracer — Pitfall 4)
builder.Services.AddBaseConsole(builder.Configuration);       // Redis soft-dep + embedded health

// D-10: bind the retry budget per process from the "Retry" section (single source of truth for the
// retry Limit so Phase 32's final-attempt check cannot desync from UseMessageRetry). Absent section →
// RetryOptions defaults (Immediate(3)). The 3 orchestrator ConsumerDefinitions inject IOptions<RetryOptions>.
builder.Services.Configure<RetryOptions>(builder.Configuration.GetSection("Retry"));

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
        // Phase 32 (req-4 / D-06): the future-fire stop. Consumes the MassTransit-auto-published
        // Fault<EntryStepDispatch> on the SAME per-replica fan-out endpoint as Start/Stop (NOT the shared
        // orchestrator-result queue — Pitfall 5) so EVERY replica receives the fault and the
        // schedule-owning one unschedules the Quartz job (others no-op).
        x.AddConsumer<FaultUnscheduleConsumer, FaultUnscheduleConsumerDefinition>()
            .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });   // per-replica fan-out (D-06)
        // ORCH-RESULT-02: shared competing-consumer (NO InstanceId/Temporary) — the inverse of the
        // Start/Stop fan-out. ResultConsumerDefinition binds the stable "orchestrator-result" endpoint.
        x.AddConsumer<ResultConsumer, ResultConsumerDefinition>();
        // 24.1 / D-24.1-05: the boot gate + scheduled redelivery are removed, so the delayed message
        // scheduler (AddDelayedMessageScheduler / UseDelayedMessageScheduler) and its
        // rabbitmq_delayed_message_exchange plugin dependency are gone. No configureBus needed.
    });

// --- Runtime wiring (Phase 23 Plan 04): Quartz + L1 store + scheduler + lifecycle + hydration ---
builder.Services.AddQuartz();                                              // default MS-DI job factory + RAMJobStore
builder.Services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);
builder.Services.AddSingleton<IWorkflowL1Store, WorkflowL1Store>();
builder.Services.AddSingleton<WorkflowScheduler>();
builder.Services.AddSingleton<WorkflowLifecycle>();
builder.Services.AddSingleton<IStepDispatcher, StepDispatcher>();          // Plan 03 dispatch single-owner (result + fire share it)
builder.Services.AddSingleton<StepAdvancement>();                          // Plan 03 pure match helper (ResultConsumer dependency)

// METRIC-04: the code-owned "Orchestrator" meter + its two business counters. The holder is a
// DI-singleton (IMeterFactory pattern); ConfigureOpenTelemetryMeterProvider additively attaches the
// meter to the shared MeterProvider that AddBaseConsoleObservability (line 20) already built — mirrors
// the Phase-29 ConfigureOpenTelemetryLoggerProvider seam, preserving the D-02 MeterName const symmetry.
builder.Services.AddSingleton<OrchestratorMetrics>();
builder.Services.ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(OrchestratorMetrics.MeterName));
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
