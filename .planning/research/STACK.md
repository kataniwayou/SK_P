# Technology Stack — v3.4.0 (BaseConsole + Orchestrator Messaging)

**Project:** Steps API — v3.4.0 milestone
**Researched:** 2026-05-30
**Scope:** ONLY the NEW capabilities — `BaseConsole.Core` (Generic-Host console base), `Orchestrator` console, MassTransit/RabbitMQ messaging, `Messaging.Contracts`. The v3.3.0 stack (.NET 8.0.421, EF Core 8, Postgres 17, StackExchange.Redis 2.13.1, OpenTelemetry 1.15.x, FluentValidation 12, Mapperly, RFC 7807, health probes) is FIXED and not re-evaluated here.

---

## TL;DR — What to add to `Directory.Packages.props`

| PackageVersion | Version | Confidence | Why |
|----------------|---------|------------|-----|
| `MassTransit` | **8.5.5** | HIGH | Last stable 8.x; Apache-2.0; bus abstractions + in-memory harness types |
| `MassTransit.RabbitMQ` | **8.5.5** | HIGH | RabbitMQ transport; pulls `RabbitMQ.Client 7.x` |
| `Microsoft.AspNetCore.App` (framework ref) | n/a (shared FX) | HIGH | Enables embedded Kestrel + health-check endpoints inside the console |
| `AspNetCore.HealthChecks.UI.Client` | **9.0.0** | HIGH | Already pinned — reuse for the JSON health body writer in the console |

**No OpenTelemetry instrumentation package for MassTransit is needed** — MassTransit emits OTel natively (see Observability section). You add a string source/meter name, not a NuGet package.

**Do NOT add:** MassTransit v9.x (commercial license), `OpenTelemetry.Instrumentation.MassTransit` (deprecated/beta-only), `OpenTelemetry.Instrumentation.AspNetCore` to the console (locked decision — console has no AspNetCore instrumentation), Quartz.NET (future milestone — flagged below).

---

## CRITICAL: MassTransit licensing trap

**Pin MassTransit at 8.5.5. Do NOT let it float to 9.x.**

- MassTransit **v8.x stays Apache-2.0 forever** and receives security patches through end of 2026. [Confidence: HIGH — multiple sources + Milan Jovanović analysis.]
- MassTransit **v9.0+ (first stable 9.1.1 released 2026-05-13) is a COMMERCIAL product.** Minimum **$400/month**; a free tier "may" apply under $1M revenue (discretionary language). The NuGet package page for v9 explicitly states "MassTransit is a commercial product that must be licensed." [Confidence: HIGH.]
- The license cutover is **at the v8→v9 boundary only** — it did NOT happen mid-8.x. All 8.x releases (8.0.0 … 8.5.5) are Apache-2.0. [Confidence: HIGH.]
- **8.5.5 is the latest STABLE 8.x** (released 2025-10-22). Anything higher on NuGet (8.5.10-develop.*, 9.x) is either a prerelease `-develop` build or the commercial v9 line — avoid both. [Confidence: HIGH — verified on nuget.org.]

**CPM enforcement:** because the solution uses Central Package Management, pinning `8.5.5` in `Directory.Packages.props` is sufficient to prevent an accidental v9 pull — but add a comment block (mirroring the Npgsql 8.0.9 cautionary comment already in the file) so a future `dotnet add package` or Dependabot bump to 9.x is caught in review.

---

## Recommended Stack

### Messaging core
| Technology | Version | Purpose | Why |
|------------|---------|---------|-----|
| `MassTransit` | 8.5.5 | Bus abstractions, `IBus`/`IPublishEndpoint`/`ISendEndpointProvider`, consumer registration, filters (consume/send/publish middleware), in-memory `ITestHarness` types | The .NET-idiomatic message framework; the consume/publish filter pipeline is exactly the seam needed for inbound `correlationId → AsyncLocal + MEL scope` and outbound `stamp ICorrelated` propagation. Apache-2.0 at 8.x. |
| `MassTransit.RabbitMQ` | 8.5.5 | RabbitMQ transport (`UsingRabbitMq`), topology config, instance-unique queue binding for fan-out | The locked topology (fan-out control = each replica binds an instance-unique queue) maps directly to MassTransit's `ReceiveEndpoint` + `cfg.ConfigureEndpoints` / temporary-queue patterns. Transitively pins `RabbitMQ.Client 7.x`. |

