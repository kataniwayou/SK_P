# Phase 29: Structured Execution-Scope Logging - Pattern Map

**Mapped:** 2026-06-02
**Files analyzed:** 11 (5 new + 4 modified + 2 new/extended test artifacts)
**Analogs found:** 11 / 11 (every primitive is already proven in-repo for `CorrelationId`)

All analog files and every line anchor cited in CONTEXT.md / RESEARCH.md were re-read and **verified this session** (see "Anchor Verification" at the bottom). This phase is additive: mirror the existing `CorrelationId` path byte-faithfully for the other five ids.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs` (NEW) | middleware (consume filter) | event-driven (bus consume) | `src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs` | exact |
| `src/Messaging.Contracts/ExecutionLogScope.cs` (NEW) | config / constants POCO | n/a (leaf) | `src/Messaging.Contracts/CorrelationKeys.cs` | exact |
| `src/BaseProcessor.Core/Observability/ProcessorIdLogEnricher.cs` (NEW) | provider (OTel `BaseProcessor<LogRecord>`) | transform (log enrichment) | none — see "No Analog Found" + RESEARCH D-04 verified API | new mechanism |
| `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` (MODIFY) | config (DI registration) | request-response (bus build) | itself — the existing `UseConsumeFilter` line | exact (in-file) |
| `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` (MODIFY) | config (DI registration) | request-response | itself + `BaseConsoleObservabilityExtensions.cs` logging block | role-match |
| `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` (MODIFY) | consumer | event-driven (consume → write/send) | `InboundCorrelationConsumeFilter.cs` (BeginScope shape) | role-match |
| `src/Orchestrator/Scheduling/WorkflowFireJob.cs` (MODIFY) | job (Quartz) | event-driven (timer fire) | `InboundCorrelationConsumeFilter.cs` (BeginScope shape) | role-match |
| `tests/BaseApi.Tests/Console/ConsoleExecutionScopeFilterTests.cs` (NEW) | test (unit, in-mem harness) | event-driven | `tests/BaseApi.Tests/Console/ConsoleCorrelationFilterTests.cs` | exact |
| `tests/BaseApi.Tests/Observability/ProcessorIdEnricherTests.cs` (NEW) | test (unit, LogRecord capture) | transform | `ConsoleCorrelationFilterTests.cs` (harness scaffolding) + RESEARCH D-04 | role-match |
| `tests/BaseApi.Tests/.../EntryStepDispatchScopeTests.cs` + `WorkflowFireJobScopeTests.cs` (NEW) | test (unit, scope-capture) | event-driven | `ConsoleCorrelationFilterTests.cs` probe pattern | role-match |
| `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` (EXTEND) | test (E2E, RealStack) | request-response (ES poll) | itself — the existing `advanceQuery` block (`:151-167`) | exact (in-file) |

---

## Pattern Assignments

### `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs` (NEW — middleware, event-driven)

**Analog:** `src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs` (the byte-faithful template; MUST stay unchanged per SC#2 / L5)

**Imports + class signature to mirror** (analog lines 1-5, 29-31):
```csharp
using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;

namespace BaseConsole.Core.Messaging;

public sealed class InboundCorrelationConsumeFilter<T>(
    ICorrelationAccessor accessor, ILogger<InboundCorrelationConsumeFilter<T>> logger)
    : IFilter<ConsumeContext<T>> where T : class
```

**Core scope-state + Probe pattern** (analog lines 33-43):
```csharp
public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
{
    var corrId = (context.Message as ICorrelated)?.CorrelationId.ToString()
                 ?? context.CorrelationId?.ToString()
                 ?? Guid.NewGuid().ToString();
    accessor.Set(corrId);
    using (logger.BeginScope(new Dictionary<string, object> { [CorrelationKeys.LogScope] = corrId }))
        await next.Send(context);
}

