using BaseConsole.Core.DependencyInjection;
using MassTransit;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Keeper;
using Keeper.Consumers;
using Keeper.Observability;
using OpenTelemetry.Metrics;   // ConfigureOpenTelemetryMeterProvider (via OpenTelemetry.Extensions.Hosting)

// Thin-shell composition root (KEEP-01). Generic Host — Host.CreateApplicationBuilder, NOT
// WebApplication. BaseConsole.Core supplies all infra (metrics-only OTel, Redis soft-dep,
// embedded health, the MassTransit bus + correlation pipeline). Keeper mirrors Orchestrator
// MINUS the scheduler/L1/hydration/metrics runtime block and MINUS the default-readiness-service
// removal — Keeper has no hydration, so readiness flips on bus-start (D-06): the base library's
// default readiness service is KEPT here, not stripped.
var builder = Host.CreateApplicationBuilder(args);

builder.AddBaseConsoleObservability(builder.Configuration);   // metrics-only OTel (no tracer)
builder.Services.AddBaseConsole(builder.Configuration);       // Redis soft-dep + embedded health + default readiness service (KEPT — D-06)

// DLQ-04 / D-09 — bind the shared Immediate(N) retry budget from the "Retry" section (the single
// source of truth the fault consumer definition's UseMessageRetry reads). Phase 36 consumers inherit this pattern.
builder.Services.Configure<RetryOptions>(builder.Configuration.GetSection("Retry"));

// PROBE-01 (D-04) — bind the bounded probe-loop knobs. Redis (IConnectionMultiplexer) is ALREADY a
// registered singleton via AddBaseConsole (line above chains the Redis registration) — do NOT add it again.
builder.Services.Configure<ProbeOptions>(builder.Configuration.GetSection("Probe"));

// D-10 — bind the composite-backup TTL knob from the "Backup" section (mirrors "Probe"/"Retry"). The
// TTL is applied only at the Keeper UPDATE write (Phase 46), never baked into the key builder.
builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection("Backup"));

// PROBE-01 — the shared bounded probe-loop helper (stateless; ctor-injects the singleton multiplexer +
// IOptions<ProbeOptions>). Both fault consumers depend on it.
builder.Services.AddSingleton<Keeper.Recovery.L2ProbeRecovery>();

// KHARD-03 — the shared recovery body both fault consumers delegate to (ctor-injects L2ProbeRecovery + KeeperMetrics).
builder.Services.AddSingleton<Keeper.Recovery.KeeperRecoveryHandler>();

// KMET-01 — the code-owned "Keeper" meter + its eight instruments. The holder is a DI-singleton
// (IMeterFactory pattern, no static Meter — D-01); ConfigureOpenTelemetryMeterProvider additively
// attaches the meter to the shared MeterProvider AddBaseConsoleObservability (line 18) already built —
// mirrors Orchestrator/Program.cs:72-73, preserving the const-to-AddMeter symmetry. The histogram's
// second-scale buckets ride on the instrument's InstrumentAdvice (Route A — DiagnosticSource 10.0.0),
// so no AddView is needed in this lambda.
builder.Services.AddSingleton<KeeperMetrics>();
builder.Services.ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(KeeperMetrics.MeterName));

// D-02/D-03/D-14 — the surviving reactive fault consumer binds the stable shared durable queue
// keeper-fault-recovery (competing-consumer round-robin, NOT the Start/Stop per-replica fan-out). Plain
// AddConsumer + the stable EndpointName on its definition => one durable queue binding the
// Fault<EntryStepDispatch> message-type exchange. NO per-replica auto-delete endpoint override (KEEP-02).
// D-14 (DARK): the former Fault<ExecutionResult> consumer is NO LONGER registered — its wire type was
// deleted in v4.0.0 (RETIRE-01/D-06); the reactive recovery FEATURE is carried by the
// Fault<EntryStepDispatch> consumer below (the FaultExecutionResultConsumer file is retained, retargeted,
// and dormant — its retirement is RETIRE-03, Phases 47/48). The endpoint-level retry is owned solely by
// FaultEntryStepDispatchConsumerDefinition (Pitfall 3).
builder.Services.AddBaseConsoleMessaging(builder.Configuration, x =>
{
    x.AddConsumer<FaultEntryStepDispatchConsumer, FaultEntryStepDispatchConsumerDefinition>();
});

var host = builder.Build();
await host.RunAsync();
