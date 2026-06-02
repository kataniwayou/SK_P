# Phase 30: Runtime & Business Metrics — Research

**Researched:** 2026-06-02
**Domain:** .NET 8 `System.Diagnostics.Metrics` (IMeterFactory) → OpenTelemetry SDK → OTel Collector Prometheus exporter → Prometheus HTTP API; real-stack E2E proof
**Confidence:** HIGH (every delegated item verified against the actual repo + current OTel/Prometheus docs)

## Summary

All seven delegated verification items were checked against the live repo files and current OpenTelemetry / Prometheus reality. **Six of the seven CONTEXT assumptions are confirmed correct.** The one item that needs the planner's attention is not a contradiction of a locked *decision* but an **architectural placement wrinkle** the CONTEXT text under-specifies: `AddMeter("BaseProcessor")` **cannot** be added inside `BaseConsoleObservabilityExtensions` (in `BaseConsole.Core`) because `BaseConsole.Core` has NO project reference to `BaseProcessor.Core` (the dependency runs the other way: `BaseProcessor.Core → BaseConsole.Core`, one-directional firewall). The repo already solved exactly this problem in Phase 29 for the log enricher using `ConfigureOpenTelemetryLoggerProvider` inside `AddBaseProcessor`. The meter analog — `ConfigureOpenTelemetryMeterProvider((sp, mp) => mp.AddMeter("BaseProcessor"))` registered inside `AddBaseProcessor` — is the correct, precedent-backed home. CONTEXT D-02/D-05 say "`AddMeter("BaseProcessor")` in `BaseConsoleObservabilityExtensions` / `BaseProcessor.Core` wiring" — the planner MUST resolve this ambiguity in favor of the `BaseProcessor.Core` side via `ConfigureOpenTelemetryMeterProvider`.

The collector behavior (`_total` suffix, `.`→`_` normalization, resource→label conversion, `ProcessorId` case preservation), the Prometheus server port (`localhost:9090`), the `/api/v1/query` response shape, the `IMeterFactory` DI pattern, every increment call-site, the `StepOutcome` enum, and the `RealStackWebAppFactory` reuse path are all confirmed exactly as CONTEXT assumes.

**Primary recommendation:** Implement two `IMeterFactory`-built DI-singleton metric holders (`OrchestratorMetrics`, `ProcessorMetrics`); register their meters via `WithMetrics(m => m.AddMeter("Orchestrator"))` for the orchestrator and via `ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter("BaseProcessor"))` inside `AddBaseProcessor` for the processor (mirroring the existing log-enricher seam); resolve `service.instance.id` once per process and apply it to BOTH the logs `SetResourceBuilder` and metrics `ConfigureResource` in each base lib; mirror `ElasticsearchTestClient.PollEsForLog` for a `PrometheusTestClient.PollPromForQuery` hitting `localhost:9090/api/v1/query`; promote `RealStackWebAppFactory` for a new `MetricsRoundTripE2ETests`.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| `service.instance.id` resolution + resource attr | Base libs (`BaseApi.Core`, `BaseConsole.Core`) wiring | — | Resource attr set once at OTel provider build; flows to all auto-instrumented + custom signals + logs |
| Orchestrator business counters (`dispatch_sent`, `result_consumed`) | `Orchestrator` (app) | — | Counters keyed by `ProcessorId`; sites are orchestrator-internal (`StepDispatcher`, `ResultConsumer`) |
| Processor business counters (`dispatch_consumed`, `result_sent{outcome}`) | `BaseProcessor.Core` (shared framework) | every `Processor.*` (inherits) | Counters live in the framework consumer; inherited by all processors |
| Meter registration on MeterProvider | `Orchestrator`: shared `WithMetrics`. `Processor`: `BaseProcessor.Core` via `ConfigureOpenTelemetryMeterProvider` | — | **`BaseConsole.Core` cannot reference `BaseProcessor.Core`** — see Landmine 1 |
| OTLP → `_total` suffix + `.`→`_` + resource→label | OTel Collector prometheus exporter (`compose/otel-collector-config.yaml`) | — | Generic bridge; MUST NOT change (METRIC-07) |
| PromQL engine (bottleneck expression) | Prometheus **server** container (`localhost:9090`) | — | The collector's `:8889` is a scrape endpoint with NO query engine |
| E2E proof (query Prometheus HTTP API) | `tests/BaseApi.Tests` (out-of-compose host process) | — | Mirrors the ES `PollEsForLog` discipline; hits host-published ports |

## User Constraints (from CONTEXT.md)

