# Architecture Research — v3.4.0 BaseConsole + Orchestrator Messaging

**Domain:** Reusable .NET 8 Generic-Host console base (`BaseConsole.Core`) + MassTransit/RabbitMQ integration into an existing modular-monolith web API, with a shared `Messaging.Contracts` assembly and automatic CorrelationId propagation.
**Researched:** 2026-05-30
**Confidence:** HIGH (existing codebase read directly; MassTransit filter/health-check/topology behavior verified via Context7 + official docs)

> Scope note: this mirrors the *existing* `BaseApi.Core` / `BaseApi.Service` seam, which was read directly from source — not re-researched. All "mirror" claims below cite the concrete file that establishes the pattern.

---

## Standard Architecture

### System Overview

```
┌───────────────────────────────────────────────────────────────────────────┐
│  Messaging.Contracts  (NEW shared assembly — no host dependency)            │
│  ┌────────────────┐ ┌──────────────────┐ ┌────────────────────────────┐    │
│  │ Control records│ │ ICorrelated +    │ │ Correlation machinery:     │    │
│  │ StartOrch /    │ │ field vocabulary │ │  InboundCorrelationFilter  │    │
│  │ StopOrch       │ │ {CorrelationId.. │ │  OutboundCorrelationFilter │    │
│  │ {WorkflowIds[]}│ │  ExecutionId..}  │ │  ICorrelationAccessor      │    │
│  │                │ │                  │ │  (AsyncLocal impl)         │    │
│  └────────────────┘ └──────────────────┘ └────────────────────────────┘    │
│  ┌──────────────────────────────────────────────────────────────────────┐  │
│  │ WorkflowRootProjectionContract (L2 read-side shape — moved from        │  │
│  │ BaseApi.Service; WebApi WRITES it, Orchestrator READS it)              │  │
│  └──────────────────────────────────────────────────────────────────────┘  │
└────────────┬──────────────────────────────────────────────┬────────────────┘
   referenced by                                     referenced by
             │                                                │
┌────────────▼─────────────────────┐         ┌────────────────▼────────────────┐
│  BaseApi.Service (web API)        │         │  BaseConsole.Core (NEW library)  │
│  WebApplication.CreateBuilder     │         │  Host.CreateApplicationBuilder*  │
│  + AddBaseApi (existing 7-chain)  │  RabbitMQ│  AddBaseConsole / RunAsync       │
│  + NEW AddBaseApiMessaging        │◄────────►│  ├ AddBaseConsoleObservability   │
│    (joins bus, PUBLISH only,      │ fan-out  │  ├ AddBaseConsoleRedis (lifted)  │
│     fan-out to instance queues)   │ exchange │  ├ AddBaseConsoleHealth (embedded│
│  Start/Stop endpoint → publish    │          │  │    Kestrel + IStartupGate)   │
│  RabbitMQ HARD dep on Start/Stop  │          │  └ AddBaseConsoleMessaging       │
│  path only (F4)                   │          │       (bus skeleton + filters)   │
└──────────────┬────────────────────┘         └────────────────┬─────────────────┘
               │                                                │ inherited by
               ▼                                                ▼
┌──────────────────────────────┐              ┌──────────────────────────────────┐
│ Postgres 17 / Redis 7.4 (L2)  │              │  Orchestrator (NEW console)       │
│ WebApi writes L2 root w/       │   reads L2   │  thin shell (mirror of entity     │
│ stored X-Correlation-Id        │◄─────────────┤  feature folders): registers      │
└──────────────────────────────┘              │  StartConsumer/StopConsumer +     │
                                               │  instance-unique receive endpoint │
                                               └──────────────────────────────────┘
*Host.CreateApplicationBuilder vs WebApplication-as-host — decision below (Pattern 2).
```

### Component Responsibilities

| Component | Responsibility | Layer (base / shared / concrete) |
|-----------|----------------|----------------------------------|
| `Messaging.Contracts` | Wire contracts, `ICorrelated` vocabulary, both correlation filters, `ICorrelationAccessor` (AsyncLocal), L2 read-side record | **shared** (referenced by both hosts; depends only on MassTransit abstractions + `Microsoft.Extensions.Logging.Abstractions`) |
| `BaseConsole.Core` | Generic-Host bootstrap, OTel (console flavor), Redis client, embedded-Kestrel health probes, MassTransit bus skeleton | **base library** (mirror of `BaseApi.Core`) |
| `Orchestrator` | Consumers + instance-unique receive endpoint wiring; reads L2; establishes correlated log scope; logs to scheduler seam | **concrete console** (mirror of entity feature folders + `AddAppFeatures`) |
| WebApi `AddBaseApiMessaging` | Joins bus as a **publisher only**; outbound correlation filters; no consumers, no receive endpoints | **base-library addition to `BaseApi.Core`** |

