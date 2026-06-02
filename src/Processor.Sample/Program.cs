using BaseConsole.Core.DependencyInjection;
using BaseProcessor.Core.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Processor.Sample;
using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;

// Thin-shell composition root (SAMPLE-01). Generic Host — Host.CreateApplicationBuilder, NOT
// WebApplication. AddBaseProcessor folds the whole processor stack (console base + messaging +
// identity + liveness + dispatch + heartbeat); this console supplies ONLY metrics-only observability
// + the one concrete transform registered AS the abstract BaseProcessor (the
// EntryStepDispatchConsumer resolves BaseProcessor). It does NOT call the folded extensions directly.
var builder = Host.CreateApplicationBuilder(args);

builder.AddBaseConsoleObservability(builder.Configuration);          // metrics-only OTel (no tracer)
builder.Services.AddBaseProcessor(builder.Configuration);            // identity + liveness + dispatch + heartbeat
builder.Services.AddSingleton<BaseProcessorBase, SampleProcessor>(); // the ONE concrete seam

var host = builder.Build();
await host.RunAsync();
