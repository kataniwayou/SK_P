# Phase 18: BaseConsole.Core Library - Research

**Researched:** 2026-05-30
**Domain:** .NET 8 Generic-Host class library (MassTransit/RabbitMQ messaging skeleton, OTel metrics-only, embedded-Kestrel health probes, AsyncLocal correlation filters)
**Confidence:** HIGH

## Summary

Phase 18 builds `BaseConsole.Core` — a reusable Generic-Host class library that mirrors the existing `BaseApi.Core` seam for console workers. It is validated **standalone** (no concrete console ships; the Orchestrator is Phase 19) via an in-memory Generic-Host test fixture plus the MassTransit in-memory test harness. Almost every component is a near-verbatim lift/duplicate of code already in `BaseApi.Core`; the only genuinely new construction is the two correlation filters + the `AsyncLocal` accessor (NOT created in Phase 17 — verified empirically below), and the embedded-Kestrel health listener.

The locked decisions D-01..D-09 in CONTEXT.md are authoritative and, after verification against the actual source and MassTransit 8.5.5 docs, are **correct and sufficient**. This research confirms each focus area against the real type/method surface (MassTransit `UseConsumeFilter`/`UseSendFilter`/`UsePublishFilter`, the auto-registered `ready`+`masstransit` bus health check, `AddMassTransitTestHarness`, `InstrumentationOptions.MeterName`, `FrameworkReference Microsoft.AspNetCore.App`) and adds planning-grade detail: exact signatures, registration ordering, and the six in-fixture proof points. **It does not relitigate any locked choice.**

**Primary recommendation:** Duplicate `BaseApi.Core`'s observability/redis/health/startup-gate code into `BaseConsole.Core` with the two documented OTel edits, build the embedded health listener as an independent `IHostedService` hosting a minimal `WebApplication`/Kestrel on an appsettings-configurable port, register both new correlation filters bus-wide inside `AddBaseConsoleMessaging` (`UseConsumeFilter` + `UseSendFilter` + `UsePublishFilter` on the bus factory configurator), and prove all six D-02 facts in `tests/BaseApi.Tests` using a Generic-Host fixture + `AddMassTransitTestHarness`.