public void Probe(ProbeContext context) => context.CreateFilterScope("correlation-in");
```

**Things to replicate exactly:**
- `sealed class ...<T>`, primary-ctor DI of `ILogger<...<T>>`, `: IFilter<ConsumeContext<T>> where T : class`.
- `using (logger.BeginScope(new Dictionary<string, object> { ... })) await next.Send(context);` — same `Dictionary<string,object>` scope-state shape (Claude's-Discretion item resolved: match the analog → use a `Dictionary`).
- Store id values as `.ToString()` (string), matching the analog's keyword-mapped ES shape (A3).
- `Probe` override returning `context.CreateFilterScope("execution-scope-in")`.

**Things to DIVERGE on (per D-01/D-03):**
- **NO** `ICorrelationAccessor` ctor param — the execution filter does not touch `CorrelationId` (D-01). Ctor is `ILogger<InboundExecutionScopeConsumeFilter<T>>` only.
- Body-read `IExecutionCorrelated` (not `ICorrelated`); for a non-`IExecutionCorrelated` message, `await next.Send(context); return;` pass-through no-op (D-03).
- Scope ONLY the five execution ids; skip any `Guid.Empty` value (build the dictionary conditionally):
```csharp
if (context.Message is not IExecutionCorrelated ec) { await next.Send(context); return; }
var state = new Dictionary<string, object>();
if (ec.WorkflowId  != Guid.Empty) state[ExecutionLogScope.WorkflowId]  = ec.WorkflowId.ToString();
if (ec.StepId      != Guid.Empty) state[ExecutionLogScope.StepId]      = ec.StepId.ToString();
if (ec.ProcessorId != Guid.Empty) state[ExecutionLogScope.ProcessorId] = ec.ProcessorId.ToString();
if (ec.ExecutionId != Guid.Empty) state[ExecutionLogScope.ExecutionId] = ec.ExecutionId.ToString();
if (ec.EntryId     != Guid.Empty) state[ExecutionLogScope.EntryId]     = ec.EntryId.ToString();
using (logger.BeginScope(state)) await next.Send(context);
```
- `IExecutionCorrelated` (verified `IExecutionCorrelated.cs:9-16`) declares: `ExecutionId, WorkflowId, StepId, ProcessorId, EntryId` (plus `CorrelationId` via base `ICorrelated`). Do NOT scope `CorrelationId` here (D-01).

---

### `src/Messaging.Contracts/ExecutionLogScope.cs` (NEW — constants POCO)

**Analog:** `src/Messaging.Contracts/CorrelationKeys.cs` (verified — pure POCO, zero usings, no MassTransit ref; L6)

**Full analog** (lines 1-8):
```csharp
namespace Messaging.Contracts;

/// <summary>Cross-service correlation log-scope key. MUST equal the literal
/// CorrelationIdMiddleware uses so OTel IncludeScopes serializes one Elasticsearch attribute.</summary>
public static class CorrelationKeys
{
    public const string LogScope = "CorrelationId";
}
```

**Things to replicate:**
- `namespace Messaging.Contracts;` file-scoped; `public static class`; `public const string` members; NO usings (keeps the project MassTransit-free — L6).
- Key string == structured-param name (SPEC constraint LOG-03) so scope-derived and param-derived attributes coincide on the same ES `attributes.<Key>` field.

**Target shape** (Claude's-Discretion layout resolved; `CorrelationId` deliberately stays in `CorrelationKeys`, NOT here — D-01):
```csharp
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

---

### `src/BaseProcessor.Core/Observability/ProcessorIdLogEnricher.cs` (NEW — OTel provider, transform)

**Analog:** NONE in-repo (this is the only genuinely new mechanism — D-04). Use the RESEARCH-verified OTel API (`29-RESEARCH.md` D-04 section). The only in-repo references are the dependency types it consumes.

**Dependency it reads** — `IProcessorContext.Id` (verified `IProcessorContext.cs:34-36`):
```csharp
/// <summary>The resolved processor Id (null until Loop A completes).</summary>
Guid? Id { get; }
```
This is `Guid?` — **null until identity resolves**; the enricher MUST be null-safe (emit nothing, never `Guid.Empty` — SPEC constraint).