**MassTransit.Testing note:** The in-memory test harness (`ITestHarness`, `InMemoryTestHarness`) is **part of the core `MassTransit` package** in v8 — there is NO separate `MassTransit.Testing` NuGet to add. Register via `services.AddMassTransitTestHarness(...)` from the `MassTransit` package. This is what exercises the outbound send/publish filter via a synthetic downstream send this milestone. [Confidence: HIGH — the harness types ship in core; legacy `MassTransit.TestFramework` is not required for `AddMassTransitTestHarness`.]

### Generic Host (console worker)
| Technology | Version | Purpose | Why |
|------------|---------|---------|-----|
| `Microsoft.Extensions.Hosting` | (8.0.x, shared framework via SDK) | `Host.CreateApplicationBuilder()` → `HostApplicationBuilder` (implements `IHostApplicationBuilder`) | Console-side mirror of the web API's `WebApplicationBuilder`. Critically, `HostApplicationBuilder` implements `IHostApplicationBuilder` — the SAME interface `AddBaseApiObservability` already extends — so the OTel composition-root pattern lifts over **verbatim** (no signature change). |

**Do NOT add a `Microsoft.Extensions.Hosting` PackageReference** for a .NET 8 app — but note a plain console using `Microsoft.NET.Sdk` (not `.Web`/`.Worker`) does NOT include the hosting assemblies by default. Two clean options:

1. **`Microsoft.NET.Sdk.Worker`** project SDK — gives `Microsoft.Extensions.Hosting` + worker template out of the box. Good only if NO embedded HTTP endpoints were needed.
2. **`Microsoft.NET.Sdk.Web` project SDK** (RECOMMENDED for the Orchestrator + `BaseConsole.Core` consumers) — implicitly references the `Microsoft.AspNetCore.App` shared framework, which makes embedded Kestrel + `MapHealthChecks` available. See next section.

### Embedded health-check HTTP endpoints inside the console — RECOMMENDED APPROACH
| Technology | Version | Purpose | Why |
|------------|---------|---------|-----|
| `Microsoft.AspNetCore.App` (FrameworkReference) | shared FX (8.0.x) | Provides Kestrel, routing, `AddHealthChecks`, `MapHealthChecks` | Lets the console reuse the EXISTING three-probe pattern (`/health/live|ready|startup`) and the `IStartupGate`/`StartupCompletionService` hosted-service pattern from `BaseApi.Core` with zero re-design. |
| `AspNetCore.HealthChecks.UI.Client` | 9.0.0 (already pinned) | `UIResponseWriter.WriteHealthCheckUIResponse` JSON body | Same writer the API uses — keep health response shape identical across API and console. |

**The exact mechanism (HIGH confidence):**

`WebApplication.CreateBuilder(args)` IS a Generic Host — `WebApplicationBuilder` wraps the same `HostApplicationBuilder` and exposes `IHostApplicationBuilder`. For a console worker that also needs HTTP health endpoints, use:

```csharp
var builder = WebApplication.CreateBuilder(args);   // Generic Host + Kestrel
// builder is IHostApplicationBuilder -> AddBaseConsoleObservability(builder, cfg) lifts verbatim
builder.AddBaseConsole(...);          // OTel logs+metrics, Redis, health checks
builder.AddBaseConsoleMessaging(...); // MassTransit + RabbitMQ
var app = builder.Build();
app.MapHealthChecks("/health/live",    new() { Predicate = r => r.Tags.Contains("self") });
app.MapHealthChecks("/health/ready",   new() { Predicate = r => r.Tags.Contains("ready") });
app.MapHealthChecks("/health/startup", new() { Predicate = r => r.Tags.Contains("startup") });
await app.RunAsync();
```

