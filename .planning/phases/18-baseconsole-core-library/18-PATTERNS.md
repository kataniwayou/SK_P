# Phase 18: BaseConsole.Core Library - Pattern Map

**Mapped:** 2026-05-30
**Files analyzed:** 18 (15 new src files + 1 csproj edit + 1 test csproj edit + test fixtures/classes)
**Analogs found:** 11 with concrete analogs / 18 total (4 are NET-NEW with structural references only)

> This phase is mirror-heavy. CONTEXT `<canonical_refs>` and RESEARCH "Mirror Source Map" name the analogs; every analog path below was VERIFIED to exist and READ this session. Where a file is genuinely net-new (the two correlation filters, the accessor, the embedded Kestrel listener), it is marked NET-NEW with the nearest structural reference and the verified MassTransit `IFilter<>` shape from RESEARCH.md — no false analog is forced.

> **Hard guardrails carried from CONTEXT/RESEARCH (apply to ALL relevant files):**
> - TFM `net8.0` inherited from `Directory.Build.props` — NEVER declare `net9.0`. `TreatWarningsAsErrors=true` → must compile warning-clean.
> - CPM: `<PackageReference>` entries carry NO `Version=`.
> - NO `BaseConsole.Core → BaseApi.Core` ProjectReference (D-08) — duplicate the ~40 LOC instead.
> - OTel is **metrics + logs only**: NEVER `.WithTracing` / `TracerProvider` / `.AddSource("MassTransit")` (Pitfall 1).
> - `/health/live` predicate is **self-only** — never Redis/RMQ under `"live"` (Pitfall 2).
> - Scope key casing is load-bearing: use `CorrelationKeys.LogScope` (`"CorrelationId"`) verbatim — never a renamed literal.

---

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/BaseConsole.Core/BaseConsole.Core.csproj` | config | — | `src/BaseApi.Core/BaseApi.Core.csproj` | role-match (delta: no EF/MVC/Swagger pkgs; add MassTransit; ref Messaging.Contracts) |
| `…/DependencyInjection/BaseConsoleObservabilityExtensions.cs` | config (DI extension) | request-response (host build) | `ObservabilityServiceCollectionExtensions.cs` | exact (lift + 2 edits) |
| `…/DependencyInjection/ConsoleRedisServiceCollectionExtensions.cs` | config (DI extension) | CRUD (connection) | `RedisServiceCollectionExtensions.cs` | exact (duplicate; `internal`→reachable) |
| `…/DependencyInjection/BaseConsoleServiceCollectionExtensions.cs` | config (composition root) | request-response | `BaseApiServiceCollectionExtensions.cs` | role-match (non-generic; no TDbContext) |
| `…/DependencyInjection/ConsoleHealthServiceCollectionExtensions.cs` | config (DI extension) | request-response | `HealthServiceCollectionExtensions.cs` | role-match (drop NpgSql; add MT-ready surfacing + listener reg) |
| `…/DependencyInjection/MessagingServiceCollectionExtensions.cs` | config (DI extension) | event-driven / pub-sub | RESEARCH Pattern 4 (no in-repo MassTransit reg yet) | partial (structural ref only) |
| `…/Health/IStartupGate.cs` (`IStartupGate`+`StartupGate`) | model (latch) | event-driven | `Health/IStartupGate.cs` | exact (duplicate verbatim) |
| `…/Health/StartupHealthCheck.cs` | service (health check) | request-response | `Health/StartupHealthCheck.cs` | exact (duplicate verbatim) |
| `…/Health/StartupCompletionService.cs` | service (IHostedService) | event-driven | `Health/StartupCompletionService.cs` **Phase-5 variant** | role-match (build SIMPLER shape — see delta) |
| `…/Health/EmbeddedHealthEndpointService.cs` | service (IHostedService) | request-response (HTTP) | `BaseApiApplicationBuilderExtensions.cs` (`MapHealthChecks` x3) + `HealthServiceCollectionExtensions.cs` | partial (NET-NEW hosting shape; mirror predicates/writer) |
| `…/Messaging/ICorrelationAccessor.cs` + `AsyncLocalCorrelationAccessor.cs` | utility (AsyncLocal accessor) | event-driven | — | **NET-NEW** (RESEARCH Pattern 5) |
| `…/Messaging/InboundCorrelationConsumeFilter.cs` | middleware (MT filter) | event-driven (consume) | — | **NET-NEW** (`IFilter<ConsumeContext>`) |
| `…/Messaging/OutboundCorrelationSendFilter.cs` | middleware (MT filter) | event-driven (send) | — | **NET-NEW** (`IFilter<SendContext<T>>`) |
| `…/Messaging/OutboundCorrelationPublishFilter.cs` | middleware (MT filter) | pub-sub (publish) | — | **NET-NEW** (`IFilter<PublishContext<T>>`) |
| `tests/BaseApi.Tests/BaseApi.Tests.csproj` (modify) | config | — | existing csproj | edit-only (add ProjectReference to BaseConsole.Core) |
| `tests/…/ConsoleTestHostFixture.cs` | test (fixture) | request-response | `HealthEndpointsTests.cs` dead-port fixtures | role-match (Generic-Host, not WebApplicationFactory) |
| `tests/…/ConsoleHealthLiveTests.cs` / `ConsoleStartupGateTests.cs` | test | request-response | `HealthEndpointsTests.cs` | exact (dead-port + gate-removed patterns) |
| `tests/…/ConsoleObservabilityTests.cs` | test (container) | request-response | `MetricsExportTests.cs` + deleted `TraceExportTests` | role-match (assert no TracerProvider; MT meter present) |
| `tests/…/ConsoleCorrelationFilterTests.cs` | test (harness) | event-driven | — (`AddMassTransitTestHarness`) | partial (NET-NEW; harness vehicle) |

---

## Pattern Assignments

### `src/BaseConsole.Core/BaseConsole.Core.csproj` (config)

**Analog:** `src/BaseApi.Core/BaseApi.Core.csproj` (lines 1-35, 90-100)

**PropertyGroup pattern** (lines 23-28) — copy structure, swap names:
```xml
<PropertyGroup>
  <RootNamespace>BaseConsole.Core</RootNamespace>
  <AssemblyName>BaseConsole.Core</AssemblyName>
  <GenerateDocumentationFile>true</GenerateDocumentationFile>
  <NoWarn>$(NoWarn);CS1591</NoWarn>
