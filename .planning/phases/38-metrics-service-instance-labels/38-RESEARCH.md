# Phase 38: Uniform `service_name` + Instance Labels Across All Metrics — Research

**Researched:** 2026-06-06
**Domain:** OpenTelemetry .NET 1.15.3 metrics resource labeling + a runtime MeterProvider swap seam
**Confidence:** HIGH (codebase verified line-by-line; OTel listener/lifecycle model verified against official docs + GitHub issues)

---

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01** — Combine `{name}_{version}` **SDK-side in C#**: set the **metrics** resource `service.name = $"{name}_{version}"` directly. Applies to all four services. Collector stays the dumb `resource_to_telemetry_conversion: true` promoter — **no collector `transform`/`metricstransform` processor added**.
- **D-02** — Processor dynamic `service_name` uses the **MeterProvider swap** (GA-2 #1): build an initial provider with the placeholder resource; when DB identity resolves in Loop A, **dispose that provider and build a new one** with `service.name = {db.Name}_{db.Version}`. The `Meter` objects (BaseProcessor / MassTransit / runtime) are NOT recreated — provider #2 re-subscribes by meter name. Placeholder series and resolved series are genuinely distinct Prom series; the old one goes stale (expected).
- **D-03** — The swap seam is **deferred to this research** (pin the cleanest owner/lifecycle at OTel .NET 1.15.3). Rejected alternative GA-2 #2 (export-time resource-rewrite wrapper) — do not re-litigate.
- **D-04** — **Retain** `src/Processor.Sample/appsettings.json` `Service:Name`/`Service:Version` as the boot-window placeholder.
- **D-05** — Before DB identity resolves, processor metric `service_name` = appsettings `{name}_{version}` (`processor-sample_3.5.0`) — **not** a `processor-pending` sentinel.
- **D-06** — Shared `AddBaseConsoleObservability` path (`cfg.Require("Service:Name")`) is used **unchanged** to build the initial placeholder resource; processor-specific behavior is *only* the later swap. **No overload, no separate method.**
- **D-07** — **Keep** the standalone `service.version` resource attr (→ `service_version` Prom label) on metrics alongside the combined `service_name`.
- **D-08** — Update all in-repo PromQL consumers with the **exact literal** combined value (`service_name="sk-api_3.2.0"`), NOT a regex match.

### Claude's Discretion
- Exact owner type / DI shape of the swap holder, the build/dispose ordering, and the call-site mechanics in Loop A (this research recommends a concrete design below).
- Whether `MLBL-02` needs any new wiring vs. pure verification (research finds: pure verification — instance id is already at resource level).

### Deferred Ideas (OUT OF SCOPE)
- GA-2 #2 export-time resource-rewrite exporter wrapper (fallback only if the swap proves too invasive).
- Collector-side `{name}_{version}` transform (rejected D-01).
- New Keeper instruments + their labeling → **Phase 39**.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| MLBL-01 | Combined `service_name={name}_{version}` on all metric series (4 consoles) | Single SDK-side edit point per base lib; see "MLBL-01 combine site" below — touch the **metrics** `ConfigureResource` only |
| MLBL-02 | Non-empty `service_instance_id` on runtime + HTTP + business families | Already at resource level (Phase 30) — verification only; harness exists (`PrometheusTestClient.VectorNonEmpty` + label inspection) |
| MLBL-03 | Processor name+version from DB (single source of truth) + boot placeholder + swap | Contract/responder/context extension (mechanical) + the MeterProvider swap (the one hard mechanism — full design below) |
| MLBL-04 | Logs `service.name` stays bare (metrics-only change) | Logs resource is a **separate `SetResourceBuilder`** block — combine cannot leak if confined to the metrics `ConfigureResource`. Processor logs are already bare (appsettings `Name`); DB `Name` is bare by construction |
| MLBL-05 | All in-repo PromQL consumers reconciled + verified | Exactly **2** code files with literals (inventory below); no rules/dashboards; per-console versions reported |
</phase_requirements>

---

## Summary

This phase is two unequal halves. **The mechanical half** (MLBL-01/02/04/05 + the contract/context plumbing of MLBL-03) is a small, well-bounded set of edits to known files: change two `.AddService(...)` resource builds to set `service.name = $"{name}_{version}"`, extend a record + a responder + a context with `Name`/`Version`, and update exactly **two** test files carrying bare `service_name="..."` literals. **The hard half** is the processor MeterProvider swap (D-02/D-03).

The swap is genuinely non-trivial because of how OTel .NET 1.15.3 owns the MeterProvider: `services.AddOpenTelemetry().WithMetrics(...)` registers the `MeterProvider` as a **DI singleton** whose `Build()` is forced by `TelemetryHostedService.StartAsync`, and whose `Dispose()` is owned by the DI container at host shutdown. The metrics Resource is frozen at `Build()`. To apply a DB-sourced resource that only becomes known async in `ProcessorStartupOrchestrator` Loop A, you must own a provider **outside** that managed singleton lifecycle. The verified-good design is a singleton `MeterProviderHolder` that builds provider #1 (placeholder) via `Sdk.CreateMeterProviderBuilder()`, and on `SetIdentity` builds provider #2 (DB-sourced) and disposes #1 — **without** the host also disposing it (avoid double-dispose), without leaking the OTLP gRPC channel (Dispose flushes + shuts the reader/exporter), and without writing to a disposed provider (sequence the swap *before* `MarkHealthy`).

Crucially, the OTel listener model makes D-02 sound: **meters subscribe to providers by NAME, not by instance.** A `Meter` created via `IMeterFactory` at DI time is picked up by *any* `MeterProvider` whose builder called `.AddMeter("<that name>")`, regardless of whether the provider was built before or after the meter. So provider #2 re-subscribes to the existing `BaseProcessor` / `MassTransit` / runtime meters with no meter recreation — exactly as D-02 assumes. `[VERIFIED: opentelemetry-dotnet docs/metrics/customizing-the-sdk]`

**Primary recommendation:** Implement a singleton `MeterProviderHolder` in `BaseProcessor.Core` that owns a `Sdk.CreateMeterProviderBuilder()`-built provider (NOT host-registered for the processor's metrics), seeded with the placeholder resource at construction and swapped to the DB resource synchronously inside Loop A immediately after `context.SetIdentity(found.Message)` and **before** the queue-bind / `MarkHealthy`. Dispose order: build #2 first, repoint `_current`, then `ForceFlush()` + `Dispose()` #1.

---

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| `{name}_{version}` combine for the 3 singletons | Backend / shared base libs (`BaseApi.Core`, `BaseConsole.Core`) | — | One edit point per lib (the metrics `ConfigureResource`); applies uniformly via `cfg.Require` |
| Processor DB-sourced `service_name` | Processor startup brain (`ProcessorStartupOrchestrator` Loop A) + new `MeterProviderHolder` | DB / API (identity round-trip) | DB is single source of truth; resolves async; needs a swappable provider owned outside the host lifecycle |
| Resource→Prom label promotion | Collector (`otel-collector-config.yaml`) | — | `resource_to_telemetry_conversion: true` — **unchanged** (D-01) |
| Logs `service.name` (stays bare) | Logs resource (`builder.Logging.AddOpenTelemetry` block) | — | Separate `SetResourceBuilder`; combine must not reach here (MLBL-04) |
| PromQL consumer reconciliation | Tests (`tests/BaseApi.Tests/Observability/*`) | — | Only test assertions consume `service_name`; no rules/dashboards |

---

## Standard Stack

All pins are **already present** in `Directory.Packages.props` (CPM). No new packages. `[VERIFIED: Directory.Packages.props]`

### Core
| Library | Version | Purpose | Notes |
|---------|---------|---------|-------|
| OpenTelemetry | 1.15.3 | SDK core — `Sdk.CreateMeterProviderBuilder()`, `MeterProviderBuilder`, `ResourceBuilder`, `MeterProvider.ForceFlush/Dispose` | The swap lives here |
| OpenTelemetry.Extensions.Hosting | 1.15.3 | `AddOpenTelemetry()`, `ConfigureResource`, `ConfigureOpenTelemetryMeterProvider`, `TelemetryHostedService` | The host-managed singleton path the swap must step around for the processor |
| OpenTelemetry.Exporter.OpenTelemetryProtocol | 1.15.3 | `AddOtlpExporter()` (gRPC → `http://otel-collector:4317`) | Default reader = `PeriodicExportingMetricReader`, 60s interval `[VERIFIED: opentelemetry.io exporters docs]` |
| OpenTelemetry.Instrumentation.Runtime | 1.15.0 | `AddRuntimeInstrumentation()` (process.runtime.dotnet.* runtime family) | Instrumentation versions on own 1.15.0 cadence |
| `System.Diagnostics.Metrics.IMeterFactory` | .NET 8 BCL | DI-blessed `Meter` creation (`ProcessorMetrics`, `OrchestratorMetrics`) | Meters live in DI; subscribed by NAME — survives the swap |

**No `AddPrometheusExporter` anywhere in .NET** — confirmed; the collector's prometheus exporter does all resource→label promotion. `[VERIFIED: grep — only AddOtlpExporter in src/]`

**Version note:** `Service:Version` is strict SemVer (FluentValidation VALID-06), so `{name}_{version}` stays low-cardinality.

---

## The Swap Seam (D-03) — DECISIVE RECOMMENDATION

### Verified OTel .NET 1.15.3 facts the design rests on

1. **Meter subscription is by NAME, not by `Meter` instance.** "AddMeter can be called multiple times… supports wildcard subscription… The OpenTelemetry SDK creates a `MeterListener` that subscribes to your `Meter` instances." A provider built later still picks up meters created earlier (and vice versa) as long as it `.AddMeter("<name>")`. → Provider #2 re-subscribes to the existing `BaseProcessor`, `MassTransit`, and runtime meters with **zero meter recreation**. `[CITED: github.com/open-telemetry/opentelemetry-dotnet docs/metrics/customizing-the-sdk/README.md]`
2. **A provider's config is frozen at `Build()`** — "It is not possible to add meters once the provider is built." This is exactly why a *mutate* is impossible and a *swap* is required. `[CITED: same]`
3. **`Sdk.CreateMeterProviderBuilder()`** returns a standalone `MeterProviderBuilder` whose `Build()` yields a `MeterProvider` **not** owned by the host/DI container. `[CITED: opentelemetry.io metrics/sdk docs]`
4. **`MeterProvider.Dispose()` flushes + shuts the pipeline** — the `MetricReader` responds to Shutdown, flushing remaining metrics and releasing the OTLP exporter (gRPC channel). `ForceFlush()` is available to push the in-flight 60s-window batch *before* Dispose. → No exporter leak if the holder calls `ForceFlush()` then `Dispose()`. `[CITED: opentelemetry.io metrics/sdk + issue #2979]`
5. **`AddOpenTelemetry().WithMetrics(...)` registers the MeterProvider as a single DI singleton**; `TelemetryHostedService` (inserted at index 0) forces `Build()` at `StartAsync` and the DI container disposes the provider at shutdown. "Only a single TracerProvider and/or MeterProvider will be created for a given IServiceCollection." → If the processor *also* let the host build a metrics provider, you'd have two providers double-exporting. The design below makes the **holder the sole owner** of the processor's metrics provider. `[CITED: github OpenTelemetry.Extensions.Hosting README + OpenTelemetryServicesExtensions.cs]`
6. **Two live providers on the same meter names = transient double-export** (each has its own reader/exporter; both receive measurements). Harmless for a sub-second swap window but cleaner to minimize — see ordering below. `[VERIFIED: issue #4636 — multiple providers coexist, each witnesses measurements]`

### LANDMINE the brief got slightly wrong — there is NO host-built metrics provider to fight on the processor *iff* you choose ownership cleanly

The brief frames the problem as "the host normally owns the MeterProvider as a managed singleton; take ownership so the host doesn't double-dispose." There are **two viable ownership models**; pick **Model A** (cleaner, recommended):

#### Model A (RECOMMENDED): Holder is the *sole* owner of the processor's metrics provider; the shared `WithMetrics(...)` block does NOT run on the processor's metrics path

- **Problem:** D-06 says the shared `AddBaseConsoleObservability` path is used **unchanged**, and that path calls `builder.Services.AddOpenTelemetry().ConfigureResource(...).WithMetrics(...)` — which *would* register a host-owned metrics provider. If the holder also builds one, both export → duplicate series during the entire process lifetime, not just the swap window.
- **Resolution:** The shared path is used **unchanged for the LOGS resource and for the singletons' metrics**. For the processor, the holder must be the only metrics provider. Two equally-clean ways to honor "unchanged shared path" while avoiding a double provider:
  - **A1 (least surprising):** Keep the shared `WithMetrics(...)` call (placeholder resource) as the processor's **provider #1** — i.e. the host DOES build provider #1 with the placeholder. The holder does NOT build #1; it only builds **#2** on resolve. At swap time the holder disposes the **host's** provider #1 and installs its own #2. **Risk:** disposing a DI-container-owned object out from under the container → the container will Dispose it *again* at shutdown. `MeterProvider.Dispose()` is idempotent in OTel .NET (second Dispose is a no-op), so double-dispose is *safe*, but it's implicit and fragile. **Not recommended.**
  - **A2 (recommended):** On the processor, **suppress the host metrics provider** and have the holder own BOTH #1 and #2. Concretely: after the shared `AddBaseConsoleObservability` runs, the processor composition root (`AddBaseProcessor`) removes the host's metrics-provider registration (mirror the existing `StartupCompletionService` removal idiom at `BaseProcessorServiceCollectionExtensions.cs:136-141`) **OR** — simpler and more surgical — the processor does NOT rely on the shared `WithMetrics` for export and instead the holder builds #1 at startup with the same placeholder resource + the same `.AddMeter(...)` set + `.AddOtlpExporter()`. The holder is registered as a singleton + an `IHostedService` whose `StartAsync` builds #1 and whose `Dispose` disposes `_current`. The logs block (shared) is untouched → MLBL-04 safe.

  Given D-06's "unchanged shared path, no overload, no separate method" constraint, the **lowest-friction** concrete shape is: leave `AddBaseConsoleObservability` exactly as-is (it builds the logs resource AND a host metrics provider with the placeholder resource — this is provider #1), and the holder owns ONLY **provider #2**, performing an explicit `ForceFlush + Dispose` on the host provider #1 at swap time, accepting the idempotent double-dispose at shutdown. This is **A1**, and it keeps the shared method literally unchanged. If the planner prefers zero double-dispose, choose A2 and add a one-line host-provider removal in `AddBaseProcessor`.

  **Recommendation for the planner:** choose **A1** if "shared path literally unchanged" is the hard constraint (double-dispose is provably safe — `MeterProvider.Dispose` is idempotent); choose **A2** if "no implicit reliance on Dispose-idempotency" is preferred (costs one removal line in `AddBaseProcessor`, the same idiom already used for `StartupCompletionService`). **Flag this as the one design fork for `/gsd-discuss-phase` or the planner to resolve** — both are correct; A1 minimizes diff, A2 minimizes implicit behavior.

### The holder type (concrete)

```csharp
// BaseProcessor.Core/Observability/MeterProviderHolder.cs  (NEW)
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace BaseProcessor.Core.Observability;

/// <summary>
/// MLBL-03 (D-02/D-03): owns the processor's metrics MeterProvider OUTSIDE the host's managed
/// singleton lifecycle so the DB-sourced service.name can be applied after async identity resolve.
/// Builds provider #2 with the resolved resource and disposes the prior provider. Meters are NOT
/// recreated — provider #2 re-subscribes to the SAME meter NAMES (BaseProcessor / MassTransit /
/// runtime). The single resolved service.instance.id (Phase 30) is captured ONCE and reused across
/// the swap so logs + metrics #1 + metrics #2 all carry the identical value.
/// </summary>
public sealed class MeterProviderHolder : IDisposable
{
    private readonly string _instanceId;          // captured ONCE (Phase 30 invariant preserved across swap)
    private readonly string _serviceVersion;      // appsettings Service:Version (kept as service.version attr, D-07)
    private MeterProvider _current;

    public MeterProviderHolder(/* inject: resolved instanceId, appsettings name+version, meter-name set */) { ... }

    private MeterProvider Build(string serviceName) =>
        Sdk.CreateMeterProviderBuilder()
           .ConfigureResource(r => r
               .AddService(serviceName: serviceName, serviceVersion: _serviceVersion)   // service.version kept (D-07)
               .AddAttributes(new[] {
                   new KeyValuePair<string, object>("service.instance.id", _instanceId) // SAME id across swap
               }))
           .AddMeter(ProcessorMetrics.MeterName)                 // "BaseProcessor"
           .AddMeter(MassTransit.Monitoring.InstrumentationOptions.MeterName)  // "MassTransit"
           .AddRuntimeInstrumentation()
           .AddOtlpExporter()
           .Build();

    /// <summary>Called from Loop A immediately after SetIdentity, BEFORE the queue-bind/MarkHealthy.</summary>
    public void SwapTo(string resolvedServiceName)
    {
        var next = Build(resolvedServiceName);   // (1) build #2 first — provider exists before we kill #1
        var prior = _current;
        _current = next;                          // (2) repoint BEFORE disposing prior
        prior.ForceFlush(milliseconds: 5000);     // (3) push the placeholder window's in-flight batch
        prior.Dispose();                          // (4) flush+shutdown reader, release OTLP gRPC channel
    }

    public void Dispose() => _current?.Dispose(); // host shutdown — single owner, no double-dispose (A2)
}
```

### The call-site in Loop A (exact)

In `ProcessorStartupOrchestrator.ExecuteAsync`, the swap fires **inside the `if (resp.Is(out ... found))` block, right after `context.SetIdentity(found!.Message)`** and before `break;` (or equivalently before the Loop-B / queue-bind / `MarkHealthy` sequence):

```csharp
if (resp.Is(out Response<ProcessorIdentityFound>? found))
{
    context.SetIdentity(found!.Message);
    meterProviderHolder.SwapTo($"{found.Message.Name}_{found.Message.Version}");  // ← swap HERE
    logger.LogInformation("Identity resolved for hash {Hash}: processor {ProcessorId}", hash, found.Message.Id);
    break;
}
```

Inject `MeterProviderHolder` into `ProcessorStartupOrchestrator`'s primary constructor (one new ctor param). `MeterProviderHolder` is `services.AddSingleton<MeterProviderHolder>()` in `AddBaseProcessor`.

### Why this ordering is race-safe (heartbeat / `MarkHealthy`)

- The swap completes **synchronously inside Loop A before `break`**, which is itself **before** Loop B, the queue-bind, and `context.MarkHealthy()`. `[VERIFIED: ProcessorStartupOrchestrator.cs:79-159]`
- The processor's **business counters** (`processor_dispatch_consumed`, `processor_result_sent`, `processor_dispatch_deduped`) only increment inside `EntryStepDispatchConsumer.Consume`, which can only run **after** the `{id:D}` queue is bound — which is after `await handle.Ready` — which is after the swap. So no business metric can be written to provider #1 after it's disposed. `[VERIFIED: ProcessorMetrics.cs + dispatch consumer is bound at Completion, post-swap]`
- **Correction to the brief:** the `ProcessorLivenessHeartbeat` writes **only to Redis**, NOT a metric (`ProcessorLivenessHeartbeat.cs` has no `Counter`/`Meter` usage). There is no "heartbeat business counter." The only metrics-write race is the dispatch path, which is post-bind/post-swap. The runtime + MassTransit meters DO emit during boot (against provider #1) — that's fine; they emit against `_current` (whichever is live), and the swap's build-#2-before-dispose-#1 ordering guarantees `_current` is always a live provider. `[VERIFIED: ProcessorLivenessHeartbeat.cs:61-122]`
- The only transient window: between `_current = next` and `prior.Dispose()`, **both** providers are momentarily alive → a runtime measurement in that ~ms window double-exports one data point. Harmless (the placeholder series goes stale anyway; cumulative-temporality dedup at Prom is by series identity, and the two series have different `service_name`).

### service.instance.id preservation across the swap (Phase 30 invariant)

The single-resolve invariant (resolved once per process; logs + metrics share it) must survive the swap. The current `ResolveInstanceId()` runs inside `AddBaseConsoleObservability` and is applied to BOTH the logs resource and the (placeholder) metrics resource. The holder's provider #2 must carry the **same** value. Two ways:
- **Preferred:** resolve the instance id once at composition time and pass it into the holder ctor (so logs, provider #1, provider #2 all read the identical string). This means surfacing the resolved id from the shared path to the holder — e.g. register the resolved id as a singleton string/record, or have the holder re-run the **same** `POD_NAME → HOSTNAME → MachineName → GUID` precedence. **CAUTION:** re-running the precedence risks the GUID fallback differing → use the *captured* value, never a second resolve. This is the exact Phase 30 D-10 hazard called out in the code comments.
- **DRIFT GUARD (IN-03):** if the holder re-implements the precedence, it becomes a **4th** copy of the `POD_NAME ?? HOSTNAME ?? MachineName ?? GUID` expression that must change in lock-step with the 3 existing copies (`ObservabilityServiceCollectionExtensions`, `BaseConsoleObservabilityExtensions`, `ResolveInstanceIdFacts`). **Strongly prefer capturing the already-resolved value** to avoid adding a 4th drift site.

---

## MLBL-01 combine site (the mechanical edit)

**Edit point — METRICS resource only, both base libs:**

`BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs:64-67` and
`BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs:69-72`:

Current (metrics):
```csharp
.ConfigureResource(r => r
    .AddService(serviceName: serviceName, serviceVersion: serviceVersion)   // <- bare name
    .AddAttributes(instanceAttrs))
```
Target:
```csharp
.ConfigureResource(r => r
    .AddService(serviceName: $"{serviceName}_{serviceVersion}", serviceVersion: serviceVersion)  // combined name; service.version kept (D-07)
    .AddAttributes(instanceAttrs))
```

**Confirmed:** `ResourceBuilder.AddService(serviceName, serviceVersion)` sets `service.name` AND `service.version`. Setting `service.name = "{name}_{version}"` while still passing `serviceVersion` yields both `service_name="sk-api_3.2.0"` and `service_version="3.2.0"` Prom labels (D-07 honored). `[VERIFIED: AddService signature in resources docs; existing code uses it]`

**MLBL-04 guarantee (logs untouched):** the **LOGS** resource is a *separate* `SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName, serviceVersion)...)` block (`...Extensions.cs:56-58` / `:61-63`). As long as the combine is applied ONLY in the `ConfigureResource` (metrics) block and NOT in the `SetResourceBuilder` (logs) block, logs `service.name` stays bare. The two blocks are textually distinct → no accidental leak if the planner edits only the metrics line. `[VERIFIED: both files, lines cited]`

**Processor logs already bare:** processor logs `service.name` = appsettings `Name` (`processor-sample`) today; that stays bare (no version, no DB swap applied to logs). The DB `Name` is bare by construction (it is the entity `Name` column, not `{Name}_{Version}`). MLBL-04's "any processor-name log query reconciled to the DB `Name`" is a no-op here because the *logs* path is never swapped — logs keep the appsettings bare `Name`. **No logs-side change required.** `[VERIFIED: BaseConsoleObservabilityExtensions.cs:51-60 — logs block reads serviceName unchanged]`

---

## MLBL-03 contract/context plumbing (mechanical)

| File | Change |
|------|--------|
| `src/Messaging.Contracts/ProcessorQueries.cs:7-8` | Extend `ProcessorIdentityFound(Guid Id, Guid? InputSchemaId, Guid? OutputSchemaId, Guid? ConfigSchemaId)` with `string Name, string Version` (positional record extension) |
| `src/BaseApi.Service/.../GetProcessorBySourceHashConsumer.cs:25-26` | Populate `new ProcessorIdentityFound(p.Id, ..., p.Name, p.Version)` (the read DTO already exposes `Name`/`Version` via `ProcessorReadDto`) |
| `src/BaseProcessor.Core/Identity/IProcessorContext.cs` | Add `string? Name { get; }` + `string? Version { get; }` (mirror the schema-Id properties; subject to the same WR-03 memory-visibility invariant — safe to read only after `IsHealthy`) |
| `src/BaseProcessor.Core/Identity/ProcessorContext.cs:57-63` | `SetIdentity` stores `Name = identity.Name; Version = identity.Version;` |
| `tests/BaseApi.Tests/Observability/ProcessorIdEnricherTests.cs:41-55` | `StubContext` implements `IProcessorContext` — **must add the new `Name`/`Version` members or it won't compile.** (Landmine: any other `IProcessorContext` test stub/fake also needs the two new members.) |

**Note (WR-03):** the swap reads `found.Message.Name`/`.Version` directly off the response message (not via the context properties), so the memory-visibility invariant is moot at the swap call-site — the values come straight from the just-received message. The context still stores them for any reader that needs them post-Healthy.

**Acceptance subtlety (D-05 placeholder vs resolved):** before resolve, metric `service_name = processor-sample_3.5.0` (appsettings, via provider #1). After resolve, `service_name = {seeded DB Name}_{seeded DB Version}`. The Phase-30 `MetricsRoundTripE2ETests` seeds the processor row via `SeedProcessorAsync` with `Name: $"sample-proc-{guid:N}"`, `Version: "1.0.0"` when it does NOT pre-exist — so a fresh seed yields `service_name="sample-proc-{guid}_1.0.0"`. The live container resolves the EXISTING row by embedded SourceHash (GET-or-create), so the asserted value depends on what's already seeded. **Planner action:** the MLBL-03 (ii) assertion must read the seeded row's actual `{Name}_{Version}` (not hardcode), OR seed with a known fixed Name/Version and assert that literal. `[VERIFIED: MetricsRoundTripE2ETests.cs:319-343]`

---

## MLBL-05 — PromQL consumer inventory + per-console versions

### Live `Service:Version` per console (for D-08 exact literals)
| Console | appsettings `Service:Name` | `Service:Version` | Combined `service_name` literal |
|---------|---------------------------|-------------------|----------------------------------|
| sk-api | `sk-api` | `3.2.0` | `sk-api_3.2.0` |
| orchestrator | `orchestrator` | `3.4.0` | `orchestrator_3.4.0` |
| keeper | `keeper` | `3.7.0` | `keeper_3.7.0` |
| processor-sample (boot placeholder) | `processor-sample` | `3.5.0` | `processor-sample_3.5.0` |
| processor-sample (steady state) | from DB row | from DB row | `{db.Name}_{db.Version}` (dynamic) |

`[VERIFIED: src/{BaseApi.Service,Orchestrator,Keeper,Processor.Sample}/appsettings.json — lines 10-11 each]`

### In-repo PromQL consumers with bare `service_name="..."` literals (must update — D-08)
| File | Lines | Current literal | New literal |
|------|-------|-----------------|-------------|
| `tests/BaseApi.Tests/Observability/MetricsExportTests.cs` | 49, 97, 99 | `service_name="sk-api"` (3 occurrences) | `service_name="sk-api_3.2.0"` |
| `tests/BaseApi.Tests/Observability/SchemasMetricsE2ETests.cs` | 102 (+ comment 16, 85, 111) | `service_name="sk-api"` | `service_name="sk-api_3.2.0"` |

`[VERIFIED: grep service_name= over **/*.cs — only these two files carry the label in a query]`

### Other consumers — verified state
- `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` — **doc-comment only** (lines 26-29 narrative "service.name → service_name"). No literal query. Reconcile the narrative comment to mention `{name}_{version}` (cosmetic; no behavior). `[VERIFIED]`
- `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs` — queries business counters by `ProcessorId`, NOT by `service_name`; asserts `service_instance_id` presence on a runtime series. **Does NOT filter on `service_name`** → unaffected by D-01, but is the natural home to ADD an MLBL-01/02 `service_name="..._..."` assertion across families. `[VERIFIED: MetricsRoundTripE2ETests.cs:116-164]`
- `prometheus.yml` — **no recording rules, no alert rules** (`evaluation_interval` set but zero rules; comment line 9 confirms "no rules defined"). `[VERIFIED: prometheus.yml:1-31]`
- **No committed Grafana dashboards** — no `*.rules.yml`, no dashboard JSON found. `[VERIFIED: glob **/*.rules.yml → none; CONTEXT confirms]`

**MLBL-05 conclusion:** the only updateable query sites are the 2 test files above (4 literal occurrences total). The phase summary must record "no rules/dashboards; test assertions are the sole consumers."

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Re-tagging metrics with a new `service.name` after startup | A custom `MetricProcessor` that rewrites resource attrs at export time (the deferred GA-2 #2) | The MeterProvider swap (D-02) — `Sdk.CreateMeterProviderBuilder()` + Dispose | User chose the swap; the wrapper is the documented fallback only |
| Flushing the OTLP channel before disposing provider #1 | A manual gRPC channel close / sleep | `MeterProvider.ForceFlush()` then `Dispose()` | Dispose already drives reader Shutdown → exporter flush; ForceFlush handles the 60s window |
| Re-subscribing meters to provider #2 | Recreating `Meter`/`Counter` objects | Just `.AddMeter("<same name>")` on builder #2 | Subscription is by NAME; existing meters are auto-picked-up |
| Resolving `service.instance.id` a second time for provider #2 | A 4th copy of the env-precedence expression | Capture the once-resolved value and reuse | Phase 30 D-10 single-resolve invariant; avoids GUID-fallback divergence + a 4th IN-03 drift site |
| URL-encoding PromQL in tests | Manual escaping | `Uri.EscapeDataString` (already in `PrometheusTestClient`) | PromQL `{ } " =` break unencoded URLs (existing pattern) |

**Key insight:** the swap's correctness comes entirely from the OTel listener-by-name model + Dispose-flushes-pipeline guarantee — both are SDK contracts, not things to reimplement.

---

## Runtime State Inventory (rename/identity-change phase)

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | Processor identity lives in Postgres (`ProcessorEntity.Name`/`.Version`, validated non-empty). **No metric label value is persisted** — Prom series are append-only telemetry, not stored state. The placeholder series `service_name="processor-sample_3.5.0"` will exist in Prom's TSDB until retention ages it out (expected stale series, per D-02). | None — stale Prom series are by-design; no migration. The DB already holds Name/Version (no schema change; no migration) |
| Live service config | Collector `resource_to_telemetry_conversion: true` is the only live knob and is **unchanged** (D-01). No collector workflow/UI state. | None |
| OS-registered state | None — no OS-registered names embed `service_name`. | None — verified (metrics are runtime-only) |
| Secrets/env vars | `OTEL_EXPORTER_OTLP_ENDPOINT` is the live OTLP knob (per MEMORY: appsettings `OpenTelemetry:Endpoint` key is dead). The swap's provider #2 `.AddOtlpExporter()` reads the SAME env-configured endpoint as #1 — **confirm provider #2 inherits the OTLP endpoint env var** (it does, since `AddOtlpExporter()` reads `OTEL_EXPORTER_OTLP_ENDPOINT` by default). | Verify provider #2's `AddOtlpExporter()` picks up the env endpoint (no explicit endpoint passed in shared path → relies on env default) |
| Build artifacts | The processor's embedded `SourceHash` assembly attribute is unaffected (identity-by-hash unchanged). | None |

**Critical env-var note for the planner:** the shared `AddBaseConsoleObservability` calls bare `.AddOtlpExporter()` (no endpoint arg) → it relies on `OTEL_EXPORTER_OTLP_ENDPOINT`. The holder's `Build()` must do the **same bare `.AddOtlpExporter()`** so provider #2 hits the identical collector. Do NOT hardcode the endpoint in the holder. `[VERIFIED: BaseConsoleObservabilityExtensions.cs:73 bare AddOtlpExporter; MEMORY OTLP gotcha]`

---

## Common Pitfalls

### Pitfall 1: Double provider → duplicate series for the WHOLE process life
**What goes wrong:** if the host builds a metrics provider (via the unchanged shared `WithMetrics`) AND the holder builds one too, both export every measurement → every processor series doubles.
**How to avoid:** the holder must be the **sole** owner of the processor metrics provider (Model A2), OR reuse the host's provider as #1 and only build #2 in the holder (Model A1, accepting idempotent double-dispose). Resolve this fork before planning tasks.
**Warning sign:** `processor_dispatch_consumed_total` shows 2× the expected count, or two series differing only by an internal label.

### Pitfall 2: Writing to a disposed provider (the heartbeat race — actually the dispatch race)
**What goes wrong:** disposing provider #1 while a meter still emits to it → measurements dropped or an ObjectDisposedException path.
**How to avoid:** build #2 and repoint `_current` BEFORE disposing #1; sequence the whole swap before the queue-bind/`MarkHealthy` so the dispatch counters (the only post-Healthy writers) never target #1. The runtime/MassTransit meters always write to `_current`, which is never disposed while current.
**Warning sign:** a brief gap in runtime series at the swap instant, or a logged ObjectDisposed.

### Pitfall 3: OTLP gRPC channel leak on swap
**What goes wrong:** abandoning provider #1 without Dispose leaks its `PeriodicExportingMetricReader` + gRPC channel; over many swaps (tests) this leaks sockets.
**How to avoid:** `ForceFlush()` then `Dispose()` provider #1 in `SwapTo`. There is exactly ONE swap per process, so leakage is bounded, but tests that build/swap repeatedly must dispose.
**Warning sign:** growing channel/socket count in a hermetic test that exercises the holder.

### Pitfall 4: `service.instance.id` divergence across the swap
**What goes wrong:** provider #2 resolves a *new* instance id (esp. the GUID fallback) → metrics #1 and #2 (and logs) carry different `service_instance_id`.
**How to avoid:** capture the once-resolved id and pass it to the holder; never re-resolve. (Phase 30 D-10.)
**Warning sign:** `MetricsRoundTripE2ETests` instance-id assertion passes but two series for the same process show different `service_instance_id`.

### Pitfall 5: Combine leaks into logs (MLBL-04 violation)
**What goes wrong:** editing the wrong `AddService` (the logs `SetResourceBuilder` block) adds `_{version}` to logs `service.name` → breaks the Phase-35 ES `service.name="keeper"` queries.
**How to avoid:** edit ONLY the metrics `ConfigureResource` block (lines cited). Add a hermetic assertion that the logs resource `service.name` has no `_` suffix.
**Warning sign:** Phase-35 SC3 ES assertion `service.name="keeper"` returns empty.

### Pitfall 6: Forgotten `IProcessorContext` stub recompile
**What goes wrong:** adding `Name`/`Version` to `IProcessorContext` breaks every test fake (`StubContext` in `ProcessorIdEnricherTests`) that implements the interface → compile failure.
**How to avoid:** grep for `: IProcessorContext` and update all implementers.
**Warning sign:** Debug/Release build error CS0535 (missing interface member).

### Pitfall 7: Prometheus pull-latency in the new family assertions
**What goes wrong:** querying Prom immediately after the swap returns the placeholder series (not yet aged) and/or no resolved series yet (SDK 60s export interval + 15s scrape).
**How to avoid:** reuse the existing `PollPromForQuery` / `PollPrometheusUntilSumAtLeast` budgets (90-120s) from the Phase-30 harness. Assert the resolved `service_name` PRESENCE, don't assert the placeholder's ABSENCE within the same short window.
**Warning sign:** flaky "resolved series not found" in the RealStack E2E.

---

## Validation Architecture

> nyquist_validation is enabled (no `workflow.nyquist_validation:false` in config). This section maps each MLBL requirement to an observable signal + sampling rate.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit v3 3.2.2 (`xunit.v3`, `xunit.v3.assert`) `[VERIFIED: Directory.Packages.props:121-123]` |
| Config | per-project; `[Collection("Observability")]` serializes env-mutating tests |
| Hermetic run | `dotnet test --filter-not-trait "Category=RealStack"` (per MEMORY: MTP filter is `-- --filter-class` / trait-based) |
| RealStack run | full compose stack up; `[Trait("Category","RealStack")]` opt-in |
| Prom query client | `PrometheusTestClient` → `http://localhost:9090/api/v1/query` (poll w/ backoff) |

### Observable signals per requirement
| Req | Observable signal | Test type | Where |
|-----|-------------------|-----------|-------|
| MLBL-01 | `service_name="{name}_{version}"` present on a runtime, an HTTP, and a business series; NO bare-name series for the 4 services | RealStack scrape assertion | extend `MetricsRoundTripE2ETests` + `MetricsExportTests` (HTTP family, sk-api) |
| MLBL-02 | non-empty `service_instance_id` on runtime + HTTP + business series | RealStack scrape assertion | `MetricsRoundTripE2ETests` already asserts runtime; ADD HTTP + business family checks |
| MLBL-03 (i) | `ProcessorIdentityFound` has Name/Version; responder populates | hermetic unit | new contract/responder test |
| MLBL-03 (ii) | live processor business series `service_name = {db.Name}_{db.Version}` | RealStack | `MetricsRoundTripE2ETests` — assert resolved series carries the seeded `{Name}_{Version}` |
| MLBL-03 (iii) | appsettings keys retained | static / file assertion | grep appsettings (or a config-load test) |
| MLBL-03 (iv) | pre-resolve series `service_name = processor-sample_3.5.0` | RealStack (timing-sensitive — boot window) OR hermetic holder test | a hermetic test of `MeterProviderHolder` (build #1 → resource has placeholder name) is the robust signal; the live boot-window series is racy to catch |
| MLBL-04 | logs resource `service.name` has no `_` suffix; Phase-35 ES `service.name="keeper"` still passes | hermetic (resource attr) + RealStack ES | add a hermetic logs-resource assertion; rerun Phase-35 SC3 |
| MLBL-05 | zero bare `service_name="<name>"` literals remain for the 4 services; updated Phase-11 assertion passes | static grep + RealStack | grep gate + `MetricsExportTests`/`SchemasMetricsE2ETests` green |

### Hermetic signal for the swap (most valuable, race-free)
A hermetic `MeterProviderHolderFacts` test: construct the holder with a known instance id + placeholder name; assert provider #1's resource carries `service.name="processor-sample_3.5.0"` and the captured `service.instance.id`; call `SwapTo("db-name_9.9.9")`; assert the new provider's resource carries `service.name="db-name_9.9.9"` and the SAME `service.instance.id`; assert `Dispose` on #1 was called (no leak). This proves MLBL-03 (iv)+(ii) mechanics + Pitfall 4 (instance-id preservation) without the live boot-window race. **This is the single highest-value new test.**

### Sampling rate
- **Per task commit:** hermetic suite (`--filter-not-trait Category=RealStack`) — fast, covers the holder + contract + logs-resource assertions.
- **Per wave merge:** full hermetic suite green.
- **Phase gate:** RealStack `MetricsRoundTripE2ETests` (resolved `service_name` + instance-id across families) + Phase-35 SC3 ES (logs bare) + the static grep gate, all green before `/gsd-verify-work`.

### Wave 0 gaps
- [ ] `tests/.../MeterProviderHolderFacts.cs` — hermetic swap proof (NEW; highest value)
- [ ] Contract/responder test for `ProcessorIdentityFound` Name/Version population (NEW)
- [ ] Update `StubContext` + any `IProcessorContext` fake for the 2 new members (else compile break)
- [ ] Extend `MetricsRoundTripE2ETests` with `service_name="{name}_{version}"` assertions across families
- [ ] Logs-resource bare-`service.name` hermetic assertion (MLBL-04)

---

## Code Examples

### Combined service.name on the metrics resource (the MLBL-01 edit)
```csharp
// Source: existing BaseConsoleObservabilityExtensions.cs:64-67 (metrics block) — modify in place
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r
        .AddService(serviceName: $"{serviceName}_{serviceVersion}", serviceVersion: serviceVersion)
        .AddAttributes(instanceAttrs))   // service.instance.id preserved (Phase 30)
    .WithMetrics(m => m
        .AddMeter(InstrumentationOptions.MeterName)   // "MassTransit"
        .AddRuntimeInstrumentation()
        .AddOtlpExporter());
// LOGS block above (SetResourceBuilder) stays bare — DO NOT touch (MLBL-04)
```

### Standalone swappable provider (the holder core)
```csharp
// Source: opentelemetry.io metrics/sdk + customizing-the-sdk — Sdk.CreateMeterProviderBuilder()
var provider = Sdk.CreateMeterProviderBuilder()
    .ConfigureResource(r => r
        .AddService(serviceName: resolvedName, serviceVersion: version)
        .AddAttributes(new[] { new KeyValuePair<string, object>("service.instance.id", capturedId) }))
    .AddMeter("BaseProcessor")
    .AddMeter("MassTransit")
    .AddRuntimeInstrumentation()
    .AddOtlpExporter()   // bare — reads OTEL_EXPORTER_OTLP_ENDPOINT (do NOT hardcode)
    .Build();
// swap: build next; repoint _current; prior.ForceFlush(5000); prior.Dispose();
```

---

## State of the Art

| Old approach | Current approach | Impact |
|--------------|------------------|--------|
| Mutate the metrics Resource after startup | Impossible — Resource frozen at `Build()`; must swap providers | Drives the entire D-02 design |
| Recreate meters for the new provider | Re-subscribe by NAME via `.AddMeter("<name>")` | No meter churn; existing `IMeterFactory` meters survive |
| Manually flush exporter before dispose | `MeterProvider.Dispose()` drives reader Shutdown → exporter flush | `ForceFlush()` only needed for the in-flight 60s window |

**Deprecated/outdated:** none relevant — OTel .NET 1.15.x is current for this stack; `.DisableHttpMetrics()` is .NET 9+ (not available here, .NET 8) — irrelevant to this phase.

---

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | (resolved — was: ProcessorReadDto Name/Version presence) | — | VERIFIED: `ProcessorReadDto` has `Name` (line 44) + `Version` (line 45) — responder edit `new ProcessorIdentityFound(p.Id, ..., p.Name, p.Version)` is valid. No longer an assumption. |
| A2 | `MeterProvider.Dispose()` is idempotent (safe double-dispose under Model A1) | Swap seam A1 | LOW — OTel .NET disposes are idempotent by contract; if not, A1 double-dispose throws → use A2 |
| A3 | Bare `.AddOtlpExporter()` on provider #2 inherits `OTEL_EXPORTER_OTLP_ENDPOINT` exactly as provider #1 | Runtime State / env | LOW-MED — confirmed default behavior; verify in the hermetic holder test that no explicit endpoint is needed |
| A4 | `MassTransit.Monitoring.InstrumentationOptions.MeterName == "MassTransit"` and is the only MassTransit meter the processor needs in provider #2 | holder `.AddMeter` set | LOW — matches the shared path's existing `.AddMeter(InstrumentationOptions.MeterName)` |

---

## Open Questions

1. **Model A1 vs A2 ownership fork (the one real design decision).**
   - What we know: both are correct; A1 = zero shared-path diff + idempotent double-dispose; A2 = one removal line in `AddBaseProcessor` + holder owns both providers, no implicit Dispose reliance.
   - Recommendation: **A2** if the team dislikes implicit double-dispose; **A1** if "shared path literally unchanged" (D-06) is read strictly. Surface to the planner / a brief discuss touch.
2. **MLBL-03 (iv) boot-window series — assert live or hermetic?**
   - The live pre-resolve `processor-sample_3.5.0` series is racy (it may already be swapped by the time Prom scrapes). The hermetic `MeterProviderHolderFacts` proves the same mechanics deterministically.
   - Recommendation: assert MLBL-03 (iv) **hermetically** via the holder test; treat any live boot-window observation as best-effort.
3. **`ProcessorReadDto` Name/Version presence (A1).** Confirm during planning by reading the DTO; trivial, but gates the responder edit.

---

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| OTel SDK 1.15.3 (+ exporter, hosting) | swap + combine | ✓ (CPM pinned) | 1.15.3 | — |
| OTel Instrumentation.Runtime | runtime family assertion | ✓ | 1.15.0 | — |
| compose stack (collector :8889, prometheus :9090) | RealStack scrape assertions | ✓ (compose) | — | hermetic holder test covers the swap mechanics if stack unavailable |
| Postgres (seeded processor row) | MLBL-03 (ii) live resolve | ✓ (compose :5433 host) | — | — |

No missing dependencies. The hermetic holder test removes the hard dependency on the live stack for the core swap proof.

---

## Sources

### Primary (HIGH)
- `Directory.Packages.props` — OTel 1.15.3 / 1.15.0 pins, xUnit v3, MassTransit 8.5.5 `[VERIFIED]`
- `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs` — logs vs metrics resource blocks, `ResolveInstanceId`, `AddMeter("MassTransit")`, bare `AddOtlpExporter` `[VERIFIED]`
- `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` — webapi analog `[VERIFIED]`
- `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` — Loop A `SetIdentity` call-site (line 81), Completion ordering (145-162) `[VERIFIED]`
- `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` — `ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter("BaseProcessor"))` (122), `StartupCompletionService` removal idiom (136-141) `[VERIFIED]`
- `src/BaseProcessor.Core/Observability/ProcessorMetrics.cs` — meter-by-name, IMeterFactory `[VERIFIED]`
- `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs` — Redis-only, NO metric (corrects the brief) `[VERIFIED]`
- `src/Messaging.Contracts/ProcessorQueries.cs`, `GetProcessorBySourceHashConsumer.cs`, `IProcessorContext.cs`, `ProcessorContext.cs` — plumbing edit points `[VERIFIED]`
- `src/{BaseApi.Service,Orchestrator,Keeper,Processor.Sample}/appsettings.json` — Service:Version per console `[VERIFIED]`
- `compose/otel-collector-config.yaml` — `resource_to_telemetry_conversion: true`, no new processor `[VERIFIED]`
- `prometheus.yml` — no rules/dashboards `[VERIFIED]`
- `tests/BaseApi.Tests/Observability/{MetricsExportTests,SchemasMetricsE2ETests,Helpers/PrometheusTestClient,ResolveInstanceIdFacts,ProcessorIdEnricherTests}.cs` + `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs` — harness patterns + literal inventory `[VERIFIED]`

### Secondary (MEDIUM-HIGH, official docs / GitHub)
- OpenTelemetry .NET — Customizing the SDK for Metrics (AddMeter by name; config frozen at Build) `[CITED: github.com/open-telemetry/opentelemetry-dotnet/blob/main/docs/metrics/customizing-the-sdk/README.md]`
- OpenTelemetry Metrics SDK spec (`Sdk.CreateMeterProviderBuilder`, ForceFlush/Shutdown/Dispose flush) `[CITED: opentelemetry.io/docs/specs/otel/metrics/sdk/]`
- OpenTelemetry.Extensions.Hosting README + `OpenTelemetryServicesExtensions.cs` (single MeterProvider per IServiceCollection; TelemetryHostedService) `[CITED: github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Extensions.Hosting/]`
- OTLP exporter default reader 60s interval `[CITED: opentelemetry.io/docs/languages/dotnet/exporters/]`
- Multiple MeterProviders coexistence + same-name behavior `[CITED: opentelemetry-dotnet issues #4636, #6412, #2979]`

### Tertiary (LOW — flagged)
- None load-bearing; all swap claims cross-verified against official docs + codebase.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — CPM-pinned, no new packages
- Swap seam (D-03): HIGH — listener-by-name + Dispose-flush + standalone-builder all verified against official docs; the A1/A2 ownership fork is a clean design choice, not an unknown
- MLBL-01/04 combine + logs isolation: HIGH — exact lines identified; logs/metrics resource blocks are textually separate
- MLBL-05 inventory + versions: HIGH — grep-exhaustive, versions read from live appsettings
- MLBL-03 plumbing: HIGH — call sites verified; one A1 assumption (`ProcessorReadDto` carries Name/Version) flagged

**Research date:** 2026-06-06
**Valid until:** ~2026-07-06 (stable stack; OTel 1.15.x pinned by CPM)

---

## RESEARCH COMPLETE
