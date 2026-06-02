# Phase 30: Runtime & Business Metrics - Pattern Map

**Mapped:** 2026-06-02
**Files analyzed:** 9 (3 NEW src, 4 MODIFY src, 2 NEW test)
**Analogs found:** 9 / 9 (every file has a strong in-repo analog)

> **READ FIRST — meter-placement compile firewall.** `AddMeter("BaseProcessor")` MUST NOT go in
> `BaseConsoleObservabilityExtensions` (`BaseConsole.Core`). `BaseConsole.Core` has **no project
> reference** to `BaseProcessor.Core` (dependency runs the other way), so it cannot see
> `ProcessorMetrics.MeterName`. Register the processor meter inside `AddBaseProcessor`
> (`BaseProcessor.Core`) via `services.ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(...))` —
> the **exact** additive seam Phase 29 used for the log enricher at
> `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs:101-103`
> (`ConfigureOpenTelemetryLoggerProvider`). The meter analog is `ConfigureOpenTelemetryMeterProvider`,
> same package (`OpenTelemetry.Extensions.Hosting` 1.15.x). Use the SAME seam in
> `Orchestrator/Program.cs` for `AddMeter("Orchestrator")` to keep D-02 const symmetry.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/Orchestrator/Observability/OrchestratorMetrics.cs` (NEW) | observability holder (DI singleton) | transform (instrument factory) | `src/BaseProcessor.Core/Observability/ProcessorIdLogEnricher.cs` | role-match (sealed DI-singleton OTel holder, ctor-injected dep) |
| `src/BaseProcessor.Core/Observability/ProcessorMetrics.cs` (NEW) | observability holder (DI singleton) | transform (instrument factory) | `src/BaseProcessor.Core/Observability/ProcessorIdLogEnricher.cs` (same dir) | role-match |
| `src/Orchestrator/Program.cs` (MODIFY) | config / composition root | request-response (DI wiring) | `BaseProcessorServiceCollectionExtensions.cs:101-103` (logger-provider seam) | exact (same `ConfigureOpenTelemetry*Provider` seam) |
| `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` (MODIFY) | config / composition root | request-response (DI wiring) | itself, lines 101-103 (Phase-29 precedent) | exact (copy the log-enricher seam shape) |
| `src/Orchestrator/Dispatch/StepDispatcher.cs` (MODIFY) | service (dispatch single-owner) | event-driven (broker Send) | itself (add increment after `endpoint.Send`) | exact (single dispatch owner, D-05) |
| `src/Orchestrator/Consumers/ResultConsumer.cs` (MODIFY) | consumer | event-driven (consume) | itself (increment at top of `Consume`) | exact (D-06) |
| `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` (MODIFY) | consumer | event-driven (consume + Send) | itself (refactor `SendOne`+loop → `SendResult`) | exact (D-07/D-08) |
| `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` (MODIFY) | config (OTel wiring seam) | request-response (DI wiring) | itself + `BaseConsoleObservabilityExtensions.cs` (sibling) | exact (shared-resource + instance-id, D-09/D-10) |
| `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs` (MODIFY) | config (OTel wiring seam) | request-response (DI wiring) | itself + `ObservabilityServiceCollectionExtensions.cs` (sibling) | exact |
| `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` (NEW) | test helper | request-response (HTTP poll) | `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs` (sibling) | exact (poll-with-backoff) |
| `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs` (NEW) | test (E2E) | event-driven (round-trip + query) | `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` (sibling) | exact (RealStackWebAppFactory reuse) |

## Pattern Assignments

### `src/Orchestrator/Observability/OrchestratorMetrics.cs` (NEW — observability holder)

**Analog:** `src/BaseProcessor.Core/Observability/ProcessorIdLogEnricher.cs` (the existing sealed,
DI-singleton, ctor-injected OTel-namespace holder — closest in-repo analog for a DI-registered
observability singleton). The `IMeterFactory` instrument-creation shape is from RESEARCH Pattern 1.

**Holder shape to copy** (sealed + primary-ctor-style dependency, namespace `*.Observability`, matching
`ProcessorIdLogEnricher` conventions). Counter names are snake_case, **no `_total`** (D-03 — the
collector appends `_total`):
```csharp
using System.Diagnostics.Metrics;