### Locked Decisions (D-01..D-14)
- **D-01:** Two DI-registered singleton holders — `OrchestratorMetrics` (in `Orchestrator`), `ProcessorMetrics` (in `BaseProcessor.Core`) — each built via `IMeterFactory.Create(meterName)`, exposing `Counter<long>` fields, injected into call-sites.
- **D-02:** Meter names = `"Orchestrator"` and `"BaseProcessor"` — a const each, referenced by both `AddMeter(...)` and the holder's `Create(...)` (names MUST match).
- **D-03:** Instruments declared **without** `_total` suffix, snake_case literal (`orchestrator_dispatch_sent`, `orchestrator_result_consumed`, `processor_dispatch_consumed`, `processor_result_sent`); the collector appends `_total`. No collector config change.
- **D-04:** *Sent* counters increment **after** the broker `Send` returns; *consumed* counters increment **at the top** of `Consume`.
- **D-05:** `orchestrator_dispatch_sent` increments in `StepDispatcher.DispatchAsync` after `endpoint.Send`, tagged `ProcessorId`. Single owner covers both `WorkflowFireJob` fire and `ResultConsumer` continuation.
- **D-06:** `orchestrator_result_consumed` increments at the top of `ResultConsumer.Consume`, keyed by `m.ProcessorId`, counting every consumed result.
- **D-07:** `processor_dispatch_consumed` increments at the top of `EntryStepDispatchConsumer.Consume`, keyed by `context.Id`; `Immediate(3)` retry re-increment accepted.
- **D-08:** `processor_result_sent{outcome}` — route both send paths (`SendOne` + the final loop) through one private `SendResult(ExecutionResult)` that increments after `Send`, tag `outcome = er.Outcome.ToString().ToLowerInvariant()` + `ProcessorId`.
- **D-09:** Duplicate a tiny `static string ResolveInstanceId()` helper in EACH base lib (`BaseApi.Core` + `BaseConsole.Core`).
- **D-10:** Resolve the id **once per process** (`POD_NAME → HOSTNAME → Environment.MachineName → Guid.NewGuid()`), then apply `AddService(...)` + `AddAttributes([new("service.instance.id", id)])` to BOTH the logs `SetResourceBuilder` and metrics `ConfigureResource` in each base lib.
- **D-11:** New `PrometheusTestClient` in `tests/BaseApi.Tests/Observability/Helpers/`, same backoff poll discipline as `ElasticsearchTestClient`.
- **D-12:** One method `PollPromForQuery(promQL, predicate, timeoutMs)` hitting `GET /api/v1/query?query=…`.
- **D-13:** Query the Prometheus **server** at `localhost:9090`, NOT the collector's `:8889`.
- **D-14:** New `MetricsRoundTripE2ETests` (`[Trait("Category","RealStack")]`) reusing `RealStackWebAppFactory`.

### Claude's Discretion
- Exact backoff constants / poll-timeout budgets in `PrometheusTestClient`.
- Internal field/const naming within the two holders.
- Whether `RealStackWebAppFactory` is promoted to a shared fixture or duplicated minimally.

### Deferred Ideas (OUT OF SCOPE)
None — discussion stayed in-scope. Histograms, an in-flight "processing" gauge/outcome, dashboards, alerting/recording rules, and k8s pod-name wiring are all explicitly out of scope per 30-SPEC.md.

## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| METRIC-01 | `service.instance.id` resource attr on ALL metrics AND ALL logs, no collector change | Verified: `resource_to_telemetry_conversion: true` already on; `AddAttributes` on shared resource → `service_instance_id` label; env precedence pattern standard (§Item 4) |
| METRIC-02 | Runtime metrics (`process_runtime_dotnet_*`) carry the instance label (all 3 process types) | Free consequence of METRIC-01 — `AddRuntimeInstrumentation` is in both base libs; resource attr applies provider-wide |
| METRIC-03 | WebApi HTTP server metrics carry the instance label; `/health/*` filter preserved | `AddAspNetCoreInstrumentation` in `BaseApi.Core`; `filter/health_metrics` untouched (§collector config) |
| METRIC-04 | Orchestrator counters `dispatch_sent`/`result_consumed` keyed by `ProcessorId`, no `workflowId` | Sites confirmed: `StepDispatcher.cs:26`, `ResultConsumer.cs:44` (§Item 6) |
| METRIC-05 | Processor counters `dispatch_consumed`/`result_sent{outcome}` in `BaseProcessor.Core` | Sites confirmed: `EntryStepDispatchConsumer.cs:48` + two send paths (§Item 6); `StepOutcome` enum confirmed |
| METRIC-06 | Bottleneck measurability — shared `ProcessorId` label, PromQL by-`ProcessorId` expression | `ProcessorId` label-case preserved by exporter; `/api/v1/query` shape confirmed (§Items 1, 5) |
| METRIC-07 | Code-owned metric shape; collector metrics pipeline unchanged | `git diff` gate; `_total` + normalization are exporter-default, no config edit (§Item 1) |

## Standard Stack

### Core (all already pinned in `Directory.Packages.props` — NO new packages needed)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `System.Diagnostics.Metrics` (BCL) | net8.0 | `Meter`, `Counter<long>`, `IMeterFactory` | Built-in; no package. `IMeterFactory` auto-registered by the generic host in .NET 8 [VERIFIED: Microsoft Learn metrics-instrumentation] |
| `OpenTelemetry` | 1.15.3 | SDK core, `ResourceBuilder`, `ConfigureResource` | Pinned [VERIFIED: Directory.Packages.props:77] |
| `OpenTelemetry.Extensions.Hosting` | 1.15.3 | `AddOpenTelemetry()`, `ConfigureOpenTelemetryMeterProvider` | Pinned [VERIFIED: :78] |
| `OpenTelemetry.Exporter.OpenTelemetryProtocol` | 1.15.3 | `AddOtlpExporter()` | Pinned [VERIFIED: :79] |
| `OpenTelemetry.Instrumentation.Runtime` | 1.15.0 | `process_runtime_dotnet_*` (METRIC-02) | Pinned, already wired in both base libs [VERIFIED: :84] |
| `OpenTelemetry.Instrumentation.AspNetCore` | 1.15.0 | `http_server_request_duration_*` (METRIC-03) | Pinned, in `BaseApi.Core` [VERIFIED: :81] |

### Supporting (tests — already pinned)
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| `xunit.v3` | 3.2.2 | `[Fact]`, `[Trait]` | E2E test class |
| `System.Net.Http` (BCL) | net8.0 | `HttpClient` to `localhost:9090` | `PrometheusTestClient` |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `IMeterFactory.Create` DI holder (D-01) | `static Meter` field | Static state leaks across the shared hermetic-test process (single test host); `IMeterFactory` is the .NET 8+ blessed pattern and isolates by service collection. CONTEXT chose correctly. [VERIFIED: Microsoft Learn] |

**Installation:** None — every dependency is already referenced. This phase adds zero `PackageReference` and zero `PackageVersion` entries.

## Architecture Patterns

### System Architecture Diagram (signal flow)

