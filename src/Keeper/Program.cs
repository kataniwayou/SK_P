using BaseConsole.Core.DependencyInjection;
using MassTransit;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Keeper;

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
// source of truth the recovery consumer definition's UseMessageRetry reads).
builder.Services.Configure<RetryOptions>(builder.Configuration.GetSection("Retry"));

// PROBE-01 (D-04) — bind the bounded probe-loop knobs. Redis (IConnectionMultiplexer) is ALREADY a
// registered singleton via AddBaseConsole (line above chains the Redis registration) — do NOT add it again.
builder.Services.Configure<ProbeOptions>(builder.Configuration.GetSection("Probe"));

// D-03/D-06 — partition count (8) + gate-wait bound (300s) knobs (mirrors Probe/Retry). PartitionCount
// drives the keeper-recovery UsePartitioner slot count; GateWaitSeconds bounds the once-at-entry gate await.
builder.Services.Configure<RecoveryOptions>(builder.Configuration.GetSection("Recovery"));

// PROBE-01 — the v4 BitHealthLoop's L2 probe helper (stateless; ctor-injects the singleton multiplexer).
builder.Services.AddSingleton<Keeper.Recovery.L2ProbeRecovery>();

// KEEP-03 (D-09): the L2 BIT health gate singleton — written by the BIT loop, read by the Phase-46 consumer.
builder.Services.AddSingleton<Keeper.Health.IL2HealthGate, Keeper.Health.L2HealthGate>();
// KEEP-01/02 (D-06): the proactive BIT loop hosted service (edge-triggered global pause/resume + gate driver).
builder.Services.AddHostedService<Keeper.Health.BitHealthLoop>();

builder.Services.AddBaseConsoleMessaging(builder.Configuration, x =>
{
    // KEEP-04..09 (D-02/D-06/D-07-additive) — the three gate-open-only recovery consumers co-located on the
    // shared queue:keeper-recovery endpoint. ReinjectConsumerDefinition is the SINGLE OWNER of the endpoint-level
    // retry + the three UsePartitioner<T> calls; the other two definitions no-op (Pitfalls 1 & 4). The
    // recovery consumers ctor-inject IConnectionMultiplexer / ISendEndpointProvider (via the bus),
    // IL2HealthGate (line above), and IOptions<Retry/Recovery> (all already bound) — no new AddSingleton.
    x.AddConsumer<Keeper.Recovery.ReinjectConsumer, Keeper.Recovery.ReinjectConsumerDefinition>();
    x.AddConsumer<Keeper.Recovery.InjectConsumer,   Keeper.Recovery.InjectConsumerDefinition>();
    x.AddConsumer<Keeper.Recovery.DeleteConsumer,   Keeper.Recovery.DeleteConsumerDefinition>();
});

var host = builder.Build();
await host.RunAsync();