</PropertyGroup>
```

**FrameworkReference pattern** (line 34) — keep verbatim (CONSOLE-05; gives Kestrel + health checks without Web SDK; `Sdk="Microsoft.NET.Sdk"`, NOT `.Web`):
```xml
<FrameworkReference Include="Microsoft.AspNetCore.App" />
```

**ItemGroup deltas vs BaseApi.Core (DO NOT copy the EF/MVC/Swagger/NpgSql groups):**
- DROP: all of `Microsoft.EntityFrameworkCore*`, `Npgsql*`, `EFCore.NamingConventions`, `FluentValidation*`, `Asp.Versioning*`, `Swashbuckle*`, `AspNetCore.HealthChecks.NpgSql`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`.
- KEEP: `StackExchange.Redis`, `AspNetCore.HealthChecks.UI.Client`, `OpenTelemetry`, `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Instrumentation.Runtime`.
- ADD: `MassTransit`, `MassTransit.RabbitMQ`.
- ADD ProjectReference (the ONLY ProjectReference; Messaging.Contracts is a POCO leaf — see `Messaging.Contracts.csproj` confirming it has no MassTransit ref so filters can't live there):
```xml
<ProjectReference Include="..\Messaging.Contracts\Messaging.Contracts.csproj" />
```
All package versions resolve via CPM — **NO `Version=`** (mirror the BaseApi.Core comment discipline at lines 11-12).

---

### `…/DependencyInjection/BaseConsoleObservabilityExtensions.cs` (config, request-response)

**Analog:** `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` — **LIFT + exactly 2 edits in the metrics chain.**

**Imports + signature** (analog lines 1-10, 37-38) — `IHostApplicationBuilder` signature is REQUIRED (D-06: `builder.Logging.AddOpenTelemetry` needs `ILoggingBuilder`):
```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using MassTransit;   // NEW — for InstrumentationOptions.MeterName

public static IHostApplicationBuilder AddBaseConsoleObservability(
    this IHostApplicationBuilder builder, IConfiguration cfg)
```

**Fail-fast config read** (analog lines 42-43) — lift verbatim (use the console's own `Service:Name`/`Service:Version` per D-07; the `cfg.Require(...)` helper lives in BaseApi.Core/Configuration — **must be duplicated or its console equivalent used**, see Shared Patterns):
```csharp
var serviceName    = cfg.Require("Service:Name");
var serviceVersion = cfg.Require("Service:Version");
```

**MEL logs block** (analog lines 48-56) — **LIFT BYTE-FOR-BYTE.** `IncludeScopes = true` is load-bearing (serializes `"CorrelationId"` to the shared ES attribute):
```csharp
builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes           = true;
    o.ParseStateValues        = true;
    o.SetResourceBuilder(ResourceBuilder.CreateDefault()
        .AddService(serviceName: serviceName, serviceVersion: serviceVersion));
    o.AddOtlpExporter();
});
```

**Metrics block — the 2-edit delta** (analog lines 60-73). REMOVE the two ASP.NET/HTTP lines (analog 70-71); ADD the MassTransit meter; keep Runtime + OTLP; **NO `.WithTracing`:**
```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(serviceName: serviceName, serviceVersion: serviceVersion))
    .WithMetrics(m => m
        // REMOVED vs API: .AddAspNetCoreInstrumentation()  .AddHttpClientInstrumentation()
        .AddMeter(InstrumentationOptions.MeterName)   // NEW — "MassTransit"
        .AddRuntimeInstrumentation()                  // kept
        .AddOtlpExporter());
return builder;
```

---

### `…/DependencyInjection/ConsoleRedisServiceCollectionExtensions.cs` (config, CRUD)

**Analog:** `src/BaseApi.Core/DependencyInjection/RedisServiceCollectionExtensions.cs` — **DUPLICATE.**

**Imports** (analog lines 1-4):
```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
```

**Core singleton pattern** (analog lines 45-58) — duplicate; `abortConnect=false` is supplied by the appsettings connection string (boot-safe soft dep, D-14 rationale at analog lines 17-23):
```csharp
var connStr = cfg.RequireConnectionString("Redis");
services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(connStr));
```

**Visibility delta (Pitfall 4):** analog is `internal static` (line 43). The duplicate must be reachable from `AddBaseConsole`. Same-assembly `internal` is fine if both live in `BaseConsole.Core`; promote to `public` only if invoked cross-assembly. **Do NOT keep a stale `internal` that the composition root can't see.**

**Options-binding delta (RESEARCH Open-Q 3):** analog line 63 binds `RedisProjectionOptions` (an EF/projection type). Phase 18 only needs the `IConnectionMultiplexer` singleton — **DROP the `services.Configure<RedisProjectionOptions>(...)` line** (the console reads L2 in Phase 19; that options type is a BaseApi.Core concern and pulling it would violate D-08). Register the multiplexer only.

---

### `…/DependencyInjection/BaseConsoleServiceCollectionExtensions.cs` (config, composition root)

**Analog:** `src/BaseApi.Core/DependencyInjection/BaseApiServiceCollectionExtensions.cs` — **mirror the chained `Add*` seam, non-generic.**

**Chain pattern** (analog lines 24-34) — drop the generic `<TDbContext>` and all DB sub-extensions; chain console infra (D-07: non-generic, no DbContext):
```csharp
public static IServiceCollection AddBaseConsole(
    this IServiceCollection services, IConfiguration cfg)
    => services
        .AddBaseConsoleRedis(cfg)        // Redis soft-dep singleton
        .AddBaseConsoleHealth(cfg);      // startup gate + checks + EmbeddedHealthEndpointService reg
//  Observability is chained SEPARATELY on the builder (needs ILoggingBuilder — mirror analog lines 35-36 comment).
//  Messaging is a SEPARATE call (AddBaseConsoleMessaging) because it takes the consumer-registration lambda (D-06).
```

**Key delta:** the API keeps Observability out of this chain (analog comment lines 35-36). The console additionally keeps **Messaging** out of this chain — it is its own `Add*` call carrying the `configureConsumers` lambda (D-06). `AddBaseConsole` = Redis + health only.

---

### `…/DependencyInjection/ConsoleHealthServiceCollectionExtensions.cs` (config, request-response)

**Analog:** `src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs`

**Gate + checks pattern** (analog lines 17-29) — mirror tag discipline; **DROP `.AddNpgSql(...)` (no DB); the MT bus `ready` check is auto-registered by `AddMassTransit`** (Pattern 3 — do not hand-roll it here):
```csharp
services.AddSingleton<IStartupGate, StartupGate>();
services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })       // live = self-only
    .AddCheck<StartupHealthCheck>("startup", tags: new[] { "startup", "ready" });
    // NO .AddNpgSql — console has no DB. MT bus 'ready' check is auto-registered in AddBaseConsoleMessaging.

services.AddHostedService<StartupCompletionService>();      // Phase-5 variant (MarkReady on StartAsync)
services.AddHostedService<EmbeddedHealthEndpointService>(); // independent Kestrel (D-04)
```

**Cross-container caveat (RESEARCH Pattern 2 caveat / Open-Q 1 — HIGH IMPACT):** the `self`/`startup` checks above register in the OUTER host DI, but the embedded listener has its OWN DI. The planner MUST decide how `/health/ready` reflects the MT bus check inside the inner listener (share gate instance + surface `IBusHealth.CheckHealth()` as a custom inner `"ready"` check, OR register MT health in the inner container). This file registers the outer-host checks; the listener (next file) wires the inner ones.

---

### `…/DependencyInjection/MessagingServiceCollectionExtensions.cs` (config, event-driven)

**Analog:** No existing in-repo MassTransit registration (`AddMassTransit` is net-new to this repo). Structural reference: **RESEARCH Pattern 4 (lines 277-299), VERIFIED via Context7.** Seam mirrors `AddBaseApi` vs `AddAppFeatures` — base = infra, concrete = consumers (D-06).

**Registration shape** (RESEARCH lines 286-297) — `configureConsumers` is the concrete seam (empty this phase; P19 adds consumers). Both outbound filters + the inbound filter registered bus-wide:
```csharp
public static IServiceCollection AddBaseConsoleMessaging(
    this IServiceCollection services, IConfiguration cfg, Action<IBusRegistrationConfigurator> configureConsumers)
{
    services.AddMassTransit(x =>
    {
        configureConsumers(x);                       // concrete seam — empty Phase 18
        x.UsingRabbitMq((ctx, c) =>
        {
            c.Host(/* RabbitMq host/creds from cfg — use cfg.Require* fail-fast */);
            c.UseConsumeFilter(typeof(InboundCorrelationConsumeFilter), ctx);     // CORR-01, bus-wide
            c.UseSendFilter(typeof(OutboundCorrelationSendFilter<>), ctx);        // CORR-02
            c.UsePublishFilter(typeof(OutboundCorrelationPublishFilter<>), ctx);  // CORR-02
            c.ConfigureEndpoints(ctx);
        });
    });
    services.AddSingleton<ICorrelationAccessor, AsyncLocalCorrelationAccessor>(); // accessor for both filters
    return services;
}
```

**Deltas / cautions:**
- Do NOT call `ConfigureHealthCheckOptions` — default tags `["ready","masstransit"]` are exactly what CONSOLE-HEALTH-03 needs; custom tags would REPLACE the defaults (RESEARCH Pattern 3, line 270).
- Inbound filter ordering is Claude's Discretion: bus-level `UseConsumeFilter` places it ahead of consumer code (RESEARCH line 299) — correct placement.
- Use the same `cfg.Require*` fail-fast helpers for RabbitMq host/credentials reads as the rest of the composition boundary (Shared Patterns).

---

### `…/Health/IStartupGate.cs` (model, event-driven)

**Analog:** `src/BaseApi.Core/Health/IStartupGate.cs` — **DUPLICATE VERBATIM** (already `public sealed`, ~30 LOC). Only change the namespace to `BaseConsole.Core.Health`.

**Interface** (analog lines 20-27) + **latch impl** (analog lines 47-56) — copy exactly; the `Interlocked.Exchange`/`Volatile.Read` thread-safe one-shot is the contract:
```csharp
public interface IStartupGate
{
    bool IsReady { get; }
    void MarkReady();
}

public sealed class StartupGate : IStartupGate
{
    private int _isReady;
    public bool IsReady => Volatile.Read(ref _isReady) == 1;
    public void MarkReady() => Interlocked.Exchange(ref _isReady, 1);
}
```

---

### `…/Health/StartupHealthCheck.cs` (service, request-response)

**Analog:** `src/BaseApi.Core/Health/StartupHealthCheck.cs` — **DUPLICATE VERBATIM** (already `public sealed`, ~10 LOC). Change namespace only; optionally reword the Unhealthy message (console has no migrations — "migrations pending" wording at analog line 28 is API-specific).

**Full pattern** (analog lines 21-29):
```csharp
public sealed class StartupHealthCheck(IStartupGate gate) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(gate.IsReady
            ? HealthCheckResult.Healthy("Startup complete")
            : HealthCheckResult.Unhealthy("Startup not complete"));  // dropped "(migrations pending)" — no DB
}
```

---

### `…/Health/StartupCompletionService.cs` (service, IHostedService)

**Analog:** `src/BaseApi.Core/Health/StartupCompletionService.cs` — ⚠️ **DO NOT lift the current source.** The current file (analog lines 38-75) is the **Phase-8 migration variant** (injects `BaseDbContext`, calls `MigrateAsync`, uses `IServiceScopeFactory`). The console has no DB. **Build the simpler Phase-5-era shape** (D-05, RESEARCH line 127, Pitfall 4):

```csharp
using Microsoft.Extensions.Hosting;

namespace BaseConsole.Core.Health;

public sealed class StartupCompletionService(IStartupGate gate) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        gate.MarkReady();                 // host came up — no DB, no migration
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

**What to DROP vs analog:** `BaseDbContext`/`IServiceScopeFactory`/`MigrateAsync`/the try-catch-LogCritical block (analog lines 40-71). **Anti-pattern to avoid:** copying the migration variant drags `BaseDbContext` into a DB-less worker → won't compile / wrong semantics (RESEARCH Anti-Patterns + Pitfall 4).

---

### `…/Health/EmbeddedHealthEndpointService.cs` (service, IHostedService — HTTP)

**Structural references (NET-NEW hosting shape — no clean in-repo analog for "Kestrel inside an IHostedService"):**
- Tag predicates + `UIResponseWriter` body: `src/BaseApi.Core/DependencyInjection/BaseApiApplicationBuilderExtensions.cs` lines 46-60.
- Check registration + tags: `src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs` lines 20-29.
- Full reference implementation: **RESEARCH Pattern 2 (lines 229-259).**

**Three `MapHealthChecks` calls — mirror the API predicates BYTE-FOR-BYTE** (analog `BaseApiApplicationBuilderExtensions.cs` lines 46-60), but hosted inside an inner minimal `WebApplication` because Generic Host has no `MapHealthChecks` (Anti-Pattern, RESEARCH line 348):
```csharp
_app.MapHealthChecks("/health/live", new HealthCheckOptions {
    Predicate = c => c.Tags.Contains("live"),
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse });
_app.MapHealthChecks("/health/ready", new HealthCheckOptions {
    Predicate = c => c.Tags.Contains("ready"),
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse });
_app.MapHealthChecks("/health/startup", new HealthCheckOptions {
    Predicate = c => c.Tags.Contains("startup"),
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse });
```

**NEW hosting deltas (RESEARCH Pattern 2 + caveat):**
- `IHostedService.StartAsync` builds + starts an inner `WebApplication` on `ConsoleHealth:Port` (default `8081`, D-04); `StopAsync` stops it.
- Independent of the bus → `/health/live` answers while the bus connects and with both deps dead.
- **Share the OUTER `IStartupGate` instance into the inner DI** (`b.Services.AddSingleton(_gate)`); register `self`+`startup` inner checks; surface the MT bus `ready` check via the Open-Q-1 resolution (custom inner `IHealthCheck` reading `IBusHealth.CheckHealth()`, OR register MT health in the inner container). **This is the #1 wiring decision for the planner.**
- Class name / options-binding shape = Claude's Discretion (suggested `EmbeddedHealthEndpointService`).

---

### `…/Messaging/ICorrelationAccessor.cs` + `AsyncLocalCorrelationAccessor.cs` (utility — NET-NEW)

**Analog:** None — VERIFIED absent (`src/Messaging.Contracts/*.cs` glob = ICorrelated/CorrelationKeys/Start-Stop only; it is POCO-only with no MassTransit ref per `Messaging.Contracts.csproj` lines 6-8, so it physically can't host this). Structural reference: **RESEARCH Pattern 5 (lines 304-309).**

```csharp
public interface ICorrelationAccessor { string? Get(); void Set(string? value); }

public sealed class AsyncLocalCorrelationAccessor : ICorrelationAccessor   // register Singleton
{
    private static readonly AsyncLocal<string?> _current = new();
    public string? Get() => _current.Value;
    public void Set(string? value) => _current.Value = value;
}
```
**Type decision (RESEARCH Open-Q 2):** accessor is `string?` (preserves arbitrary HTTP ids for the log scope); the outbound envelope stamp does `Guid.TryParse`. Name/namespace = Claude's Discretion (suggested `BaseConsole.Core.Messaging`). Lives in `BaseConsole.Core`, NOT `Messaging.Contracts`.

---

### `…/Messaging/InboundCorrelationConsumeFilter.cs` (middleware — NET-NEW)

**Analog:** None. Structural reference: **RESEARCH Pattern 5 (lines 311-326)**, VERIFIED `IFilter<ConsumeContext>` surface. Consumes `CorrelationKeys.LogScope` from `Messaging.Contracts` (`CorrelationKeys.cs` line 7 = `"CorrelationId"`).

```csharp
public sealed class InboundCorrelationConsumeFilter(
    ICorrelationAccessor accessor, ILogger<InboundCorrelationConsumeFilter> logger)
    : IFilter<ConsumeContext>
{
    public async Task Send(ConsumeContext context, IPipe<ConsumeContext> next)
    {
        var corrId = context.CorrelationId?.ToString() ?? Guid.NewGuid().ToString();
        accessor.Set(corrId);
        using (logger.BeginScope(new Dictionary<string, object> { [CorrelationKeys.LogScope] = corrId }))
            await next.Send(context);
    }
    public void Probe(ProbeContext context) => context.CreateFilterScope("correlation-in");
}
```
**Load-bearing:** scope key MUST be `CorrelationKeys.LogScope` (`"CorrelationId"`) — never a renamed literal (Pitfall 3; matches `CorrelationIdMiddleware`'s `ItemKey = "CorrelationId"`). Treat the inbound id as untrusted text (ASVS V5 — do not log unbounded values).

---

### `…/Messaging/OutboundCorrelationSendFilter.cs` + `OutboundCorrelationPublishFilter.cs` (middleware — NET-NEW)

**Analog:** None. Structural reference: **RESEARCH Pattern 5 (lines 329-341)**, VERIFIED `IFilter<SendContext<T>>` / `IFilter<PublishContext<T>>`.

```csharp
// D-01: stamp the ENVELOPE (context.CorrelationId), gated on ICorrelated — do NOT mutate the record body.
public sealed class OutboundCorrelationSendFilter<T>(ICorrelationAccessor accessor)
    : IFilter<SendContext<T>> where T : class
{
    public Task Send(SendContext<T> context, IPipe<SendContext<T>> next)
    {
        if (context.Message is ICorrelated && Guid.TryParse(accessor.Get(), out var id))
            context.CorrelationId = id;                 // envelope, not body (D-01)
        return next.Send(context);
    }
    public void Probe(ProbeContext context) => context.CreateFilterScope("correlation-out-send");
}
// OutboundCorrelationPublishFilter<T> : IFilter<PublishContext<T>> — identical body against PublishContext.
```
**D-01 (load-bearing):** stamp `context.CorrelationId` only; `ICorrelated` stays GET-ONLY (`ICorrelated.cs` lines 4-12 confirm get-only) — do NOT reopen `Messaging.Contracts` for setters. Phase 20's synthetic harness asserts the envelope `CorrelationId`, so the Phase 18 filter test must read the same surface.

---

### Test files (`tests/BaseApi.Tests/...`)

**csproj edit:** add to `tests/BaseApi.Tests/BaseApi.Tests.csproj`:
```xml
<ProjectReference Include="..\..\src\BaseConsole.Core\BaseConsole.Core.csproj" />
```

**`ConsoleTestHostFixture.cs`** — analog pattern: `HealthEndpointsTests.cs` dead-port fixtures (lines 251-306, 327-378). The console fixture uses `Host.CreateApplicationBuilder` + the three `AddBaseConsole*` calls + `Build()` (NOT `WebApplicationFactory<Program>`). Reuse the **dead-port env-var-in-ctor + restore-on-dispose** discipline (analog lines 261-305) and the **Pitfall-3 port-unbound pre-flight** (analog lines 348-377) for the dead-Redis/dead-RabbitMQ variants. Both deps point at dead ports; assert `IBus` resolvable and host boots without throwing.

**`ConsoleHealthLiveTests.cs` / `ConsoleStartupGateTests.cs`** — analog: `HealthEndpointsTests.cs`.
- Live-with-deps-dead: mirror `HealthLive_200_When_Redis_Unreachable` (analog lines 197-206) → GET inner listener `/health/live`, assert 200 + `"status":"Healthy"` (analog lines 38-43).
- Startup-flips: mirror `Test_HealthStartup_200_After_GateFlipped_By_HostedService` (analog lines 80-95).
- Startup-negative (gate never flipped): mirror `HealthNoStartupCompletionFixture` (analog lines 447-463) — remove `StartupCompletionService` by **Type identity** (`d.ImplementationType == typeof(StartupCompletionService)`), assert 503 (analog lines 98-110).
- Body-no-secrets: mirror `Test_HealthReady_Body_Has_Per_Check_Status_But_No_Sensitive_Fields` (analog lines 113-134) — `Assert.DoesNotContain("Password=", body)` etc.

**`ConsoleObservabilityTests.cs`** — analog: `MetricsExportTests.cs` shape + the deleted `TraceExportTests`. Assert `provider.GetService<TracerProvider>()` is null (D-02 fact #4, Pitfall 1); assert `MeterProvider` resolvable + MassTransit meter present; assert no AspNetCore/HttpClient instrumentation services registered.

**`ConsoleCorrelationFilterTests.cs`** — NET-NEW vehicle: `AddMassTransitTestHarness` (ships in core MassTransit 8.5.5; no extra NuGet). Register both outbound filters + a probe consumer; set the ambient accessor; publish an `ICorrelated` test message → assert inbound scope/accessor populated and outbound `SendContext.CorrelationId` stamped (D-02 fact #6). Use a **Guid-parseable** id so `context.CorrelationId` is set (RESEARCH Open-Q 2 / Assumption A3).

---

## Shared Patterns

### Fail-fast config reads at the composition boundary
**Source:** `src/BaseApi.Core/Configuration/RequiredConfig.cs` (`cfg.Require(...)` / `cfg.RequireConnectionString(...)` — used at `ObservabilityServiceCollectionExtensions.cs:42-43`, `RedisServiceCollectionExtensions.cs:50`, `HealthServiceCollectionExtensions.cs:26`).
**Apply to:** `AddBaseConsoleObservability` (`Service:Name`/`Service:Version`), `AddBaseConsoleRedis` (`Redis`), `AddBaseConsoleMessaging` (RabbitMq host/creds).
**Delta (D-08):** these helpers live in `BaseApi.Core` — must NOT be referenced cross-assembly. **Duplicate `RequiredConfig.cs` into `BaseConsole.Core/Configuration/`** (a few extension methods) the same way Redis/StartupGate are duplicated, OR inline the null-check pattern. Planner: confirm placement.
```csharp
var serviceName = cfg.Require("Service:Name");
var connStr     = cfg.RequireConnectionString("Redis");
```

### Health tag discipline (3-probe predicate)
**Source:** `HealthServiceCollectionExtensions.cs:21-26` (check tags) + `BaseApiApplicationBuilderExtensions.cs:46-60` (predicates).
**Apply to:** `ConsoleHealthServiceCollectionExtensions` (outer checks) + `EmbeddedHealthEndpointService` (inner `MapHealthChecks`).
**Invariants (locked):** `live` → only the always-Healthy `"self"` check (NEVER Redis/RMQ — Pitfall 2/5); `ready` → `StartupHealthCheck` + MT bus check; `startup` → `StartupHealthCheck`. MT bus check is tagged `"ready"`, never `"live"`.

### Health JSON body writer (keeps console + API shape identical)
**Source:** `BaseApiApplicationBuilderExtensions.cs:49,54,59` — `UIResponseWriter.WriteHealthCheckUIResponse` (from `HealthChecks.UI.Client`).
**Apply to:** all three `MapHealthChecks` in `EmbeddedHealthEndpointService`.
**Security (ASVS V7):** body must not leak connection strings/stack traces — mirror the API's `T-05-READY-DB-EXPOSE` assertions in the console health tests.

### Correlation scope key (cross-service ES join)
**Source:** `src/Messaging.Contracts/CorrelationKeys.cs:7` — `LogScope = "CorrelationId"`.
**Apply to:** `InboundCorrelationConsumeFilter` `BeginScope`. Casing is load-bearing — drift breaks the single-field ES join (Pitfall 3). `IncludeScopes = true` in the observability MEL block is what serializes it.

### Sealed-type + namespace + CPM discipline
**Source:** every Core type is `public sealed` (`StartupGate.cs:47`, `StartupHealthCheck.cs:21`); csproj comment discipline `BaseApi.Core.csproj:11-12`.
**Apply to:** all new `BaseConsole.Core` types (`public sealed`); all package refs NO `Version=`; warning-clean compile (`TreatWarningsAsErrors=true`).

---

## No Analog Found (NET-NEW — use RESEARCH patterns, cite structural refs)

| File | Role | Data Flow | Reason / Nearest Reference |
|------|------|-----------|----------------------------|
| `ICorrelationAccessor.cs` + `AsyncLocalCorrelationAccessor.cs` | utility | event-driven | No accessor exists anywhere (Messaging.Contracts is POCO-only). Ref: RESEARCH Pattern 5 (lines 304-309). |
| `InboundCorrelationConsumeFilter.cs` | middleware | event-driven | No MassTransit filter exists in repo. Ref: RESEARCH Pattern 5 (lines 311-326), `IFilter<ConsumeContext>`. |
| `OutboundCorrelationSendFilter.cs` / `OutboundCorrelationPublishFilter.cs` | middleware | event-driven / pub-sub | No outbound filter exists. Ref: RESEARCH Pattern 5 (lines 329-341). |
| `EmbeddedHealthEndpointService.cs` | service (IHostedService) | request-response | "Kestrel inside an IHostedService" has no in-repo analog (the API uses the primary `WebApplication`). Mirror predicates/writer from `BaseApiApplicationBuilderExtensions.cs:46-60`; hosting shape from RESEARCH Pattern 2 (lines 229-259). |
| `MessagingServiceCollectionExtensions.cs` | config | event-driven | First `AddMassTransit` registration in the repo. Ref: RESEARCH Pattern 4 (lines 286-297), Context7-VERIFIED. |
| `ConsoleCorrelationFilterTests.cs` | test | event-driven | First `AddMassTransitTestHarness` usage. Ref: RESEARCH Validation Architecture (lines 433) + Pattern 5. |

---

## Open Wiring Decisions for the Planner (from RESEARCH Open Questions)

1. **Inner-Kestrel `/health/ready` ↔ outer MT bus health check** (RESEARCH OQ1 — #1 priority). Two-container design (D-04) means the inner listener doesn't auto-see the outer bus check. Pick: surface `IBusHealth.CheckHealth()` via a custom inner `"ready"` check, OR register MT health in the inner container. Validate with a `/health/ready`-reflects-bus-state test.
2. **Correlation id type** (OQ2): accessor `string?`; outbound stamps `context.CorrelationId` only when `Guid.TryParse` succeeds. Confirm the Phase 18 harness test uses a Guid-parseable id.
3. **`RequiredConfig` duplication** (D-08 consequence): decide whether to duplicate `RequiredConfig.cs` into `BaseConsole.Core/Configuration/` or inline the fail-fast null checks.

---

## Metadata

**Analog search scope:** `src/BaseApi.Core/DependencyInjection/`, `src/BaseApi.Core/Health/`, `src/Messaging.Contracts/`, `tests/BaseApi.Tests/Observability/`.
**Files scanned (read this session):** `ObservabilityServiceCollectionExtensions.cs`, `RedisServiceCollectionExtensions.cs`, `HealthServiceCollectionExtensions.cs`, `BaseApiServiceCollectionExtensions.cs`, `BaseApiApplicationBuilderExtensions.cs`, `Health/IStartupGate.cs`, `Health/StartupHealthCheck.cs`, `Health/StartupCompletionService.cs`, `CorrelationKeys.cs`, `ICorrelated.cs`, `StartOrchestration.cs`, `HealthEndpointsTests.cs`, `MetricsExportTests.cs`, `BaseApi.Core.csproj`, `Messaging.Contracts.csproj`.
**All CONTEXT/RESEARCH-named analog paths VERIFIED to exist via Glob.**
**Pattern extraction date:** 2026-05-30
**No source files modified — PATTERNS.md is the only file written.**