```
                          per-process: ResolveInstanceId() = POD_NAME | HOSTNAME | MachineName | Guid
                                                  │
                                                  ▼  (applied ONCE to shared resource)
   ┌───────────────────────────────────────────────────────────────────────────┐
   │  OTel SDK in each service (resource: service.name + service.version +       │
   │                            service.instance.id)                             │
   │                                                                             │
   │  logs ──SetResourceBuilder(shared)──┐        metrics ──ConfigureResource(shared)──┐
   │   • MEL bridge (Phase 29 scopes)    │         • AddRuntimeInstrumentation          │
   │                                     │         • AddAspNetCoreInstrumentation (API) │
   │                                     │         • AddMeter("Orchestrator"|"BaseProcessor")
   │                                     │             ↳ Counter<long> increments       │
   └─────────────┬───────────────────────┘──────────────────┬──────────────────────────┘
                 │ OTLP gRPC :4317                           │ OTLP gRPC :4317
                 ▼                                           ▼
        ┌──────────────────────────────────────────────────────────────┐
        │  otel-collector (sk-otel-collector)  — UNCHANGED (METRIC-07)   │
        │  logs  → elasticsearch exporter → http://elasticsearch:9200    │
        │  metrics → filter/health_metrics → prometheus exporter :8889   │
        │            (add_metric_suffixes=true → _total;  . → _ ;        │
        │             resource_to_telemetry_conversion=true → labels)    │
        └───────────────┬───────────────────────────┬──────────────────┘
            ES query     │                           │  scrape every 15s
        (PollEsForLog)   ▼                           ▼
            ┌──────────────────────┐      ┌─────────────────────────────────┐
            │ Elasticsearch :9200  │      │ Prometheus server :9090 (PromQL) │
            │ (logs incl.          │      │  scrapes otel-collector:8889     │
            │  service.instance.id)│      │  GET /api/v1/query?query=…       │
            └──────────────────────┘      └──────────────┬──────────────────┘
                                                         │ PollPromForQuery (NEW)
                                          MetricsRoundTripE2ETests (host process, out-of-compose)
```

A reader traces the primary case: a counter `.Add(1, tag)` call in code → OTLP → collector renames to `_total` + promotes resource attrs to labels → Prometheus scrapes `:8889` → the E2E queries `:9090/api/v1/query` and asserts the series + the bottleneck expression.

### Recommended placement (file map — all EXISTING files except the 3 NEW ones)
```
src/
├── BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs   # EDIT: shared resource + instance.id
├── BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs     # EDIT: shared resource + instance.id (+ ResolveInstanceId helper)
├── BaseProcessor.Core/
│   ├── Observability/ProcessorMetrics.cs                                          # NEW: IMeterFactory holder + "BaseProcessor" const
│   ├── DependencyInjection/BaseProcessorServiceCollectionExtensions.cs            # EDIT: AddSingleton<ProcessorMetrics> + ConfigureOpenTelemetryMeterProvider(AddMeter)
│   └── Processing/EntryStepDispatchConsumer.cs                                     # EDIT: inject ProcessorMetrics; +SendResult; increments
└── Orchestrator/
    ├── Observability/OrchestratorMetrics.cs                                        # NEW: IMeterFactory holder + "Orchestrator" const
    ├── Program.cs                                                                  # EDIT: AddSingleton<OrchestratorMetrics>; AddMeter in WithMetrics OR via Configure
    ├── Dispatch/StepDispatcher.cs                                                  # EDIT: inject OrchestratorMetrics; increment after Send
    └── Consumers/ResultConsumer.cs                                                 # EDIT: inject OrchestratorMetrics; increment at top
tests/BaseApi.Tests/
├── Observability/Helpers/PrometheusTestClient.cs                                  # NEW: PollPromForQuery
└── Orchestrator/MetricsRoundTripE2ETests.cs                                       # NEW: [Trait Category RealStack]
```

### Pattern 1: IMeterFactory DI-singleton metric holder
**What:** A sealed class constructed with `IMeterFactory`, creating the `Meter` and `Counter<long>` fields once.
**When to use:** Every custom metric in a DI app (.NET 8 blessed pattern).
**Example (orchestrator):**
```csharp
// Source: https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation [CITED]
namespace Orchestrator.Observability;

public sealed class OrchestratorMetrics
{
    public const string MeterName = "Orchestrator";   // D-02 — must match AddMeter("Orchestrator")

    public Counter<long> DispatchSent { get; }
    public Counter<long> ResultConsumed { get; }

    public OrchestratorMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        // D-03: snake_case, NO _total — the collector appends _total
        DispatchSent   = meter.CreateCounter<long>("orchestrator_dispatch_sent");
        ResultConsumed = meter.CreateCounter<long>("orchestrator_result_consumed");
    }
}
```
Increment with a tag (the tag KEY case is preserved through the exporter — see Landmine 2):
```csharp
_metrics.DispatchSent.Add(1, new KeyValuePair<string, object?>("ProcessorId", processorId.ToString("D")));
```

### Pattern 2: meter registration — TWO different seams (CRITICAL)
**Orchestrator** (`Program.cs`): the meter name belongs on the shared MeterProvider. Either add it inside the base-lib `WithMetrics` (not possible — base lib has no `OrchestratorMetrics.MeterName` const) OR, in `Orchestrator/Program.cs`, after `AddBaseConsoleObservability`, attach via `ConfigureOpenTelemetryMeterProvider`:
```csharp
builder.Services.AddSingleton<OrchestratorMetrics>();
builder.Services.ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(OrchestratorMetrics.MeterName));
```
**Processor** (`AddBaseProcessor`): the meter name const lives in `BaseProcessor.Core`; `BaseConsole.Core` cannot see it. Use the SAME additive seam Phase 29 used for the log enricher (`ConfigureOpenTelemetryLoggerProvider`, see `BaseProcessorServiceCollectionExtensions.cs:102`):
```csharp
services.AddSingleton<ProcessorMetrics>();
services.ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(ProcessorMetrics.MeterName));
```
`ConfigureOpenTelemetryMeterProvider` is the exact meter-provider analog of the `ConfigureOpenTelemetryLoggerProvider` already in the repo and is exported by `OpenTelemetry.Extensions.Hosting` 1.15.x. [VERIFIED: repo uses `ConfigureOpenTelemetryLoggerProvider` from the same package]