**Target implementation** (RESEARCH-verified `BaseProcessor<LogRecord>.OnEnd` + Attributes-append idiom; safe on the repo's OTel 1.15.3 — L4):
```csharp
using BaseProcessor.Core.Identity;
using Messaging.Contracts;           // ExecutionLogScope.ProcessorId
using OpenTelemetry;                 // BaseProcessor<T>
using OpenTelemetry.Logs;            // LogRecord

namespace BaseProcessor.Core.Observability;

public sealed class ProcessorIdLogEnricher(IProcessorContext context) : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord record)
    {
        if (context.Id is not { } id) return;   // null-safe: nothing before identity resolves
        record.Attributes = (record.Attributes ?? Array.Empty<KeyValuePair<string, object?>>())
            .Append(new KeyValuePair<string, object?>(ExecutionLogScope.ProcessorId, id.ToString()))
            .ToList();
    }
}
```

**Things to replicate (from RESEARCH, not a code analog):**
- Inherit `OpenTelemetry.BaseProcessor<LogRecord>`, override `OnEnd(LogRecord)`. Must be thread-safe, non-blocking, must not throw.
- Reassign `record.Attributes` (it is `IReadOnlyList<KeyValuePair<string,object?>>` — cannot mutate in place); `.Append(kvp).ToList()`.
- Use `id.ToString()` (string) to match the `keyword`-mapped ES field shape (`EsIndexNames.cs:50-61`).
- Key = `ExecutionLogScope.ProcessorId` (== `"ProcessorId"`) so it coincides on the same ES field as scoped/param `ProcessorId`.
- **Lives in `BaseProcessor.Core`** (depends on `IProcessorContext`) — NOT in `BaseConsole.Core` (L3).

---

### `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` (MODIFY — DI registration)

**Analog:** the existing `UseConsumeFilter` registration in this same file (verified `:48-59`).

**Existing registration block** (lines 48-59):
```csharp
x.UsingRabbitMq((ctx, c) =>
{
    c.Host(rabbitHost, h => { h.Username(rabbitUser); h.Password(rabbitPass); });
    c.UseConsumeFilter(typeof(InboundCorrelationConsumeFilter<>), ctx);   // CORR-01 bus-wide (open-generic)
    c.UseSendFilter(typeof(OutboundCorrelationSendFilter<>), ctx);        // CORR-02
    c.UsePublishFilter(typeof(OutboundCorrelationPublishFilter<>), ctx);  // CORR-02
    configureBus?.Invoke(ctx, c);
    c.ConfigureEndpoints(ctx);
});
```

**Edit (D-02):** add ONE line immediately AFTER the correlation `UseConsumeFilter` (line 51) so `CorrelationId` is the OUTER scope and the execution id-set nests inside:
```csharp
c.UseConsumeFilter(typeof(InboundCorrelationConsumeFilter<>), ctx);            // CORR-01 (OUTER)
c.UseConsumeFilter(typeof(InboundExecutionScopeConsumeFilter<>), ctx);         // LOG-02 (INNER) — ADD
```
**Things to replicate:** open-generic `typeof(...<>)` + `ctx` shape (the verified MassTransit 8.5.5 bus-wide scoped-consume-filter idiom). This single registration covers BOTH consoles (orchestrator `ResultConsumer` ← `ExecutionResult` and processor `EntryStepDispatchConsumer` ← `EntryStepDispatch`); both implement `IExecutionCorrelated`. No per-console wiring.

---

### `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` (MODIFY — DI registration)

**Analog:** this same file's existing `AddSingleton`/`AddHostedService` registrations (verified `:48-110`) + the OTel logging block in `BaseConsoleObservabilityExtensions.cs:43-51`.

**Where the enricher registers (L3 — processor side ONLY, NOT the shared observability extension):**
The processor already registers its singletons here (verified `:88` `services.AddSingleton<IProcessorContext, ProcessorContext>();`). Add the enricher as a sibling singleton AND register it on the processor's logger provider:
```csharp
// existing precedent (line 88):
services.AddSingleton<IProcessorContext, ProcessorContext>();

// ADD (D-04 / RESEARCH-recommended option 1 — DI-resolved):
services.AddSingleton<ProcessorIdLogEnricher>();
services.ConfigureOpenTelemetryLoggerProvider((sp, lp) => lp.AddProcessor(sp.GetRequiredService<ProcessorIdLogEnricher>()));
// (planner/executor confirms the exact deferred-config overload against OTel 1.15.3; the DI-resolved
//  AddProcessor<T>() + AddSingleton<T>() pair is the verified idiom — RESEARCH D-04.)
```

**Things to replicate:**
- Registration lives in `AddBaseProcessor` (processor composition root) so ONLY the processor gets the enricher (orchestrator has no `IProcessorContext` — registering in the shared extension would throw at DI resolution — L3 / Pitfall 3).
- Reuse the EXISTING OTel logging bridge (`BaseConsoleObservabilityExtensions.cs:43-51`, `IncludeScopes=true`, `ParseStateValues=true`) UNCHANGED — the enricher is ADDED via `AddProcessor`, never by editing those options (anti-pattern in RESEARCH).
- DI-resolved overload is required because the enricher needs the SINGLETON `IProcessorContext` (the instance overload `AddProcessor(new ...)` cannot resolve DI).

**Reference — the unchanged shared logging block** (`BaseConsoleObservabilityExtensions.cs:43-51`, do NOT edit):
```csharp
builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes           = true;   // load-bearing: serializes BeginScope KVPs as attributes
    o.ParseStateValues        = true;
    o.SetResourceBuilder(ResourceBuilder.CreateDefault()
        .AddService(serviceName: serviceName, serviceVersion: serviceVersion));
    o.AddOtlpExporter();
});
```

---

### `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` (MODIFY — consumer, event-driven)

**Analog (scope-state shape):** `InboundCorrelationConsumeFilter.cs:39` `BeginScope(new Dictionary<string,object> {...})`.

**Verified mint sites + write/send lines** (this file):
- `:140` `var newEntryId = NewId.NextGuid();` (the output EntryId — minted PER RESULT inside the loop `:131-150`)
- `:144-147` `await db.StringSetAsync(L2ProjectionKeys.ExecutionData(newEntryId), r.OutputData, ...)` (the L2 write)
- `:149` `built.Add(BuildCompleted(dispatch, newEntryId));` — `BuildCompleted` (`:182-188`) mints `ExecutionId = NewId.NextGuid()` at `:186`
- `:158` resolve endpoint; `:159-164` `foreach (var er in built) await endpoint.Send(er, CancellationToken.None);` (the sends)

**Existing loop body to wrap** (lines 131-150, the Completed path):
```csharp
foreach (var r in results)
{
    if (!ProcessorJsonSchemaValidator.TryValidate(context.OutputDefinition, r.OutputData, out var outErrors))
    { built.Add(BuildFailed(dispatch, string.Join("; ", outErrors))); continue; }

    var newEntryId = NewId.NextGuid();
    await db.StringSetAsync(
        L2ProjectionKeys.ExecutionData(newEntryId), r.OutputData,
        expiry: TimeSpan.FromSeconds(opts.ExecutionDataTtlSeconds));
    built.Add(BuildCompleted(dispatch, newEntryId));
}
```

**Edit (D-05):** open a nested `BeginScope` (SAME `ExecutionLogScope` keys, same `Dictionary<string,object>` shape) carrying the per-result minted `ExecutionId` + output `EntryId`, wrapping ONLY the write/send log lines. MEL inner-overrides-outer means those lines report the minted ids rather than the inbound `Guid.Empty` the filter skipped. The consumer's primary ctor already injects `ILogger<EntryStepDispatchConsumer> logger` (`:46`).

**Things to replicate:** `using (logger.BeginScope(new Dictionary<string, object> { [ExecutionLogScope.ExecutionId] = ..., [ExecutionLogScope.EntryId] = newEntryId.ToString() })) { ... }`. Keep the scope MINIMAL — wrap the loop-body write/send for the Completed path; do NOT over-reach into the early-return `SendOne` Failed/Cancelled paths (`:72/:87/:106/:119/:125`) — D-05's wording is narrow (Pitfall 2). Note `ExecutionId` is minted INSIDE `BuildCompleted` (`:186`); to scope the SAME value the executor mints it once in the loop and passes it in (small refactor of `BuildCompleted`'s signature, or mint before the scope). **Add the LOG-04a hermetic test asserting exactly one entry per key with the minted value (L2/A4).**

