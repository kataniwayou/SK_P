# Phase 29: Structured Execution-Scope Logging - Research

**Researched:** 2026-06-02
**Domain:** .NET MEL log scopes + OpenTelemetry logs bridge (OTel .NET 1.15.3) + MassTransit 8.5.5 consume filters
**Confidence:** HIGH

## Summary

This phase is logs-only and additive. Every mechanism it needs already exists and is proven in the
codebase for `CorrelationId`; the work is to replicate that proven path for the other five execution
ids. The CorrelationId pipeline — body-read consume filter → `logger.BeginScope(Dictionary)` →
OTel `IncludeScopes=true`+`ParseStateValues=true` → ES `attributes.CorrelationId` — is the template
to mirror byte-faithfully (verified: `InboundCorrelationConsumeFilter.cs:39`,
`BaseConsoleObservabilityExtensions.cs:43-51`, `EsIndexNames.cs:71`).

The only genuinely new mechanism is the `ProcessorId` enricher (D-04): a custom
`OpenTelemetry.BaseProcessor<LogRecord>` that appends a `ProcessorId` attribute in `OnEnd`. The OTel
API for this is verified below and is safe on the repo's pinned 1.15.3 (the v1.5–v1.7 `State`/`Attributes`
desync data-loss bug does NOT apply at 1.15.x with `ParseStateValues=true`).

**Primary recommendation:** Mirror `InboundCorrelationConsumeFilter` exactly (Dictionary<string,object>
scope state); add `ExecutionLogScope` as a sibling POCO constants class; implement the enricher as a
DI-resolved `BaseProcessor<LogRecord>` registered via `o.AddProcessor<T>()` inside the EXISTING
`AddBaseConsoleObservability` logging block, reassigning `record.Attributes` via `.Append(kvp).ToList()`
only when `IProcessorContext.Id` is non-null.

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| LOG-01 | Six ids surface as scope-sourced ES `attributes.*` via the unchanged OTel bridge | The bridge is unchanged (`BaseConsoleObservabilityExtensions.cs:43-51`); scope-key == attribute-name proven by `EsIndexNames.CorrelationIdFieldPath="attributes.CorrelationId"`. The new keys follow the identical path. See "Scope → ES attribute" below. |
| LOG-02 | Open-generic bus-wide `InboundExecutionScopeConsumeFilter<T>` registered once in `AddBaseConsoleMessaging` | Mirror `InboundCorrelationConsumeFilter<T>` shape + `UseConsumeFilter(typeof(...<>), ctx)` registration at `MessagingServiceCollectionExtensions.cs:51`. Filter body-reads `IExecutionCorrelated`. |
| LOG-03 | `ExecutionLogScope` constants in `Messaging.Contracts`; keys == param names; `Guid.Empty` skipped | Sibling to `CorrelationKeys.cs` (pure POCO, no MassTransit ref — verified `Messaging.Contracts` has zero MassTransit using). Skip-empty enforced in the filter's dictionary-build. |
| LOG-04 | Nested `BeginScope` for minted ids + `ProcessorId` enricher (null-safe) | Mint sites verified: `EntryStepDispatchConsumer.cs:140` (EntryId), `:186` (ExecutionId via `NewId.NextGuid()`). Enricher API verified below (D-04 section). |
| LOG-05 | `WorkflowFireJob.Execute` explicit `BeginScope(CorrelationId + WorkflowId)` | Mint site verified: `WorkflowFireJob.cs:54` (`correlationId = NewId.NextGuid()`); `workflowId` parsed at `:40`. Job runs outside consume pipeline (filters never see it). |
| LOG-06 | No regression + one real-stack ES proof of ≥1 scoped id | `SampleRoundTripE2ETests` (D-08) is the target; `PollEsForLog` helper verified. **See Landmine L1 — the existing WorkflowId assertion is template-sourced, not scope-sourced.** |
</phase_requirements>

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Scope the inbound execution id-set on every consume | Messaging middleware (MassTransit consume filter) | — | Ambient, cross-consumer; registered once bus-wide. Mirrors the correlation filter's tier. |
| Carry minted (post-consume) ExecutionId/EntryId | Consumer body (nested `BeginScope`) | — | The minted ids do not exist until mid-consume; only the consumer knows them. |
| Attach ProcessorId to ALL processor logs (incl. pre-identity) | OTel SDK logger-provider processor (`BaseProcessor<LogRecord>`) | — | Must cover logs emitted before any scope opens (startup, heartbeat); a scope can't. Reads the process-wide singleton `IProcessorContext`. |
| Scope the Quartz-fire CorrelationId+WorkflowId | Quartz job body (explicit `BeginScope`) | — | Runs outside the consume pipeline; no filter sees it. |
| Serialize scopes/params → ES attributes | OTel logs bridge (unchanged) | Collector + ES | Reused verbatim; `IncludeScopes`+`ParseStateValues` already on. |

## Standard Stack

All packages already pinned (Central Package Management, `Directory.Packages.props`). **No new packages.**

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| OpenTelemetry | 1.15.3 | `BaseProcessor<LogRecord>`, `OpenTelemetryLoggerOptions.AddProcessor`, `LogRecord` | [VERIFIED: Directory.Packages.props:77]. `BaseProcessor<T>` lives in the `OpenTelemetry` namespace. |
| OpenTelemetry.Extensions.Hosting | 1.15.3 | `builder.Logging.AddOpenTelemetry` host wiring | [VERIFIED: Directory.Packages.props:78] |
| OpenTelemetry.Exporter.OpenTelemetryProtocol | 1.15.3 | `AddOtlpExporter()` (already wired) | [VERIFIED: Directory.Packages.props:79] |
| MassTransit | 8.5.5 | `IFilter<ConsumeContext<T>>`, `UseConsumeFilter(typeof(...<>), ctx)` | [VERIFIED: Directory.Packages.props:137]. Last Apache-2.0 line — do NOT bump. |
| Microsoft.Extensions.Logging (MEL) | runtime 8.0.x | `ILogger.BeginScope`, scope semantics | [VERIFIED: framework; `ILogger<T>` injected throughout] |
| xunit.v3 | 3.2.2 | hermetic test framework | [VERIFIED: Directory.Packages.props:121] |
| MassTransit.Testing (`ITestHarness`) | 8.5.5 | in-memory harness for filter tests | [VERIFIED: `ConsoleCorrelationFilterTests.cs:4` uses `MassTransit.Testing`] |