### Pattern 3: shared resource with instance.id (replaces today's two `AddService` calls)
```csharp
// In each base lib's observability extension — resolve ONCE (D-10):
var instanceId = ResolveInstanceId();   // POD_NAME → HOSTNAME → MachineName → Guid
Action<ResourceBuilder> configureResource = r => r
    .AddService(serviceName: serviceName, serviceVersion: serviceVersion)
    .AddAttributes(new[] { new KeyValuePair<string, object>("service.instance.id", instanceId) });

builder.Logging.AddOpenTelemetry(o => { /*...*/ o.SetResourceBuilder(ResourceBuilder.CreateDefault().Apply(configureResource)); /*...*/ });
builder.Services.AddOpenTelemetry().ConfigureResource(configureResource).WithMetrics(/* unchanged */);
```
> Note: `ResourceBuilder.CreateDefault()` is the existing logs idiom; `ConfigureResource(r => …)` already starts from a default builder. Keep both starting points consistent so the SAME id lands on both. The lambda capturing one `instanceId` local satisfies the D-10 "resolve once" correctness requirement directly.

### `ResolveInstanceId()` helper (duplicated per D-09)
```csharp
internal static string ResolveInstanceId() =>
    Environment.GetEnvironmentVariable("POD_NAME")
    ?? Environment.GetEnvironmentVariable("HOSTNAME")
    ?? Environment.MachineName
    ?? Guid.NewGuid().ToString("N");
// NOTE: Environment.MachineName never returns null, so the GUID branch only fires if
// MachineName throws/empties — keep it as the documented final fallback (matches D-10 intent).
```

