using BaseConsole.Core.DependencyInjection;
using BaseProcessor.Core.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Processor.BadConfig;
using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;

// Thin-shell composition root (CFG-08 subject). Generic Host — Host.CreateApplicationBuilder, NOT
// WebApplication. AddBaseProcessor folds the whole processor stack (console base + messaging +
// identity + liveness + dispatch + heartbeat, incl. the ProcessorStartupOrchestrator that runs
// Gate A); this console supplies ONLY metrics-only observability + the one concrete transform
// registered AS the abstract BaseProcessor (the EntryStepDispatchConsumer resolves BaseProcessor).
// It does NOT call the folded extensions directly — unmodified AddBaseProcessor preserves the
// Phase-57 stay-up clash posture (no startup-orchestrator override).
var builder = Host.CreateApplicationBuilder(args);

builder.AddBaseConsoleObservability(builder.Configuration);            // metrics-only OTel (no tracer)
builder.Services.AddBaseProcessor(builder.Configuration);              // identity + liveness + dispatch + heartbeat
builder.Services.AddSingleton<BaseProcessorBase, BadConfigProcessor>(); // the ONE concrete seam

var host = builder.Build();
await host.RunAsync();