---

### `src/Orchestrator/Scheduling/WorkflowFireJob.cs` (MODIFY — Quartz job, event-driven)

**Analog (scope-state shape):** `InboundCorrelationConsumeFilter.cs:39` `BeginScope(new Dictionary<string,object> {...})`.

**Verified anchors** (this file): `workflowId` parsed at `:40`; `correlationId = NewId.NextGuid()` at `:54`; early returns at `:42-44` (unparseable) and `:46-51` (workflow absent) happen BEFORE the correlationId mint; the dispatch loop is `:56-72`; liveness refresh + reschedule `:74-86`. The ctor already injects `ILogger<WorkflowFireJob> logger` (`:35`).

**Existing post-mint body** (lines 53-86, abbreviated):
```csharp
var correlationId = NewId.NextGuid();   // :54

foreach (var entryStepId in wf.EntryStepIds) { ... await dispatcher.DispatchAsync(...); }
// liveness refresh + self-reschedule
```

**Edit (D-06):** wrap the post-mint body (after `:54`, where BOTH ids are known) in an explicit `BeginScope` carrying `CorrelationId` (via `CorrelationKeys.LogScope`) and `WorkflowId` (via `ExecutionLogScope.WorkflowId`), since the Quartz job runs outside the consume pipeline (no filter sees it):
```csharp
var correlationId = NewId.NextGuid();
using (logger.BeginScope(new Dictionary<string, object>
{
    [CorrelationKeys.LogScope]     = correlationId.ToString(),
    [ExecutionLogScope.WorkflowId] = workflowId.ToString(),
}))
{
    foreach (var entryStepId in wf.EntryStepIds) { ... }
    // liveness refresh + reschedule
}
```
**Things to replicate:** same `Dictionary<string,object>` scope-state shape; `.ToString()` string values; uses `CorrelationKeys.LogScope` for the correlation id (this is the ONE place the job owns CorrelationId — it is NOT in the consume pipeline) and `ExecutionLogScope.WorkflowId` for the workflow id.