### Anti-Patterns to Avoid
- **Adding `service.instance.id` as a per-instrument tag** — violates the constraint; would NOT cover auto-instrumented runtime/HTTP metrics or logs. MUST be a resource attribute.
- **Resolving the instance id twice** (once for logs, once for metrics) — the GUID fallback could differ between the two resources (D-10 correctness). Resolve into one local.
- **Putting `AddMeter("BaseProcessor")` in `BaseConsoleObservabilityExtensions`** — compile error: `BaseConsole.Core` has no reference to `BaseProcessor.Core` (Landmine 1).
- **Editing `compose/otel-collector-config.yaml`** — METRIC-07 gate fails on any metrics-pipeline diff.
- **Tagging counters with `workflowId`** — cardinality violation (D-03/SPEC constraint).
- **Hand-building the PromQL query string from untrusted input** — use a static/templated query literal (mirrors the ES helper's `queryBody` discipline).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Meter lifetime/isolation | static `Meter` field | `IMeterFactory.Create` (auto-registered) | Isolates by service collection; no cross-test static leak [VERIFIED: Microsoft Learn] |
| `_total` suffix on counters | manual `_total` in code | collector `add_metric_suffixes` default | D-03; appending it in code would yield `_total_total` |
| `.`→`_` / resource→label mapping | custom collector processor | existing `resource_to_telemetry_conversion: true` | METRIC-07 — collector stays a dumb bridge |
| Poll-with-backoff for scrape latency | bespoke loop | mirror `ElasticsearchTestClient.PollEsForLog` | Proven exponential backoff (200ms→3.2s); D-11 |
| Instance id resolution | new shared lib | duplicate ~6-line helper (D-09) | `Messaging.Contracts` is wrong home; `BaseConsole.Core` can't ref `BaseApi.Core`; a new lib is overkill |

**Key insight:** The whole phase is "wire existing, blessed mechanisms together." Nothing here should be custom infrastructure — the collector already does the naming/labeling, the host already provides `IMeterFactory`, and the ES helper already encodes the poll discipline.

## Common Pitfalls

### Pitfall 1 (LANDMINE): `AddMeter("BaseProcessor")` placement
**What goes wrong:** Following CONTEXT D-02/D-05 literally ("`AddMeter("BaseProcessor")` in `BaseConsoleObservabilityExtensions`") produces a compile error or forces a forbidden project reference.
**Why it happens:** Dependency direction is `BaseProcessor.Core → BaseConsole.Core` (one-way firewall; `BaseConsole.Core.csproj:13` "NO ProjectReference to the API base library" and its only ref is `Messaging.Contracts`). `BaseConsole.Core` cannot see `ProcessorMetrics.MeterName`.
**How to avoid:** Register the processor meter inside `AddBaseProcessor` (in `BaseProcessor.Core`) via `services.ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(ProcessorMetrics.MeterName))` — the exact additive seam Phase 29 used for `ConfigureOpenTelemetryLoggerProvider` (`BaseProcessorServiceCollectionExtensions.cs:102`). The orchestrator meter can also use `ConfigureOpenTelemetryMeterProvider` in its own `Program.cs` for symmetry.
**Warning signs:** A planner task that says "edit `BaseConsoleObservabilityExtensions` to add `AddMeter("BaseProcessor")`" — reject it.

### Pitfall 2 (LANDMINE): `IProcessorContext.Id` is `Guid?` (nullable)
**What goes wrong:** `processor_dispatch_consumed` / `processor_result_sent` are incremented in `EntryStepDispatchConsumer`, tagged from `context.Id` (D-07). But `IProcessorContext.Id` is `Guid?` — null until identity resolves (`IProcessorContext.cs:36`).
**Why it happens:** The consume path only runs AFTER the runtime binds `queue:{id:D}` post-`MarkHealthy`, so identity IS resolved by then (CONTEXT notes "safe to read on the consume path (post-Healthy)"). But the compiler still sees `Guid?`.
**How to avoid:** Tag with `context.Id!.Value.ToString("D")` (or `context.Id.GetValueOrDefault().ToString("D")` defensively). The dispatch consumer cannot run before Healthy, so the bang is justified — but document it so reviewers don't flag a NRE. Use the SAME `"D"` format string the dispatch queue uses (`queue:{processorId:D}`) for label consistency.
**Warning signs:** A nullable-reference build warning, or a task tagging with raw `context.Id`.

### Pitfall 3: `StepOutcome` has a `Processing = 0` member that is OUT OF SCOPE
**What goes wrong:** `StepOutcome` enum = `Processing=0, Completed=1, Failed=2, Cancelled=3` (`StepOutcome.cs:15-21`). D-08 says `outcome ∈ {completed, failed, cancelled}` (no "processing"). `er.Outcome.ToString().ToLowerInvariant()` would emit `"processing"` IF a `Processing` result were ever sent.
**Why it happens:** The enum carries `Processing` for `StepEntryCondition` int-parity, but `EntryStepDispatchConsumer` only ever builds `Completed`/`Failed`/`Cancelled` results (`BuildCompleted`/`BuildFailed`/`BuildCancelled` — confirmed; no `BuildProcessing`). So in practice the tag is always one of the three terminal values.
**How to avoid:** The `ToString().ToLowerInvariant()` approach (D-08) is correct because no `Processing` `ExecutionResult` is ever sent. No filtering needed — but a task comment should note that the three terminal outcomes are the only ones the send paths produce. The SPEC's acceptance ("outcome ∈ the three terminal values") holds by construction.
**Warning signs:** A future `BuildProcessing` would silently add a `processing` series — out of scope this phase.

### Pitfall 4: scrape→export→scrape latency budget in the E2E
**What goes wrong:** A single immediate Prometheus query after the round-trip misses the sample. Prometheus scrapes the collector every 15s (`prometheus.yml:9`), and the collector batches OTLP exports.
**Why it happens:** Two async hops: SDK→collector (export interval, default 60s metric reader unless OTLP push is sooner) and collector→Prometheus (15s scrape). `send_timestamps: true` on the exporter reduces apparent latency (`otel-collector-config.yaml:70`), but the floor is still a scrape interval.
**How to avoid:** `PollPromForQuery` MUST poll-with-timeout (D-11/D-12). Budget generously: the existing E2E uses 120s for ES (`SampleRoundTripE2ETests.cs:85`); use a comparable or larger budget for the metric series (the OTLP metric export interval can be up to 60s on top of the 15s scrape). Consider that the *first* scrape containing the sample may be up to ~15s after export, and export itself up to the periodic reader interval.
**Warning signs:** A flaky E2E that passes locally but fails on a cold scrape; an immediate (non-polling) assert.

### Pitfall 5: query the SERVER (`:9090`), not the exporter (`:8889`)
**What goes wrong:** Hitting `localhost:8889` gives a raw `/metrics` text scrape with NO PromQL engine — the bottleneck `sum by (ProcessorId)(rate(...))` cannot be evaluated there.
**Why it happens:** `:8889` is the collector's prometheus *exporter* (scrape source); `:9090` is the Prometheus *server* (query engine). Both are host-published (`compose.yaml:67` and `:98`).
**How to avoid:** `PrometheusTestClient.BaseAddress = http://localhost:9090/` and POST/GET to `/api/v1/query` (D-13 — confirmed correct).
**Warning signs:** A test client pointed at `:8889`.

### Pitfall 6: real-stack fixture requires the FULL compose stack up (incl. prometheus)
**What goes wrong:** `MetricsRoundTripE2ETests` needs the `prometheus` container healthy AND scraping; the orchestrator + processor-sample containers must be running to produce counter increments.
**Why it happens:** This is a `[Trait("Category","RealStack")]` test (hermetic filter `Category!=RealStack` excludes it). It drives the SAME seed→Start→round-trip as `SampleRoundTripE2ETests`, then queries Prometheus.
**How to avoid:** Reuse `RealStackWebAppFactory` (D-14) — it sets the host-port env overrides (RMQ 5673, Redis 6380, Postgres 5433, otel 4317). The Prometheus port (9090) is NOT in the factory's env overrides because the test client connects to it directly (not through the WebApi). Document the precondition: `docker compose up` (all services) before running RealStack tests.

## Code Examples

### `PrometheusTestClient.PollPromForQuery` (mirror of `PollEsForLog`)
```csharp
// Source: pattern lifted from tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs [VERIFIED: repo]
//         + Prometheus /api/v1/query shape [CITED: https://prometheus.io/docs/prometheus/latest/querying/api/]
public sealed class PrometheusTestClient : IDisposable
{
    private const int InitialDelayMs = 200;
    private const int MaxDelayMs     = 3_200;
    private readonly HttpClient _prom = new() { BaseAddress = new Uri("http://localhost:9090/") };

    public void Dispose() => _prom.Dispose();

    /// Polls GET /api/v1/query until `predicate(data)` is true OR timeout. Returns the matching
    /// `data` JsonElement (status=="success"), or null on timeout.
    public async Task<JsonElement?> PollPromForQuery(
        string promQL, Func<JsonElement, bool> predicate, int timeoutMs, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var delay = InitialDelayMs;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var url = $"api/v1/query?query={Uri.EscapeDataString(promQL)}";
                using var resp = await _prom.GetAsync(url, ct);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("status", out var s) && s.GetString() == "success"
                        && root.TryGetProperty("data", out var data)
                        && predicate(data))
                    {
                        using var detached = JsonDocument.Parse(data.GetRawText());
                        return detached.RootElement.Clone();
                    }
                }
            }
            catch (HttpRequestException) { /* prometheus briefly unreachable — retry */ }

            var remaining = (int)(timeoutMs - sw.ElapsedMilliseconds);
            if (remaining <= 0) break;
            await Task.Delay(Math.Min(delay, remaining), ct);
            delay = Math.Min(delay * 2, MaxDelayMs);
        }
        return null;
    }
}
```
**Predicate helpers** (vector non-empty for series-existence; numeric for the bottleneck expression):
```csharp
static bool VectorNonEmpty(JsonElement data) =>
    data.TryGetProperty("resultType", out var rt) && rt.GetString() == "vector"
    && data.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Array
    && r.GetArrayLength() > 0;

// scalar result is [unix_time, "value"]; vector result[i].value is [unix_time, "value"] (string!)
static bool HasNumericValue(JsonElement data) =>
    (data.GetProperty("resultType").GetString() == "scalar"
        && double.TryParse(data.GetProperty("result")[1].GetString(), out _))
    || (data.GetProperty("resultType").GetString() == "vector"
        && data.GetProperty("result").EnumerateArray()
            .Any(e => double.TryParse(e.GetProperty("value")[1].GetString(), out _)));
```
> Sample values are quoted JSON strings (`"1"`, not `1`) — parse via `.GetString()` then `double.TryParse` [CITED: Prometheus API docs].

### Series-existence + bottleneck assertions (in `MetricsRoundTripE2ETests`)
```csharp
// (a) series exists for the exercised ProcessorId, with ProcessorId + service_instance_id, no workflowId
var sent = await prom.PollPromForQuery(
    $"orchestrator_dispatch_sent_total{{ProcessorId=\"{procId:D}\"}}", VectorNonEmpty, PromPollTimeoutMs, ct);
Assert.NotNull(sent);
var consumed = await prom.PollPromForQuery(
    $"processor_dispatch_consumed_total{{ProcessorId=\"{procId:D}\"}}", VectorNonEmpty, PromPollTimeoutMs, ct);
Assert.NotNull(consumed);

// (b) the by-ProcessorId bottleneck expression evaluates to a numeric result (METRIC-06)
var bottleneck = await prom.PollPromForQuery(
    $"sum by (ProcessorId)(rate(orchestrator_dispatch_sent_total{{ProcessorId=\"{procId:D}\"}}[5m])) " +
    $"- sum by (ProcessorId)(rate(processor_dispatch_consumed_total{{ProcessorId=\"{procId:D}\"}}[5m]))",
    HasNumericValue, PromPollTimeoutMs, ct);
Assert.NotNull(bottleneck);
```
> The `service_instance_id`-present and no-`workflowId` clauses can be asserted by inspecting `result[0].metric` keys in the returned `data` (the `JsonElement` is retained via `.Clone()`).

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| static `Meter`/`Counter` fields | `IMeterFactory` DI holder | .NET 8 (2023) | DI-isolated meters; auto-registered by host [VERIFIED] |
| `OpenMetricsText` only on `:8889` | OTLP-native + classic both supported | OTel collector / Prom 3.x | Repo uses classic scrape (`metrics_path: /metrics`); fine |
| Prometheus 2.x label rules (no UTF-8) | Prom 3.x UTF-8 metric/label names | Prom 3.0 (2024) | Repo pins `prom/prometheus:v3.11.3`; `ProcessorId` label is valid either way [VERIFIED: compose.yaml:87] |

**Deprecated/outdated:** Nothing in the chosen stack is deprecated. `MassTransit 8.5.5` is the last Apache-2.0 line (do NOT bump to 9.x) — not relevant to this phase but noted in CPM.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | The OTLP metric export interval + 15s scrape means the E2E needs a poll budget comparable to or larger than the ES E2E's 120s. The exact periodic-export-reader interval was not read from code (the OTLP exporter is wired bare `AddOtlpExporter()`). | Pitfall 4 | If the default reader interval (60s) applies, a too-short poll timeout flakes. Mitigation: budget ≥120s; planner should set a generous constant (Claude's discretion per D-11). |
| A2 | `Environment.MachineName` is non-null in the target containers, so the `Guid.NewGuid()` branch is effectively unreachable in normal operation. | ResolveInstanceId helper | None functional — the GUID fallback still satisfies the "non-empty" acceptance; only affects whether instance ids are stable across restarts (k8s pod name is out of scope anyway). |