**Installation:** none — all pins present.

**Version verification:** Skipped registry calls — versions are CPM-pinned and must NOT change this
phase (MassTransit 9.x is commercial; OTel/MEL bumps are out of scope). The pinned OTel 1.15.3 is the
relevant fact: it is well past the v1.7.0 fix for the log-enrichment data-loss bug (see Landmine L4).

## Architecture Patterns

### System Architecture Diagram (the proven CorrelationId path, replicated for the 5 ids)

```
                  ORCHESTRATOR                                   PROCESSOR
                  ============                                   =========

  WorkflowFireJob.Execute (Quartz, outside pipeline)
     mint correlationId (NewId.NextGuid())  :54
     ┌─ BeginScope({CorrelationId, WorkflowId})  ← LOG-05 (D-06)
     │     log "fire ..." ──────────────────────────────┐
     │  dispatcher.DispatchAsync(... Send EntryStepDispatch
     └────────────────────────────────────────────────┐ │
                                                       │ │
   queue:{processorId} ◄───────────────────────────────┘ │
        │                                                 │
        ▼  consume EntryStepDispatch (IExecutionCorrelated)│
   ┌─ InboundCorrelationConsumeFilter<T>  (OUTER scope)    │   ProcessorId enricher
   │     {CorrelationId = body.CorrelationId}              │   (BaseProcessor<LogRecord>)
   │  ┌─ InboundExecutionScopeConsumeFilter<T> (INNER)  ◄──┘   OnEnd: if Id!=null
   │  │     {WorkflowId, StepId, ProcessorId,                    append {ProcessorId} ── attaches
   │  │      ExecutionId(skip Empty), EntryId(skip Empty)}       to EVERY processor LogRecord
   │  │  EntryStepDispatchConsumer.Consume
   │  │     ... mint newEntryId :140, mint executionId :186
   │  │     ┌─ BeginScope({ExecutionId=minted, EntryId=minted}) ← LOG-04 (D-05, inner-overrides-outer)
   │  │     │    StringSetAsync(L2)  /  endpoint.Send(ExecutionResult)
   │  │     └─ (log lines here report MINTED ids, not inbound Guid.Empty)
   │  └─                                                       Send ExecutionResult
   └─                                                          │
   queue:orchestrator-result ◄──────────────────────────────────┘
        │  consume ExecutionResult (IExecutionCorrelated)
   ┌─ InboundCorrelationConsumeFilter  (OUTER) + ExecutionScope (INNER)
   │     ResultConsumer.Consume → advance DAG
   └─

  ALL log lines emitted inside a scope carry the scope KVPs as MEL state →
  OTel IncludeScopes + ParseStateValues serialize each KVP →
  OTLP → collector → Elasticsearch  attributes.<Key>
```