- This is the **simplest correct way** to get Kestrel + `MapHealthChecks` inside a long-running message-consumer host. The MassTransit consumers run as hosted services started by the same host; Kestrel serves the probes on the side. [Confidence: HIGH.]
- The `ready` probe flips when the MassTransit bus has started — **MassTransit auto-registers a bus health check tagged `ready`** (see Health section), so the locked "ready flips when the bus has started" requirement is satisfied with NO custom check. [Confidence: HIGH.]
- **`AspNetCore` OTel instrumentation must NOT be added** in the console (locked decision). Using `WebApplication`/Kestrel does not force it — instrumentation is opt-in via `AddAspNetCoreInstrumentation()`, which `AddBaseConsoleObservability` simply will not call. Health-probe HTTP traffic generating metrics is therefore a non-issue. [Confidence: HIGH.]

**Alternative considered (rejected):** raw `Host.CreateApplicationBuilder()` (`Microsoft.NET.Sdk.Worker`) + a hand-rolled `HttpListener`/TCP probe. Rejected because it forfeits the existing tag-disciplined three-probe pattern, the JSON body writer, and the auto-registered MassTransit `ready` check — re-implementing all three by hand is pure churn against a validated pattern. A TCP-probe approach is only worth it when you explicitly want to avoid the AspNetCore framework; here the framework is already a first-class dependency of the sibling API, so the cost is zero.

### Messaging.Contracts (shared assembly)
| Technology | Version | Purpose | Why |
|------------|---------|---------|-----|
| (no new package) | — | `StartOrchestration`/`StopOrchestration` records, `ICorrelated` vocabulary, L2 root shape | Contracts assembly should be **dependency-light**: plain records + interfaces. MassTransit discovers message types structurally — contracts do NOT need to reference MassTransit. Keep it referencing only `System.*` so both WebApi and Orchestrator can depend on it without dragging the bus into the contract layer. [Confidence: HIGH — MassTransit message contracts are POCOs by design.] |

### RabbitMQ (compose dev stack)
| Technology | Version | Purpose | Why |
|------------|---------|---------|-----|
| `rabbitmq` Docker image | **`4.1.8-management-alpine`** | Broker for the dev compose stack | 4.1 is the proven, widely-deployed current series; `-management` gives the management UI (:15672) for dev inspection; `-alpine` keeps the image ~70–93 MB. 4.2.0 exists but is very recent — stay on the 4.1 line for dev stability. The project's convention is EXACT image pins (`postgres:17-alpine`, `redis:7.4-alpine`), so pin **`rabbitmq:4.1.8-management-alpine`**. [Confidence: HIGH — verified on Docker Hub; 4.1.8 latest in the 4.1 line.] |

**Compose healthcheck (HIGH confidence):**
```yaml
healthcheck:
  test: ["CMD", "rabbitmq-diagnostics", "-q", "ping"]
  interval: 30s
  timeout: 10s
  retries: 5
  start_period: 40s
```
`rabbitmq-diagnostics -q ping` is the canonical liveness probe. WebApi/Orchestrator services should `depends_on: { rabbitmq: { condition: service_healthy } }` for the Start/Stop path (RabbitMQ is a HARD dependency for that path per the locked decision; CRUD is unaffected because the bus is only touched on Start/Stop publish).

---

## OpenTelemetry instrumentation story for MassTransit