**All other claims are VERIFIED against repo files or CITED from official docs.**

## Open Questions

1. **Orchestrator meter registration seam — `WithMetrics` vs `ConfigureOpenTelemetryMeterProvider`?**
   - What we know: `Orchestrator/Program.cs` calls `AddBaseConsoleObservability` (which owns the shared `WithMetrics` block in `BaseConsole.Core`). The `OrchestratorMetrics.MeterName` const lives in the `Orchestrator` project, invisible to `BaseConsole.Core`.
   - What's unclear: whether to add `AddMeter("Orchestrator")` via a literal string inside the base-lib `WithMetrics` (loses the const symmetry of D-02) or via `ConfigureOpenTelemetryMeterProvider` in `Orchestrator/Program.cs` (preserves the const, mirrors the processor seam).
   - Recommendation: use `ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(OrchestratorMetrics.MeterName))` in `Orchestrator/Program.cs` — keeps D-02's "a const each, referenced by both `AddMeter` and `Create`" literally true and keeps both services symmetric. This is HIGH confidence given the Phase 29 logger-provider precedent.

2. **Does `processor-sample` need a config/env change to surface a stable `service.instance.id`?**
   - What we know: containers get `HOSTNAME` (Docker sets it to the container id) automatically; no `POD_NAME` outside k8s.
   - What's unclear: nothing blocking — `HOSTNAME` (container id) is a valid per-replica id and satisfies the "non-empty `service_instance_id`" acceptance. k8s Downward-API wiring is explicitly out of scope.
   - Recommendation: no compose change needed; the helper falls through to `HOSTNAME` (container id) automatically. Document that the acceptance asserts non-empty, not pod-name-equality (SPEC Q2).

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET 8 SDK | build/test | ✓ (repo builds today) | 8.0.x | — |
| Docker compose stack (postgres, ES, otel-collector, prometheus, redis, rabbitmq, orchestrator, processor-sample) | RealStack E2E | ✓ (defined in `compose.yaml`) | per-image pins | — |
| Prometheus server | bottleneck PromQL | ✓ host `localhost:9090` | `prom/prometheus:v3.11.3` | — |
| OTel collector prom exporter | scrape source | ✓ `otel-collector:8889` (internal) | contrib 0.152.0 | — |

