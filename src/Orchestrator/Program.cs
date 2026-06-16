using BaseConsole.Core.DependencyInjection;
using MassTransit;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Orchestrator.Configuration;
using Orchestrator.Consumers;
using Orchestrator.Dispatch;
using Orchestrator.Hydration;
using Orchestrator.L1;
using Orchestrator.Observability;
using Orchestrator.Recovery;
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
        // PAUSE-02/03/04: Pause + Resume share their own dedicated per-replica fan-out endpoint
        // "orchestrator-pauseresume-{instanceId}" (ConcurrentMessageLimit=1, single retry ownership held
        // by the Pause definition) so they don't throttle Start/Stop/Result (RESEARCH §5b).
        x.AddConsumer<PauseWorkflowConsumer, PauseWorkflowConsumerDefinition>()
            .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
        x.AddConsumer<ResumeWorkflowConsumer, ResumeWorkflowConsumerDefinition>()
            .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
        // ORCH-02 / D-08: global pause/resume on a NEW per-replica fan-out endpoint
        // "orchestrator-global-pauseresume-{instanceId}" (SAME instanceId → one temp fan-out queue per
        // replica; ConcurrentMessageLimit=1, single retry ownership held by the PauseAll definition),
        // independent from "orchestrator-pauseresume" so Phase 48 can drop the old per-workflow endpoint
        // with zero entanglement.
        x.AddConsumer<PauseAllConsumer, PauseAllConsumerDefinition>()
            .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
        x.AddConsumer<ResumeAllConsumer, ResumeAllConsumerDefinition>()
            .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
        // ORCH-01 / D-07: the TypedResultConsumer<T> family — four shared competing-consumers (NO
        // InstanceId/Temporary), the inverse of the Start/Stop fan-out. All four co-locate on the stable
        // "orchestrator-result" endpoint; StepCompletedConsumerDefinition owns the single endpoint-level
        // UseMessageRetry, the other three definitions are intentional no-ops (Pitfall 4). Routing is by
        // message type via each subclass's Outcome knob — no status if/switch. A Keeper-INJECT'd
        // StepCompleted is processed identically to a direct one by StepCompletedConsumer.
        x.AddConsumer<StepCompletedConsumer,  StepCompletedConsumerDefinition>();
        x.AddConsumer<StepFailedConsumer,     StepFailedConsumerDefinition>();
        x.AddConsumer<StepCancelledConsumer,  StepCancelledConsumerDefinition>();
        x.AddConsumer<StepProcessingConsumer, StepProcessingConsumerDefinition>();
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
builder.Services.AddSingleton<StepAdvancement>();                          // Plan 03 pure match helper (pipeline FORWARD dependency)

// Phase 71 (ORCV-01..05): the L2-gated result-recovery pipeline + its options. The TypedResultConsumer<T>
// family constructor-injects OrchestratorResultPipeline (reverses 24.1's L1-only result posture). RetryOptions
// (the bounded RetryLoop budget) + OrchestratorRecoveryOptions (the data-TTL source for the atomic FORWARD
// write) are bound from config — mirroring the keeper/processor "Retry"/TTL binding.
builder.Services.Configure<RetryOptions>(builder.Configuration.GetSection("Retry"));
builder.Services.Configure<OrchestratorRecoveryOptions>(builder.Configuration.GetSection("Recovery"));
builder.Services.AddScoped<OrchestratorResultPipeline>();                   // scoped: it consumes the scoped ISendEndpointProvider (mirrors ProcessorPipeline)

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