> **CRITICAL CORRECTION (verified this session):** The objective header says ".NET 9 / C#". The actual codebase targets **`net8.0`** (`Directory.Build.props:29`, `<TargetFramework>net8.0</TargetFramework>`, .NET 8.0.421 SDK). `BaseConsole.Core` MUST inherit `net8.0` from `Directory.Build.props` like every other project. Do not introduce a `net9.0` TFM. All MassTransit 8.5.5 / OTel 1.15.x / AspNetCore.App-8.0 surfaces below are net8.0-correct.

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01 (CORR-02 stamping mechanism):** The outbound send/publish filter stamps the **MassTransit envelope** (`SendContext.CorrelationId` / `PublishContext.CorrelationId`) from the ambient AsyncLocal accessor, gated on `message is ICorrelated`. It does **NOT** mutate the record body, so `ICorrelated` stays get-only. Do NOT reopen the frozen `Messaging.Contracts` contract to add setters this phase.
- **D-02 (standalone validation):** Validate via a test-only minimal Generic-Host fixture in `tests/BaseApi.Tests` (e.g. `ConsoleTestHostFixture`) composing `AddBaseConsole` + `AddBaseConsoleObservability` + `AddBaseConsoleMessaging`. Six proof points (host boots; `/health/live`=200 with Redis+RabbitMQ dead; `/health/startup` flips Healthy; no `TracerProvider`; MT meter registered + AspNetCore/HttpClient instrumentation absent; both filters registered + exercised via `AddMassTransitTestHarness`). No throwaway sample host ships inside the library.
- **D-03 (close gate):** Existing **dual-SHA** (`psql \l` + `redis-cli --scan`) + 3-consecutive-GREEN. The triple-SHA `rabbitmqctl list_queues` snapshot is NOT introduced here — Phase 18 uses the in-memory harness (no real broker). Broker leak gate is Phase 20 (TEST-RMQ-05).
- **D-04 (health port):** Embedded health-listener port is appsettings-configurable with a sensible default (e.g. `ConsoleHealth:Port`, default `8081`). The minimal Kestrel runs as an `IHostedService` **independent of the bus**, so `/health/live` answers while the bus is still connecting.
- **D-05 (startup gate):** `/health/startup` is a host-initialized latch — a duplicated `IStartupGate` + a console `StartupCompletionService` that calls `MarkReady()` on `StartAsync` (the Phase-5-era variant, NOT the Phase-8 migration variant). Three-way split: `startup` = host came up; `ready` = MassTransit bus started (MT's auto-registered `ready`-tagged check, no hand-rolled latch); `live` = process alive (self-only). The MassTransit bus health check MUST be tagged `"ready"`, never `"live"`.
- **D-06 (composition surface):** Mirror the `BaseApi.Core` seam exactly. Public surface = three calls + run: `builder.AddBaseConsoleObservability(cfg)` (separate call on `IHostApplicationBuilder`); `builder.Services.AddBaseConsole(cfg)` (Redis + startup gate + embedded health hosted service); `builder.Services.AddBaseConsoleMessaging(cfg, configureConsumers)` (RabbitMQ host + bus-wide outbound correlation filters; concrete passes a consumer-registration lambda); then `await host.RunAsync()`.
- **D-07 (non-generic):** `AddBaseConsole` is non-generic (no `TDbContext` — consoles have no DbContext). All config (`Service:Name`/`Service:Version`, OTLP endpoint, RabbitMQ host/credentials, Redis connection string) flows through appsettings (`cfg`); nothing host-specific hardcoded in the base.
- **D-08 (no BaseConsole→BaseApi dependency):** `IStartupGate`/`StartupGate`/`StartupHealthCheck` and the Redis registration are **duplicated** into `BaseConsole.Core` (~40 LOC + the Redis extension). A `BaseConsole.Core → BaseApi.Core` ProjectReference is forbidden (would drag EF Core + ASP.NET MVC into a worker host). Extraction to a shared `Hosting.Abstractions` assembly is deferred until a third host type appears.
- **D-09 (project location):** New project at `src/BaseConsole.Core/`, consistent with the existing three source projects; tests stay in `tests/BaseApi.Tests`.

### Claude's Discretion
- Filter registration **ordering** (inbound consume filter must run before the log scope opens / before user consumer code) — standard MassTransit pipeline wiring; planner/executor decides exact placement.
- Exact `ICorrelationAccessor` / AsyncLocal accessor **type name and namespace** (research SUMMARY names `ICorrelationAccessor` + `AsyncLocalCorrelationAccessor`; planner confirms whether it lives in `Messaging.Contracts` or `BaseConsole.Core` — note it was NOT created in Phase 17, so it is new here).
- Embedded-health-listener **class name** (suggestion `EmbeddedHealthEndpointService`); default port value; whether health config binds to an options record.
- `BaseConsole.Core.csproj` shape (TFM/Nullable inheritance from `Directory.Build.props`, `FrameworkReference` + MassTransit/MassTransit.RabbitMQ/StackExchange.Redis/OTel PackageReferences, no `Version=` thanks to CPM) — follows the Phase 1 csproj-inheritance idiom.

### Deferred Ideas (OUT OF SCOPE)
- Flatten `src/` to a single repo root — NOT adopted; cross-cutting Phase-1 reversal.
- `ICorrelated` settable properties (body-field stamping) — revisit only when a real `ICorrelated` implementer needs it (Processor milestone); D-01 keeps it get-only.
- Triple-SHA `rabbitmqctl list_queues` close gate — Phase 20 (TEST-RMQ-05).
- Extract IStartupGate/Redis to a shared `Hosting.Abstractions` assembly — only if/when a third host type appears.
- Two-bus fan-out test, ES correlation E2E proof, synthetic outbound harness send — Phase 19/20 (need concrete Orchestrator + real broker).
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| CONSOLE-01 | Reusable `BaseConsole.Core` library with Generic-Host composition root (`AddBaseConsole`/`RunAsync`) wired in a handful of lines (mirrors `AddBaseApi`/`UseBaseApi`). | Mirror map below; `BaseApiServiceCollectionExtensions.cs:24` is the chain template; `Host.CreateApplicationBuilder` + `IHostApplicationBuilder` carry the OTel call verbatim. |
| CONSOLE-02 | Console-flavored OTel — MEL-bridge logs + runtime metrics + MassTransit meter (`InstrumentationOptions.MeterName`) via OTLP; NO AspNetCore/HttpClient instrumentation, NO `.WithTracing`. | Two-edit diff from `ObservabilityServiceCollectionExtensions.cs` documented below; `InstrumentationOptions.MeterName` = `"MassTransit"` (CITED). |
| CONSOLE-03 | Singleton `IConnectionMultiplexer` Redis client, soft dependency (`abortConnect=false`), lifted from `BaseApi.Core`. | `RedisServiceCollectionExtensions.cs:43-66` lift; promote `internal`→`public` (see Pitfall 4). |
| CONSOLE-04 | MassTransit bus skeleton `AddBaseConsoleMessaging(cfg, configureConsumers)` — RabbitMQ host + global outbound correlation filter + concrete consumer-registration callback. | `AddMassTransit` + `UsingRabbitMq` + `UseSendFilter`/`UsePublishFilter` (VERIFIED via Context7); seam mirrors `AddBaseApi` vs `AddAppFeatures`. |
| CONSOLE-05 | References `Microsoft.AspNetCore.App` via `FrameworkReference` (stays a library, not Web SDK). | `Sdk="Microsoft.NET.Sdk"` + `<FrameworkReference Include="Microsoft.AspNetCore.App" />` gives Kestrel/health-check/minimal-hosting types without becoming a web app (CITED STACK.md). |
| CONSOLE-HEALTH-01 | `/health/live|ready|startup` over an embedded minimal HTTP listener inside the Generic Host (`MapHealthChecks` is `WebApplication`-only → inner minimal Kestrel in a hosted service). | Pattern below; `EmbeddedHealthEndpointService : IHostedService` hosting a minimal `WebApplication`. |
| CONSOLE-HEALTH-02 | `/health/live` returns 200 without touching RabbitMQ or Redis (live → self only), even when both down. | Tag predicate `c.Tags.Contains("live")` → only the `"self"` Healthy check; mirrors `HealthServiceCollectionExtensions.cs:22`. |
| CONSOLE-HEALTH-03 | `/health/ready` Healthy only once the MassTransit bus has started (reuses MT auto `ready`-tagged check, no hand-rolled latch); Unhealthy while broker unreachable. | MT auto-registers `ready`+`masstransit` check (VERIFIED). |
| CONSOLE-HEALTH-04 | `IStartupGate` + `StartupHealthCheck` duplicated into `BaseConsole.Core` (no `BaseConsole.Core → BaseApi.Core` dependency). | D-08; `IStartupGate.cs` + `StartupHealthCheck.cs` are already `public sealed` — copy verbatim. |
| CORR-01 | Inbound consume filter resolves the correlation value → AsyncLocal accessor → MEL log scope under literal `"CorrelationId"` (same key as `CorrelationIdMiddleware`, so OTel `IncludeScopes` serializes to identical ES attribute). | `IFilter<ConsumeContext>` + `CorrelationKeys.LogScope` (= `"CorrelationId"`, `CorrelationKeys.cs:7`); `BeginScope` with the literal key (VERIFIED middleware uses same). |
| CORR-02 | Outbound send/publish filter stamps the ambient AsyncLocal correlationId onto every outgoing `ICorrelated` message (envelope, per D-01). | `IFilter<SendContext<T>>` + `IFilter<PublishContext<T>>` setting `context.CorrelationId`, gated on `message is ICorrelated`. |
</phase_requirements>

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Generic-Host composition (`AddBaseConsole`) | Backend / Console worker | — | This IS the worker-host base library; no browser/SSR tier exists. |
| Console OTel (logs+metrics) | Backend / Console worker | OTel Collector (external) | Observability is wired at the host composition root on `IHostApplicationBuilder`; export is to the Collector. |
| Redis soft-dep client | Backend / Console worker | Redis (external, soft) | Singleton `IConnectionMultiplexer`; lazy-connect, never blocks boot. |
| Embedded health endpoints | Backend / Console worker (embedded Kestrel) | K8s probes (external) | Kestrel-in-hosted-service exposes HTTP probes; an HTTP surface inside a worker, deliberately isolated on its own port. |
| MassTransit bus skeleton | Backend / Console worker | RabbitMQ (external, hard for bus) | `AddMassTransit`+`UsingRabbitMq` owns connection lifecycle; concrete supplies consumers. |
| Correlation filters (in/out) | Backend / Messaging middleware | MEL/OTel logs (external) | MassTransit pipeline middleware; inbound opens the MEL scope, outbound stamps the envelope. |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| MassTransit | 8.5.5 | Bus abstractions, consumer registration, consume/send/publish filter pipeline, in-memory `ITestHarness` | Last Apache-2.0 8.x line; v9+ is commercial. The filter pipeline is the exact seam for correlation. [CITED: STACK.md — nuget.org verified 2026-05-30] |
| MassTransit.RabbitMQ | 8.5.5 | RabbitMQ transport (`UsingRabbitMq`), topology config | Pulls `RabbitMQ.Client >= 7.1.2` transitively; net8.0-clean. [CITED: STACK.md] |
| Microsoft.AspNetCore.App | shared FX (8.0.x) | Kestrel, routing, `AddHealthChecks`, `MapHealthChecks`, minimal `WebApplication` | `FrameworkReference` gives the embedded health listener + reuse of the validated 3-probe pattern with zero re-design. [CITED: STACK.md] |
| StackExchange.Redis | 2.13.1 | `IConnectionMultiplexer` soft-dep client | Lifted verbatim from `BaseApi.Core`; `abortConnect=false` boot-safety pattern. [VERIFIED: Directory.Packages.props:120] |
| OpenTelemetry (+ Extensions.Hosting + Exporter.OTLP + Instrumentation.Runtime) | 1.15.3 / 1.15.0 | MEL-bridge logs + runtime metrics + OTLP export | Already pinned; `AddMeter`/string overloads support the MassTransit meter. [VERIFIED: Directory.Packages.props:72-79] |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| AspNetCore.HealthChecks.UI.Client | 9.0.0 | `UIResponseWriter.WriteHealthCheckUIResponse` JSON body writer | In the embedded listener's `MapHealthChecks`, to keep the health response shape identical to the API. [VERIFIED: Directory.Packages.props:91] |
| MassTransit (test harness) | 8.5.5 | `AddMassTransitTestHarness`, `ITestHarness`, in-memory transport | In `tests/BaseApi.Tests` to exercise both filters with no real broker. Ships in the **core** package — no separate `MassTransit.Testing` NuGet. [VERIFIED: Context7] |
| xunit.v3 / NSubstitute | 3.2.2 / 5.3.0 | Test framework + mocking | Existing test stack in `tests/BaseApi.Tests`. [VERIFIED: Directory.Packages.props:109-111] |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Embedded Kestrel **in an IHostedService** (D-04 locked) | `WebApplication` as the **primary** host (STACK.md §"RECOMMENDED") | STACK.md's "make the whole console a `WebApplication`" pattern is **rejected by CONTEXT D-04** — it couples `/health/live` to the bus lifecycle, blurs console-vs-webapp, and risks pulling AspNetCore instrumentation into OTel. **Follow D-04 (independent hosted service), not STACK.md's primary-host shortcut.** See Open Question 1. |
| MassTransit 8.5.5 | MassTransit 9.x | Commercial ($400/mo min). Locked off via CPM pin + comment. |
| Duplicate IStartupGate/Redis | `BaseConsole.Core → BaseApi.Core` ProjectReference | Forbidden (D-08) — drags EF Core + MVC into the worker. |

**Installation (no new CPM pins needed — all already in `Directory.Packages.props`):**
```xml
<!-- src/BaseConsole.Core/BaseConsole.Core.csproj — Sdk="Microsoft.NET.Sdk", NOT .Web -->
<ItemGroup>
  <FrameworkReference Include="Microsoft.AspNetCore.App" />   <!-- Kestrel + health checks (CONSOLE-05) -->
  <PackageReference Include="MassTransit" />
  <PackageReference Include="MassTransit.RabbitMQ" />
  <PackageReference Include="AspNetCore.HealthChecks.UI.Client" />
  <PackageReference Include="StackExchange.Redis" />
  <PackageReference Include="OpenTelemetry" />
  <PackageReference Include="OpenTelemetry.Extensions.Hosting" />
  <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" />
  <PackageReference Include="OpenTelemetry.Instrumentation.Runtime" />
  <ProjectReference Include="..\Messaging.Contracts\Messaging.Contracts.csproj" />
</ItemGroup>
```
No `Version=` (CPM). TFM/Nullable/strictness inherit from `Directory.Build.props`. MassTransit/MassTransit.RabbitMQ 8.5.5, StackExchange.Redis 2.13.1, OTel 1.15.x, AspNetCore.HealthChecks.UI.Client 9.0.0 are already pinned (Phase 17 / earlier).

**Version verification note:** Per CONTEXT, the locked versions are authoritative and not to be relitigated. STACK.md verified MassTransit 8.5.5 (Apache-2.0, net8.0, released 2025-10-22) and the v9-commercial boundary on nuget.org on 2026-05-30; this session did not re-run `npm view`/nuget (irrelevant — versions are CPM-locked and out of scope to change).

## Mirror Source Map (what to lift / duplicate / build-new)

| Target in `BaseConsole.Core` | Source in `BaseApi.Core` | Action | Notes |
|------------------------------|--------------------------|--------|-------|
| `AddBaseConsoleObservability(IHostApplicationBuilder, IConfiguration)` | `ObservabilityServiceCollectionExtensions.cs:37` | Lift + 2 edits | Drop `.AddAspNetCoreInstrumentation()` + `.AddHttpClientInstrumentation()`; add `.AddMeter(InstrumentationOptions.MeterName)`; keep `.AddRuntimeInstrumentation()`, MEL bridge, `IncludeScopes=true`, OTLP, resource builder. **Keep NO `.WithTracing`.** |
| Console Redis extension (`AddBaseConsoleRedis` or folded into `AddBaseConsole`) | `RedisServiceCollectionExtensions.cs:43-66` | Duplicate | Source is `internal`; the duplicate must be reachable from the console composition root (make `public` or keep `internal` within the same assembly chain). Drop the `RedisProjectionOptions` binding if the console does not read projections config — but Orchestrator (P19) reads L2, so keep the `IConnectionMultiplexer` singleton; the options binding is optional this phase. |
| `IStartupGate` + `StartupGate` | `Health/IStartupGate.cs` | Duplicate verbatim | Already `public sealed`. ~30 LOC. |
| `StartupHealthCheck` | `Health/StartupHealthCheck.cs` | Duplicate verbatim | Already `public sealed`. Tagged `"startup"`+`"ready"`. ~10 LOC. |
| Console `StartupCompletionService` | `Health/StartupCompletionService.cs` | **Build the Phase-5 variant, NOT the current source** | ⚠️ The *current* `StartupCompletionService.cs` is the **Phase-8 migration variant** (injects `BaseDbContext`, calls `MigrateAsync`). The console has no DB. Build the simpler Phase-5-era shape: inject `IStartupGate`, `StartAsync` calls `_gate.MarkReady()` and returns; `StopAsync` = `Task.CompletedTask`. (D-05.) |
| Embedded health listener | `BaseApiApplicationBuilderExtensions.cs:46-60` (`MapHealthChecks` x3 + predicates + `UIResponseWriter`) + `HealthServiceCollectionExtensions.cs:20-29` (check registration + tags) | Build-new `IHostedService` | Same tag predicates + body writer, but hosted inside a minimal `WebApplication` in an `IHostedService` (Generic Host has no `MapHealthChecks`). |
| `ICorrelationAccessor` + `AsyncLocalCorrelationAccessor` | — (does not exist anywhere yet) | **Build-new** | Verified absent: `Messaging.Contracts` has only ICorrelated/CorrelationKeys/Start-Stop/Projections; no accessor or filters. |
| `InboundCorrelationConsumeFilter` | — | **Build-new** | `IFilter<ConsumeContext>` (non-generic). |
| `OutboundCorrelationSendFilter<T>` / `OutboundCorrelationPublishFilter<T>` | — | **Build-new** | `IFilter<SendContext<T>>` / `IFilter<PublishContext<T>>`, `where T : class`. |

> **Provenance correction (verified):** The project-research SUMMARY/ARCHITECTURE docs place the filters + accessor in `Messaging.Contracts`. CONTEXT.md `<specifics>` + the verified state of the repo override this: `Messaging.Contracts.csproj` is POCO-only with **NO MassTransit PackageReference** (`Messaging.Contracts.csproj:6-8`), so it physically cannot host `IFilter<>` types. The filters + accessor therefore live in **`BaseConsole.Core`** (which references MassTransit). This is consistent with CONTEXT's "Build them in `BaseConsole.Core` this phase." [VERIFIED: glob of `src/Messaging.Contracts` + csproj read]

## Architecture Patterns

### System Architecture Diagram

```
                  appsettings (cfg)  ──┐  Service:Name/Version, OTLP endpoint,
                                       │  RabbitMq host/creds, Redis conn str
                                       ▼
   Host.CreateApplicationBuilder(args)  →  IHostApplicationBuilder
        │
        ├── builder.AddBaseConsoleObservability(cfg)        [on IHostApplicationBuilder]
        │       ├─ builder.Logging.AddOpenTelemetry(MEL bridge, IncludeScopes=true, OTLP)
        │       └─ builder.Services.AddOpenTelemetry().WithMetrics(
        │               AddMeter("MassTransit") + AddRuntimeInstrumentation() + OTLP)
        │               ── NO .WithTracing,  NO AspNetCore/HttpClient instrumentation
        │
        ├── builder.Services.AddBaseConsole(cfg)
        │       ├─ Redis: AddSingleton<IConnectionMultiplexer>(Connect(connStr, abortConnect=false))   [lazy]
        │       ├─ AddSingleton<IStartupGate, StartupGate>()
        │       ├─ AddHealthChecks(): "self"→[live] ; StartupHealthCheck→[startup,ready]
        │       ├─ AddHostedService<StartupCompletionService>()        (MarkReady on StartAsync)
        │       └─ AddHostedService<EmbeddedHealthEndpointService>()   ┐ independent Kestrel
        │                                                              │ on ConsoleHealth:Port (8081)
        │                                                              ▼
        │            ┌─────────── minimal WebApplication (inner) ───────────┐
        │            │  MapHealthChecks("/health/live",  Tags.Contains("live"))   → 200 always
        │            │  MapHealthChecks("/health/ready", Tags.Contains("ready"))  → startup gate AND MT bus
        │            │  MapHealthChecks("/health/startup",Tags.Contains("startup"))→ StartupHealthCheck
        │            └────────────────────────────────────────────────────┘
        │
        └── builder.Services.AddBaseConsoleMessaging(cfg, configureConsumers)
                └─ AddMassTransit(x =>
                       configureConsumers(x);          ← concrete seam (empty this phase; P19 adds consumers)
                       x.UsingRabbitMq((ctx, cfg2) => {
                           cfg2.Host(rabbitConnStr);
                           cfg2.UseConsumeFilter(typeof(InboundCorrelationConsumeFilter), ctx);   ← inbound, bus-wide
                           cfg2.UseSendFilter(typeof(OutboundCorrelationSendFilter<>), ctx);      ← outbound
                           cfg2.UsePublishFilter(typeof(OutboundCorrelationPublishFilter<>), ctx);← outbound
                           cfg2.ConfigureEndpoints(ctx);     (no concrete endpoints this phase)
                       }))
                   └─ auto-registers bus IHealthCheck tagged [ready, masstransit]
        │
        ▼
   await builder.Build().RunAsync()
```

Data flow on consume (proven only via harness this phase): published message → InboundCorrelationConsumeFilter reads `ConsumeContext.CorrelationId`/`ICorrelated.CorrelationId` → `accessor.Set(...)` → `BeginScope(["CorrelationId"]=...)` → next.Send → (Phase 19 consumer). On send/publish: ambient accessor → OutboundCorrelation*Filter sets `context.CorrelationId` when `message is ICorrelated`.

### Recommended Project Structure
```
src/BaseConsole.Core/
├── BaseConsole.Core.csproj                 # Sdk=Microsoft.NET.Sdk + FrameworkReference AspNetCore.App
├── DependencyInjection/
│   ├── BaseConsoleServiceCollectionExtensions.cs    # AddBaseConsole (chain)        ← mirror AddBaseApi
│   ├── BaseConsoleObservabilityExtensions.cs        # AddBaseConsoleObservability   ← mirror + 2 edits
│   ├── ConsoleRedisServiceCollectionExtensions.cs   # lifted from BaseApi.Core
│   ├── ConsoleHealthServiceCollectionExtensions.cs  # gate + checks + EmbeddedHealthEndpointService reg
│   └── MessagingServiceCollectionExtensions.cs      # AddBaseConsoleMessaging(cfg, configureConsumers)
├── Health/
│   ├── IStartupGate.cs / StartupGate.cs             # duplicated
│   ├── StartupHealthCheck.cs                        # duplicated
│   ├── StartupCompletionService.cs                  # Phase-5 variant (MarkReady on StartAsync)
│   └── EmbeddedHealthEndpointService.cs             # IHostedService → minimal Kestrel
├── Messaging/
│   ├── ICorrelationAccessor.cs / AsyncLocalCorrelationAccessor.cs   # NEW
│   ├── InboundCorrelationConsumeFilter.cs           # IFilter<ConsumeContext>
│   ├── OutboundCorrelationSendFilter.cs             # IFilter<SendContext<T>>
│   └── OutboundCorrelationPublishFilter.cs          # IFilter<PublishContext<T>>
└── (optional) Hosting/BaseConsoleHost.cs            # RunAsync(args, configureConsumers) convenience
```
(Accessor/filter namespace is Claude's Discretion per CONTEXT — `BaseConsole.Core.Messaging` shown; planner may prefer a `Correlation/` folder.)

### Pattern 1: OTel console flavor — the two-edit diff (CONSOLE-02)
**What:** Lift `AddBaseApiObservability` (`ObservabilityServiceCollectionExtensions.cs:37`) and change exactly two things in the metrics chain.
**When:** The single observability call on `IHostApplicationBuilder`.
```csharp
// Source: mirror of ObservabilityServiceCollectionExtensions.cs:60-73, console flavor
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(serviceName, serviceVersion))
    .WithMetrics(m => m
        // REMOVED vs API: .AddAspNetCoreInstrumentation()  .AddHttpClientInstrumentation()
        .AddMeter(InstrumentationOptions.MeterName)   // "MassTransit" — bus counters/gauges/histograms (NEW)
        .AddRuntimeInstrumentation()                  // kept from API
        .AddOtlpExporter());
// builder.Logging.AddOpenTelemetry(...) MEL block (IncludeScopes=true, OTLP) lifts BYTE-FOR-BYTE.
// NO .WithTracing — never add a TracerProvider (Pitfall 1).
```
`InstrumentationOptions` lives in the `MassTransit` namespace; `InstrumentationOptions.MeterName` is the string `"MassTransit"`. [CITED: masstransit.massient.com observability]

### Pattern 2: Embedded health listener as an independent IHostedService (CONSOLE-HEALTH-01/02, D-04)
**What:** A `WebApplication`/Kestrel built **inside** an `IHostedService.StartAsync`, bound to `ConsoleHealth:Port` (default 8081), with the same three `MapHealthChecks` tag predicates and `UIResponseWriter` body as the API. It starts **independently of the bus** so `/health/live` answers while the bus is connecting and with Redis+RabbitMQ ports dead.
**When:** Registered by `AddBaseConsole`.
```csharp
// Source: tag predicates + body writer mirror BaseApiApplicationBuilderExtensions.cs:46-60
internal sealed class EmbeddedHealthEndpointService : IHostedService
{
    private readonly IStartupGate _gate;
    private readonly IConfiguration _cfg;
    private WebApplication? _app;
    public EmbeddedHealthEndpointService(IStartupGate gate, IConfiguration cfg) { _gate = gate; _cfg = cfg; }

    public async Task StartAsync(CancellationToken ct)
    {
        var b = WebApplication.CreateBuilder();
        var port = _cfg.GetValue<int?>("ConsoleHealth:Port") ?? 8081;
        b.WebHost.UseUrls($"http://0.0.0.0:{port}");
        b.Services.AddSingleton(_gate);                                   // share the OUTER gate instance
        b.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
            .AddCheck<StartupHealthCheck>("startup", tags: new[] { "startup", "ready" });
        //  + the MassTransit "ready" bus check — see Pattern 3 caveat (must be reachable from this inner DI)
        _app = b.Build();
        _app.MapHealthChecks("/health/live",    new HealthCheckOptions {
            Predicate = c => c.Tags.Contains("live"),
            ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse });
        _app.MapHealthChecks("/health/ready",   new HealthCheckOptions {
            Predicate = c => c.Tags.Contains("ready"),
            ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse });
        _app.MapHealthChecks("/health/startup", new HealthCheckOptions {
            Predicate = c => c.Tags.Contains("startup"),
            ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse });
        await _app.StartAsync(ct);
    }
    public async Task StopAsync(CancellationToken ct) { if (_app is not null) await _app.StopAsync(ct); }
}
```
> **Cross-container caveat (HIGH-impact planning detail):** Health checks resolve from the **inner** `WebApplication`'s DI, NOT the outer host's. The `IStartupGate` is shared by registering the same instance (`AddSingleton(_gate)`). The **MassTransit bus health check is registered in the OUTER host's DI** (by `AddMassTransit`), so it does NOT automatically reach the inner listener's `/health/ready`. Resolution options for `ready`-flips-on-bus-started (CONSOLE-HEALTH-03), in order of cleanliness:
> 1. **Register `AddMassTransit` (or at least its health check) in the inner listener's DI** alongside the endpoints — but the bus must be the SAME bus, so this couples the two containers.
> 2. **Read bus status via a shared singleton:** the supported programmatic read is `IBusHealth.CheckHealth()` (resolve the outer `IBusHealth`/`IBus` and surface it as a custom inner `IHealthCheck` tagged `"ready"`).
> 3. **Run the whole console as one DI container** so the bus check and endpoints share DI (this is what STACK.md's primary-`WebApplication` approach gives for free — but D-04 rejects that).
>
> **This is the single most important unresolved wiring detail for the planner** (Open Question 1). Whichever option is chosen, `/health/ready` must reflect the MT `ready` check, and `/health/live` must remain self-only.

### Pattern 3: Bus-started readiness — MassTransit's own `ready` check (CONSOLE-HEALTH-03)
**What:** `AddMassTransit(...)` auto-registers an `IHealthCheck` tagged **`ready`** + **`masstransit`**. It reports Healthy once the bus has started and connected, Degraded if the broker drops mid-run, Unhealthy on startup failure. No hand-rolled latch.
**Verified detail:** Default tags are exactly `ready` and `masstransit`. If you call `ConfigureHealthCheckOptions(...)` and add custom tags, **custom tags REPLACE the defaults** — you must re-add `ready`+`masstransit` manually. **Recommendation: leave defaults alone** (don't call `ConfigureHealthCheckOptions` this phase). [VERIFIED: Context7 masstransit.massient.com/configuration]
```json
// VERIFIED bus health-check JSON shape (entry "masstransit-bus", tags ["ready","masstransit"])
{ "status": "Healthy", "entries": { "masstransit-bus": { "tags": ["ready","masstransit"], "status": "Healthy" } } }
```
> RabbitMQ posture for the console is the **inverse** of the WebApi: the console's `/health/ready` SHOULD go Unhealthy when the broker drops — that's the MT default, so do nothing special. (The WebApi's soft-on-CRUD `MinimalFailureStatus=Degraded` is a Phase 19 concern, MSG-WEBAPI-04.)

### Pattern 4: Bus-wide filter registration (CORR-01, CORR-02, CONSOLE-04)
**What:** Register all three correlation filters on the **bus factory configurator** so they apply bus-wide. Inbound is non-generic; outbound are open-generic.
**Verified registration surface** (Context7 masstransit.massient.com/guides/middleware):
- Inbound, bus-wide (applies to every receive endpoint via `ConfigureEndpoints`): `cfg.UseConsumeFilter(typeof(InboundCorrelationConsumeFilter), context);` — note a non-generic `IFilter<ConsumeContext>` runs for all messages; the open-generic form `UseConsumeFilter<TFilter>(context)` is for `IFilter<ConsumeContext<T>>`.
- Outbound send: `cfg.UseSendFilter(typeof(OutboundCorrelationSendFilter<>), context);`
- Outbound publish: `cfg.UsePublishFilter(typeof(OutboundCorrelationPublishFilter<>), context);`
- Per-endpoint inbound (`e.UseConsumeFilter(...)`) is **Phase 19** (concrete instance-unique endpoint) — not this phase.
```csharp
// Source: VERIFIED Context7 middleware docs — registration shapes confirmed
services.AddMassTransit(x =>
{
    configureConsumers(x);                            // concrete seam (empty this phase)
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(cfg2HostFromConfig);
        cfg.UseConsumeFilter(typeof(InboundCorrelationConsumeFilter), ctx);     // CORR-01, bus-wide
        cfg.UseSendFilter(typeof(OutboundCorrelationSendFilter<>), ctx);        // CORR-02
        cfg.UsePublishFilter(typeof(OutboundCorrelationPublishFilter<>), ctx);  // CORR-02
        cfg.ConfigureEndpoints(ctx);
    });
});
```
**Ordering (Claude's Discretion):** the inbound consume filter must execute before user consumer code so the AsyncLocal + MEL scope are established. Registering it via `UseConsumeFilter` at the bus level places it in the consume pipeline ahead of the consumer; that is the standard placement.

### Pattern 5: The two filters + accessor (CORR-01, CORR-02, D-01)
```csharp
// Source: pattern from ARCHITECTURE.md §Pattern 5 + CorrelationKeys.cs:7; VERIFIED IFilter<ConsumeContext> surface
public sealed class AsyncLocalCorrelationAccessor : ICorrelationAccessor   // register Singleton
{
    private static readonly AsyncLocal<string?> _current = new();
    public string? Get() => _current.Value;
    public void Set(string? value) => _current.Value = value;
}

public sealed class InboundCorrelationConsumeFilter(
    ICorrelationAccessor accessor, ILogger<InboundCorrelationConsumeFilter> logger)
    : IFilter<ConsumeContext>
{
    public async Task Send(ConsumeContext context, IPipe<ConsumeContext> next)
    {
        var corrId = context.CorrelationId?.ToString()                 // MT envelope
                     ?? Guid.NewGuid().ToString();
        accessor.Set(corrId);
        // CorrelationKeys.LogScope == "CorrelationId" — the SAME literal CorrelationIdMiddleware uses,
        // so OTel IncludeScopes=true serializes ONE Elasticsearch attribute across services.
        using (logger.BeginScope(new Dictionary<string, object> { [CorrelationKeys.LogScope] = corrId }))
            await next.Send(context);
    }
    public void Probe(ProbeContext context) => context.CreateFilterScope("correlation-in");
}

// D-01: stamp the ENVELOPE (context.CorrelationId), gated on ICorrelated — do NOT mutate the record body.
public sealed class OutboundCorrelationSendFilter<T>(ICorrelationAccessor accessor)
    : IFilter<SendContext<T>> where T : class
{
    public Task Send(SendContext<T> context, IPipe<SendContext<T>> next)
    {
        if (context.Message is ICorrelated
            && Guid.TryParse(accessor.Get(), out var id))
            context.CorrelationId = id;                                  // envelope, not body (D-01)
        return next.Send(context);
    }
    public void Probe(ProbeContext context) => context.CreateFilterScope("correlation-out-send");
}
// OutboundCorrelationPublishFilter<T> : IFilter<PublishContext<T>> — identical body against PublishContext.
```
> **Casing is load-bearing.** `CorrelationKeys.LogScope = "CorrelationId"` (PascalCase, `CorrelationKeys.cs:7`) MUST be the scope key. `CorrelationIdMiddleware` uses the identical literal (`CorrelationIdMiddleware.cs:52`, `ItemKey = "CorrelationId"`). Any drift creates a different ES field and silently breaks the cross-service join (Pitfall 3).
>
> **Correlation-id representation note for the planner (Open Question 2):** the HTTP edge stores a 32-char lowercase-hex `Guid.NewGuid().ToString("N")` *string* (`CorrelationIdMiddleware.cs:94`), but inbound values can be **any ASCII string** ≤128 chars (echoed verbatim). The MassTransit envelope `CorrelationId` is a `Guid?`. So the accessor is typed `string?` (preserves arbitrary HTTP ids for the log scope) while the outbound envelope stamp requires `Guid.TryParse`. This phase only proves stamping mechanics via the harness; the Phase 20 E2E proof drives a real HTTP id. The planner should confirm whether the synthetic harness send (Phase 20) uses a Guid-parseable id so `context.CorrelationId` is set — or whether outbound correlation is asserted via a header instead. Flagged, not blocking Phase 18.

### Anti-Patterns to Avoid
- **`app.MapHealthChecks` on the Generic Host:** `MapHealthChecks`/`IEndpointRouteBuilder` is `WebApplication`-only. Use the embedded inner Kestrel (Pattern 2).
- **`.AddSource("MassTransit")` / any `.WithTracing`:** resurrects the removed traces pipeline (Pitfall 1).
- **Hand-rolled "bus started" latch:** duplicates the auto-registered MT `ready` check (Pattern 3).
- **Bus or Redis check under the `live` predicate:** liveness must be self-only (Pitfall 2).
- **Renaming the scope key** (`"correlation_id"`, `"correlationId"`): breaks the single-field ES join.
- **Lifting the *current* `StartupCompletionService`:** it is the Phase-8 migration variant (injects `BaseDbContext`). Build the Phase-5 variant instead.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Bus-started readiness signal | Custom `IHostedService` awaiting `IBusControl.StartAsync` + a second gate | MT auto-registered `ready`+`masstransit` health check | Built-in; also tracks mid-run broker drops (Degraded). [VERIFIED] |
| In-memory filter testing | A fake `IConsumeContext` / manual pipe | `AddMassTransitTestHarness` (`ITestHarness`) | Ships in core MassTransit; substitutes in-memory transport for RabbitMQ — no broker needed. [VERIFIED] |
| Correlation log scope | A custom logging enricher | `ILogger.BeginScope` + `IncludeScopes=true` + the `"CorrelationId"` literal | Already the API's proven mechanism; OTel serializes it with no renaming. |
| Health JSON body | Custom JSON writer | `UIResponseWriter.WriteHealthCheckUIResponse` (AspNetCore.HealthChecks.UI.Client) | Keeps console + API health shape identical. |
| Redis boot safety | Try/catch around connect + retry loop | `abortConnect=false` in the connection string + lazy Singleton | Boot never crashes on dead Redis; the maintainer-blessed pattern. |

**Key insight:** Phase 18 is overwhelmingly *assembly* of existing, validated patterns — the only net-new code is the two filters + accessor + the embedded-listener `IHostedService`. Hand-rolling any of the above re-implements something the framework or the sibling library already provides.

## Common Pitfalls

### Pitfall 1: Traces resurrection (no-traces-backend platform)
**What goes wrong:** A copied `.AddSource("MassTransit")` or any `.WithTracing(...)` re-enables a `TracerProvider`, exporting spans to a Collector with no traces pipeline.
**Why it happens:** Every MassTransit-OTel tutorial leads with tracing; metrics-only is the unusual posture.
**How to avoid:** Console OTel = MEL logs + `.WithMetrics(AddMeter("MassTransit") + AddRuntimeInstrumentation())` only. Never add a tracer provider.
**Warning signs:** A `TracerProvider` resolvable from the console container; Collector "traces pipeline not configured" errors.
**Phase-18 proof:** D-02 fact #4 — assert no `TracerProvider` is resolvable (console analog of the deleted `TraceExportTests`).

### Pitfall 2: `/health/live` accidentally touching the bus/Redis (tag-discipline violation)
**What goes wrong:** Registering the MT/Redis check under `"live"`, or mapping `/health/live` with no predicate, makes a transient broker blip flip liveness → K8s restarts the pod.
**Why it happens:** `AddMassTransit` auto-registers its check; without a constrained predicate it lands in the default set.
**How to avoid:** `/health/live` predicate = `c => c.Tags.Contains("live")` mapping only to the always-Healthy `"self"` check. Bus check stays `"ready"`.
**Warning signs:** `MapHealthChecks("/health/live")` with no `Predicate`; bus/Redis check resolvable under `live`.
**Phase-18 proof:** D-02 fact #2 — `/health/live` = 200 with both Redis and RabbitMQ ports dead.

### Pitfall 3: Correlation scope-key drift / outbound filter omitted
**What goes wrong:** Inbound filter set but outbound forgotten (chain dies at first hop), or the scope key differs from `"CorrelationId"` (different ES field).
**How to avoid:** Implement BOTH filters; use `CorrelationKeys.LogScope` (the shared constant) verbatim; stamp the envelope per D-01.
**Warning signs:** Only inbound registered; logs land under a different field name than the API.
**Phase-18 proof:** D-02 fact #6 — both filters registered AND exercised via `AddMassTransitTestHarness` (inbound: scope/accessor populated; outbound: envelope `CorrelationId` stamped).

### Pitfall 4: Lifting the wrong `StartupCompletionService` / Redis visibility
**What goes wrong:** (a) Copying the current `StartupCompletionService.cs` drags `BaseDbContext`/`MigrateAsync` into a DB-less worker (won't compile / wrong semantics). (b) `RedisServiceCollectionExtensions` is `internal` — a naive copy may keep `internal` and be unreachable from the console's composition chain if split across types.
**How to avoid:** Build the Phase-5 `StartupCompletionService` (MarkReady on StartAsync). Ensure the duplicated Redis extension is reachable from `AddBaseConsole` (same assembly `internal` is fine; cross-assembly invocation needs `public`).
**Warning signs:** `BaseDbContext` reference in the console; `RedisProjectionOptions`/EF types leaking in.

### Pitfall 5: Cross-container health-check resolution (embedded listener)
**What goes wrong:** The inner Kestrel's `/health/ready` does not see the outer host's MT bus check (separate DI containers), so readiness never reflects bus state.
**How to avoid:** Share the gate instance into the inner DI; surface bus status via one of the three options in Pattern 2's caveat. **Decide this explicitly in the plan.**
**Warning signs:** `/health/ready` returns Healthy regardless of bus state, or 500s because the bus check type isn't registered in the inner container.

## Code Examples

All load-bearing code examples are inline in Patterns 1-5 above with source attributions. The registration shapes (`UseConsumeFilter`/`UseSendFilter`/`UsePublishFilter`, `AddMassTransitTestHarness`, `ConfigureHealthCheckOptions` tags) are VERIFIED against Context7 `masstransit.massient.com` this session.

## Runtime State Inventory

Not applicable — Phase 18 is a **greenfield code-only** phase (new library + new tests). No rename/refactor/migration. No stored data, live service config, OS-registered state, secrets/env-var renames, or build artifacts are altered.

- Stored data: None — new library, no datastore keys changed.
- Live service config: None — no broker/service exists this phase (in-memory harness only).
- OS-registered state: None.
- Secrets/env vars: None renamed. New appsettings keys (`ConsoleHealth:Port`, `RabbitMq:*`, `Redis`, `Service:*`) are **read by** the console but supplied per-concrete-console in Phase 19 — no env var renamed.
- Build artifacts: A new `BaseConsole.Core.csproj` is added to `SK_P.sln`; the test project gains a `ProjectReference` to it. No stale artifacts to clean (new project).

## Validation Architecture

**Test framework:** xunit.v3 3.2.2 (`Directory.Packages.props:110`), NSubstitute 5.3.0, in `tests/BaseApi.Tests`.

| Property | Value |
|----------|-------|
| Framework | xunit.v3 3.2.2 |
| Config file | none — xunit.v3 auto-discovers; `[Collection]` attributes serialize shared-resource tests |
| Quick run command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter FullyQualifiedName~Console` |
| Full suite command | `dotnet test` (solution root) |

**The six D-02 proof points → test map:**

| Req | Behavior | Test type | Mechanism | File (Wave 0) |
|-----|----------|-----------|-----------|---------------|
| CONSOLE-01/04 | Host boots through full `AddBaseConsole*` chain | integration | `ConsoleTestHostFixture` builds `Host.CreateApplicationBuilder` + all three Add calls + `Build()`; assert no throw and `IBus` resolvable | `ConsoleTestHostFixture.cs` ❌ |
| CONSOLE-HEALTH-02 | `/health/live` = 200 with Redis + RabbitMQ ports dead | integration | Fixture with dead Redis/RabbitMQ conn strings; HTTP GET inner listener `/health/live`; assert 200 + `"status":"Healthy"` (mirror `HealthEndpointsTests` dead-port pattern) | `ConsoleHealthLiveTests.cs` ❌ |
| CONSOLE-HEALTH-04/D-05 | `/health/startup` flips Healthy after host init | integration | Boot fixture; assert `/health/startup` = 200 after `StartAsync`; negative variant removing `StartupCompletionService` → 503 (mirror `HealthNoStartupCompletionFixture`) | `ConsoleStartupGateTests.cs` ❌ |
| CONSOLE-02 | No `TracerProvider` resolvable | unit/container | `provider.GetService<TracerProvider>()` is null in the console container | `ConsoleObservabilityTests.cs` ❌ |
| CONSOLE-02 | MassTransit meter registered; AspNetCore/HttpClient instrumentation absent | unit/container | Assert `MeterProvider` resolvable; assert no AspNetCore/HttpClient instrumentation services registered (mirror API metrics-shape assertions) | `ConsoleObservabilityTests.cs` ❌ |
| CORR-01/02 | Both filters registered + behavior exercised | unit (harness) | `AddMassTransitTestHarness` with the two outbound filters + a probe consumer; publish an `ICorrelated` test message with ambient accessor set → assert inbound scope/accessor populated and outbound `SendContext.CorrelationId` stamped | `ConsoleCorrelationFilterTests.cs` ❌ |

**Sampling rate:**
- Per task commit: `dotnet test ... --filter FullyQualifiedName~Console` (fast subset, < 30s).
- Per wave merge: full `dotnet test`.
- Phase gate: full suite GREEN 3-consecutive + **dual-SHA** (`psql \l` + `redis-cli --scan`) BEFORE=AFTER (D-03 — NOT triple-SHA; no real broker this phase).

**Wave 0 gaps:**
- [ ] `tests/BaseApi.Tests/.../ConsoleTestHostFixture.cs` — the in-memory Generic-Host fixture (the D-02 vehicle).
- [ ] `tests/BaseApi.Tests/BaseApi.Tests.csproj` — add `<ProjectReference Include="..\..\src\BaseConsole.Core\BaseConsole.Core.csproj" />`.
- [ ] Dead-dependency fixture variants (dead Redis port; dead/absent RabbitMQ) — mirror `HealthDeadRedisFixture` / dead-port patterns from `HealthEndpointsTests.cs`.
- [ ] Harness-based correlation test using `AddMassTransitTestHarness` (no separate NuGet — ships in core MassTransit).
- No new test-framework install needed (xunit.v3 + harness already available).

## Security Domain

> `security_enforcement` is not set to `false` in config.json (absent = enabled). This phase is a library skeleton with no auth surface, no user input handling, and no real network endpoints exposed beyond a localhost health port. ASVS applicability is minimal.

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | No auth in scope (worker base). |
| V3 Session Management | no | No sessions. |
| V4 Access Control | no | No endpoints beyond health probes (no auth required on probes by design). |
| V5 Input Validation | partial | Correlation id from inbound messages: the inbound filter must treat the id as untrusted text. The HTTP-edge already rejects CR/LF/control chars (`CorrelationIdMiddleware.IsValid`). The console reads the **stored** id from L2 (Phase 19) — but the inbound filter should not log unbounded/unsanitized header values. |
| V6 Cryptography | no | No crypto introduced. Never hand-roll any. |
| V7 Error Handling / Logging | yes | Do NOT log full message bodies (could carry sensitive workflow data); log `CorrelationId` + ids only (PITFALLS.md Security). |

### Known Threat Patterns for this stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Log injection via unsanitized correlation id in MEL scope | Tampering / Repudiation | Reuse the API's ASCII-printable/length validation discipline; the id reaching the console is already edge-validated, but treat as untrusted. |
| RabbitMQ default `guest/guest` over non-localhost | Spoofing / EoP | Compose-internal only for dev; real creds + network policy before any non-local deploy (Phase 19+ concern; flagged). |
| Health endpoint information disclosure | Information Disclosure | `UIResponseWriter` body must not leak connection strings/stack traces — mirror the API's `T-05-READY-DB-EXPOSE` assertions (no `Password=`, no stack-trace markers in body). |

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `OpenTelemetry.Instrumentation.MassTransit` NuGet | Built-in OTel (`ActivitySource "MassTransit"` + `Meter` `InstrumentationOptions.MeterName`) | MassTransit 8.0.0 | No instrumentation package; add a meter string, not a NuGet. [CITED: contrib #778] |
| `IHostBuilder` + `ConfigureServices` | `Host.CreateApplicationBuilder` → `HostApplicationBuilder : IHostApplicationBuilder` | .NET 8 | Same `IHostApplicationBuilder` the API's OTel call extends — OTel call lifts verbatim. [CITED: STACK.md] |
| MassTransit Apache-2.0 (all 8.x) | MassTransit v9+ commercial ($400/mo) | v9.1.1, 2026-05-13 | Pinned at 8.5.5; CPM comment guards against accidental bump. |

**Deprecated/outdated:**
- `OpenTelemetry.Instrumentation.MassTransit` — deprecated; do not add.
- Phase-8 migration-variant `StartupCompletionService` — correct for the API, **wrong** for the console (no DB).

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | The non-generic `cfg.UseConsumeFilter(typeof(InboundCorrelationConsumeFilter), ctx)` applies the inbound filter bus-wide ahead of consumers, satisfying CORR-01 "wired bus-wide" with no concrete endpoint this phase. | Pattern 4 | LOW — Context7 confirms `UseConsumeFilter` is a bus-factory-level registration; if a concrete receive endpoint is required for the filter to instantiate, the filter-registration test may need a probe endpoint via the harness (already planned). |
| A2 | Sharing the `IStartupGate` singleton into the inner Kestrel DI, and surfacing the MT bus check via `IBusHealth`/registering MT in the inner DI, is the cleanest way to make `/health/ready` reflect bus state given D-04's two-container design. | Pattern 2 caveat, OQ1 | MEDIUM — exact wiring is the main open design point; whichever option the planner picks must be validated by a `/health/ready` test. |
| A3 | The outbound filter sets `context.CorrelationId` (Guid) only when the ambient id is Guid-parseable; arbitrary HTTP string ids flow through the MEL scope but not the Guid envelope. | Pattern 5, OQ2 | MEDIUM — if Phase 20's synthetic harness asserts envelope `CorrelationId` for a non-Guid id, the filter must also/instead stamp a header. Flag for Phase 19/20 plan; does not block Phase 18 mechanics. |
| A4 | `AddMassTransitTestHarness` uses the in-memory transport and requires no RabbitMQ, satisfying D-02 fact #6 standalone. | Validation Architecture | LOW — VERIFIED via Context7 + STACK.md; harness ships in core package. |

## Open Questions

1. **Embedded health listener — how does the inner Kestrel's `/health/ready` see the outer MassTransit bus health check?**
   - What we know: D-04 mandates an independent `IHostedService`-hosted Kestrel; the MT `ready` check is auto-registered in the OUTER host DI; the inner listener has its own DI.
   - What's unclear: which of the three resolution options (register MT in inner DI / surface `IBusHealth` via a custom inner check / single container) the plan adopts.
   - Recommendation: prefer surfacing the outer `IBusHealth.CheckHealth()` through a small custom inner `IHealthCheck` tagged `"ready"`, OR register the bus health in the inner container — and add a `/health/ready`-reflects-bus-state test. This is the #1 thing for the planner to lock.

2. **Correlation id type: `string` accessor vs `Guid` envelope.**
   - What we know: HTTP edge ids are arbitrary ASCII strings (often 32-hex); MT envelope `CorrelationId` is `Guid?`; D-01 stamps the envelope.
   - What's unclear: whether the Phase 20 synthetic send (and the real E2E) uses Guid-parseable ids so `context.CorrelationId` is set, or whether outbound correlation must also ride a header for non-Guid ids.
   - Recommendation: type the accessor `string?`; in the outbound filter, set `context.CorrelationId` when `Guid.TryParse` succeeds (and optionally also set a `"CorrelationId"` header for non-Guid fidelity). Confirm the harness assertion target in the Phase 18 filter test. Does not block Phase 18.

3. **Redis options binding scope.** `RedisServiceCollectionExtensions` also binds `RedisProjectionOptions`. The console reads L2 in Phase 19 (Orchestrator), not Phase 18.
   - Recommendation: duplicate only the `IConnectionMultiplexer` singleton registration this phase; defer/optionally include the options binding. Confirm whether `BaseConsole.Core` needs `RedisProjectionOptions` (it likely does for Phase 19 reads, but Phase 18's library can register it harmlessly).

## Environment Availability

> Phase 18 is validated entirely with the in-memory MassTransit harness and a dead-port health fixture — **no real external dependencies are required to validate it** (D-02/D-03). The dual-SHA gate (D-03) touches Postgres + Redis, which the existing test infra already provisions.

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK 8.0.421 | Build/test | ✓ (project baseline) | 8.0.x | — |
| MassTransit in-memory harness | Filter validation | ✓ (core package, CPM-pinned 8.5.5) | 8.5.5 | — |
| RabbitMQ broker | NOT required this phase | n/a | — | In-memory harness (no broker) |
| Postgres / Redis (for dual-SHA gate) | D-03 close gate | ✓ (existing Testcontainers/compose dev tier) | per existing | — |

**Missing dependencies with no fallback:** None.
**Missing dependencies with fallback:** RabbitMQ — deliberately not used; in-memory harness is the locked vehicle (D-02).

## Project Constraints (from Directory.Build.props / Directory.Packages.props)

- `TreatWarningsAsErrors=true`, `Nullable=enable`, `ImplicitUsings=enable`, `LangVersion=latest`, `AnalysisMode=latest`, `EnforceCodeStyleInBuild=true` — zero-warning compile enforced. `BaseConsole.Core` must build warning-clean.
- TFM `net8.0` inherited from `Directory.Build.props`; **do not** redeclare or change to `net9.0`.
- CPM (`ManagePackageVersionsCentrally=true`): `<PackageReference>` entries carry NO `Version=`.
- No project-specific `CLAUDE.md` exists at repo root (verified absent this session) — repo conventions come from the Directory.*.props files and the `.planning` decision history.

## Sources

### Primary (HIGH confidence)
- Existing codebase, read directly this session: `src/BaseApi.Core/DependencyInjection/{ObservabilityServiceCollectionExtensions,RedisServiceCollectionExtensions,HealthServiceCollectionExtensions,BaseApiServiceCollectionExtensions,BaseApiApplicationBuilderExtensions}.cs`; `src/BaseApi.Core/Health/{IStartupGate,StartupHealthCheck,StartupCompletionService}.cs`; `src/BaseApi.Core/Middleware/CorrelationIdMiddleware.cs`; `src/Messaging.Contracts/{ICorrelated,CorrelationKeys,Messaging.Contracts.csproj}.cs`; `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs`; `Directory.Build.props`; `Directory.Packages.props`; glob of `src/Messaging.Contracts` (filters/accessor confirmed ABSENT).
- Context7 `/websites/masstransit_massient` (fetched 2026-05-30): `UseConsumeFilter`/`UseSendFilter`/`UsePublishFilter` registration; `IFilter<ConsumeContext<T>>`/`<SendContext<T>>`/`<PublishContext<T>>`; `AddMassTransitTestHarness`/`ITestHarness`; auto-registered `ready`+`masstransit` bus health check + `ConfigureHealthCheckOptions`/`MinimalFailureStatus` + JSON shape; bus-wide `UseConsumeFilter` via `ConfigureEndpoints`.
- `.planning/CONTEXT.md` (D-01..D-09, authoritative); `.planning/REQUIREMENTS.md` (CONSOLE-*/CORR-*).

### Secondary (MEDIUM confidence)
- `.planning/research/{STACK,PITFALLS,SUMMARY,ARCHITECTURE}.md` — project research (HIGH per their own assessment; treated as CITED for version/licensing facts verified there on nuget.org 2026-05-30). Note: their placement of filters in `Messaging.Contracts` is corrected here against the verified POCO-only csproj.

### Tertiary (LOW confidence)
- None relied upon.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all versions CPM-pinned and verified; MassTransit/OTel/AspNetCore.App surfaces confirmed.
- Architecture / mirror map: HIGH — source read directly; filter/health/harness APIs confirmed via Context7.
- Pitfalls: HIGH — drawn from PITFALLS.md + verified against the actual locked posture.
- Open wiring detail (inner-Kestrel ↔ bus health): MEDIUM — three valid options; planner must lock one (OQ1).

**Research date:** 2026-05-30
**Valid until:** ~2026-06-30 (stable stack; MassTransit 8.x patched through end-2026; revisit only if the TFM or MassTransit pin changes).