---

### `tests/BaseApi.Tests/Console/ConsoleExecutionScopeFilterTests.cs` (NEW — unit test, in-mem harness)

**Analog:** `tests/BaseApi.Tests/Console/ConsoleCorrelationFilterTests.cs` (verified — probe-consumer + in-memory harness pattern)

**Imports + harness wiring to mirror** (analog lines 1-8, 60-74):
```csharp
using BaseConsole.Core.Messaging;
using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

// ...
await using var provider = new ServiceCollection()
    .AddMassTransitTestHarness(x =>
    {
        x.AddConsumer<ProbeConsumer>();
        x.UsingInMemory((ctx, cfg) =>
        {
            cfg.UseConsumeFilter(typeof(InboundExecutionScopeConsumeFilter<>), ctx);  // SUT (replaces the 3 CORR filters)
            cfg.ConfigureEndpoints(ctx);
        });
    })
    .BuildServiceProvider(true);
```

**Probe-consumer capture pattern to mirror** (analog lines 34-43): a consumer with a `public static volatile` captured field, set in `Consume`, asserted after `harness.Consumed.Any<...>(ct)`.

**Things to replicate:**
- `TestContext.Current.CancellationToken`; `harness.Start()` / `try { ... } finally { await harness.Stop(ct); }`; `Assert.True(await harness.Consumed.Any<...>(ct))`.
- Probe message implements `IExecutionCorrelated` with known ids (set ONE id to `Guid.Empty` to prove the skip — LOG-03 acceptance).

**Things to DIVERGE on (D-07 / RESEARCH A2):** the correlation test captures via the real `ICorrelationAccessor`; the execution filter has NO accessor (D-01). Capture the scope `Dictionary<string,object>` instead — add a tiny capturing `ILoggerProvider`/`ILogger` test double whose `BeginScope<TState>(TState)` records the state. **Grep `tests/` for an existing capturing logger double before building one** (A2). Cases (D-07): (a) `IExecutionCorrelated` → scope carries the 5 ids; (b) a `Guid.Empty` id → NO entry for that key; (c) non-`IExecutionCorrelated` → consumed without throwing, no execution scope.

---

### `tests/BaseApi.Tests/Observability/ProcessorIdEnricherTests.cs` (NEW — unit test, LogRecord capture)

**Analog:** RESEARCH D-07 enricher-test recipe + harness scaffolding style from `ConsoleCorrelationFilterTests.cs`.

**Pattern:** build a `LoggerFactory` with the `ProcessorIdLogEnricher` + a downstream in-memory LogRecord-capturing `BaseProcessor<LogRecord>` (registered AFTER the enricher) and a fake `IProcessorContext`. Cases: (A) `IProcessorContext.Id = known Guid` → captured `LogRecord.Attributes` contains `KeyValuePair("ProcessorId", id.ToString())`; (B) `Id = null` → no `ProcessorId` attribute, no exception, no `Guid.Empty`.

**Things to replicate:** a fake `IProcessorContext` exposing settable `Guid? Id` (the only member the enricher reads); assert on the FINAL `record.Attributes` after the enricher's `OnEnd`.

---

### `tests/BaseApi.Tests/.../EntryStepDispatchScopeTests.cs` + `WorkflowFireJobScopeTests.cs` (NEW — unit tests)