namespace Orchestrator.Observability;

public sealed class OrchestratorMetrics
{
    public const string MeterName = "Orchestrator";   // D-02 — MUST match AddMeter("Orchestrator")

    public Counter<long> DispatchSent { get; }
    public Counter<long> ResultConsumed { get; }

    public OrchestratorMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        DispatchSent   = meter.CreateCounter<long>("orchestrator_dispatch_sent");      // D-03
        ResultConsumed = meter.CreateCounter<long>("orchestrator_result_consumed");    // D-03
    }
}
```

**Tag-key case-preservation note** (from RESEARCH): the `ProcessorId` tag KEY is preserved through the
collector exporter (no lowercasing) — emit `"ProcessorId"` (PascalCase) so the SPEC's
`sum by (ProcessorId)(...)` PromQL works verbatim. Use `"D"` GUID format to match the dispatch queue
naming (`queue:{processorId:D}`).

---

### `src/BaseProcessor.Core/Observability/ProcessorMetrics.cs` (NEW — observability holder)

**Analog:** `src/BaseProcessor.Core/Observability/ProcessorIdLogEnricher.cs` (SAME directory — drop the
new holder beside it).

**Holder shape** (same as orchestrator, but two processor counters; `result_sent` is incremented with
an `outcome` tag at the call-site, NOT here):
```csharp
using System.Diagnostics.Metrics;

namespace BaseProcessor.Core.Observability;

public sealed class ProcessorMetrics
{
    public const string MeterName = "BaseProcessor";   // D-02 — MUST match AddMeter("BaseProcessor")

    public Counter<long> DispatchConsumed { get; }
    public Counter<long> ResultSent { get; }

    public ProcessorMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        DispatchConsumed = meter.CreateCounter<long>("processor_dispatch_consumed");   // D-03
        ResultSent       = meter.CreateCounter<long>("processor_result_sent");         // D-03
    }
}
```

---

### `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` (MODIFY — meter registration)

**Analog: ITSELF, lines 101-103** — the Phase-29 log-enricher seam. Copy that exact two-line shape for
the meter. **This is the compile-firewall fix (Landmine 1).**

**Existing precedent to mirror** (`BaseProcessorServiceCollectionExtensions.cs:101-103`):
```csharp
services.AddSingleton<ProcessorIdLogEnricher>();
services.ConfigureOpenTelemetryLoggerProvider((sp, lp) =>
    lp.AddProcessor(sp.GetRequiredService<ProcessorIdLogEnricher>()));
```

**New code to add** (same seam, meter-provider variant — `using OpenTelemetry.Metrics;` +
`using BaseProcessor.Core.Observability;` already present at line 5):
```csharp
services.AddSingleton<ProcessorMetrics>();
services.ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(ProcessorMetrics.MeterName));
```
> Place alongside step 6b (the log-enricher block). `ConfigureOpenTelemetryMeterProvider` is in
> `OpenTelemetry.Extensions.Hosting` (already referenced — it provides the sibling
> `ConfigureOpenTelemetryLoggerProvider`). No `BaseConsole.Core` edit for the processor meter.

---

### `src/Orchestrator/Program.cs` (MODIFY — meter registration)

**Analog:** the same `ConfigureOpenTelemetry*Provider` seam (above). Add after `AddBaseConsoleObservability`
(line 20) / in the singleton-wiring block (lines 47-52, beside `AddSingleton<IStepDispatcher, StepDispatcher>`).

**New code** (preserves D-02 const symmetry — the base lib can't see `OrchestratorMetrics.MeterName`):
```csharp
using Orchestrator.Observability;
using OpenTelemetry.Metrics;   // ConfigureOpenTelemetryMeterProvider lives via OpenTelemetry.Extensions.Hosting