**MassTransit emits OTel natively — no instrumentation NuGet package.** [Confidence: HIGH — official docs + open-telemetry/opentelemetry-dotnet-contrib issue #778 confirms the standalone `OpenTelemetry.Instrumentation.MassTransit` package is deprecated because "MassTransit v8.0.0 and later has built-in OTEL support".]

How it wires into the EXISTING `ObservabilityServiceCollectionExtensions` pattern:

- **Tracing:** MassTransit publishes spans under an `ActivitySource` named `DiagnosticHeaders.DefaultListenerName` (the string `"MassTransit"`). You would add it via `.WithTracing(t => t.AddSource("MassTransit"))`. **BUT** the locked decision is NO traces backend (`.WithTracing` is deliberately stripped from `AddBaseApiObservability`). So the console's `AddBaseConsoleObservability` should ALSO omit `.WithTracing` entirely. MassTransit will still create `Activity` objects, but with no listener/exporter they are cheap no-ops. **No action needed; do not add a tracing pipeline.** [Confidence: HIGH.]

- **Metrics (THE relevant one for metrics-only, no-traces):** MassTransit exposes a `Meter` whose name is `InstrumentationOptions.MeterName` (the string `"MassTransit"`). Add it to the metrics pipeline:
  ```csharp
  builder.Services.AddOpenTelemetry()
    .ConfigureResource(...)
    .WithMetrics(m => m
      .AddMeter(InstrumentationOptions.MeterName)  // "MassTransit" — bus counters/gauges/histograms
      .AddRuntimeInstrumentation()                 // process.runtime.* (lifted from API)
      .AddOtlpExporter());
  ```
  MassTransit's meter emits receive/consume/send/publish counters, in-flight gauges, and consume-duration histograms — exactly the operational metrics wanted at the OTel Collector → Prometheus. [Confidence: HIGH — official observability docs enumerate the counter/gauge/histogram tables.]

- **Console-flavored OTel (per locked decision):** logs via MEL bridge (`builder.Logging.AddOpenTelemetry`, `IncludeScopes=true` — reuse the `"CorrelationId"` scope key) + metrics = `MassTransit` meter + `AddRuntimeInstrumentation()`. **NO** `AddAspNetCoreInstrumentation()`, **NO** `AddHttpClientInstrumentation()` (the orchestrator makes no outbound HTTP this milestone). **NO** `.WithTracing`.

**Integration verdict:** the existing `AddBaseApiObservability(this IHostApplicationBuilder, IConfiguration)` signature ports to `AddBaseConsoleObservability` with two edits: drop the AspNetCore/HttpClient instrumentation lines, add `.AddMeter(InstrumentationOptions.MeterName)`. The OTLP exporter, resource builder, and MEL-bridge logs block are byte-for-byte reusable.

---

## MassTransit ↔ RabbitMQ connection & health considerations

- **Connection resilience:** MassTransit manages the RabbitMQ connection lifecycle with built-in retry/reconnect. Unlike the Redis "soft dependency / `abortConnect=false`" pattern, the bus is a HARD dependency for Start/Stop — but MassTransit's `IBusControl` start is resilient: if RabbitMQ is briefly unavailable at boot, the bus keeps retrying the connection rather than crashing the host. The compose `depends_on: service_healthy` gate makes a clean-boot race unlikely. [Confidence: HIGH.]
- **Bus health check (auto-registered):** `AddMassTransit(...)` registers an ASP.NET Core health check for the bus, tagged **`ready`** (and `masstransit`). With the embedded-Kestrel approach, mapping `/health/ready` with `Predicate = r => r.Tags.Contains("ready")` makes the readiness probe automatically reflect bus connectivity — the locked "ready flips when the bus has started" requirement is satisfied for free. The bus check reports `Healthy` once the broker connection is established and endpoints are ready. [Confidence: HIGH — MassTransit health-check integration is a documented first-class feature.]
- **`/health/live` discipline:** keep `live` tagged `self`-only (never touches the bus or Redis) — mirrors the API's Pitfall-15 discipline so a transient broker blip does not kill the pod via the liveness probe.
- **`RabbitMQ.Client` version:** MassTransit.RabbitMQ 8.5.5 pulls `RabbitMQ.Client >= 7.1.2`. RabbitMQ.Client 7.x fully supports .NET 8 and is the modern async-first client. No manual pin of `RabbitMQ.Client` is required (and is discouraged — let MassTransit own that transitive version). [Confidence: HIGH — verified the 8.5.5 dependency declaration.]

---

## .NET 8 vs 9 compatibility traps (flagged)

1. **MassTransit 9.x = commercial.** (Covered above — the headline trap.) Pin 8.5.5; comment the CPM entry.
2. **MassTransit 8.5.x supports BOTH net8.0 and net9.0** — pinning 8.5.5 does not force a .NET 9 runtime. Stays on the pinned .NET 8.0.421 SDK / `net8.0` TFM. [Confidence: HIGH — package targets net8.0, netstandard2.0, net472.]
3. **`RabbitMQ.Client 7.x`** is net8.0-clean; no net9-only surface leaks. [Confidence: HIGH.]
4. **`AspNetCore.HealthChecks.*` 9.0.0 packages run fine on the .NET 8 shared framework** — already proven in `BaseApi.Service` (the API pins `AspNetCore.HealthChecks.NpgSql 9.0.0` against net8.0). The 9.0.0 versioning is the library's own cadence, not a runtime requirement. [Confidence: HIGH — existing codebase precedent.]
5. **OpenTelemetry 1.15.x** (already pinned) supports the `AddMeter`/`AddSource` string overloads needed for MassTransit — no OTel version bump required. [Confidence: HIGH.]

---

## FUTURE-milestone dependency (flagged, NOT researched this milestone)

- **Quartz.NET** — the orchestrator's scheduler is explicitly OUT OF SCOPE this milestone (the deliverable stops at the "scheduler job start" log seam). When the Processor milestone adds the scheduler, the relevant packages will be `Quartz` + `Quartz.Extensions.Hosting` (Generic-Host integration), and potentially `MassTransit.Quartz` (8.5.x line, Apache-2.0) if message-based scheduling is wanted. **Do NOT add any Quartz package in v3.4.0.** The bus-world `Guid CorrelationId` minted per-trigger by Quartz (per the locked CorrelationId reconciliation decision) is a future concern. [Flagged only — not version-verified.]

---

## Installation (csproj `<PackageReference>` — versions resolved by CPM)

`Directory.Packages.props` additions:
```xml
<!-- v3.4.0 / Messaging — MassTransit PINNED at last Apache-2.0 8.x.
     DO NOT bump to 9.x: v9 (first stable 9.1.1, 2026-05-13) is a COMMERCIAL
     product ($400/mo minimum). v8.x stays Apache-2.0; patched through end-2026. -->
<PackageVersion Include="MassTransit" Version="8.5.5" />
<PackageVersion Include="MassTransit.RabbitMQ" Version="8.5.5" />
```

`BaseConsole.Core.csproj` (the reusable library) — use `Microsoft.NET.Sdk.Web` so the shared framework is available, OR keep it `Microsoft.NET.Sdk` with an explicit `<FrameworkReference Include="Microsoft.AspNetCore.App" />`:
```xml
<ItemGroup>
  <FrameworkReference Include="Microsoft.AspNetCore.App" />  <!-- Kestrel + health checks -->
  <PackageReference Include="MassTransit" />
  <PackageReference Include="MassTransit.RabbitMQ" />
  <PackageReference Include="AspNetCore.HealthChecks.UI.Client" />
  <!-- StackExchange.Redis lifted from BaseApi.Core dependency set -->
  <PackageReference Include="StackExchange.Redis" />
  <PackageReference Include="OpenTelemetry" />
  <PackageReference Include="OpenTelemetry.Extensions.Hosting" />
  <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" />
  <PackageReference Include="OpenTelemetry.Instrumentation.Runtime" />
</ItemGroup>
```

`Messaging.Contracts.csproj` — `Microsoft.NET.Sdk`, NO MassTransit reference (POCO contracts only).

`Orchestrator.csproj` — `Microsoft.NET.Sdk.Web`; references `BaseConsole.Core` + `Messaging.Contracts`.

Compose:
```yaml
rabbitmq:
  image: rabbitmq:4.1.8-management-alpine
  ports: ["5672:5672", "15672:15672"]
  healthcheck:
    test: ["CMD", "rabbitmq-diagnostics", "-q", "ping"]
    interval: 30s
    timeout: 10s
    retries: 5
    start_period: 40s
```

---

## Alternatives Considered

| Category | Recommended | Alternative | Why Not |
|----------|-------------|-------------|---------|
| Message framework | MassTransit 8.5.5 | MassTransit 9.x | Commercial ($400/mo); no business case this milestone |
| Message framework | MassTransit 8.5.5 | OpenTransit (community v8 fork) | Real option, but unproven fork; v8 is patched through end-2026 — no urgency to leave the canonical package. Re-evaluate at the v8 EOL milestone. |
| Message framework | MassTransit 8.5.5 | Raw `RabbitMQ.Client` | Loses consume/publish filter pipeline = the exact seam needed for CorrelationId propagation; would hand-roll topology + retry |
| MassTransit OTel | native (built-in) | `OpenTelemetry.Instrumentation.MassTransit` | Deprecated (contrib issue #778); beta-only; superseded by built-in support since v8.0 |
| Console host + probes | `WebApplication` (Kestrel) | `Host.CreateApplicationBuilder` + TCP/HttpListener probe | Forfeits the validated tag-disciplined 3-probe pattern, JSON body writer, and auto-registered MT `ready` check |
| RabbitMQ image | `rabbitmq:4.1.8-management-alpine` | `4.2.0-management-alpine` | 4.2 too new for a dev baseline; 4.1 is the proven series |
| RabbitMQ image | `...-management-alpine` | plain `rabbitmq:4.1.8` | Management UI is valuable for dev message inspection; alpine keeps size down |

---

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| MassTransit version + licensing | HIGH | nuget.org verified 8.5.5 stable Apache-2.0; v9 commercial confirmed across 4 sources |
| Native OTel (no instrumentation pkg) | HIGH | Official observability docs + contrib issue #778 |
| Embedded Kestrel health endpoints | HIGH | `WebApplication` is a Generic Host; existing API pattern lifts directly |
| MassTransit auto `ready` health check | HIGH | Documented first-class MT feature |
| RabbitMQ image + healthcheck | HIGH | Docker Hub tags + canonical `rabbitmq-diagnostics -q ping` |
| RabbitMQ.Client transitive version | HIGH | 8.5.5 declares `RabbitMQ.Client >= 7.1.2` |
| MassTransit.Testing harness in core | HIGH | `AddMassTransitTestHarness` ships in the `MassTransit` package |

## Sources

- [MassTransit on NuGet (v9.1.1 stable, commercial)](https://www.nuget.org/packages/MassTransit) — HIGH
- [MassTransit 8.5.5 on NuGet (Apache-2.0, net8.0, 2025-10-22)](https://www.nuget.org/packages/MassTransit/8.5.5) — HIGH
- [MassTransit.RabbitMQ 8.5.5 on NuGet](https://www.nuget.org/packages/MassTransit.RabbitMQ/8.5.5) — HIGH
- [.NET library MassTransit going commercial with V9 — Hacker News](https://news.ycombinator.com/item?id=43565690) — MEDIUM
- [MediatR and MassTransit Going Commercial — Milan Jovanović](https://www.milanjovanovic.tech/blog/mediatr-and-masstransit-going-commercial-what-this-means-for-you) — HIGH
- [MassTransit Commercial License](https://massient.com/license) — HIGH
- [MassTransit Observability docs (ActivitySource + Meter names)](https://masstransit.massient.com/documentation/configuration/observability) — HIGH
- [opentelemetry-dotnet-contrib issue #778 — deprecate standalone MT instrumentation](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/issues/778) — HIGH
- [Instrument MassTransit with OpenTelemetry in .NET (OneUptime, 2026-02)](https://oneuptime.com/blog/post/2026-02-06-instrument-masstransit-message-bus-opentelemetry-dotnet/view) — MEDIUM
- [ASP.NET Core health checks — Microsoft Learn (8.0)](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-8.0) — HIGH
- [WorkerService + HealthCheck gist](https://gist.github.com/CarlosLanderas/d62e3b21d27c9cece31fe147bed467c9) — LOW (pattern reference)
- [rabbitmq Official Image — Docker Hub](https://hub.docker.com/_/rabbitmq/) — HIGH
- [rabbitmq:4.1-management-alpine tags (4.1.8 latest)](https://hub.docker.com/_/rabbitmq/tags?name=4.1) — HIGH
- [rabbitmq-diagnostics man page](https://www.rabbitmq.com/docs/man/rabbitmq-diagnostics.8) — HIGH
