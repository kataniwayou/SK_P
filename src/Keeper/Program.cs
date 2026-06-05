using BaseConsole.Core.DependencyInjection;
using MassTransit;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Keeper.Consumers;

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
// source of truth PlaceholderConsumerDefinition.UseMessageRetry reads). Phase 35/36 consumers inherit this pattern.
builder.Services.Configure<RetryOptions>(builder.Configuration.GetSection("Retry"));

// D-02 — plain AddConsumer + stable EndpointName = competing-consumer (round-robin), NOT the
// Start/Stop per-replica fan-out. NO per-replica auto-delete endpoint override anywhere — the
// stable durable shared queue is the whole point (KEEP-02 + net-zero close-gate SHA, Pitfall 1).
builder.Services.AddBaseConsoleMessaging(builder.Configuration,
    x => x.AddConsumer<PlaceholderConsumer, PlaceholderConsumerDefinition>());

var host = builder.Build();
await host.RunAsync();
