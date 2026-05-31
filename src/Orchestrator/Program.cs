using BaseConsole.Core.DependencyInjection;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orchestrator.Consumers;

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

builder.Services.AddBaseConsoleMessaging(builder.Configuration, x =>
{
    x.AddConsumer<StartOrchestrationConsumer, StartOrchestrationConsumerDefinition>()
        .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
    x.AddConsumer<StopOrchestrationConsumer, StopOrchestrationConsumerDefinition>()
        .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
});

var host = builder.Build();
await host.RunAsync();