### Pattern 1: Body-read consume filter mirroring InboundCorrelationConsumeFilter (LOG-02)
**What:** Open-generic `IFilter<ConsumeContext<T>> where T : class`, reads ids off
`context.Message as IExecutionCorrelated`, opens ONE `BeginScope(Dictionary<string,object>)`, wraps
`await next.Send(context)`. Pass-through (no scope) for non-`IExecutionCorrelated` messages.
**When to use:** This is the canonical shape; do not deviate.
**Example (the template to mirror — VERIFIED `InboundCorrelationConsumeFilter.cs:29-44`):**
```csharp
// Source: src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs
public sealed class InboundCorrelationConsumeFilter<T>(
    ICorrelationAccessor accessor, ILogger<InboundCorrelationConsumeFilter<T>> logger)
    : IFilter<ConsumeContext<T>> where T : class
{
    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        var corrId = (context.Message as ICorrelated)?.CorrelationId.ToString()
                     ?? context.CorrelationId?.ToString() ?? Guid.NewGuid().ToString();
        accessor.Set(corrId);
        using (logger.BeginScope(new Dictionary<string, object> { [CorrelationKeys.LogScope] = corrId }))
            await next.Send(context);
    }
    public void Probe(ProbeContext context) => context.CreateFilterScope("correlation-in");
}
```
**New filter shape (the planner's target — D-01/D-03):**
```csharp
// InboundExecutionScopeConsumeFilter<T> — scopes ONLY the 5 execution ids (NOT CorrelationId — D-01)
public sealed class InboundExecutionScopeConsumeFilter<T>(
    ILogger<InboundExecutionScopeConsumeFilter<T>> logger) : IFilter<ConsumeContext<T>> where T : class
{
    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        if (context.Message is not IExecutionCorrelated ec)
        {
            await next.Send(context);   // D-03 pass-through no-op
            return;
        }
        var state = new Dictionary<string, object>();
        if (ec.WorkflowId  != Guid.Empty) state[ExecutionLogScope.WorkflowId]  = ec.WorkflowId.ToString();
        if (ec.StepId      != Guid.Empty) state[ExecutionLogScope.StepId]      = ec.StepId.ToString();
        if (ec.ProcessorId != Guid.Empty) state[ExecutionLogScope.ProcessorId] = ec.ProcessorId.ToString();
        if (ec.ExecutionId != Guid.Empty) state[ExecutionLogScope.ExecutionId] = ec.ExecutionId.ToString();
        if (ec.EntryId     != Guid.Empty) state[ExecutionLogScope.EntryId]     = ec.EntryId.ToString();
        // NOTE: if state is empty (all Guid.Empty), an empty-dictionary scope is harmless;
        // prefer to still open it for uniform structure, OR skip — planner's choice, both behave.
        using (logger.BeginScope(state))
            await next.Send(context);
    }
    public void Probe(ProbeContext context) => context.CreateFilterScope("execution-scope-in");
}
```
Note: the new filter needs NO `ICorrelationAccessor` (D-01 — it does not touch CorrelationId).
Store the id value as `.ToString()` to byte-match the correlation filter (it scopes a string, and
`EsIndexNames` was verified against string scope values landing as `keyword`).

### Pattern 2: Registration after the correlation filter (D-02, LOG-02)
**VERIFIED registration site — `MessagingServiceCollectionExtensions.cs:48-58`:**
```csharp
x.UsingRabbitMq((ctx, c) =>
{
    c.Host(rabbitHost, h => { h.Username(rabbitUser); h.Password(rabbitPass); });
    c.UseConsumeFilter(typeof(InboundCorrelationConsumeFilter<>), ctx);       // CORR-01 (OUTER)
    c.UseConsumeFilter(typeof(InboundExecutionScopeConsumeFilter<>), ctx);    // ← LOG-02 ADD HERE (INNER)
    c.UseSendFilter(typeof(OutboundCorrelationSendFilter<>), ctx);
    c.UsePublishFilter(typeof(OutboundCorrelationPublishFilter<>), ctx);
    configureBus?.Invoke(ctx, c);
    c.ConfigureEndpoints(ctx);
});
```
Open-generic `typeof(...<>)` + `ctx` is the verified MassTransit 8.5.5 shape for a bus-wide scoped
consume filter (the XML doc on `InboundCorrelationConsumeFilter` confirms this is the accepted shape).
This single registration covers BOTH the orchestrator (`ResultConsumer` ← `ExecutionResult`) and the
processor (`EntryStepDispatchConsumer` ← `EntryStepDispatch`) — both implement `IExecutionCorrelated`
(VERIFIED: `ExecutionResult.cs:11`, `EntryStepDispatch.cs:10`). No per-console wiring. WebApi is NOT
affected — it does not call `AddBaseConsoleMessaging` for these consumers (it is the producer side).

### Pattern 3: `ExecutionLogScope` constants (LOG-03)
**What:** A sibling to `CorrelationKeys` (VERIFIED `CorrelationKeys.cs` — pure POCO, no usings).
```csharp
// src/Messaging.Contracts/ExecutionLogScope.cs — keys MUST equal the structured-param names
namespace Messaging.Contracts;
public static class ExecutionLogScope
{
    public const string WorkflowId  = "WorkflowId";
    public const string StepId      = "StepId";
    public const string ProcessorId = "ProcessorId";
    public const string ExecutionId = "ExecutionId";
    public const string EntryId     = "EntryId";
}
```
Constraint (SPEC): key string == param name so scope-derived and template-param-derived attributes
coincide on the same ES field. CorrelationId is deliberately NOT here — it stays in `CorrelationKeys`
(D-01). `Messaging.Contracts` MUST stay MassTransit-free (it is — VERIFIED).

### Pattern 4: Nested `BeginScope` for minted ids (LOG-04 / D-05)
**What:** Inside `EntryStepDispatchConsumer`, after minting `newEntryId` (:140) and the per-result
`executionId` (:186), wrap ONLY the write/send log lines in a nested scope using the SAME
`ExecutionLogScope` keys. MEL inner-scope-overrides-outer semantics mean those lines report the minted
ids instead of the inbound `Guid.Empty` the outer filter skipped. [VERIFIED: MEL scope nesting — the
last-opened scope's KVPs take precedence on duplicate keys when the bridge flattens scopes; the OTel
MEL bridge enumerates scopes outer→inner so the inner write wins. See Landmine L2 for the one caveat.]
**Anchor (VERIFIED `EntryStepDispatchConsumer.cs:129-165`):** the per-result loop mints `newEntryId`
at :140 and `BuildCompleted`→`ExecutionId = NewId.NextGuid()` at :186; the write is :144, the sends
are :164/:177. The nested scope must enclose the L2 write + the `endpoint.Send` for the minted ids to
appear on those lines. Note `ExecutionId`/`EntryId` are minted PER RESULT inside the loop — the nested
scope belongs inside the loop iteration (each result has its own minted pair).

### Pattern 5: `ProcessorId` enricher — the only new mechanism (LOG-04 / D-04)
See the dedicated "D-04 Enricher" section below — this is the highest-risk discretion area.

### Pattern 6: WorkflowFireJob explicit scope (LOG-05 / D-06)
**Anchor (VERIFIED `WorkflowFireJob.cs:37-87`):** `workflowId` parsed at :40, `correlationId` minted at
:54. The explicit scope wraps the body AFTER both ids are known. Because the early no-id-parse return
(:42-44) and the workflow-absent return (:49) happen BEFORE the correlationId mint, the scope can only
cover the post-mint body. Use `CorrelationKeys.LogScope` for CorrelationId and `ExecutionLogScope.WorkflowId`
for WorkflowId (D-06):
```csharp
using (logger.BeginScope(new Dictionary<string, object>
{
    [CorrelationKeys.LogScope]       = correlationId.ToString(),
    [ExecutionLogScope.WorkflowId]   = workflowId.ToString(),
}))
{
    foreach (var entryStepId in wf.EntryStepIds) { ... }
    // liveness refresh + reschedule
}
```

### Anti-Patterns to Avoid
- **Re-scoping CorrelationId in the execution filter** — D-01 forbids it; would create a duplicate/competing scope entry.
- **Touching `InboundCorrelationConsumeFilter.cs`** — SC#2 requires it byte-unchanged. The close-gate / a hermetic test asserts this.
- **Interpolating ids into message text** (T-18-04) — ids go ONLY as scope/param values, never `$"...{id}..."` in the template string.
- **Reconfiguring the OTel logging block** — reuse `IncludeScopes`/`ParseStateValues` as-is. The enricher is ADDED via `AddProcessor`, not by changing the options.
- **Adding new packages** — everything is pinned; MassTransit 9.x is commercial.

## D-04 Enricher — verified API surface + registration (the load-bearing discretion area)

### The processor host's OTel logging block (the registration call site)
The Processor.Sample host calls `builder.AddBaseConsoleObservability(...)`
(VERIFIED `Processor.Sample/Program.cs:15`), which contains the logging block at
`BaseConsoleObservabilityExtensions.cs:43-51`. The enricher registers INSIDE that existing block:

```csharp
// src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs:43-51 (current)
builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes           = true;
    o.ParseStateValues        = true;
    o.SetResourceBuilder(...);
    o.AddOtlpExporter();
    // ← ADD: o.AddProcessor<ProcessorIdLogEnricher>();   (DI-resolved overload)
});
```

**CRITICAL placement decision (planner must resolve):** `AddBaseConsoleObservability` lives in
`BaseConsole.Core` and is called by BOTH the orchestrator and the processor (VERIFIED:
`Orchestrator/Program.cs:20` AND `Processor.Sample/Program.cs:15`). The enricher reads
`IProcessorContext`, which exists ONLY in the processor (`BaseProcessor.Core`). Two viable options:

1. **(Recommended) Register the enricher in the PROCESSOR's composition, not in the shared
   `AddBaseConsoleObservability`.** The cleanest seam is to add a separate logging-provider
   configuration on the processor side. OTel supports configuring the logger provider from the
   service collection independently of the `builder.Logging.AddOpenTelemetry` call site via
   `services.ConfigureOpenTelemetryLoggerProvider(...)` / the DI-aware `AddProcessor<T>()` registration
   [CITED: docs/builders/configure-opentelemetry-provider.md — deferred `Action<IServiceProvider,...>`
   overloads exist]. Put the registration in `AddBaseProcessor` (`BaseProcessorServiceCollectionExtensions.cs`)
   so only the processor gets it. This keeps `BaseConsole.Core` ignorant of `IProcessorContext`
   (no new cross-project dependency) and satisfies D-04 ("registered on the processor's logger provider").
2. Add an optional callback parameter to `AddBaseConsoleObservability` so the processor injects its
   enricher. More plumbing; option 1 is simpler.

Either way the enricher type lives in `BaseProcessor.Core` (it depends on `BaseProcessor.Core.Identity.IProcessorContext`).

### DI-resolved processor registration (VERIFIED idiom)
```csharp
// Resolve the enricher (and its IProcessorContext dependency) from DI:
builder.Services.AddSingleton<ProcessorIdLogEnricher>();
// ... in the logger-provider config:
o.AddProcessor<ProcessorIdLogEnricher>();   // generic overload → resolved from the container
```
[CITED: docs/builders/add-opentelemetry.md — "Register a custom processor that is resolved from the
dependency injection container: `.AddProcessor<MyFilteringProcessor>()` + `AddSingleton<MyFilteringProcessor>()`".]
This is required because the enricher must read the SINGLETON `IProcessorContext` (constructor-injected);
the instance overload `AddProcessor(new ...)` cannot resolve DI services.

### The enricher implementation (VERIFIED OnEnd + Attributes-append idiom)
```csharp
// src/BaseProcessor.Core/Observability/ProcessorIdLogEnricher.cs  (new)
using System.Linq;                       // .Append / .ToList
using BaseProcessor.Core.Identity;
using Microsoft.Extensions.Logging;      // LogRecord lives in OpenTelemetry.Logs
using OpenTelemetry;                      // BaseProcessor<T>
using OpenTelemetry.Logs;                 // LogRecord

public sealed class ProcessorIdLogEnricher(IProcessorContext context) : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord record)
    {
        // Null-safe (SPEC constraint): emit NOTHING before identity resolves — never Guid.Empty.
        if (context.Id is not { } id) return;

        // record.Attributes is IReadOnlyList<KeyValuePair<string,object?>> — settable but immutable
        // in place. Reassign with the appended KVP. With ParseStateValues=true, Attributes is the
        // populated, authoritative collection on OTel 1.15.x (no State desync — see Landmine L4).
        record.Attributes = (record.Attributes ?? Array.Empty<KeyValuePair<string, object?>>())
            .Append(new KeyValuePair<string, object?>(ExecutionLogScope.ProcessorId, id.ToString()))
            .ToList();
    }
}
```
[VERIFIED idiom: AWS "Developing Custom Processors using OpenTelemetry in .NET 8" —
`record.Attributes = record.Attributes.Append(new KeyValuePair<string,object>(...)).ToList();`]
[VERIFIED type: `record.Attributes` is `IReadOnlyList<KeyValuePair<string,object?>>` — cannot mutate
in place, must reassign — multiple sources incl. opentelemetry-dotnet Discussion #5631.]
[VERIFIED API: custom log enrichment = inherit `OpenTelemetry.BaseProcessor<LogRecord>`, override
`OnEnd(LogRecord)`; must be thread-safe, non-blocking, must not throw — opentelemetry-dotnet
docs/logs/extending-the-sdk/README.md.]

Use `id.ToString()` (string value) to match the keyword-mapped ES field shape the suite relies on
(`EsIndexNames.cs` documents `attributes.*` strings map directly to `keyword`).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Ambient id propagation | Thread ids through method signatures | MEL `BeginScope` + OTel `IncludeScopes` | The whole point of the phase (T-18-04); proven for CorrelationId. |
| Per-consumer scope wiring | A scope in each consumer body | One bus-wide open-generic consume filter | D-02; covers both consoles with zero per-consumer code. |
| Scope→attribute serialization | A custom OTLP attribute writer | The unchanged `ParseStateValues=true` bridge | Already wired (`:43-51`); SPEC forbids reconfiguring. |
| LogRecord attribute append | A custom exporter or formatter | `BaseProcessor<LogRecord>.OnEnd` + `Attributes.Append().ToList()` | Standard OTel extension point (verified). |
| ES poll/backoff in the E2E | A new polling loop | `ElasticsearchTestClient.PollEsForLog` | Verified helper with 404/empty-hits tolerance + backoff. |

**Key insight:** Every primitive this phase needs is already in the repo and proven for CorrelationId.
The risk is NOT "can it be done" — it is "mirror the existing shape exactly, and don't accidentally
prove the wrong thing in the E2E" (Landmine L1).

## Runtime State Inventory

This is an additive code-only phase (new classes + small edits + tests). No rename/migration.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | None — no datastore key/collection/id is renamed. ES attribute fields are write-time additive (new `attributes.WorkflowId/StepId/ProcessorId/ExecutionId/EntryId` keys appear on new docs; existing docs untouched). | None — verified by the additive-only design. |
| Live service config | None — no n8n/Datadog/Tailscale-style external config. The collector + ES mapping is dynamic-template (`all_strings_to_keywords`, `EsIndexNames.cs`) so new `attributes.*` string keys map to `keyword` automatically with no template change. | None — verified by `EsIndexNames.cs` dynamic-template note. |
| OS-registered state | None — no Task Scheduler / pm2 / systemd registration touches these ids. | None. |
| Secrets/env vars | None — no secret or env var references the scope keys. `OTEL_EXPORTER_OTLP_ENDPOINT` is the live OTLP knob (unchanged). | None. |
| Build artifacts | None — new `.cs` files compile into existing assemblies; no package rename, no egg-info analog. | None — verified by additive code-only scope. |

## Common Pitfalls

### Pitfall 1: Proving the wrong thing in the real-stack E2E (LOG-06)
**What goes wrong:** `SampleRoundTripE2ETests` (the D-08 target) ALREADY queries
`attributes.WorkflowId` (VERIFIED `:158`). That hit is currently produced by the orchestrator's
`StartOrchestrationConsumer.LogInformation("Start reload for WorkflowId={WorkflowId}", workflowId)`
(VERIFIED `StartOrchestrationConsumer.cs:35`) — a **named template placeholder**, which
`ParseStateValues=true` already surfaces as `attributes.WorkflowId`. So the existing assertion passes
WITHOUT any scope work, and would NOT prove the SPEC's "from a scope" requirement.
**Why it happens:** `StartOrchestration` (plural `WorkflowIds`) is NOT `IExecutionCorrelated`, so the
new execution-scope filter never scopes it. The existing green hit is template-sourced, not scope-sourced.
**How to avoid:** The LOG-06 assertion must tie `attributes.WorkflowId` to a log line that gets
WorkflowId ONLY via the new scope — i.e. a PROCESSOR-side log (the processor consumes
`EntryStepDispatch`, which IS `IExecutionCorrelated`, so its consume logs get WorkflowId only from the
new filter), scoped to `resource.attributes.service.name == "processor-sample"` (or whatever the
processor's `Service:Name` is — verify in the processor appsettings), with a body.text that only a
scoped processor log carries. Alternatively assert on a DIFFERENT id that has NO template-placeholder
source anywhere (e.g. `attributes.ProcessorId` from the enricher, or `attributes.ExecutionId`).
**Warning signs:** The new E2E passes even with the new filter/enricher reverted → it's proving the
template, not the scope.
**See Landmine L1 for the concrete planner action.**

### Pitfall 2: Nested-scope override not landing on the right lines
**What goes wrong:** The nested `BeginScope` in `EntryStepDispatchConsumer` is placed too narrowly
(or too widely) and the minted ids don't appear on the write/send log lines — or the early
Failed/Cancelled `SendOne` paths (`:72/:87/:106/:119/:125`) are NOT inside any nested scope, so their
ExecutionId is the minted-in-builder value but not scoped.
**Why it happens:** ExecutionId/EntryId are minted PER RESULT inside the loop (`:140`, `:186`), and the
early-return Failed/Cancelled builders ALSO mint an ExecutionId (`:194`, `:204`) but with
`EntryId = Guid.Empty`. The SPEC text says scope "the per-result minted ExecutionId + output EntryId" —
that is the COMPLETED path (loop body, :140-149). The early-return business-failure paths mint an
ExecutionId too but are outside the loop.
**How to avoid:** Scope the loop-body write/send for the Completed path (D-05 is explicitly about "the
write/send log lines"). For the early Failed/Cancelled SendOne paths, the inbound filter already scoped
WorkflowId/StepId/ProcessorId; ExecutionId there is minted in the builder and not separately scoped —
that is acceptable per D-05's narrow wording ("the minted ExecutionId and output EntryId" on the
write/send path). Planner: keep the nested scope minimal and explicit; do not over-reach into the
early returns unless a requirement demands it (it does not).

### Pitfall 3: Enricher placement creates a cross-project dependency / runs on the orchestrator
**What goes wrong:** Putting `o.AddProcessor<ProcessorIdLogEnricher>()` inside the SHARED
`AddBaseConsoleObservability` forces `BaseConsole.Core` to reference `IProcessorContext`
(`BaseProcessor.Core`) — a layering inversion — AND registers the enricher on the ORCHESTRATOR too
(which has no `IProcessorContext`), throwing at DI resolution.
**How to avoid:** Register the enricher ONLY in the processor composition (`AddBaseProcessor`), not in
the shared observability extension (see "D-04 Enricher — placement decision", option 1).
**Warning signs:** Orchestrator fails to start with a missing-`IProcessorContext` DI error.

### Pitfall 4: Close-gate triple-SHA leak from the new E2E
**What goes wrong:** The extended real-stack assertion seeds Redis/RMQ/PG state that isn't drained,
breaking the `psql \l` + `redis-cli --scan` + `rabbitmqctl list_queues` BEFORE==AFTER invariant.
**How to avoid:** `SampleRoundTripE2ETests` already has net-zero teardown discipline
(`L2KeysToCleanup`, `ParentIndexMembersToSrem`, leaves the steady-state liveness key — VERIFIED
`:130-142`, `:399-421`). If the LOG-06 extension only ADDS an ES read (no new seeded state), the
invariant holds unchanged. ES docs are not part of the triple-SHA (logs are append-only telemetry),
so an extra ES query adds nothing to clean up. Prefer extending the EXISTING test method (add one more
`PollEsForLog` assertion) over adding a new seeded test.

### Pitfall 5: MassTransit filter ordering vs. scope nesting
**What goes wrong:** Registering the execution filter BEFORE the correlation filter would make
ExecutionId-set the outer scope and CorrelationId the inner — cosmetically fine for flattened ES
attributes, but D-02 specifies correlation-outer for predictable nesting.
**How to avoid:** Register `UseConsumeFilter(typeof(InboundExecutionScopeConsumeFilter<>), ctx)`
immediately AFTER the correlation `UseConsumeFilter` line (`MessagingServiceCollectionExtensions.cs:51`).
Both KVPs land in ES regardless of order (distinct keys), so this is correctness-neutral but follow D-02.

## Code Examples

### Hermetic filter test (mirror ConsoleCorrelationFilterTests — D-07)
```csharp
// Source pattern: tests/BaseApi.Tests/Console/ConsoleCorrelationFilterTests.cs:51-101
// A probe consumer captures the scope KVPs at consume time. Mirror exactly, but:
//  - probe message implements IExecutionCorrelated with known ids (one Guid.Empty to prove skip)
//  - the probe consumer reads the scope via an injected capture, OR (simpler) asserts on a captured
//    ILogger scope. The correlation test captures ICorrelationAccessor; the execution filter has no
//    accessor (D-01), so capture the scope dictionary directly:
//      * register a fake/capturing ILoggerProvider, OR
//      * have the probe consumer log + assert on a test-double scope collector.
// Wiring (verbatim from the correlation test) — in-memory harness, bus-wide filter:
await using var provider = new ServiceCollection()
    .AddMassTransitTestHarness(x =>
    {
        x.AddConsumer<ExecutionProbeConsumer>();
        x.UsingInMemory((ctx, cfg) =>
        {
            cfg.UseConsumeFilter(typeof(InboundExecutionScopeConsumeFilter<>), ctx);  // SUT
            cfg.ConfigureEndpoints(ctx);
        });
    })
    .BuildServiceProvider(true);
// Cases to assert (D-07): (a) IExecutionCorrelated message → scope carries the 5 ids;
// (b) a Guid.Empty-valued id → NO scope entry for that key (LOG-03 acceptance);
// (c) a non-IExecutionCorrelated message → consumed without throwing, no execution scope.
```
**Scope-capture mechanism note:** The correlation test asserts via `ICorrelationAccessor` (a real
service). The execution filter exposes no such accessor (D-01). The standard MEL way to capture scope
state in a hermetic test is a fake `ILoggerProvider`/`ILogger` whose `BeginScope<TState>(TState state)`
records the `TState` (the `Dictionary<string,object>`). The planner should add a tiny capturing logger
provider in the test project (NOT a new package). [ASSUMED] this is the simplest capture — the repo may
already have a capturing logger helper; grep `tests/` for an existing `ILoggerProvider` test double before
building one.

### Enricher hermetic test (D-07)
```csharp
// Build a MeterProvider-free LoggerFactory with the enricher + an in-memory exporter (or a capturing
// BaseProcessor downstream) and a fake IProcessorContext.
//  Case A: IProcessorContext.Id = a known Guid → emitted LogRecord.Attributes contains
//          KeyValuePair("ProcessorId", id.ToString()).
//  Case B: IProcessorContext.Id = null → no "ProcessorId" attribute, no exception, no Guid.Empty.
// Use an in-memory LogRecord-capturing BaseProcessor<LogRecord> registered AFTER the enricher to
// inspect the final Attributes. (OTel 1.15.x: the InMemoryExporter or a custom capture processor works.)
```

### Real-stack ES assertion (LOG-06 / D-08) — extend SampleRoundTripE2ETests
```csharp
// Source: tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs:150-167 (existing block)
// The existing block already queries attributes.WorkflowId scoped to service.name="orchestrator"
// and matches "Start reload for WorkflowId=" — that is TEMPLATE-sourced (Pitfall 1 / Landmine L1).
// ADD a second PollEsForLog that proves a SCOPE-sourced id on the PROCESSOR side, e.g.:
var scopeProofQuery = $$"""
  {
    "size": 5,
    "sort": [ { "@timestamp": { "order": "desc" } } ],
    "query": {
      "bool": {
        "must": [
          { "term": { "attributes.WorkflowId": "{{wfId}}" } },
          { "term": { "resource.attributes.service.name": "<processor-service-name>" } }
        ]
      }
    }
  }
  """;
// VERIFY <processor-service-name> from the processor's appsettings Service:Name before pinning it.
var scopeProof = await es.PollEsForLog(scopeProofQuery, timeoutMs: EsPollTimeoutMs, ct: ct);
Assert.NotNull(scopeProof);  // proves WorkflowId reached ES from the new scope on a processor log
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Log-enrichment processor must set both `State` AND `StateValues`/`Attributes` to avoid desync data loss | SDK auto-syncs; setting `Attributes` alone is safe | OTel .NET v1.7.0 | The repo is on 1.15.3 → the `record.Attributes = ...Append(...).ToList()` idiom is safe; no `State` mirror needed (Landmine L4). [VERIFIED: opentelemetry-dotnet issue #5186] |
| MassTransit 8.x (Apache-2.0) | 9.x is commercial | mid-2024 | Stay on 8.5.5 (CPM-pinned); do not bump. |

**Deprecated/outdated:** none relevant to this phase.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | The processor host's `Service:Name` (for the ES `resource.attributes.service.name` term in the LOG-06 proof) is `"processor-sample"` or similar — must be read from the processor appsettings before pinning the query. | Code Examples / Pitfall 1 | Low — wrong service-name term yields zero hits; planner verifies the actual value from appsettings. |
| A2 | The simplest hermetic scope-capture is a small capturing `ILoggerProvider` test double; the repo may already have one. | Code Examples (filter test) | Low — if none exists, add a ~20-line test-only double; no new package. |
| A3 | Storing scope id values as `.ToString()` (string) — matching the correlation filter — is what keeps them keyword-mapped in ES. The correlation filter scopes a string and `EsIndexNames` confirms string→keyword. | Pattern 1 / Enricher | Low — Guid scoped as Guid would still serialize, but string matches the proven path exactly. |
| A4 | MEL inner-`BeginScope`-overrides-outer on duplicate keys holds for the OTel MEL bridge's scope flattening (inner wins). | Pattern 4 / D-05 | Medium — if the bridge keeps BOTH duplicate-key entries (outer+inner) rather than inner-wins, the minted-id line could carry two ExecutionId attributes. The hermetic D-05 test (probe-capture the emitted LogRecord, assert minted value present) de-risks this; planner MUST include that assertion. See Landmine L2. |

## Open Questions

1. **Does the OTel MEL bridge collapse duplicate scope keys to inner-wins, or emit both?** (A4)
   - What we know: MEL enumerates scopes outer→inner; OTel `IncludeScopes` flattens scope KVPs into
     `LogRecord` attributes. ES stores `attributes.<Key>` as keyword.
   - What's unclear: whether a duplicate key (outer Guid.Empty-skipped vs inner minted) yields one or
     two attribute entries on the wrapped line. The outer filter SKIPS Guid.Empty ExecutionId/EntryId
     (D-03), so for the dispatch path there is NO outer ExecutionId/EntryId entry to collide with —
     the inner is the only writer. **This likely makes the question moot for the dispatch path**
     (the outer skip + inner mint produces exactly one entry, the intended attributes — confirming D-05's
     design). Verify with the hermetic D-05 probe test capturing the final LogRecord.Attributes.
   - Recommendation: the planner's D-05 hermetic test asserts the emitted LogRecord carries the minted
     ExecutionId/EntryId and exactly one entry per key. This is cheap and removes the only Medium-risk assumption.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK 8.0.4xx | build/test | ✓ (repo builds today) | 8.0.x | — |
| Docker compose stack (PG 5433, Redis 6380, RMQ 5673, OTLP 4317, ES 9200) | LOG-06 real-stack E2E + close-gate | Assumed up (prior phases ran it) | — | Hermetic tests run without it; only the RealStack-tagged E2E + close-gate need it. |
| OpenTelemetry collector 0.152.0 + ES 8.15.5 | ES `attributes.*` shape | ✓ (Wave 0 verified, `EsIndexNames.cs`) | 0.152.0 / 8.15.5 | — |

**Missing dependencies with no fallback:** none identified for code work. The real-stack E2E requires
the full compose stack incl. the `processor-sample` container (VERIFIED `SampleRoundTripE2ETests`
preconditions). Confirm `docker compose ps` shows all healthy before running RealStack-tagged tests.

## Validation Architecture

Nyquist validation is enabled (no config override found disabling it). The phase is test-heavy by
design (LOG-06).

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 3.2.2 (`Directory.Packages.props:121`) |
| Config file | per-project `BaseApi.Tests.csproj` (MTP — Microsoft.Testing.Platform) |
| Quick run command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --configuration Release --no-build -- --filter-class *ConsoleExecutionScopeFilterTests` (MTP filter is `-- --filter-class`, VERIFIED `phase-17-close.ps1:79-80`) |
| Full suite command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --configuration Release --no-build` (VERIFIED close-gate scripts) |
| Hermetic-only filter | RealStack tests are `[Trait("Category","RealStack")]`; the hermetic filter excludes `Category=RealStack` (VERIFIED `SampleRoundTripE2ETests.cs:66-70`) |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| LOG-02 | Filter scopes 5 ids for IExecutionCorrelated; no-op otherwise | unit (in-mem harness) | `dotnet test ... -- --filter-class *ExecutionScopeFilterTests` | ❌ Wave 0 |
| LOG-03 | Keys==param names; Guid.Empty produces no entry | unit | same class | ❌ Wave 0 |
| LOG-04a | Nested scope carries minted ExecutionId+EntryId on write/send | unit (probe-capture LogRecord) | `... --filter-class *EntryStepDispatchScopeTests` | ❌ Wave 0 |
| LOG-04b | Enricher adds ProcessorId when Id set; nothing when null | unit (LogRecord capture) | `... --filter-class *ProcessorIdEnricherTests` | ❌ Wave 0 |
| LOG-05 | WorkflowFireJob log lines carry CorrelationId+WorkflowId | unit | `... --filter-class *WorkflowFireJobScopeTests` | ❌ Wave 0 |
| LOG-01/06 | ≥1 scoped execution id round-trips to ES (processor-side, scope-sourced) | E2E RealStack | full suite incl. `*SampleRoundTripE2ETests` (extended) | ⚠ extend existing |
| LOG-06 | No log-shape regression; existing templates/assertions unchanged | regression | full suite GREEN | ✓ existing 395 facts |
| LOG-06 | Close-gate 3×GREEN + triple-SHA BEFORE==AFTER | gate | new `scripts/phase-29-close.ps1` (mirror `phase-28-close.ps1`) | ❌ Wave 0 |

### Sampling Rate
- **Per task commit:** the new test class for that task (`-- --filter-class *<TestClass>`)
- **Per wave merge:** hermetic full suite (exclude `Category=RealStack`)
- **Phase gate:** full suite (incl. RealStack) GREEN ×3 + triple-SHA, via `scripts/phase-29-close.ps1`

### Wave 0 Gaps
- [ ] `tests/.../Console/ConsoleExecutionScopeFilterTests.cs` — LOG-02/03 (mirror `ConsoleCorrelationFilterTests`)
- [ ] `tests/.../<Processor>/EntryStepDispatchScopeTests.cs` — LOG-04a (nested-scope minted-id capture)
- [ ] `tests/.../Observability/ProcessorIdEnricherTests.cs` — LOG-04b (enricher null-safe + set cases)
- [ ] `tests/.../Orchestrator/WorkflowFireJobScopeTests.cs` — LOG-05
- [ ] A capturing `ILoggerProvider`/`ILogger` test double for scope-KVP capture (if none exists — grep first) (A2)
- [ ] Extend `SampleRoundTripE2ETests` with a PROCESSOR-side scope-sourced `attributes.WorkflowId` (or `ProcessorId`/`ExecutionId`) assertion (LOG-06 / Landmine L1)
- [ ] `scripts/phase-29-close.ps1` — mirror `scripts/phase-28-close.ps1` (3× full-suite + triple-SHA)

## Security Domain

`security_enforcement` is not disabled, but this phase's surface is narrow (internal telemetry).

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V5 Input Validation / Log Injection | yes | T-18-04: ids are placed ONLY as scope/param VALUES under fixed keys, NEVER interpolated into message templates. The ids are server-minted Guids (`NewId.NextGuid()`) — opaque, not user-controlled — so log-forging risk is negligible, but the no-interpolation rule still holds (mirrors the correlation filter's T-18-04 note). |
| V7 Logging | yes | No secrets/PII in scopes — only Guids. `Guid.Empty` skipped (no noise). No log-shape regression (SC#5). |
| V6 Cryptography | no | n/a |
| V2/V3/V4 Auth/Session/Access | no | n/a — internal telemetry |

### Known Threat Patterns for .NET MEL + OTel logs
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Log injection / forging via interpolated user input | Tampering / Repudiation | Structured scope/param values only (T-18-04); ids are server-minted Guids. |
| Telemetry data loss from enricher mutating LogRecord wrong | (reliability) | OTel ≥1.7.0 auto-syncs Attributes (repo is 1.15.3) — Landmine L4. |

## Landmines (planner/executor MUST internalize)

**L1 — The existing real-stack WorkflowId assertion is TEMPLATE-sourced, not scope-sourced.**
`SampleRoundTripE2ETests.cs:158` already asserts `attributes.WorkflowId` scoped to the ORCHESTRATOR
service, fed by `StartOrchestrationConsumer`'s `"Start reload for WorkflowId={WorkflowId}"` named
placeholder (`StartOrchestrationConsumer.cs:35`). `StartOrchestration` is NOT `IExecutionCorrelated`,
so the new filter never scopes it. **The LOG-06 proof must add an assertion that can ONLY pass if the
new scope work is present** — i.e., a PROCESSOR-side log (processor consumes `EntryStepDispatch` =
`IExecutionCorrelated`, so its consume logs get WorkflowId only via the new filter), or assert
`attributes.ProcessorId` (enricher-only) / `attributes.ExecutionId` (has no template-placeholder
producer). Do not let the existing template hit masquerade as scope proof. SPEC LOG-06 says
"from a scope" explicitly.

**L2 — Verify inner-overrides-outer with the D-05 hermetic test (A4).** For the dispatch path the outer
filter SKIPS Guid.Empty ExecutionId/EntryId (D-03), so the inner nested scope is the only writer of
those keys — likely yielding exactly one correct entry. But confirm by capturing the emitted
`LogRecord.Attributes` in the LOG-04a test (assert the minted value present, single entry per key).
Do NOT assume; the bridge's duplicate-key behavior is the one unverified semantic.

**L3 — Enricher must NOT live in the shared `BaseConsole.Core` observability extension.** It depends on
`IProcessorContext` (`BaseProcessor.Core`). Registering it in the shared extension inverts layering AND
breaks the orchestrator (no `IProcessorContext`). Register it in `AddBaseProcessor`
(`BaseProcessorServiceCollectionExtensions.cs`) only, via the DI-resolved `AddProcessor<T>()` overload
(needs `AddSingleton<ProcessorIdLogEnricher>()`). See D-04 placement decision.

**L4 — The v1.5–v1.7 LogRecord-enrichment data-loss bug does NOT apply (repo is OTel 1.15.3).** With
`ParseStateValues=true` on 1.15.x, `record.Attributes = ...Append(kvp).ToList()` is safe; you do NOT
need to mirror `record.State`. (Issue #5186 affected v1.5.0–v1.7.0 only.)

**L5 — `InboundCorrelationConsumeFilter.cs` must stay byte-unchanged (SC#2).** Add a sibling file; never
edit it. A hermetic test (or the close-gate) should assert this — consider a guard test that reads the
file or asserts behavior. Do not "refactor common logic" out of it.

**L6 — `Messaging.Contracts` must stay MassTransit-free.** `ExecutionLogScope` is plain string consts
(like `CorrelationKeys`). No `using MassTransit`. VERIFIED the project currently has none.

**L7 — Close-gate triple-SHA.** The LOG-06 extension should only ADD an ES read to the existing
net-zero E2E (ES logs aren't part of the triple-SHA). Don't introduce new un-drained Redis/RMQ/PG state.

## Sources

### Primary (HIGH confidence)
- Codebase (VERIFIED, read this session): `InboundCorrelationConsumeFilter.cs`,
  `MessagingServiceCollectionExtensions.cs`, `BaseConsoleObservabilityExtensions.cs`,
  `CorrelationKeys.cs`, `IExecutionCorrelated.cs`, `IProcessorContext.cs`,
  `EntryStepDispatchConsumer.cs`, `WorkflowFireJob.cs`, `StartOrchestrationConsumer.cs`,
  `ResultConsumer.cs`, `ExecutionResult.cs`, `EntryStepDispatch.cs`, `ConsoleCorrelationFilterTests.cs`,
  `EsIndexNames.cs`, `ElasticsearchTestClient.cs`, `LogExportTests.cs`, `OrchestrationLogsE2ETests.cs`,
  `SampleRoundTripE2ETests.cs`, `Processor.Sample/Program.cs`,
  `BaseProcessorServiceCollectionExtensions.cs`, `Directory.Packages.props`, close-gate scripts.
- Context7 `/open-telemetry/opentelemetry-dotnet` (core-1.15.0): `BaseProcessor<LogRecord>.OnEnd`
  extension point; `AddProcessor` on `OpenTelemetryLoggerOptions`; DI-resolved `AddProcessor<T>()`;
  deferred `ConfigureOpenTelemetry*Provider` overloads.

### Secondary (MEDIUM confidence)
- AWS Developer Blog "Developing Custom Processors using OpenTelemetry in .NET 8" — the exact
  `record.Attributes = record.Attributes.Append(new KeyValuePair<string,object>(...)).ToList()` idiom
  + `AddProcessor` registration. (Cross-verified against OTel docs extension-point pattern.)
- opentelemetry-dotnet issue #5186 — log-enrichment data-loss bug fixed in v1.7.0 (repo is 1.15.3).
- opentelemetry-dotnet Discussion #5631 — `LogRecord.Attributes` is `IReadOnlyList<KeyValuePair<string,object?>>`, reassign-to-modify.

### Tertiary (LOW confidence)
- (none load-bearing — all critical claims cross-verified)

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all CPM-pinned and read this session.
- Architecture / filter+scope mirror: HIGH — the CorrelationId path is fully proven in-repo; this phase replicates it.
- Enricher API (D-04): HIGH — `BaseProcessor<LogRecord>.OnEnd` + Attributes-append verified across OTel docs + AWS blog + the pinned 1.15.3 (past the desync bug).
- Inner-overrides-outer scope semantics (D-05): MEDIUM — likely moot for the dispatch path (outer skips Guid.Empty), de-risked by the LOG-04a hermetic test (A4 / L2).
- LOG-06 E2E correctness: HIGH on mechanics, with a sharp planner caveat (L1 — prove scope, not template).

**Research date:** 2026-06-02
**Valid until:** ~2026-07-02 (stable; pins frozen by CPM, no fast-moving deps)

## RESEARCH COMPLETE