builder.Services.AddSingleton<OrchestratorMetrics>();
builder.Services.ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(OrchestratorMetrics.MeterName));
```
> Existing singleton-registration style to match (`Program.cs:47-51`): `builder.Services.AddSingleton<...>()`.

---

### `src/Orchestrator/Dispatch/StepDispatcher.cs` (MODIFY — increment after Send, D-05)

**Analog: ITSELF** — single build-and-`Send` owner. Inject `OrchestratorMetrics` via the primary ctor;
increment **after** `endpoint.Send` returns (D-04 symmetric rule: SENT = count-after-Send). An infra
throw on `Send` correctly skips the increment.

**Current ctor + Send** (`StepDispatcher.cs:11`, `:25-26`):
```csharp
public sealed class StepDispatcher(ISendEndpointProvider sendProvider) : IStepDispatcher
// ...
var endpoint = await sendProvider.GetSendEndpoint(new Uri($"queue:{processorId:D}"));
await endpoint.Send(msg, ct);
```

**Modified** (add ctor param + one increment after Send; `processorId` is in scope as a method arg):
```csharp
public sealed class StepDispatcher(
    ISendEndpointProvider sendProvider,
    OrchestratorMetrics metrics) : IStepDispatcher
// ...
var endpoint = await sendProvider.GetSendEndpoint(new Uri($"queue:{processorId:D}"));
await endpoint.Send(msg, ct);
// D-05: count AFTER confirmed delivery; tag ProcessorId ("D" — matches the queue name).
metrics.DispatchSent.Add(1, new KeyValuePair<string, object?>("ProcessorId", processorId.ToString("D")));
```
> Single owner covers BOTH the `WorkflowFireJob` entry fire and the `ResultConsumer` continuation (D-05).

---

### `src/Orchestrator/Consumers/ResultConsumer.cs` (MODIFY — increment at top of Consume, D-06)

**Analog: ITSELF.** Inject `OrchestratorMetrics`; increment at the **top** of `Consume` (D-04: CONSUMED
= count-at-entry), keyed by `m.ProcessorId` — counting EVERY consumed result (both the L1-hit advance
and the graceful L1-miss ack).

**Current ctor** (`ResultConsumer.cs:37-41`) + top of Consume (`:43-46`):
```csharp
public sealed class ResultConsumer(
    IWorkflowL1Store store,
    StepAdvancement advancement,
    IStepDispatcher dispatcher,
    ILogger<ResultConsumer> logger) : IConsumer<ExecutionResult>
{
    public async Task Consume(ConsumeContext<ExecutionResult> context)
    {
        var m = context.Message;
        // L1-only read (D-08): TryGet then the step map ...
```

**Modified** (add ctor param + increment BEFORE the L1 read so the L1-miss ack is also counted):
```csharp
public sealed class ResultConsumer(
    IWorkflowL1Store store,
    StepAdvancement advancement,
    IStepDispatcher dispatcher,
    OrchestratorMetrics metrics,
    ILogger<ResultConsumer> logger) : IConsumer<ExecutionResult>
{
    public async Task Consume(ConsumeContext<ExecutionResult> context)
    {
        var m = context.Message;
        // D-06: count EVERY consumed result at entry (before the L1 read — covers hit AND graceful miss).
        metrics.ResultConsumed.Add(1, new KeyValuePair<string, object?>("ProcessorId", m.ProcessorId.ToString("D")));
        // L1-only read (D-08): ...
```
> `ExecutionResult.ProcessorId` is a non-nullable `Guid` (`src/Messaging.Contracts/ExecutionResult.cs:10`)
> — no null-guard needed here (contrast the processor-side `Guid?` in Landmine 2).

---

### `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` (MODIFY — D-07 + D-08)

**Analog: ITSELF.** Two changes: (1) increment `processor_dispatch_consumed` at the **top** of `Consume`
(D-07); (2) route BOTH send paths through one private `SendResult(ExecutionResult)` that increments
`processor_result_sent{outcome}` after `Send` (D-08).

> **LANDMINE 2 — `context.Id` is `Guid?`** (`IProcessorContext.cs:36` — null until identity resolves).
> The consume path only runs post-`MarkHealthy`, so identity IS resolved; tag with
> `context.Id!.Value.ToString("D")` (the bang is justified — the runtime binds `queue:{id:D}` only after
> Healthy) and document it so reviewers don't flag a NRE. Use the SAME `"D"` format as the dispatch queue.

**Inject the holder** (`EntryStepDispatchConsumer.cs:40-46` — add `ProcessorMetrics metrics` param):
```csharp
public sealed class EntryStepDispatchConsumer(
    IConnectionMultiplexer redis,
    IProcessorContext context,
    BaseProcessor processor,
    IOptions<ProcessorLivenessOptions> options,
    ISendEndpointProvider sendProvider,
    ProcessorMetrics metrics,
    ILogger<EntryStepDispatchConsumer> logger) : IConsumer<EntryStepDispatch>
```

**D-07 — top of `Consume`** (`:48-51`, add the increment after `var dispatch = ctx.Message;`):
```csharp
public async Task Consume(ConsumeContext<EntryStepDispatch> ctx)
{
    var dispatch = ctx.Message;
    // D-07: count at entry. Immediate(3) retry re-runs consume → re-increments — accepted rate noise.
    metrics.DispatchConsumed.Add(1, new KeyValuePair<string, object?>("ProcessorId", context.Id!.Value.ToString("D")));
    var ct = ctx.CancellationToken;
    // ...
```

**D-08 — two existing send paths to unify.** The early `SendOne` (`:199-203`) and the final one-by-one
loop (`:183-190`) BOTH resolve `queue:{OrchestratorQueues.Result}` and `Send`. Refactor the early
`SendOne` to delegate to a new `SendResult`, and change the loop to call `SendResult` per item.

**Current early `SendOne`** (`:199-203`):
```csharp
private async Task SendOne(ExecutionResult result)
{
    var endpoint = await sendProvider.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"));
    await endpoint.Send(result, CancellationToken.None);
}
```

**Current final loop** (`:183-190`):
```csharp
var endpoint = await sendProvider.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"));
foreach (var er in built)
    await endpoint.Send(er, CancellationToken.None);
```

**New single owner `SendResult`** (increment AFTER Send — D-04; tag `outcome` + `ProcessorId`):
```csharp
// D-08: the single Send owner — every result (early Failed/Cancelled AND the per-result loop) goes
// through here so no send path is uncounted. Increment AFTER Send (confirmed delivery, D-04).
private async Task SendResult(ExecutionResult result)
{
    var endpoint = await sendProvider.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"));
    await endpoint.Send(result, CancellationToken.None);
    metrics.ResultSent.Add(1,
        new KeyValuePair<string, object?>("ProcessorId", context.Id!.Value.ToString("D")),
        // outcome ∈ {completed, failed, cancelled} — the build paths never emit Processing (Pitfall 3).
        new KeyValuePair<string, object?>("outcome", result.Outcome.ToString().ToLowerInvariant()));
}
```
Then: keep `SendOne` callers calling `SendResult` (rename all `await SendOne(...)` → `await SendResult(...)`,
or make `SendOne` a thin alias) AND change the final loop to `foreach (var er in built) await SendResult(er);`
(the loop no longer pre-resolves the endpoint — `SendResult` resolves it; acceptable, or hoist the
endpoint and pass it — planner's discretion, but ALL sends MUST increment).

> **`outcome` tag source** (D-08): `ExecutionResult.Outcome` is `StepOutcome`
> (`src/Messaging.Contracts/ExecutionResult.cs:10`). `StepOutcome` = `Processing=0, Completed=1,
> Failed=2, Cancelled=3` (`StepOutcome.cs:15-21`). The build paths (`BuildCompleted`/`BuildFailed`/
> `BuildCancelled`, `:210-234`) NEVER produce `Processing`, so `.ToString().ToLowerInvariant()` yields
> only {completed, failed, cancelled} by construction (Pitfall 3 — add a code comment, no filter needed).

---

### `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` (MODIFY — shared resource + instance-id, D-09/D-10)

**Analog: ITSELF + the sibling `BaseConsoleObservabilityExtensions.cs`** (identical two-`AddService`
structure). Unify the two separate `AddService` calls (logs `SetResourceBuilder` at `:53-54`; metrics
`ConfigureResource` at `:61-63`) onto ONE shared resource carrying `service.instance.id`.

**Current logs resource** (`:48-56`) and **metrics resource** (`:60-73`) BOTH call
`.AddService(serviceName, serviceVersion)` separately. Replace with a single resolved-once id applied to
both (D-10 correctness: resolve ONCE so the GUID fallback can't differ between logs and metrics):
```csharp
// D-09/D-10: resolve the per-replica instance id ONCE per process, apply to BOTH resources.
var instanceId = ResolveInstanceId();   // local — never call the resolver twice (anti-pattern)
var instanceAttrs = new[] { new KeyValuePair<string, object>("service.instance.id", instanceId) };

builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes           = true;
    o.ParseStateValues        = true;
    o.SetResourceBuilder(ResourceBuilder.CreateDefault()
        .AddService(serviceName: serviceName, serviceVersion: serviceVersion)
        .AddAttributes(instanceAttrs));        // NEW — D-10
    o.AddOtlpExporter();
});

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r
        .AddService(serviceName: serviceName, serviceVersion: serviceVersion)
        .AddAttributes(instanceAttrs))         // NEW — D-10
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()        // unchanged — METRIC-03; /health filtered at collector
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddOtlpExporter());
```

**`ResolveInstanceId()` helper — DUPLICATE per D-09** (a private/internal static on this class; `Messaging.Contracts`
is the wrong home and `BaseConsole.Core` can't ref `BaseApi.Core`):
```csharp
private static string ResolveInstanceId() =>
    Environment.GetEnvironmentVariable("POD_NAME")
    ?? Environment.GetEnvironmentVariable("HOSTNAME")
    ?? Environment.MachineName
    ?? Guid.NewGuid().ToString("N");   // MachineName is effectively non-null; GUID is the documented final fallback
```

---

### `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs` (MODIFY — shared resource + instance-id, D-09/D-10)

**Analog: ITSELF + the API sibling above.** SAME edit shape: unify the logs `SetResourceBuilder`
(`:48-49`) and metrics `ConfigureResource` (`:56-58`) onto one resolved-once `service.instance.id`, and
DUPLICATE the `ResolveInstanceId()` helper here (D-09 — independent copy). Leave the
`AddMeter(InstrumentationOptions.MeterName)` ("MassTransit", `:62`) and `AddRuntimeInstrumentation()`
exactly as-is.

> **Do NOT add `AddMeter("BaseProcessor")` here** (Landmine 1 — compile firewall). That belongs in
> `AddBaseProcessor`. This file's only metrics change is the shared-resource/instance-id unification.

---

### `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` (NEW — test helper)

**Analog:** `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs` (sibling). Copy the
class skeleton wholesale: `sealed ... : IDisposable`, `const InitialDelayMs = 200` / `MaxDelayMs = 3_200`,
long-lived `HttpClient` with a host `BaseAddress`, `Stopwatch`-based timeout loop, exponential backoff,
`HttpRequestException` swallow, and `JsonDocument.Parse(...).Clone()` detach-on-return.

**Key deltas from `ElasticsearchTestClient`:**
- `BaseAddress = new Uri("http://localhost:9090/")` (D-13 — the Prometheus **server**, NOT the
  collector's `:8889`; confirmed host-published at `compose.yaml:98`).
- Method `PollPromForQuery(string promQL, Func<JsonElement, bool> predicate, int timeoutMs, ...)` (D-12)
  doing `GET api/v1/query?query={Uri.EscapeDataString(promQL)}` (vs the ES `POST _search`).
- Success predicate: `root.status == "success" && predicate(root.data)`; return `data.Clone()`.

**Existing backoff/poll loop to copy verbatim** (`ElasticsearchTestClient.cs:67-108`):
```csharp
var sw    = Stopwatch.StartNew();
var delay = InitialDelayMs;
while (sw.ElapsedMilliseconds < timeoutMs)
{
    ct.ThrowIfCancellationRequested();
    try
    {
        // ... build request, send, on success parse + check predicate + return .Clone() ...
    }
    catch (HttpRequestException) { /* briefly unreachable — retry */ }

    var remaining = (int)(timeoutMs - sw.ElapsedMilliseconds);
    if (remaining <= 0) break;
    await Task.Delay(Math.Min(delay, remaining), ct);
    delay = Math.Min(delay * 2, MaxDelayMs);
}
return null;
```
> Prometheus `/api/v1/query` sample VALUES are quoted JSON strings (`"1"`, not `1`) — predicates parse
> via `.GetString()` + `double.TryParse`. See RESEARCH §Code Examples for `VectorNonEmpty` /
> `HasNumericValue` predicate helpers. Budget the timeout generously (≥120s — mirror the ES E2E's
> `EsPollTimeoutMs`; scrape-15s + OTLP-export interval, Pitfall 4).

---

### `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs` (NEW — RealStack E2E)

**Analog:** `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` (sibling). Reuse its
`RealStackWebAppFactory` (D-14 — promote to shared or duplicate minimally per Claude's discretion), the
`[Trait("Category", "RealStack")]` + `[Trait("Category","E2E")]` tagging (`:69-71`), the host-port env
overrides (`:380-388`), the seed→liveness-poll→Start→round-trip sequence, and the net-zero teardown
(`L2KeysToCleanup` / `ParentIndexMembersToSrem`, `:130-142`, `:427-444`).

**Sequence to copy** (`SampleRoundTripE2ETests.cs:87-137`): build factory → seed Processor/Step/Workflow
(`* * * * *` cron) → `PollForHealthyLivenessAsync` → snapshot → POST `/start` (assert 204) → register
teardown keys → `PollForNewExecutionDataKeyAsync` (proves the round-trip drove a real dispatch+result).

**Then ADD the Prometheus assertions** (replace the ES advance/scope clauses; capture `procId` from the
seed). Series-existence + bottleneck (RESEARCH §Code Examples):
```csharp
using var prom = new PrometheusTestClient();

var sent = await prom.PollPromForQuery(
    $"orchestrator_dispatch_sent_total{{ProcessorId=\"{procId:D}\"}}", VectorNonEmpty, PromPollTimeoutMs, ct);
Assert.NotNull(sent);   // METRIC-04

var consumed = await prom.PollPromForQuery(
    $"processor_dispatch_consumed_total{{ProcessorId=\"{procId:D}\"}}", VectorNonEmpty, PromPollTimeoutMs, ct);
Assert.NotNull(consumed);   // METRIC-05

var bottleneck = await prom.PollPromForQuery(
    $"sum by (ProcessorId)(rate(orchestrator_dispatch_sent_total{{ProcessorId=\"{procId:D}\"}}[5m])) " +
    $"- sum by (ProcessorId)(rate(processor_dispatch_consumed_total{{ProcessorId=\"{procId:D}\"}}[5m]))",
    HasNumericValue, PromPollTimeoutMs, ct);
Assert.NotNull(bottleneck);   // METRIC-06
```
> The `service_instance_id`-present + no-`workflowId` clauses (METRIC-04/05 acceptance) are asserted by
> inspecting `result[0].metric` keys on the returned (`.Clone()`d) `JsonElement`. The factory does NOT
> override port 9090 — the test client connects directly (Pitfall 6). Precondition: full `docker compose up`.

## Shared Patterns

### IMeterFactory DI-singleton holder (D-01)
**Source pattern:** `src/BaseProcessor.Core/Observability/ProcessorIdLogEnricher.cs` (sealed DI singleton
in a `*.Observability` namespace) + RESEARCH Pattern 1 (`IMeterFactory.Create` + `CreateCounter<long>`).
**Apply to:** both NEW holders (`OrchestratorMetrics`, `ProcessorMetrics`). `IMeterFactory` is
auto-registered by the .NET 8 generic host — inject it; never use a `static Meter` field (cross-test
leak in the shared hermetic process).

### `ConfigureOpenTelemetry*Provider` additive seam (Landmine 1 fix)
**Source:** `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs:101-103`
(`ConfigureOpenTelemetryLoggerProvider` — Phase 29 log enricher).
**Apply to:** processor meter registration (in `AddBaseProcessor`) and orchestrator meter registration
(in `Orchestrator/Program.cs`) — use the meter variant `ConfigureOpenTelemetryMeterProvider`. NEVER put
`AddMeter("BaseProcessor")` in `BaseConsoleObservabilityExtensions`.

### Resolve-once instance id + shared resource (D-09/D-10)
**Source:** the two sibling observability extensions
(`ObservabilityServiceCollectionExtensions.cs:53-63`, `BaseConsoleObservabilityExtensions.cs:48-58`).
**Apply to:** both base libs — DUPLICATE `ResolveInstanceId()` in each (D-09), resolve into ONE local,
`.AddAttributes([new("service.instance.id", id)])` on BOTH the logs and metrics resources.

### Increment timing symmetry (D-04)
**Apply to:** all four counters — SENT = `.Add(1, ...)` AFTER `endpoint.Send` returns
(`StepDispatcher`, `EntryStepDispatchConsumer.SendResult`); CONSUMED = `.Add(1, ...)` at the TOP of
`Consume` (`ResultConsumer`, `EntryStepDispatchConsumer`).

### `ProcessorId` tag-key + `"D"` format
**Apply to:** all four counters. Emit the tag key as literal `"ProcessorId"` (PascalCase — case
preserved by the collector exporter, so `sum by (ProcessorId)` works) and the value as `Guid.ToString("D")`
(matches the `queue:{processorId:D}` naming). NO `workflowId` tag (cardinality constraint, D-03/SPEC).

### Poll-with-backoff HTTP test client (D-11)
**Source:** `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs:30-110`.
**Apply to:** `PrometheusTestClient` — same `IDisposable` + `Stopwatch` + 200ms→3.2s backoff +
`HttpRequestException` swallow + `.Clone()` detach.

### RealStack E2E harness (D-14)
**Source:** `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` (`RealStackWebAppFactory`,
`[Trait("Category","RealStack")]`, host-port env overrides, net-zero teardown).
**Apply to:** `MetricsRoundTripE2ETests` — reuse the factory + seed→liveness→Start→round-trip, then
query Prometheus instead of (or in addition to) ES.

## No Analog Found

None. Every file in scope has a strong in-repo analog (the two NEW holders map to the
`ProcessorIdLogEnricher` DI-singleton shape + RESEARCH Pattern 1; the two NEW tests map to their direct
siblings; all MODIFY files are edits to themselves with the Phase-29 seam as precedent).

## Metadata

**Analog search scope:** `src/Orchestrator`, `src/BaseProcessor.Core`, `src/BaseApi.Core`,
`src/BaseConsole.Core`, `src/Messaging.Contracts`, `tests/BaseApi.Tests/Observability/Helpers`,
`tests/BaseApi.Tests/Orchestrator`, `compose.yaml`.
**Files read (analogs):** 11 (StepDispatcher, EntryStepDispatchConsumer, ResultConsumer, both
observability extensions, BaseProcessorServiceCollectionExtensions, ProcessorIdLogEnricher,
IProcessorContext, StepOutcome, ExecutionResult, Orchestrator/Program.cs, ElasticsearchTestClient,
SampleRoundTripE2ETests).
**Pattern extraction date:** 2026-06-02