---

## Recommended Project Structure

```
src/
├── Messaging.Contracts/                 # NEW shared assembly (build FIRST)
│   ├── Contracts/
│   │   ├── StartOrchestration.cs        # record { Guid[] WorkflowIds } — NO correlationId on wire
│   │   └── StopOrchestration.cs         # record { Guid[] WorkflowIds }
│   ├── Correlation/
│   │   ├── ICorrelated.cs               # mandatory-field vocabulary interface
│   │   ├── ICorrelationAccessor.cs      # ambient read/write contract
│   │   ├── AsyncLocalCorrelationAccessor.cs   # AsyncLocal<string?> impl (Singleton-safe)
│   │   ├── InboundCorrelationConsumeFilter.cs # IFilter<ConsumeContext> (non-generic, all msgs)
│   │   ├── OutboundCorrelationSendFilter.cs    # IFilter<SendContext<T>>  where T : class
│   │   └── OutboundCorrelationPublishFilter.cs # IFilter<PublishContext<T>> where T : class
│   └── Projection/
│       └── WorkflowRootProjectionContract.cs   # MOVED from BaseApi.Service (+ Liveness)
│
├── BaseConsole.Core/                    # NEW reusable library (build SECOND)
│   ├── DependencyInjection/
│   │   ├── BaseConsoleServiceCollectionExtensions.cs   # AddBaseConsole (chain)  ← mirror AddBaseApi
│   │   ├── BaseConsoleObservabilityExtensions.cs       # on IHostApplicationBuilder ← mirror AddBaseApiObservability
│   │   ├── ConsoleRedisServiceCollectionExtensions.cs  # lifted from BaseApi.Core
│   │   ├── ConsoleHealthServiceCollectionExtensions.cs # IStartupGate + checks + Kestrel hosted svc
│   │   └── MessagingServiceCollectionExtensions.cs     # AddBaseConsoleMessaging(cfg, configureConsumers)
│   ├── Health/
│   │   ├── EmbeddedHealthEndpointService.cs   # IHostedService hosting a minimal Kestrel app
│   │   └── (IStartupGate / StartupHealthCheck — see "lift vs duplicate" decision)
│   └── Hosting/
│       └── BaseConsoleHost.cs            # static RunAsync(args, configure) entry (the ~7-line mirror)
│
└── Orchestrator/                        # NEW concrete console (build THIRD, parallel w/ WebApi wiring)
    ├── Program.cs                        # ~7 lines — mirror of BaseApi.Service/Program.cs
    ├── AppMessaging.cs                   # AddAppConsumers() ← mirror of AddAppFeatures()
    └── Consumers/
        ├── StartOrchestrationConsumer.cs
        └── StopOrchestrationConsumer.cs

(modified) src/BaseApi.Core/DependencyInjection/
    └── MessagingServiceCollectionExtensions.cs  # NEW AddBaseApiMessaging (publish-only) — call #8
```

### Structure Rationale