**Analog:** `ConsoleCorrelationFilterTests.cs` capturing-scope pattern (reuse the same capturing-logger double from the filter test). 
- `EntryStepDispatchScopeTests` (LOG-04a): probe-capture the emitted `LogRecord.Attributes` on the write/send path; assert the MINTED `ExecutionId`+`EntryId` present and exactly one entry per key (de-risks L2/A4).
- `WorkflowFireJobScopeTests` (LOG-05): drive `WorkflowFireJob.Execute` and assert its log lines carry `CorrelationId` + `WorkflowId` scope values.

---

### `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` (EXTEND — RealStack E2E)

**Analog:** the existing `advanceQuery` block in this same file (verified `:150-167`) + the `ElasticsearchTestClient.PollEsForLog` helper (verified `ElasticsearchTestClient.cs:62`).

**Existing block to mirror** (lines 150-167):
```csharp
using var es = new ElasticsearchTestClient();
var advanceQuery = $$"""
  { "size": 5, "sort": [ { "@timestamp": { "order": "desc" } } ],
    "query": { "bool": { "must": [
      { "term": { "attributes.WorkflowId": "{{wfId}}" } },
      { "term": { "resource.attributes.service.name": "orchestrator" } }
    ] } } }
  """;
var advance = await es.PollEsForLog(advanceQuery, timeoutMs: EsPollTimeoutMs, ct: ct);
Assert.NotNull(advance);
Assert.Contains(StartReloadMessage, advance!.Value.GetRawText());
```