**Missing dependencies with no fallback:** None — every required container is already in `compose.yaml`. The RealStack E2E precondition is "full `docker compose up` running" (same as the existing `SampleRoundTripE2ETests`).

## Validation Architecture

> nyquist_validation is enabled (`.planning/config.json` workflow.nyquist_validation: true).

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 3.2.2 (+ xunit.v3.assert, xunit.runner.visualstudio) |
| Config file | per-project; `tests/BaseApi.Tests/BaseApi.Tests.csproj` (MTP runner) |
| Quick run command | `dotnet test tests/BaseApi.Tests -- --filter-class "*MetricsRoundTripE2ETests"` (RealStack — needs compose up) |
| Hermetic suite | `dotnet test --filter "Category!=RealStack"` (excludes RealStack/E2E) |
| Full suite | close-gate `phase-NN-close.ps1` (triple-SHA discipline; metrics are append-only, not part of the SHA) |

> Note (from MEMORY): MassTransit-style filter is `-- --filter-class` for the MTP runner; `Category!=RealStack` is the hermetic filter the repo already uses.

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| METRIC-01 | runtime metric has non-empty `service_instance_id`; logs have `resource.attributes.service.instance.id` | RealStack E2E | `MetricsRoundTripE2ETests` (Prom query + ES query) | ❌ Wave 0 |
| METRIC-02 | `process_runtime_dotnet_*` carries `service_instance_id` for all 3 services | RealStack E2E | Prom query in `MetricsRoundTripE2ETests` | ❌ Wave 0 |
| METRIC-03 | `http_server_request_duration_seconds_count` (WebApi) has `service_instance_id`; `/health/*` absent | RealStack E2E | Prom query | ❌ Wave 0 |
| METRIC-04 | `orchestrator_dispatch_sent_total` + `orchestrator_result_consumed_total` with `ProcessorId`+`service_instance_id`, no `workflowId` | RealStack E2E | Prom query (VectorNonEmpty + metric-keys inspect) | ❌ Wave 0 |
| METRIC-05 | `processor_dispatch_consumed_total` + `processor_result_sent_total{outcome}` with `ProcessorId`+`service_instance_id`, no `workflowId` | RealStack E2E | Prom query | ❌ Wave 0 |
| METRIC-06 | by-`ProcessorId` bottleneck PromQL evaluates to a numeric result | RealStack E2E | `PollPromForQuery(bottleneck, HasNumericValue)` | ❌ Wave 0 |
| METRIC-07 | collector metrics pipeline unchanged | git assertion | `git diff compose/otel-collector-config.yaml` (manual/CI; no code test) | n/a (gate) |

> Hermetic-testable slices the planner SHOULD also add (faster feedback, no compose): a unit test that `OrchestratorMetrics`/`ProcessorMetrics` construct from a real `IMeterFactory` and expose non-null counters with the expected names; a test that `ResolveInstanceId()` honors the env precedence (`POD_NAME` > `HOSTNAME` > `MachineName`). These are cheap and catch the meter-name/const-mismatch (D-02) and precedence (D-10) bugs without the full stack.