- **`Messaging.Contracts` has zero host dependency.** It references only `MassTransit` (for `ConsumeContext`/`SendContext`/`IFilter<>`) and `Microsoft.Extensions.Logging.Abstractions` (for the `BeginScope` reuse). This is what lets the web API reference it **without taking a console-host dependency** (the milestone's explicit constraint). Filters are framework abstractions, not host code.
- **`BaseConsole.Core` mirrors `BaseApi.Core` one-for-one.** `AddBaseConsole` chains sub-extensions on `IServiceCollection` exactly like `AddBaseApi<TDbContext>` chains its 7 (`BaseApiServiceCollectionExtensions.cs:24`). Observability stays a *separate* call on `IHostApplicationBuilder` for the identical reason the API does it — `builder.Logging.AddOpenTelemetry` needs `ILoggingBuilder`, which `IServiceCollection` does not expose (`ObservabilityServiceCollectionExtensions.cs:37`).
- **`Orchestrator` is a thin shell** the same way the 5 entity feature folders are: it adds nothing but its own consumers via an `AddAppConsumers()` aggregator (mirror of `AddAppFeatures()` — `Program.cs:8`).

---

## Architectural Patterns

### Pattern 1: `AddBaseConsole` / `RunAsync` — the composition-root mirror

**What:** A static `BaseConsoleHost.RunAsync` that owns the host builder and exposes a thin configure seam, plus an `AddBaseConsole(cfg, configureConsumers)` chain mirroring `AddBaseApi`. Concrete `Orchestrator/Program.cs` stays ~7 lines.

**When:** Every console built on this base.

**Trade-offs:** Centralizing the builder in a static `RunAsync` (vs. exposing `AddBaseConsole`/`UseBaseConsole` as three separate calls like the API) trades a little flexibility for a smaller concrete `Program.cs`. Recommended: expose **both** — `AddBaseConsole` for DI parity and a `RunAsync` convenience wrapper — so the concrete passes its consumer registration as a lambda.

**Example (concrete `Orchestrator/Program.cs` — the ~7-line target):**
```csharp
using BaseConsole.Core.Hosting;
using Orchestrator;

await BaseConsoleHost.RunAsync(args, x =>           // x = IBusRegistrationConfigurator
{
    x.AddConsumer<StartOrchestrationConsumer>();
    x.AddConsumer<StopOrchestrationConsumer>();
});
```
```csharp
// BaseConsole.Core/Hosting/BaseConsoleHost.cs — mirror of the WebApplication path
public static class BaseConsoleHost
{
    public static async Task RunAsync(string[] args, Action<IBusRegistrationConfigurator> configureConsumers)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddBaseConsoleObservability(builder.Configuration);          // IHostApplicationBuilder (ILoggingBuilder)
        builder.Services.AddBaseConsole(builder.Configuration, configureConsumers);
        await builder.Build().RunAsync();
    }
}
```

### Pattern 2: Host choice — `Host.CreateApplicationBuilder` + embedded Kestrel hosted service (F2)

**What:** The Generic Host has **no** `MapHealthChecks` (that is `IEndpointRouteBuilder`, a `WebApplication`-only surface). To serve `/health/live|ready|startup` over HTTP from a pure Generic Host, run a **second, minimal `WebApplication`/Kestrel listener inside an `IHostedService`** bound to a dedicated health port. The hosted service builds its own `WebApplication.CreateBuilder`, calls `MapHealthChecks` with the exact same tag predicates as `BaseApiApplicationBuilderExtensions.cs:46-60`.

**Decision (HIGH confidence): use the embedded-Kestrel-in-a-hosted-service approach (locked decision F2), not `WebApplication` as the primary host.** Rationale: the milestone wants a *console* (Generic Host) whose primary lifecycle is the MassTransit bus, with health as a side-channel. An embedded listener keeps the health surface isolated on its own port and keeps the host a worker, matching F2's "minimal Kestrel listener" wording. The alternative (make the whole console a `WebApplication`) is simpler to wire but blurs "console" vs "web app" and pulls full ASP.NET Core instrumentation into the OTel pipeline, which the milestone explicitly excludes ("no AspNetCore instrumentation," PROJECT.md:19).

**Trade-offs / wiring caveat:** Health checks must be registered in the **inner** Kestrel listener's own service collection — they cannot be resolved cross-container. The shared `IStartupGate` singleton and the MassTransit bus health status must be reachable from the inner checks. Cleanest resolution: register the **same** `IStartupGate` instance into the inner listener's container (`b.Services.AddSingleton(theSharedGateInstance)`), and surface the bus status either by keeping the bus health in the inner container or via a small shared accessor (see Pattern 3).

**Example (embedded Kestrel health host):**
```csharp
internal sealed class EmbeddedHealthEndpointService(IStartupGate gate, IConfiguration cfg) : IHostedService
{
    private WebApplication? _app;

    public async Task StartAsync(CancellationToken ct)
    {
        var b = WebApplication.CreateBuilder();
        b.WebHost.UseUrls(cfg["Health:Url"] ?? "http://0.0.0.0:8081");
        b.Services.AddSingleton(gate);                                  // share the outer gate instance
        b.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
            .AddCheck<StartupHealthCheck>("startup", tags: ["startup", "ready"]);
        // + the MassTransit "ready" bus check reaches /health/ready (Pattern 3)
        _app = b.Build();
        _app.MapHealthChecks("/health/live",    new() { Predicate = c => c.Tags.Contains("live") });
        _app.MapHealthChecks("/health/ready",   new() { Predicate = c => c.Tags.Contains("ready") });
        _app.MapHealthChecks("/health/startup", new() { Predicate = c => c.Tags.Contains("startup") });
        await _app.StartAsync(ct);
    }
    public async Task StopAsync(CancellationToken ct) { if (_app is not null) await _app.StopAsync(ct); }
}
```

> Tag discipline is identical to the web API (`live` never touches bus/DB — Pitfall 15 precedent). `self` is the only `live` check; `startup`+`ready` carry the startup gate; the bus check carries `ready`.

### Pattern 3: Bus-started readiness — MassTransit's own health check IS the signal (F3)

**What:** `AddMassTransit(...)` **automatically registers an `IHealthCheck`** into the service collection, tagged `ready` + `masstransit` by default. It reports **Healthy once the bus has started and is connected to the broker**, **Degraded** if the broker connection drops while running, and **Unhealthy** on a startup failure. This is exactly the "ready flips when the bus has started" mechanism (F3) — **no custom bus-started hosted service is needed.**

**When:** The console's `/health/ready` endpoint.

**Mechanism / wiring (HIGH confidence, verified via MassTransit docs):**
- Default tags are `ready` and `masstransit`. If you call `ConfigureHealthCheckOptions(...)` to add custom tags, **you must re-add `ready` and `masstransit` manually** — custom tags *replace* the defaults. Recommendation: leave defaults alone.
- Because the bus health check is registered by `AddMassTransit` in whichever container hosts the bus, and the health *endpoints* live in the inner Kestrel listener, the inner listener must surface that status. Cleanest: register `AddMassTransit` in the **inner** listener's DI alongside the endpoints (the bus check then flows straight into `/health/ready`). If you keep a hard container split, expose the bus status via a shared singleton the inner check reads (the supported programmatic read is `IBusHealth.CheckHealth()`).

**`StartupGate` interplay:** keep the existing `IStartupGate` semantics — `/health/startup` flips Healthy when host init finishes (Redis multiplexer constructed, consumers registered). `/health/ready` is the **AND** of `startup` + the MassTransit bus check (both tagged `ready`), so readiness is true only when *both* startup completed **and** the bus connected. This mirrors the web API's `ready` = startup gate + Npgsql (`HealthServiceCollectionExtensions.cs:21-26`).

**Trade-offs:** Relying on the built-in check (vs. a hand-rolled `IBusControl.StartAsync` latch) is less code and tracks runtime broker drops (Degraded), not just the one-time start. Set `MinimalFailureStatus` deliberately (default: `Unhealthy` on startup problems, `Degraded` on mid-run drops).

**Example (the base messaging seam):**
```csharp
services.AddMassTransit(x =>
{
    configureConsumers(x);                          // concrete adds its consumers (the seam)
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(rabbitConnString);
        cfg.UsePublishFilter(typeof(OutboundCorrelationPublishFilter<>), ctx);   // base-owned, bus-wide
        cfg.UseSendFilter(typeof(OutboundCorrelationSendFilter<>), ctx);         // base-owned, bus-wide
        configureEndpoints(ctx, cfg);               // concrete adds instance-unique receive endpoints
    });
});
```

### Pattern 4: Base bus skeleton vs concrete consumers — the `AddBaseApi` ÷ `AddAppFeatures` seam

**What:** `AddBaseConsoleMessaging(cfg, configureConsumers, configureEndpoints)` owns everything generic: `AddMassTransit`, `UsingRabbitMq`, host connection, the two **outbound** correlation filters (bus-wide), and the bus health check. The **concrete** `Orchestrator` supplies (a) `x.AddConsumer<...>()` registrations and (b) the instance-unique receive endpoint with the **inbound** consume filter applied per-endpoint.

**Where the seam is (mirror):**
- Base = `AddBaseApi<TDbContext>` (infrastructure chain, `BaseApiServiceCollectionExtensions.cs:24`).
- Concrete = `AddAppFeatures()` (entity-specific registrations, `Program.cs:8`).
- For messaging: Base = `AddBaseConsoleMessaging`; Concrete = the `configureConsumers`/`configureEndpoints` lambdas passed through.

**Why filters split base-outbound vs concrete-endpoint-inbound:** outbound `UseSendFilter`/`UsePublishFilter` are bus-wide and belong to the base (every message any console sends must be stamped). Inbound `UseConsumeFilter` is registered **per receive endpoint** (`e.UseConsumeFilter(...)`, verified via MassTransit middleware docs) — and the endpoint is concrete-owned (instance-unique name). So the base *provides* the filter type from `Messaging.Contracts`, and the concrete's endpoint configuration *applies* it. To keep the concrete thin, `AddBaseConsoleMessaging` can wrap the endpoint helper so the concrete just names its consumers and the base attaches the inbound filter + instance-unique queue name.

**Instance-unique queue (topology fan-out):** WebApi `Publish`es the control message; MassTransit's default fanout exchange delivers a copy to **every bound receive-endpoint queue**. To get fan-out (not competing consumers), each Orchestrator replica must bind a **distinct** queue name, e.g. `cfg.ReceiveEndpoint($"orchestrator-{NewId.NextGuid():N}", e => ...)` or an `IEndpointNameFormatter` with an instance suffix. Declare it **auto-delete/non-durable** so replicas don't leak queues on restart (verified: MassTransit creates durable queues by default; an instance-unique fan-out queue should be temporary).

### Pattern 5: Correlation propagation — inbound consume filter + outbound send/publish + AsyncLocal

**What:** Three filters in `Messaging.Contracts`, plus an `ICorrelationAccessor` backed by `AsyncLocal<string?>`:
- **Inbound** `IFilter<ConsumeContext>` (non-generic — runs for *all* consumed messages): reads `ICorrelated.CorrelationId` / the MT header, writes it to the `AsyncLocal` accessor, **and** opens a MEL log scope keyed `"CorrelationId"` — the *literal same key* the web API uses (`CorrelationIdMiddleware.cs:52`, `ItemKey = "CorrelationId"`). Because OTel runs with `IncludeScopes = true` (`ObservabilityServiceCollectionExtensions.cs:51`), that scope key serializes as a log attribute named `CorrelationId` with **no renaming** — so orchestrator logs land in Elasticsearch under the same field the API uses, completing the HTTP→Redis→message→log trace.
- **Outbound** `IFilter<SendContext<T>>` + `IFilter<PublishContext<T>>` (generic, `where T : class`): reads the ambient correlationId from the accessor and stamps it onto every `ICorrelated` message (and/or the MT header) so downstream hops inherit it. Exercised this milestone via the synthetic harness send (locked decision).

**Log-scope reuse (critical, HIGH confidence):** the inbound filter MUST call `logger.BeginScope(new Dictionary<string,object>{ ["CorrelationId"] = corrId })` — the **PascalCase literal**, matching `CorrelationIdMiddleware`. Any other casing produces a *different* OTel attribute and breaks the single-field correlation contract.

**Example (inbound filter — the load-bearing scope reuse):**
```csharp
public sealed class InboundCorrelationConsumeFilter(
    ICorrelationAccessor accessor, ILogger<InboundCorrelationConsumeFilter> logger)
    : IFilter<ConsumeContext>
{
    public async Task Send(ConsumeContext context, IPipe<ConsumeContext> next)
    {
        var corrId = context.CorrelationId?.ToString("N")             // MT envelope
                     ?? context.Headers.Get<string>("CorrelationId")  // explicit header
                     ?? Guid.NewGuid().ToString("N");
        accessor.Set(corrId);
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = corrId }))
            await next.Send(context);                                  // SAME key as CorrelationIdMiddleware
    }
    public void Probe(ProbeContext context) => context.CreateFilterScope("correlation");
}
```
```csharp
public sealed class AsyncLocalCorrelationAccessor : ICorrelationAccessor   // register Singleton
{
    private static readonly AsyncLocal<string?> _current = new();
    public string? Get() => _current.Value;
    public void Set(string? value) => _current.Value = value;
}
```

> The bus-world `Guid CorrelationId` (minted by the future Quartz scheduler) and the HTTP `X-Correlation-Id` string **do not unify** (PROJECT.md:28). This milestone's deliverable is that the Orchestrator reads the **stored** `X-Correlation-Id` out of the L2 root (the `WorkflowRootProjectionContract.CorrelationId` field) and opens the log scope from *that* — linking the two worlds via logs, exactly as locked.

### Pattern 6: WebApi joins the bus as publisher-only (F4)

**What:** A new `AddBaseApiMessaging(cfg)` in `BaseApi.Core` (composition call #8 in `Program.cs`, after the existing chain) calls `AddMassTransit` with **no consumers and no receive endpoints** — only `UsingRabbitMq` host config + the two outbound filters. The WebApi's `OrchestrationController` Start/Stop path resolves `IPublishEndpoint` (or `IBus`) and `Publish`es `StartOrchestration`/`StopOrchestration`. RabbitMQ becomes a **hard dependency for the Start/Stop path only**; CRUD is unaffected because CRUD never touches the bus.

**Why no console-host dependency leaks in:** the WebApi references `Messaging.Contracts` (records + filters + accessor) and the `MassTransit`/`MassTransit.RabbitMQ` NuGet — never `BaseConsole.Core` or `Orchestrator`. The bus client is symmetric across both hosts because the *only* shared code is contracts/filters, which are host-agnostic.

**Health impact (flag for requirements):** because the bus is now part of WebApi startup, MassTransit's `ready`-tagged health check joins the API's `/health/ready` set automatically. This would make `/health/ready` go unhealthy if RabbitMQ is down — even though CRUD still works. To preserve the existing CRUD-availability contract (Redis soft-dep precedent, Key Decisions table), either set the WebApi bus check's `MinimalFailureStatus = Degraded` or re-tag it off `ready`, so CRUD readiness does not flip on a broker outage. RabbitMQ is hard *for the Start/Stop endpoint*, not necessarily for the readiness probe — this is a requirements decision to lock.

---

## Data Flow

### Control flow (Start) — end-to-end correlation

```
POST /api/v1/orchestration/start  (X-Correlation-Id: abc...)
    ↓  CorrelationIdMiddleware stashes "abc" in HttpContext.Items["CorrelationId"]   [existing]
OrchestrationService builds L1 → writes L2 root  (root.correlationId = "abc")        [existing]
    ↓  OrchestrationController.Start → IPublishEndpoint.Publish(StartOrchestration{WorkflowIds})
    ↓     OutboundCorrelationPublishFilter stamps ambient corrId onto envelope        [NEW]
RabbitMQ default fanout exchange
    ↓  copy → orchestrator-{instanceA} queue   (+ {instanceB}.. for N replicas)
Orchestrator StartOrchestrationConsumer
    ↓  InboundCorrelationConsumeFilter: corrId → AsyncLocal + BeginScope("CorrelationId")  [NEW]
    ↓  per WorkflowId: GET L2 root → read WorkflowRootProjectionContract.CorrelationId ("abc")
    ↓  re-establish log scope from the STORED "abc"  → log "scheduler job start" seam
    ↓  ack-on-success
Elasticsearch: orchestrator log line carries CorrelationId="abc"  (same field as the API)
```

### Read-side contract move

```
BEFORE: BaseApi.Service.Features.Orchestration.Projection.WorkflowRootProjection  (internal sealed record)
            └ written by RedisProjectionWriter.UpsertAsync (RedisProjectionWriter.cs:66)

AFTER:  Messaging.Contracts.Projection.WorkflowRootProjectionContract  (public sealed record)
            ├ WebApi RedisProjectionWriter writes it  (1 using-swap + visibility public)
            └ Orchestrator consumer deserializes it from the L2 GET
```
The record's `[property: JsonPropertyName(...)]` targets are **load-bearing** (`WorkflowRootProjection.cs:9` / `LivenessProjection.cs:6`) — they must travel with the record into `Messaging.Contracts` so the camelCase wire shape is byte-identical on both write and read sides. `LivenessProjection` is nested inside the root, so it moves too (or the contract carries its own copy).

---

## Integration Points

### External Services

| Service | Integration Pattern | Notes |
|---------|---------------------|-------|
| RabbitMQ | MassTransit `UsingRabbitMq`; default durable fanout exchange per message type; instance-unique auto-delete receive queues for fan-out | NEW compose tier. Hard dep on WebApi Start/Stop path + the whole Orchestrator lifecycle. Default MT queues are durable — make the instance-unique fan-out queue **temporary/auto-delete** so replicas don't leak queues. |
| Redis (L2) | `IConnectionMultiplexer` singleton lifted from `BaseApi.Core` `AddBaseApiRedis` (`RedisServiceCollectionExtensions.cs:57`) | Orchestrator is **read-only** to L2 this milestone (no writes, no Quartz). `abortConnect=false` connection-string contract carries over. |
| OTel Collector | `AddBaseConsoleObservability` on `IHostApplicationBuilder` — MEL-bridge logs + runtime + **MassTransit instrumentation**, **no** AspNetCore instrumentation | Mirror of `ObservabilityServiceCollectionExtensions.cs` minus AspNetCore. `IncludeScopes=true` mandatory for the correlation field. |

### Internal Boundaries

| Boundary | Communication | Notes |
|----------|---------------|-------|
| WebApi ↔ Orchestrator | Async messages over RabbitMQ (`StartOrchestration`/`StopOrchestration`) | Contracts in `Messaging.Contracts`; no shared host code. |
| WebApi ↔ Orchestrator (state) | Redis L2 root (`WorkflowRootProjectionContract`) | WebApi writes, Orchestrator reads — one source of truth in the shared assembly. |
| `Messaging.Contracts` ↔ both hosts | Project reference (assembly), not deployment coupling | Depends only on `MassTransit` + `Logging.Abstractions` — no host packages. |
| `BaseConsole.Core` ↔ `Orchestrator` | Inheritance/composition (`AddBaseConsole` + `configureConsumers` lambda) | Mirror of `AddBaseApi` + `AddAppFeatures`. |

---

## Anti-Patterns

### Anti-Pattern 1: Using `MapHealthChecks` directly on the Generic Host
**What people do:** Call `app.MapHealthChecks(...)` expecting it to work in `Host.CreateApplicationBuilder`.
**Why it's wrong:** `MapHealthChecks`/`IEndpointRouteBuilder` is a `WebApplication`-only surface; the Generic Host has no HTTP pipeline. It won't compile/route.
**Do this instead:** Run an embedded minimal Kestrel `WebApplication` inside an `IHostedService` (Pattern 2, locked decision F2), re-using the tag predicates verbatim from `BaseApiApplicationBuilderExtensions.cs`.

### Anti-Pattern 2: Hand-rolling a "bus started" latch
**What people do:** Add a custom `IHostedService` that awaits `IBusControl.StartAsync` and flips a second gate for readiness.
**Why it's wrong:** Duplicates a built-in. `AddMassTransit` already registers a `ready`-tagged health check that reports Healthy on connect and Degraded on drop (verified). The custom latch also misses mid-run broker drops.
**Do this instead:** Let the MassTransit `ready` health check be the bus-started signal (Pattern 3, F3). Keep `IStartupGate` only for host-init (Redis/consumer registration).

### Anti-Pattern 3: Renaming the correlation log-scope key in the console
**What people do:** Use `"correlation_id"` or `"correlationId"` in the consumer's `BeginScope`.
**Why it's wrong:** OTel `IncludeScopes=true` serializes the scope key verbatim. A different key creates a *different* ES field and the HTTP-side and console-side logs no longer correlate on one field.
**Do this instead:** Reuse the literal `"CorrelationId"` (PascalCase) from `CorrelationIdMiddleware.cs:52` in the inbound filter (Pattern 5).

### Anti-Pattern 4: Competing-consumer queue for fan-out
**What people do:** Give every replica the same receive-endpoint queue name.
**Why it's wrong:** RabbitMQ load-balances one copy across replicas (competing consumers) — only one Orchestrator sees each Start. The topology requires **all** replicas to react (fan-out).
**Do this instead:** Instance-unique, auto-delete receive endpoint per replica (Pattern 4) so the fanout exchange delivers a copy to each.

### Anti-Pattern 5: WebApi referencing `BaseConsole.Core` or `Orchestrator` to publish
**What people do:** Pull console host code into the API to reuse bus wiring.
**Why it's wrong:** Couples the web host to a console host; violates the milestone constraint and bloats the API.
**Do this instead:** WebApi references only `Messaging.Contracts` + the `MassTransit`/`MassTransit.RabbitMQ` NuGet, and adds its own publish-only `AddBaseApiMessaging` (Pattern 6).

---

## New vs Modified Components

### New
| Item | Assembly | Notes |
|------|----------|-------|
| `Messaging.Contracts` project | (new) | contracts + `ICorrelated` + 3 filters + `ICorrelationAccessor`/`AsyncLocal` impl + L2 read record |
| `BaseConsole.Core` project | (new) | `AddBaseConsole`, `RunAsync`, console-flavored observability, Redis (lifted), embedded-Kestrel health, `AddBaseConsoleMessaging` |
| `Orchestrator` project | (new) | thin shell: `Program.cs` + `AddAppConsumers` + 2 consumers |
| `AddBaseApiMessaging` | BaseApi.Core (new file) | publish-only bus join for the web API |
| RabbitMQ compose tier | compose.yaml | new healthy service dependency |

### Modified
| Item | Change |
|------|--------|
| `BaseApi.Service/Program.cs` | + `builder.Services.AddBaseApiMessaging(builder.Configuration);` (composition call #8) |
| `BaseApi.Service` `RedisProjectionWriter.cs` | swap `WorkflowRootProjection` → `Messaging.Contracts.WorkflowRootProjectionContract` (using-swap; record made `public`) |
| `OrchestrationController` (Start/Stop) | inject `IPublishEndpoint`; `Publish` the control contract after the existing L2 write/gate |
| `WorkflowRootProjection.cs` + `LivenessProjection.cs` | **moved** out of `BaseApi.Service` into `Messaging.Contracts` (delete from Service) |
| `BaseApi.Core` OTel/health | (optionally) re-tag the bus health check off `ready` or set `MinimalFailureStatus=Degraded` so RabbitMQ-down does not flip CRUD `/health/ready` (requirements decision) |

### Decision: lift-or-duplicate `IStartupGate` / `StartupHealthCheck`
`IStartupGate`, `StartupGate`, `StartupHealthCheck` currently live in `BaseApi.Core/Health/`. Two options:
1. **Lift into a shared `Hosting.Abstractions` assembly** both base libraries reference (cleanest; avoids a `BaseConsole.Core → BaseApi.Core` dependency).
2. **Duplicate** the three tiny files into `BaseConsole.Core` (pragmatic; gate ~30 LOC, check ~10 LOC).
**Recommendation:** Option 2 (duplicate) this milestone — the types are trivial and a `BaseConsole.Core → BaseApi.Core` dependency would drag EF Core / ASP.NET MVC transitively into a console. Re-evaluate extracting a `Hosting.Abstractions` assembly if a third host appears.

---

## Suggested Build Order (dependency-respecting)

```
1. Messaging.Contracts                    (no deps on the new libs)
   1a. StartOrchestration / StopOrchestration records
   1b. ICorrelated + field vocabulary
   1c. ICorrelationAccessor + AsyncLocalCorrelationAccessor
   1d. Inbound + Outbound correlation filters (MassTransit abstractions only)
   1e. MOVE WorkflowRootProjectionContract (+ Liveness) from BaseApi.Service

2. BaseConsole.Core                        (refs: Messaging.Contracts, MassTransit.RabbitMQ, OTel, SE.Redis)
   2a. (duplicate) IStartupGate/StartupGate/StartupHealthCheck
   2b. ConsoleRedis extension (lift AddBaseApiRedis)
   2c. AddBaseConsoleObservability (console flavor: + MassTransit instr, − AspNetCore)
   2d. EmbeddedHealthEndpointService (Kestrel-in-hosted-service) + ConsoleHealth extension
   2e. AddBaseConsoleMessaging(cfg, configureConsumers, configureEndpoints) + outbound filters wired
   2f. AddBaseConsole chain + BaseConsoleHost.RunAsync

3a. Orchestrator                           (refs: BaseConsole.Core, Messaging.Contracts)   ─┐ parallel
   3a1. StartOrchestrationConsumer / StopOrchestrationConsumer (read L2, scope, log seam)   │
   3a2. AppMessaging.AddAppConsumers + instance-unique receive endpoint                     │
   3a3. Program.cs (~7 lines)                                                               │
                                                                                            │
3b. WebApi bus wiring                      (refs: Messaging.Contracts, MassTransit.RabbitMQ)─┘ parallel
   3b1. AddBaseApiMessaging (publish-only) in BaseApi.Core
   3b2. Program.cs call #8
   3b3. RedisProjectionWriter using-swap to the moved contract
   3b4. OrchestrationController Start/Stop → Publish

4. RabbitMQ compose tier + appsettings (Health:Url, RabbitMq conn) for both hosts
5. Synthetic outbound-filter harness test (locked: outbound exercised via harness send)
```

**Why this order:** `Messaging.Contracts` is the leaf both hosts depend on, so it builds first (and the read-side record move must precede the WebApi writer swap). `BaseConsole.Core` depends on the contracts (for filters) and builds second. `Orchestrator` and the WebApi bus wiring both depend only on contracts (+ `BaseConsole.Core` for the Orchestrator) and have no dependency on each other, so they parallelize. RabbitMQ infra and the harness test close it out once both endpoints exist.

---

## Scaling Considerations

| Scale | Architecture adjustments |
|-------|--------------------------|
| 1 Orchestrator replica (today) | Instance-unique queue already future-proofs fan-out; single queue, single consumer. |
| N replicas (topology-ready) | Each replica's auto-delete instance queue receives its own copy from the fanout exchange — fan-out works with zero code change (locked topology decision). |
| Load-balanced send (future) | `queue:processorId` + shared results queue (explicitly future per PROJECT.md:27) — different topology (competing consumers), not this milestone. |

### Scaling priorities
1. **First concern:** instance-queue leakage on replica churn — mitigate with auto-delete/non-durable instance queues (MT defaults are durable; override).
2. **Second concern:** RabbitMQ as a hard dep widening — keep it scoped to Start/Stop + Orchestrator lifecycle; do not let it gate CRUD readiness (Pattern 6 health note).

---

## Sources

- Existing codebase (read directly, HIGH): `BaseApi.Service/Program.cs`, `BaseApi.Core/DependencyInjection/{BaseApiServiceCollectionExtensions,BaseApiApplicationBuilderExtensions,HealthServiceCollectionExtensions,ObservabilityServiceCollectionExtensions,RedisServiceCollectionExtensions}.cs`, `BaseApi.Core/Health/{IStartupGate,StartupHealthCheck,StartupCompletionService}.cs`, `BaseApi.Core/Middleware/CorrelationIdMiddleware.cs`, `BaseApi.Service/Features/Orchestration/Projection/{WorkflowRootProjection,LivenessProjection,RedisProjectionWriter}.cs`, `.planning/PROJECT.md`.
- MassTransit health-check tags (`ready`+`masstransit`), Healthy/Degraded/Unhealthy semantics, `ConfigureHealthCheckOptions`/`MinimalFailureStatus`, custom-tags-replace-defaults (HIGH, official): https://masstransit.io/documentation/configuration
- MassTransit middleware filters — `IFilter<ConsumeContext>`, `IFilter<SendContext<T>>`, `IFilter<PublishContext<T>>`, `UseConsumeFilter`/`UseSendFilter`/`UsePublishFilter` registration (HIGH, Context7 `/challengermode/opentransit` middleware docs).
- MassTransit RabbitMQ default durable fanout exchange + competing-consumer-on-shared-queue behavior; instance-unique queue for fan-out (HIGH, official): https://masstransit.io/documentation/configuration/transports/rabbitmq
- Embedded Kestrel-in-hosted-service for health endpoints on a Generic Host; `MapHealthChecks` is `WebApplication`-only; tag predicate filtering (MEDIUM/HIGH): https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks , https://andrewlock.net/deploying-asp-net-core-applications-to-kubernetes-part-6-adding-health-checks-with-liveness-readiness-and-startup-probes/
- .NET Generic Host (`Host.CreateApplicationBuilder`, worker pattern) (HIGH, official): https://learn.microsoft.com/en-us/dotnet/core/extensions/generic-host

---
*Architecture research for: BaseConsole.Core + MassTransit/RabbitMQ integration into a modular monolith*
*Researched: 2026-05-30*