**Edit (D-08 / L1 — prove SCOPE not TEMPLATE):** ADD a SECOND `PollEsForLog` asserting a scope-sourced id on the **PROCESSOR** side. The existing `orchestrator` hit is TEMPLATE-sourced (`StartOrchestrationConsumer`'s `"Start reload for WorkflowId={WorkflowId}"`), so it would pass even with the new scope work reverted (L1 / Pitfall 1). The new assertion must scope `resource.attributes.service.name == "processor-sample"` (VERIFIED from `src/Processor.Sample/appsettings.json` → `Service:Name = "processor-sample"`; resolves assumption A1):
```csharp
var scopeProofQuery = $$"""
  { "size": 5, "sort": [ { "@timestamp": { "order": "desc" } } ],
    "query": { "bool": { "must": [
      { "term": { "attributes.WorkflowId": "{{wfId}}" } },
      { "term": { "resource.attributes.service.name": "processor-sample" } }
    ] } } }
  """;
var scopeProof = await es.PollEsForLog(scopeProofQuery, timeoutMs: EsPollTimeoutMs, ct: ct);
Assert.NotNull(scopeProof);   // WorkflowId reached ES from the new scope on a processor log
```
**Things to replicate:** the `$$"""..."""` raw-string template (NOT manual concat), `attributes.WorkflowId` field path (= `EsIndexNames` `attributes.<Key>` convention), `resource.attributes.service.name` term, `PollEsForLog(..., timeoutMs: EsPollTimeoutMs, ct: ct)`, `Assert.NotNull`. ADD ONLY an ES READ — no new seeded Redis/RMQ/PG state (preserves the net-zero teardown + close-gate triple-SHA — L7 / Pitfall 4). Existing teardown discipline verified (`:127-142`: `ParentIndexMembersToSrem`, `L2KeysToCleanup`).

---

## Shared Patterns

### MEL `BeginScope` scope-state shape
**Source:** `src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs:39`
**Apply to:** the new filter, the `EntryStepDispatchConsumer` nested scope, and `WorkflowFireJob`.
```csharp
using (logger.BeginScope(new Dictionary<string, object> { [<ExecutionLogScope.Key or CorrelationKeys.LogScope>] = <id>.ToString() }))
    /* wrapped body */;
```
- ALWAYS `Dictionary<string, object>` (resolves the Claude's-Discretion "Dictionary vs KeyValuePair list" question → match the analog → Dictionary).
- ALWAYS `.ToString()` string values (keyword-mapped ES shape — A3 / `EsIndexNames.cs:50-61`).
- Key strings come from `ExecutionLogScope.*` (new) or `CorrelationKeys.LogScope` (existing) — never literals at call sites.

### Scope-key constants POCO
**Source:** `src/Messaging.Contracts/CorrelationKeys.cs`
**Apply to:** the new `ExecutionLogScope.cs`. Pure POCO, file-scoped namespace, `public static class` + `public const string`, NO usings, NO MassTransit (L6). Key string == structured-param name (LOG-03).

### Open-generic bus-wide consume-filter registration
**Source:** `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs:51`
**Apply to:** registering the new filter — `c.UseConsumeFilter(typeof(InboundExecutionScopeConsumeFilter<>), ctx);` immediately AFTER the correlation line (D-02).

### OTel logs bridge (REUSE UNCHANGED)
**Source:** `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs:43-51`
**Apply to:** the enricher relies on `IncludeScopes=true` + `ParseStateValues=true` already on. Do NOT edit these options — add the enricher via `AddProcessor` on the processor's logger provider only (L3).

### ES `attributes.*` field path + polling
**Source:** `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs:40,71,80` + `ElasticsearchTestClient.cs:62`
**Apply to:** the LOG-06 E2E extension. `attributes.<Key>` (string → keyword, query the path DIRECTLY — never a `.keyword` sub-field), `resource.attributes.service.name`, `LogsDataStream = "logs-generic.otel-default"`, `PollEsForLog`.

---

## No Analog Found

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| `src/BaseProcessor.Core/Observability/ProcessorIdLogEnricher.cs` | OTel provider (`BaseProcessor<LogRecord>`) | transform | No existing `BaseProcessor<LogRecord>`/log-enricher in the repo — this is the only NEW mechanism (D-04). The executor uses the RESEARCH-verified API (`29-RESEARCH.md` D-04 section: `OnEnd` + `record.Attributes = ...Append(kvp).ToList()`, safe on OTel 1.15.3 per L4). Its `IProcessorContext.Id` dependency and `ExecutionLogScope.ProcessorId` key ARE in-repo and verified. |

---

## Anchor Verification

Every analog file and cited line anchor was re-read this session and confirmed:

| Cited anchor | Status |
|--------------|--------|
| `InboundCorrelationConsumeFilter.cs:29-44` (filter shape, `:39` BeginScope) | VERIFIED |
| `MessagingServiceCollectionExtensions.cs:48-59` (`:51` correlation `UseConsumeFilter`) | VERIFIED |
| `CorrelationKeys.cs:1-8` (`LogScope = "CorrelationId"`, pure POCO) | VERIFIED |
| `IExecutionCorrelated.cs:9-16` (5 execution ids + base `ICorrelated`) | VERIFIED |
| `BaseConsoleObservabilityExtensions.cs:43-51` (`IncludeScopes`/`ParseStateValues`) | VERIFIED |
| `IProcessorContext.cs:34-36` (`Guid? Id`) | VERIFIED |
| `WorkflowFireJob.cs:37-87` (`:40` workflowId, `:54` correlationId mint) | VERIFIED |
| `EntryStepDispatchConsumer.cs:131-150` (`:140` newEntryId, `:144` write), `:158-164` sends, `:182-188` BuildCompleted `:186` ExecutionId | VERIFIED |
| `BaseProcessorServiceCollectionExtensions.cs:48-110` (`:88` AddSingleton IProcessorContext) | VERIFIED |
| `ConsoleCorrelationFilterTests.cs:1-138` (probe-consumer + in-mem harness) | VERIFIED |
| `EsIndexNames.cs:40,71,80` (data stream, field paths, string→keyword) | VERIFIED |
| `ElasticsearchTestClient.cs:62` (`PollEsForLog`) | VERIFIED |
| `SampleRoundTripE2ETests.cs:150-167` (existing `advanceQuery` block), `:127-142` teardown | VERIFIED |
| `Processor.Sample/Program.cs:15-16` (`AddBaseConsoleObservability` + `AddBaseProcessor`) | VERIFIED |
| `Processor.Sample/appsettings.json` `Service:Name = "processor-sample"` (resolves A1) | VERIFIED |

## Metadata

**Analog search scope:** `src/BaseConsole.Core/Messaging`, `src/BaseConsole.Core/DependencyInjection`, `src/Messaging.Contracts`, `src/BaseProcessor.Core/{Identity,Processing,DependencyInjection}`, `src/Orchestrator/Scheduling`, `src/Processor.Sample`, `tests/BaseApi.Tests/{Console,Observability/Helpers,Orchestrator}`.
**Files scanned/read:** 13 (4 contracts/filters, 3 DI/observability extensions, 2 consumers/jobs, 1 identity interface, 3 test/helper files) + 1 appsettings.
**Pattern extraction date:** 2026-06-02