### Sampling Rate
- **Per task commit:** `dotnet test --filter "Category!=RealStack"` (hermetic — includes the new holder/precedence unit tests).
- **Per wave merge:** hermetic suite + (if compose up) `MetricsRoundTripE2ETests`.
- **Phase gate:** full hermetic + RealStack suite GREEN; `git diff compose/otel-collector-config.yaml` empty on the metrics pipeline; close-gate triple-SHA unaffected (metrics are append-only telemetry).

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs` — `PollPromForQuery` (D-11/D-12)
- [ ] `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs` — covers METRIC-01..06 (D-14)
- [ ] (recommended) hermetic unit test for the two metric holders (meter names + counter names match D-02/D-03)
- [ ] (recommended) hermetic unit test for `ResolveInstanceId()` env precedence (D-10)
- [ ] No framework install needed — xunit.v3 + helpers already present.

## Security Domain

> `security_enforcement` not present in `.planning/config.json` → treat as enabled; but this phase has a minimal security surface.

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | Dev-posture stack; no auth on Prometheus/ES (documented dev-only) |
| V3 Session Management | no | — |
| V4 Access Control | no | — |
| V5 Input Validation | yes (low) | PromQL query strings are app-constructed (static templates + `Uri.EscapeDataString` on the procId GUID) — no untrusted input reaches the query builder |
| V6 Cryptography | no | — |

### Known Threat Patterns
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Metric label cardinality blow-up (`workflowId`) | Denial of Service (Prometheus OOM) | Locked constraint: NO `workflowId`/per-execution id label (D-03/SPEC). Only bounded labels: `ProcessorId`, `outcome`, `service_instance_id` (bounded by replica count). |
| PromQL injection via test query | Tampering | Queries are static C# string templates with only a GUID `procId` interpolated (validated `^[a-f0-9-]` by construction); `Uri.EscapeDataString` on the query param. |

> The dev-posture no-auth on ES/Prometheus/RabbitMQ is a pre-existing, documented stance (compose comments) — this phase does not change it and k8s/prod hardening is out of horizon.

## Sources

### Primary (HIGH confidence)
- Repo files (VERIFIED this session): `compose/otel-collector-config.yaml`, `compose.yaml`, `prometheus.yml`, `Directory.Packages.props`, `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs`, `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs`, `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs`, `src/Orchestrator/Program.cs`, `src/Orchestrator/Dispatch/StepDispatcher.cs`, `src/Orchestrator/Consumers/ResultConsumer.cs`, `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs`, `src/BaseProcessor.Core/Identity/IProcessorContext.cs`, `src/Messaging.Contracts/StepOutcome.cs`, `src/Processor.Sample/Program.cs`, `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs`, `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs`, project `.csproj` reference graphs.
- [OTel Collector prometheus exporter README](https://github.com/open-telemetry/opentelemetry-collector-contrib/blob/main/exporter/prometheusexporter/README.md) — `add_metric_suffixes` default `true`, normalization, `resource_to_telemetry_conversion`.
- [OTel prometheus translator package](https://pkg.go.dev/github.com/open-telemetry/opentelemetry-collector-contrib/pkg/translator/prometheus) — `NormalizeLabel` preserves case; `ProcessorId` → `ProcessorId`.
- [Prometheus HTTP API](https://prometheus.io/docs/prometheus/latest/querying/api/) — `/api/v1/query` response shape; quoted-string sample values; empty vector.
- [Microsoft Learn — Creating Metrics (IMeterFactory)](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation) — host auto-registers `IMeterFactory`; singleton holder pattern.

### Secondary (MEDIUM confidence)
- Prometheus 3.x UTF-8 metric/label support (cross-referenced WebSearch + Prom OTel guide) — confirms `ProcessorId` label validity under the pinned `v3.11.3`.

### Tertiary (LOW confidence)
- None relied upon for load-bearing claims.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all packages already pinned and wired; zero new deps verified against CPM.
- Architecture (meter placement): HIGH — the `BaseConsole.Core ↛ BaseProcessor.Core` constraint and the `ConfigureOpenTelemetry*Provider` seam are both directly evidenced in the repo (Phase 29 logger-provider precedent).
- Collector behavior (`_total`, normalization, label case): HIGH — confirmed against current OTel contrib docs + the existing collector config comments that already document the same mapping.
- Increment call-sites: HIGH — all sites read this session; `StepDispatcher` is the confirmed single dispatch owner; `EntryStepDispatchConsumer` two send paths confirmed.
- Test harness: HIGH — `ElasticsearchTestClient` + `RealStackWebAppFactory` read in full; `/api/v1/query` shape cited.
- Latency budget (A1): MEDIUM — exact OTLP metric-reader interval not read from code; mitigated by a generous poll budget.

**Research date:** 2026-06-02
**Valid until:** 2026-07-02 (stable — OTel 1.15.x, Prometheus v3.11.3, .NET 8 all pinned; collector 0.152.0 pinned).

---

## RESEARCH COMPLETE

**Phase:** 30 - Runtime & Business Metrics
**Confidence:** HIGH

### Key Findings
- **All 7 delegated verification items checked against the live repo + current OTel/Prometheus docs.** Six CONTEXT assumptions confirmed exactly; one needs planner attention (see below — it is a placement ambiguity, not a wrong decision).
- **LANDMINE 1 (meter placement):** `AddMeter("BaseProcessor")` CANNOT go in `BaseConsoleObservabilityExtensions` — `BaseConsole.Core` has no project ref to `BaseProcessor.Core` (one-way firewall). Use `services.ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(...))` inside `AddBaseProcessor`, mirroring the Phase 29 `ConfigureOpenTelemetryLoggerProvider` log-enricher seam already in the repo. Recommend the same seam for the orchestrator meter in `Orchestrator/Program.cs` to keep D-02's const symmetry.
- **LANDMINE 2 (`IProcessorContext.Id` is `Guid?`):** the processor counter tag source is nullable (null pre-Healthy). Tag with `context.Id!.Value.ToString("D")` — safe because the dispatch consumer only runs post-Healthy — and document the bang.
- **D-03 CONFIRMED:** collector `add_metric_suffixes` defaults true → appends `_total`; `.`→`_` normalization applies; `ProcessorId` label case is PRESERVED (valid Prom label, no lowercasing). SPEC acceptance names + `sum by (ProcessorId)` land with NO collector config change.
- **D-13 CONFIRMED:** Prometheus server is host-published at `localhost:9090` (`compose.yaml:98`); it scrapes `otel-collector:8889`. Query the server (`:9090/api/v1/query`), not the exporter (`:8889`). `StepOutcome` (with its out-of-scope `Processing=0`) is never sent by the processor's build paths, so D-08's `ToString().ToLowerInvariant()` yields only {completed, failed, cancelled} by construction.
- **Zero new packages** — `IMeterFactory` (auto-registered by the .NET 8 generic host), OTel 1.15.x, runtime/AspNetCore instrumentation are all already pinned and wired.

### File Created
`C:\Users\UserL\source\repos\SK_P\.planning\phases\30-runtime-business-metrics\30-RESEARCH.md`

### Confidence Assessment
| Area | Level | Reason |
|------|-------|--------|
| Standard Stack | HIGH | All deps pre-pinned in CPM; verified against `Directory.Packages.props` |
| Architecture | HIGH | Dependency-graph constraint + `ConfigureOpenTelemetry*Provider` seam both evidenced in repo |
| Pitfalls | HIGH | Each landmine traced to a specific file/line read this session |
| Test harness | HIGH | ES helper + RealStack factory read in full; Prom API shape cited |
| Latency budget | MEDIUM | OTLP reader interval not read from code (A1) — mitigated by generous poll budget |

### Open Questions (non-blocking)
1. Orchestrator meter seam: recommend `ConfigureOpenTelemetryMeterProvider` in `Program.cs` (preserves D-02 const symmetry). HIGH-confidence recommendation, planner decides.
2. No compose change needed for `service.instance.id` — `HOSTNAME` (container id) is the fallback; acceptance asserts non-empty, not pod-name equality (k8s out of scope).

### Ready for Planning
Research complete. The planner can write code-anchored tasks against the exact files and line-level seams identified, with the two landmines flagged for explicit task wording.
