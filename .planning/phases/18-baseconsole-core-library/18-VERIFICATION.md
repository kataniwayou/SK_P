---
phase: 18-baseconsole-core-library
verified: 2026-05-30T10:15:00Z
status: passed
score: 5/5 must-haves verified
overrides_applied: 0
re_verification: false
---

# Phase 18: BaseConsole.Core Library Verification Report

**Phase Goal:** A reusable `BaseConsole.Core` Generic-Host library exists — the console-side mirror of `BaseApi.Core` — providing observability, Redis, embedded health probes, and a MassTransit bus skeleton with correlation filters, validated standalone before any concrete console inherits it.
**Verified:** 2026-05-30T10:15:00Z
**Status:** passed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | A console built on BaseConsole.Core boots via an AddBaseConsole/RunAsync-style chain in a handful of lines, registers a singleton soft-dependency Redis client (abortConnect=false), references Microsoft.AspNetCore.App via FrameworkReference (library not Web SDK), with NO BaseConsole.Core → BaseApi.Core dependency | ✓ VERIFIED | `BaseConsole.Core.csproj` has `Sdk="Microsoft.NET.Sdk"` + `<FrameworkReference Include="Microsoft.AspNetCore.App"/>`, sole ProjectReference is `Messaging.Contracts.csproj`, no `BaseApi.Core` token anywhere in `src/BaseConsole.Core/`. `AddBaseConsole(cfg)` chains `AddBaseConsoleRedis` + `AddBaseConsoleHealth` in 2 lines. `ConsoleRedisServiceCollectionExtensions` registers `AddSingleton<IConnectionMultiplexer>` with connection string from config (abortConnect=false in caller's appsettings). `ConsoleHostBootTests` proves boot under dead deps. |
| 2 | Console OTel emits MEL-bridge logs + runtime metrics + the MassTransit meter via OTLP with NO AspNetCore/HttpClient instrumentation and NO TracerProvider | ✓ VERIFIED | `BaseConsoleObservabilityExtensions.cs` uses `MassTransit.Monitoring.InstrumentationOptions.MeterName`, `.AddRuntimeInstrumentation()`, `.AddOtlpExporter()` in the `.WithMetrics` block; zero `.WithTracing`, `AddSource`, `AddAspNetCoreInstrumentation`, `AddHttpClientInstrumentation` in `src/BaseConsole.Core/`. `ConsoleObservabilityTests` asserts `GetService<TracerProvider>()` is null and `GetService<MeterProvider>()` is non-null, and assembly-scans for instrumentation markers. |
| 3 | /health/live returns 200 over the embedded minimal HTTP listener without touching RabbitMQ or Redis even when both are down; /health/ready reports Healthy only once the MassTransit bus has started and Unhealthy while the broker is unreachable; /health/startup served by the duplicated IStartupGate + StartupHealthCheck | ✓ VERIFIED | `EmbeddedHealthEndpointService` maps `/health/live` predicate to `Tags.Contains("live")` only (the always-Healthy "self" check — no Redis/bus check registered with that tag). `BusReadyHealthCheck` resolves outer `IBusControl.CheckHealth()` and returns `Unhealthy("Bus not started")` when null. `ConsoleHealthLiveTests` asserts 200/Healthy with dead deps. `ConsoleBusReadyHealthCheckTests.BusReadyHealthCheck_Returns_Unhealthy_When_Bus_Not_Healthy` asserts `HealthStatus.Unhealthy` with empty outer provider (deterministic broker-unreachable proof). `ConsoleStartupGateTests` asserts 200/Healthy after host init and 503/Unhealthy when `StartupCompletionService` removed by type identity. |
| 4 | AddBaseConsoleMessaging(cfg, configureConsumers) wires the RabbitMQ host + bus-wide outbound correlation send/publish filters and accepts a concrete callback for consumers/receive endpoints | ✓ VERIFIED | `MessagingServiceCollectionExtensions.AddBaseConsoleMessaging` takes `Action<IBusRegistrationConfigurator> configureConsumers`, calls `configureConsumers(x)` as the first operation in `AddMassTransit`, wires `c.UseConsumeFilter(typeof(InboundCorrelationConsumeFilter<>), ctx)`, `c.UseSendFilter(typeof(OutboundCorrelationSendFilter<>), ctx)`, `c.UsePublishFilter(typeof(OutboundCorrelationPublishFilter<>), ctx)`, `c.Host(rabbitHost, h => { h.Username/h.Password })` via `cfg.Require` fail-fast. `ConfigureEndpoints(ctx)` present. No `ConfigureHealthCheckOptions` call (bus check keeps default `["ready","masstransit"]` tags). |
| 5 | The inbound consume filter resolves the correlation value, pushes it into an AsyncLocal accessor, and opens a MEL log scope under the literal "CorrelationId" key; the outbound filter stamps the ambient correlationId onto every outgoing ICorrelated message | ✓ VERIFIED | `InboundCorrelationConsumeFilter<T>` (open-generic, fixed in af953ea): calls `accessor.Set(corrId)` then `logger.BeginScope(new Dictionary<string, object> { [CorrelationKeys.LogScope] = corrId })`. `CorrelationKeys.LogScope == "CorrelationId"` from `Messaging.Contracts`. Both outbound filters check `context.Message is ICorrelated && Guid.TryParse(accessor.Get(), out var id)` then set `context.CorrelationId = id` (envelope only, body never mutated). `ConsoleCorrelationFilterTests` exercises inbound accessor population and outbound envelope stamp via `AddMassTransitTestHarness`. |

**Score:** 5/5 truths verified

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/BaseConsole.Core/BaseConsole.Core.csproj` | Library project; FrameworkReference + MassTransit/Redis/OTel + Messaging.Contracts ProjectReference | ✓ VERIFIED | `Sdk="Microsoft.NET.Sdk"`, `<FrameworkReference Include="Microsoft.AspNetCore.App"/>`, MassTransit/MassTransit.RabbitMQ/StackExchange.Redis/AspNetCore.HealthChecks.UI.Client/OpenTelemetry family — no Version= on any PackageReference, no BaseApi.Core/EF/MVC refs |
| `src/BaseConsole.Core/Health/StartupCompletionService.cs` | Phase-5 MarkReady-on-StartAsync hosted service (no BaseDbContext) | ✓ VERIFIED | Contains `gate.MarkReady()`, `: IHostedService`; no EF/DbContext/MigrateAsync/IServiceScopeFactory |
| `src/BaseConsole.Core/DependencyInjection/ConsoleRedisServiceCollectionExtensions.cs` | Singleton IConnectionMultiplexer soft-dep registration | ✓ VERIFIED | `AddSingleton<IConnectionMultiplexer>`, `cfg.RequireConnectionString("Redis")`; no `RedisProjectionOptions`, no `AddHealthCheck` |
| `src/BaseConsole.Core/Messaging/AsyncLocalCorrelationAccessor.cs` | AsyncLocal<string?> correlation accessor | ✓ VERIFIED | `AsyncLocal<string?>`, `public sealed class AsyncLocalCorrelationAccessor : ICorrelationAccessor` |
| `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs` | Console OTel composition on IHostApplicationBuilder | ✓ VERIFIED | `AddMeter(InstrumentationOptions.MeterName)`, `IncludeScopes = true`, no `.WithTracing`, no AspNetCore/Http instrumentation |
| `src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs` | IFilter<ConsumeContext<T>> opening the CorrelationId MEL scope | ✓ VERIFIED | `CorrelationKeys.LogScope`, `accessor.Set(`, open-generic `InboundCorrelationConsumeFilter<T> : IFilter<ConsumeContext<T>>` (fixed af953ea) |
| `src/BaseConsole.Core/Messaging/OutboundCorrelationSendFilter.cs` | IFilter<SendContext<T>> envelope stamp | ✓ VERIFIED | `: IFilter<SendContext<T>>`, `is ICorrelated`, `Guid.TryParse`, `context.CorrelationId = id` |
| `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` | AddBaseConsoleMessaging bus skeleton + bus-wide filter registration | ✓ VERIFIED | `UseConsumeFilter(typeof(InboundCorrelationConsumeFilter<>)`, `UseSendFilter`, `UsePublishFilter`, `configureConsumers(x)`, `AddSingleton<ICorrelationAccessor, AsyncLocalCorrelationAccessor>()` |
| `src/BaseConsole.Core/Health/BusReadyHealthCheck.cs` | Custom inner ready check surfacing outer IBusControl.CheckHealth() | ✓ VERIFIED | `IBusControl` (not IBusHealth — confirmed API correction), `CheckHealth()`, `BusHealthStatus`, Unhealthy fallback when bus unresolved |
| `src/BaseConsole.Core/Health/EmbeddedHealthEndpointService.cs` | Inner Kestrel hosting the three MapHealthChecks with tag predicates + UIResponseWriter | ✓ VERIFIED | `MapHealthChecks("/health/live"`, `/health/ready"`, `/health/startup"` each with `Predicate = c => c.Tags.Contains(...)` and `UIResponseWriter.WriteHealthCheckUIResponse`; `ConsoleHealth:Port ?? 8081` |
| `src/BaseConsole.Core/DependencyInjection/ConsoleHealthServiceCollectionExtensions.cs` | Outer-host gate + self/startup checks + hosted-service registration | ✓ VERIFIED | `AddHostedService<EmbeddedHealthEndpointService>`, `AddHostedService<StartupCompletionService>`, `AddSingleton<IStartupGate, StartupGate>()` |
| `src/BaseConsole.Core/DependencyInjection/BaseConsoleServiceCollectionExtensions.cs` | Non-generic AddBaseConsole composition root | ✓ VERIFIED | `public static IServiceCollection AddBaseConsole(this IServiceCollection services, IConfiguration cfg)` — non-generic, no `AddBaseConsoleObservability` or `AddBaseConsoleMessaging` in chain |
| `tests/BaseApi.Tests/Console/ConsoleTestHostFixture.cs` | In-memory Generic-Host D-02 vehicle | ✓ VERIFIED | `Host.CreateApplicationBuilder`, `AddBaseConsoleObservability`, `AddBaseConsole(`, `AddBaseConsoleMessaging(`, DEAD Redis port 6399, `abortConnect=false`, ephemeral health port |
| `tests/BaseApi.Tests/Console/ConsoleObservabilityTests.cs` | No-TracerProvider + MassTransit-meter assertions | ✓ VERIFIED | `GetService<TracerProvider>()` asserted null, `GetService<MeterProvider>()` asserted non-null, assembly-scan for AspNetCore/Http instrumentation |
| `tests/BaseApi.Tests/Console/ConsoleCorrelationFilterTests.cs` | Harness-based filter behavior proof | ✓ VERIFIED | `AddMassTransitTestHarness`, test `ICorrelated` record, inbound accessor assertion, outbound envelope CorrelationId assertion |
| `tests/BaseApi.Tests/BaseApi.Tests.csproj` | ProjectReference to BaseConsole.Core | ✓ VERIFIED | `..\..\src\BaseConsole.Core\BaseConsole.Core.csproj` present at line 120 |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `BaseConsole.Core.csproj` | `Messaging.Contracts.csproj` | ProjectReference | ✓ WIRED | `<ProjectReference Include="..\Messaging.Contracts\Messaging.Contracts.csproj" />` — sole ProjectReference |
| `SK_P.sln` | `BaseConsole.Core.csproj` | solution project entry | ✓ WIRED | `"BaseConsole.Core", "src\BaseConsole.Core\BaseConsole.Core.csproj"` present |
| `MessagingServiceCollectionExtensions.cs` | `InboundCorrelationConsumeFilter.cs` | `UseConsumeFilter(typeof(InboundCorrelationConsumeFilter<>), ctx)` | ✓ WIRED | Open-generic registration confirmed; matches the fixed filter type |
| `InboundCorrelationConsumeFilter.cs` | `Messaging.Contracts/CorrelationKeys.cs` | `BeginScope` key | ✓ WIRED | `[CorrelationKeys.LogScope] = corrId` |
| `OutboundCorrelationSendFilter.cs` | `Messaging.Contracts/ICorrelated.cs` | `is ICorrelated` gate | ✓ WIRED | `context.Message is ICorrelated` check present |
| `BaseConsoleServiceCollectionExtensions.cs` | `ConsoleHealthServiceCollectionExtensions.cs` | `.AddBaseConsoleHealth(cfg)` | ✓ WIRED | Chained in `AddBaseConsole` method body |
| `EmbeddedHealthEndpointService.cs` | `BusReadyHealthCheck.cs` | inner ready check registration | ✓ WIRED | `new BusReadyHealthCheck(_outer)` + `AddCheck<BusReadyHealthCheck>` |
| `EmbeddedHealthEndpointService.cs` | `IStartupGate.cs` | shared gate instance into inner DI | ✓ WIRED | `builder.Services.AddSingleton(_gate)` |
| `tests/BaseApi.Tests/BaseApi.Tests.csproj` | `BaseConsole.Core.csproj` | ProjectReference | ✓ WIRED | Line 120 |

### Data-Flow Trace (Level 4)

Not applicable. All Phase 18 artifacts are infrastructure library components (DI extensions, filters, hosted services) and test fixtures, not dynamic-data-rendering components. The correlation data flow is exercised behaviorally by `ConsoleCorrelationFilterTests` which asserts accessor population and envelope stamping through a real in-memory harness.

### Behavioral Spot-Checks

| Behavior | Evidence | Status |
|----------|----------|--------|
| Host boots with dead Redis + dead RabbitMQ | `ConsoleHostBootTests.Host_Boots_With_Dead_Deps_And_Bus_Is_Resolvable` GREEN (245/245) | ✓ PASS |
| /health/live returns 200 + Healthy with both deps dead | `ConsoleHealthLiveTests.Live_Returns_200_When_Redis_And_RabbitMQ_Dead` GREEN | ✓ PASS |
| BusReadyHealthCheck returns Unhealthy when bus null | `ConsoleBusReadyHealthCheckTests.BusReadyHealthCheck_Returns_Unhealthy_When_Bus_Not_Healthy` GREEN | ✓ PASS |
| No TracerProvider; MeterProvider present | `ConsoleObservabilityTests` GREEN (both assertions) | ✓ PASS |
| Inbound filter populates accessor + outbound stamps envelope | `ConsoleCorrelationFilterTests` GREEN (harness-based) | ✓ PASS |
| Full 245/245 suite GREEN 3-consecutive, dual-SHA BEFORE=AFTER | Gate evidence in 18-04-SUMMARY: psql SHA = `b202692d…` MATCH; redis SHA = `e3b0c442…` (0 keys) MATCH | ✓ PASS |

### Requirements Coverage

| Requirement | Phase Scope | Description | Status | Evidence |
|-------------|-------------|-------------|--------|----------|
| CONSOLE-01 | Phase 18 | Non-generic `AddBaseConsole`/`RunAsync`-style chain | ✓ SATISFIED | `BaseConsoleServiceCollectionExtensions.AddBaseConsole(cfg)` chains Redis + health; `ConsoleHostBootTests` proves boot |
| CONSOLE-02 | Phase 18 | Console OTel: MEL-bridge + runtime metrics + MassTransit meter via OTLP, no AspNetCore/HttpClient, no TracerProvider | ✓ SATISFIED | `BaseConsoleObservabilityExtensions`; `ConsoleObservabilityTests` asserts shape |
| CONSOLE-03 | Phase 18 | Singleton soft-dep `IConnectionMultiplexer` (abortConnect=false) | ✓ SATISFIED | `ConsoleRedisServiceCollectionExtensions.AddBaseConsoleRedis`; boot resilience proven by fixture |
| CONSOLE-04 | Phase 18 | `AddBaseConsoleMessaging(cfg, configureConsumers)` bus skeleton + bus-wide filters | ✓ SATISFIED | `MessagingServiceCollectionExtensions.AddBaseConsoleMessaging`; `ConsoleCorrelationFilterTests` |
| CONSOLE-05 | Phase 18 | `FrameworkReference Microsoft.AspNetCore.App`, `Sdk=Microsoft.NET.Sdk`, no Web SDK | ✓ SATISFIED | Confirmed in `BaseConsole.Core.csproj` |
| CONSOLE-HEALTH-01 | Phase 18 | Embedded minimal HTTP listener serving /health/live|ready|startup | ✓ SATISFIED | `EmbeddedHealthEndpointService`; `ConsoleHealthLiveTests` hits real endpoint |
| CONSOLE-HEALTH-02 | Phase 18 | /health/live returns 200 without touching RabbitMQ or Redis | ✓ SATISFIED | `/health/live` maps only `"self"` check (always-Healthy); proven GREEN with dead deps |
| CONSOLE-HEALTH-03 | Phase 18 | /health/ready reflects MassTransit bus state; Unhealthy while broker unreachable | ✓ SATISFIED | `BusReadyHealthCheck` reads `IBusControl.CheckHealth()`; `ConsoleBusReadyHealthCheckTests` positively proves Unhealthy path |
| CONSOLE-HEALTH-04 | Phase 18 | `IStartupGate` + `StartupHealthCheck` duplicated into `BaseConsole.Core` (no BaseApi.Core dependency) | ✓ SATISFIED | `IStartupGate.cs`, `StartupHealthCheck.cs`, `StartupCompletionService.cs` all in `BaseConsole.Core.Health`; `ConsoleStartupGateTests` proves 200/503 |
| CORR-01 | Phase 18 | Inbound consume filter: resolves correlation, pushes to AsyncLocal, opens MEL scope under `"CorrelationId"` | ✓ SATISFIED | `InboundCorrelationConsumeFilter<T>` uses `CorrelationKeys.LogScope`; inbound assertion in `ConsoleCorrelationFilterTests` |
| CORR-02 | Phase 18 | Outbound send/publish filter stamps ambient correlationId onto every outgoing `ICorrelated` message | ✓ SATISFIED | `OutboundCorrelationSendFilter<T>` + `OutboundCorrelationPublishFilter<T>`; outbound envelope assertion in `ConsoleCorrelationFilterTests` |

**All 11 Phase 18 requirements: 11/11 SATISFIED**

No orphaned requirements. REQUIREMENTS.md maps all 11 to Phase 18 at Complete status. Requirements scoped to later phases (CORR-03, CORR-04, ORCH-CON-*, MSG-WEBAPI-*, etc.) are correctly not claimed and not verified here.

### Anti-Patterns Found

| File | Pattern | Severity | Impact |
|------|---------|----------|--------|
| `EmbeddedHealthEndpointService.cs:95-101` | `StopAsync` calls `_app.StopAsync` but never `DisposeAsync` — inner `WebApplication` not disposed | ⚠️ Warning | Resource leak (inner DI container, Kestrel server, TCP socket held until finalizer). Identified as WR-01 in code review. Does not affect library correctness or test results but is a lifecycle hygiene gap. |
| `EmbeddedHealthEndpointService.cs:55-93` | Inner Kestrel `StartAsync` has no failure isolation — a port bind failure propagates out of `Host.StartAsync()` | ⚠️ Warning | Operational robustness gap: a collision on `ConsoleHealth:Port` (production default 8081 is a real risk) would crash the whole worker. Identified as WR-02 in code review. Not triggered by any current test since ephemeral ports are used. |

Both warnings are non-blocking for Phase 18's goal: the library is validated standalone, all probes function correctly under test conditions, and neither issue is a logic defect in the stated behaviors. They are phase-19-addressable improvements.

No STUB, MISSING, or ORPHANED artifacts found. No hardcoded placeholder values, no `return null` stubs, no TODO/FIXME markers in any production source file.

### Human Verification Required

None. All phase goal behaviors are proven by automated tests (in-memory Generic-Host fixture + AddMassTransitTestHarness). The phase explicitly uses in-memory infrastructure with no external service dependencies at validation time.

### Gaps Summary

No gaps. All 5 observable truths are VERIFIED, all 11 requirements are SATISFIED, all artifacts exist and are substantive and wired, and the phase close gate evidence (245/245 GREEN 3-consecutive, dual-SHA BEFORE=AFTER, zero-warning Release+Debug build) is documented in 18-04-SUMMARY.md with operator approval.

Two code-review warnings (WR-01: missing `DisposeAsync` on inner WebApplication; WR-02: no failure isolation on embedded listener bind) are noted as lifecycle improvements deferred to Phase 19. They do not block goal achievement.

**Notable deviation from plan correctly resolved:** The plan specified `IBusHealth` as the MassTransit bus health interface; build-time confirmation proved `IBusHealth` does not exist in MassTransit 8.5.5. The implementation correctly uses `IBusControl.CheckHealth()` → `BusHealthResult.Status`. The behavioral intent is preserved and positively proven by the committed unit test.

**Notable deviation from plan correctly resolved:** The plan specified `InboundCorrelationConsumeFilter` as a non-generic `IFilter<ConsumeContext>`. MassTransit 8.5.5 requires the scoped `UseConsumeFilter` overload to receive a generic type definition; the implementation was corrected to open-generic `InboundCorrelationConsumeFilter<T> : IFilter<ConsumeContext<T>>`. MEL scope + accessor logic unchanged; correction proven GREEN by `ConsoleCorrelationFilterTests`.

---

_Verified: 2026-05-30T10:15:00Z_
_Verifier: Claude (gsd-verifier)_
