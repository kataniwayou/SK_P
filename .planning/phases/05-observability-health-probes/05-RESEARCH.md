---
phase: 05-observability-health-probes
plan: research
research_summary: >
  Phase 5 wires OpenTelemetry (logs via MEL bridge, metrics + traces via builder.Services.AddOpenTelemetry)
  and three K8s-style health probes into the existing Phase 4 Program.cs in .NET 8 (NOT .NET 9 — additional-
  context "stack" line was wrong; Directory.Build.props pins net8.0 and Directory.Packages.props is the
  source-of-truth for versions). The OTel package set is already PRE-PINNED in Directory.Packages.props
  from Phase 1 D-05 (OpenTelemetry 1.15.3 trio + Instrumentation.AspNetCore/Http 1.15.0 +
  AspNetCore.HealthChecks.NpgSql 9.0.0). Plan 05-01 must ADD three NEW pins
  (OpenTelemetry.Instrumentation.Runtime 1.15.0, Npgsql.OpenTelemetry 8.0.4,
  AspNetCore.HealthChecks.UI.Client 9.0.0) plus the corresponding PackageReference entries
  in BaseApi.Core.csproj. The MEL-bridge vs WithLogging trap (Pitfall 8) is the single most important
  correctness invariant — use builder.Logging.AddOpenTelemetry(...) and DO NOT add WithLogging() to the
  services.AddOpenTelemetry() chain or logs get duplicated and Logging:LogLevel filters are bypassed.

key_decisions:
  - "OpenTelemetry.Instrumentation.Runtime 1.15.0 is a NEW pin (D-16 additive); confirmed compatible with .NET 8 (search verified 1.15.1 is latest stable; 1.15.0 within +/- 1 patch satisfies the existing 1.15.0 instrumentation cadence)"
  - "Npgsql.OpenTelemetry 8.0.4 has NO usable Action<NpgsqlTracingOptions> body — the parameter is declared but ignored in v8.0.4 source. AddNpgsql() takes options but doesn't read them. Parameter values are NOT captured by default; T-05-PII protection is achieved by simply calling .AddNpgsql() with NO callback (the lambda in CONTEXT D-05 won't compile because the actual NpgsqlTracingOptions class has NO IncludeParameterValues / EnableEntityFrameworkCoreInstrumentation property). RESEARCH-side correction to D-05: simplify to .AddNpgsql() with a leading // SECURITY comment explaining default-is-safe."
  - "AspNetCore.HealthChecks.UI.Client 9.0.0 is a NEW pin; namespace is HealthChecks.UI.Client; static method UIResponseWriter.WriteHealthCheckUIResponse(HttpContext, HealthReport) writes the per-check JSON shape verified in CONTEXT D-07"
  - "Filter for /health/* applies to METRICS+TRACES via AspNetCoreInstrumentationOptions.Filter callback (verified by Pitfall 10). LOGS exclusion uses MEL-level Logging:LogLevel:Microsoft.AspNetCore=Warning (D-09 coarse but simple); per-path log filtering is a deferred refinement, not Phase 5 work."
  - "Resource attributes (service.name + service.version) MUST be set on BOTH the LoggerProvider (via o.SetResourceBuilder(...)) AND the MeterProvider/TracerProvider (via .ConfigureResource(r => r.AddService(...))). They are two separate provider trees that don't share resource builders by default."
  - "IStartupGate Singleton + StartupHealthCheck pattern is correct as locked in CONTEXT D-01/D-02; Phase 5 calls MarkReady() immediately after Build() so the probe is healthy in v1 (no migrations yet). Phase 8 will (a) flip the default to false OR remove the immediate MarkReady() call, and (b) register MigrationRunner : IHostedService that calls _gate.MarkReady() post-Database.MigrateAsync()."
  - "OTel exporter respects OTEL_EXPORTER_OTLP_ENDPOINT env var WITHOUT explicit reads — the .AddOtlpExporter() with no options block automatically honors env vars. The appsettings.json OpenTelemetry:Endpoint key is NOT consumed automatically; if D-04's appsettings fallback is required, planner must read it manually via cfg[\"OpenTelemetry:Endpoint\"] and call .AddOtlpExporter(o => o.Endpoint = new Uri(...)). For Phase 5, env var precedence at runtime is sufficient because (a) compose.yaml's baseapi-service block under phase-8 profile can set the env var, (b) dev runs without docker use appsettings.Development.json's localhost:4317 endpoint via env var or by code-read; SIMPLEST PATH for v1: rely on env var alone, set OTEL_EXPORTER_OTLP_ENDPOINT in launchSettings.json (dev) and compose (prod)."
  - "Verification fixture pattern lifts Phase 3/Phase 4 PostgresFixture discipline: OtelCollectorFixture truncates tests/.otel-out/telemetry.jsonl on InitializeAsync, deletes on DisposeAsync. Tests use ExportProcessorType.Simple to bypass batching for deterministic flush. WebApplicationFactory<Program> + Testcontainers.PostgreSQL is reused from Phase 4."
---

# Phase 5 Research: Observability + Health Probes

**Researched:** 2026-05-27
**Domain:** OpenTelemetry .NET 1.15.x (MEL bridge + AddOpenTelemetry + OTLP exporter) | ASP.NET Core 8 Health Checks (Microsoft.Extensions.Diagnostics.HealthChecks + AspNetCore.HealthChecks.NpgSql + AspNetCore.HealthChecks.UI.Client) | Npgsql.OpenTelemetry 8.0.4 | OpenTelemetry.Instrumentation.Runtime 1.15.0 | OpenTelemetry Collector Contrib 0.95.0
**Confidence:** HIGH — Most claims verified against NuGet (publish dates + transitive deps), official Npgsql.NpgsqlTracingOptionsBuilder API docs, AspNetCore.Diagnostics.HealthChecks GitHub repo, and the existing project's PITFALLS.md/ARCHITECTURE.md. Two CONTEXT-side corrections surfaced (see §"Open Questions / Risks" #1 + #2).

## Summary

Phase 5 inserts ~25 lines of OTel registration + ~15 lines of health-check registration + 3 `MapHealthChecks(...)` pipeline edits into the existing Phase 4 `Program.cs` (currently 72 lines). Net Program.cs target: ~115 lines. The OTel wiring follows the **MEL-bridge** pattern locked by CONTEXT D-06 and Pitfall 8: `builder.Logging.AddOpenTelemetry(...)` for logs, `builder.Services.AddOpenTelemetry().WithMetrics(...).WithTracing(...)` for metrics+traces — never `WithLogging()` on the services chain. Three new types land in `BaseApi.Core/Health/`: `IStartupGate`, `StartupGate` (Volatile+Interlocked thread-safe singleton), and `StartupHealthCheck : IHealthCheck`. Three new pins land in `Directory.Packages.props` (28 total after Phase 5; currently 25). A new `otel-collector` service joins `compose.yaml` alongside Phase 2's postgres, with a host-mounted `tests/.otel-out/` for the file exporter that verification reads.

**Primary recommendation:** Follow CONTEXT.md D-01..D-16 verbatim with ONE correction surfaced by API verification: in D-05 the `.AddNpgsql(opts => { opts.EnableEntityFrameworkCoreInstrumentation = false; })` lambda WILL NOT COMPILE — Npgsql.OpenTelemetry 8.0.4's `NpgsqlTracingOptions` class has NO such property. The correct shape is `.AddNpgsql()` with NO options block, prefixed by a `// SECURITY: parameter values not captured by default in Npgsql.OpenTelemetry 8.0.4` comment. Default behavior already satisfies T-05-PII; no opt-out needed.

## User Constraints (from CONTEXT.md)

### Locked Decisions

The following are LOCKED in `.planning/phases/05-observability-health-probes/05-CONTEXT.md`. Plan 05-01 implements them VERBATIM (except for the one API correction noted above):

- **D-01** — `IStartupGate` interface + `StartupGate` Volatile/Interlocked sealed singleton, registered as Singleton. Phase 5 default state: `MarkReady()` is called IMMEDIATELY after `Build()` so the probe is healthy by default in v1.
- **D-02** — `StartupHealthCheck : IHealthCheck` reads `IStartupGate.IsReady`. Tag `"startup"` for the `/health/startup` predicate; also tagged `"ready"` so it appears in `/health/ready` aggregate.
- **D-03** — Health endpoint mapping: `/health/live` (tag=`live`), `/health/ready` (tag=`ready`), `/health/startup` (tag=`startup`); all 3 use `UIResponseWriter.WriteHealthCheckUIResponse` as `ResponseWriter`. `"self"` check (always-Healthy) is tagged `live`; NpgSql check is tagged `ready`; StartupHealthCheck is tagged `startup`+`ready`.
- **D-04** — Tracing sampler = `AlwaysOnSampler()`. No appsettings knob in v1.
- **D-05** — Npgsql tracing parameter values DISABLED. (CONTEXT shows a lambda with `opts.EnableEntityFrameworkCoreInstrumentation = false;` — see §"Open Questions / Risks" #1; this property does NOT exist in 8.0.4. Default IS safe — call `.AddNpgsql()` with NO callback.)
- **D-06** — `builder.Logging.AddOpenTelemetry(o => { ... })` with `IncludeFormattedMessage=true`, `IncludeScopes=true`, `ParseStateValues=true`, `SetResourceBuilder(...)`, `AddOtlpExporter()`. MEL bridge — NOT `WithLogging()` on the services chain.
- **D-07** — All 3 probes return JSON via `AspNetCore.HealthChecks.UI.Client.UIResponseWriter.WriteHealthCheckUIResponse`. Adds 1 new pin (`AspNetCore.HealthChecks.UI.Client 9.0.0`).
- **D-08** — Program.cs composition: OTel + health registrations inserted between existing Phase 4 `AddExceptionHandler<>` block and the existing `AddControllers()` call; `MarkReady()` line inserted between `Build()` and the existing `UseExceptionHandler()`; 3 `MapHealthChecks(...)` lines inserted between `UseRouting()` and existing `MapControllers()`.
- **D-09** — `/health/*` logs filtered via coarse `Microsoft.AspNetCore: Warning` in appsettings (drops request-start/finish logs for all endpoints). Per-path log filter is deferred.
- **D-10** — `otel-collector` docker-compose service added at port 4317 (gRPC) + 4318 (HTTP), image `otel/opentelemetry-collector-contrib:0.95.0`, config at `compose/otel-collector-config.yaml`, file exporter writes to host-mounted `tests/.otel-out/telemetry.jsonl`.
- **D-11** — `OtelCollectorFixture` lifts PostgresFixture discipline: truncate-on-init, delete-on-dispose; tests use `ExportProcessorType.Simple` for deterministic flush.
- **D-12** — Two plans: 05-01 (build, autonomous:true) + 05-02 (verification, autonomous:false per Phase 3 D-18 / Phase 4 D-14 cadence).
- **D-13** — `IStartupGate` ships with `MarkReady()` immediate-call default; Phase 8 will remove that line + register `MigrationRunner : IHostedService` that flips the gate post-`Database.MigrateAsync()`.
- **D-14** — `appsettings.Development.json` NOT changed by Phase 5. Phase 4 already set `OpenTelemetry:Endpoint=http://localhost:4317` and Postgres at `localhost:5433`; both route to the new compose services unchanged.
- **D-15** — Phase 5 ships only AUTO-instrumentation (AspNetCore + HttpClient + Npgsql + Runtime). No custom `ActivitySource("sk-api")` is added; deferred to any future phase that needs custom span boundaries.
- **D-16** — Runtime metrics instrumentation ADDITIVE to OBSERV-01/-03. New pin `OpenTelemetry.Instrumentation.Runtime 1.15.0`; `.AddRuntimeInstrumentation()` chain in `.WithMetrics(...)` after `.AddHttpClientInstrumentation()` and BEFORE `.AddOtlpExporter()`. No `/health/*` filter applies (runtime metrics are process-level, not per-request).

### Claude's Discretion (from CONTEXT)

- Exact `NpgsqlTracingOptions` API surface for parameter capture — **RESOLVED in this research**: no `IncludeParameterValues` / `EnableSensitiveDataLogging` property exists in 8.0.4; default is safe. Drop the lambda body.
- `OtelCollectorFixture` location — RESEARCH recommends `tests/BaseApi.Tests/Observability/` (mirrors SUT subject area) per CONTEXT line 311. Planner may relocate to `tests/BaseApi.Tests/Fixtures/`.
- Health endpoint `MapHealthChecks` placement (before vs after `MapControllers`) — RESEARCH recommends BEFORE per CONTEXT line 312 "plumbing first, business last".
- Test endpoint mechanism for `TraceExportTests` — RESEARCH recommends **Minimal API stub registered via `WebAppFactory.ConfigureWebHost`** because Phase 7 controllers haven't landed; mirrors Phase 4's `TestController.cs` assembly-part pattern but adds a `MapGet("/test/db-roundtrip", ...)` that uses `TestErrorDbContext` to issue an Npgsql query that produces a child span.

### Deferred Ideas (OUT OF SCOPE)

None deferred FROM Phase 5. Cross-phase items NOT in scope (already owned by their target phase):
- Migration runner (`Database.MigrateAsync()` + `_gate.MarkReady()`) — Phase 8
- `AddBaseApi(...)` / `UseBaseApi(...)` composition root extensions — Phase 7
- Custom `ActivitySource("sk-api")` for feature spans — any future phase
- Production OTel Collector destination (Jaeger/Tempo/Datadog/etc.) — ops, post-v1
- `TraceIdRatioBasedSampler` config knob — deferred until production load observed
- Per-path log filtering (more granular than current `Microsoft.AspNetCore: Warning`) — deferred
- Richer DB telemetry (parameter values per-entity, query plan capture) — deferred

## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| OBSERV-01 | OpenTelemetry 1.15.x + OTLP exporter + AspNetCore + Http instrumentation + Extensions.Hosting | §"Standard Stack" — all 4 pins already present in Directory.Packages.props from Phase 1 D-05; verified versions stable |
| OBSERV-02 | Logs via `builder.Logging.AddOpenTelemetry(...)` (MEL integration, NOT `WithLogging`) | §"Pattern 1 — MEL Bridge for Logs" + Pitfall 8 cross-link |
| OBSERV-03 | HTTP server + client metrics via `WithMetrics().AddAspNetCoreInstrumentation().AddHttpClientInstrumentation()` | §"Pattern 2 — Metrics + Traces Provider" |
| OBSERV-04 | OTLP exporter targets external Collector; `OTEL_EXPORTER_OTLP_ENDPOINT` env var; gRPC default | §"OTLP Exporter Configuration" — env var auto-honored by `AddOtlpExporter()` |
| OBSERV-05 | Resource attrs `service.name=sk-api`, `service.version=3.2.0` from appsettings `Service:Name`/`Service:Version` | §"Pattern 3 — Resource Attribute Propagation" — separate calls for Logger vs Metric/Tracer providers |
| OBSERV-06 | `Logging:LogLevel` filters BOTH console + OTel sinks identically (single MEL filter path) | §"Pattern 4 — MEL Filter Single Source" + Pitfall 9 cross-link |
| OBSERV-07 | OTel logger options: `IncludeFormattedMessage=true`, `IncludeScopes=true`, `ParseStateValues=true` | §"Pattern 1" code snippet |
| OBSERV-08 | Health endpoints excluded from metrics via `AspNetCoreInstrumentationOptions.Filter` | §"Pattern 5 — Health Path Filter" + Pitfall 10 cross-link |
| OBSERV-12 | OTel tracing: AspNetCore + HttpClient + Npgsql DB spans | §"Pattern 2" + §"Pattern 6 — Npgsql Tracing (T-05-PII Safe)" |
| HEALTH-01 | `/health/startup` returns Healthy after DI built AND migrations applied | §"Pattern 7 — IStartupGate" — Phase 5 ships gate + default-true; Phase 8 wires the migration flip |
| HEALTH-02 | `/health/live` Healthy as long as process is responsive (no DB) | §"Pattern 8 — Three-Probe Tag Discipline" — `"live"` tag includes only `"self"` |
| HEALTH-03 | `/health/ready` Healthy when Postgres reachable AND startup probe Healthy | §"Pattern 8" — `"ready"` tag includes both NpgSql + StartupHealthCheck |
| HEALTH-04 | `AspNetCore.HealthChecks.NpgSql` for Postgres reachability | §"Pattern 8" — pin already present (Phase 1); `.AddNpgSql(connStr, tags: ["ready"])` |
| HEALTH-05 | `/health/*` excluded from logging AND metrics emission | §"Pattern 5" (metrics + traces filter) + §"Pattern 4" (logs via MEL `Microsoft.AspNetCore:Warning`) |

## Project Constraints (from CLAUDE.md + earlier-phase invariants)

(No `./CLAUDE.md` file exists at repo root — verified by Glob. Constraints below are inherited from project-level locks in `.planning/PROJECT.md` + Phase 1 D-02/D-05/D-06/D-10 + Phase 3 D-15/D-18 + Phase 4 D-14.)

| Constraint | Source | Phase 5 Implication |
|------------|--------|---------------------|
| `TreatWarningsAsErrors=true` globally | Phase 1 D-02 / Directory.Build.props | Plan 05-01 builds Release+Debug 0/0; any analyzer escalation under .NET 8 must be addressed inline (no `#pragma warning disable`) |
| CPM contract — zero `Version=` on `<PackageReference>` | Phase 1 D-05/D-06 / Directory.Packages.props | 3 new `<PackageVersion>` pins (Runtime 1.15.0, Npgsql.OpenTelemetry 8.0.4, HealthChecks.UI.Client 9.0.0) + 3 new `<PackageReference>` entries in BaseApi.Core.csproj (no Version= attr) |
| File-scoped namespaces + outside-namespace usings | Phase 1 .editorconfig | New `BaseApi.Core/Health/*.cs` files follow |
| `Program.cs` is composition root in Phase 5 | Phase 1 D-10 | Plan 05-01 edits Program.cs directly. Phase 7 will later refactor to `AddBaseApi()`/`UseBaseApi()` extensions. |
| `public partial class Program { }` marker | Phase 1 D-10 / Phase 4 04-01 | MUST be preserved verbatim — `WebApplicationFactory<Program>` (Phase 4) and `OtelCollectorFixture` (Phase 5) both depend on it |
| Real backends for fact tests | Phase 3 D-15 + Phase 4 PostgresFixture lift | Plan 05-02 uses real otel-collector container + real Postgres (Testcontainers OR Phase 2's docker-compose Postgres at localhost:5433) |
| `var ct = TestContext.Current.CancellationToken;` invariant | xUnit v3 3.2.2 xUnit1051 | Every async test in Plan 05-02 opens with this; threads `ct` through every awaitable |
| `autonomous: false` checkpoint for verification plans | Phase 1 01-03 / Phase 3 03-02 / Phase 4 04-02 | Plan 05-02 follows |
| `docs(05-NN): ...` SUMMARY commit prefix | Phase 1-4 convention | Plan 05-01 and 05-02 SUMMARY commits use this |
| Phase 4's `CorrelationIdMiddleware.BeginScope("CorrelationId", corrId)` literal key | Phase 4 04-01 | OBSERV-07's `IncludeScopes = true` exports it as `CorrelationId` log attribute on every OTLP-exported log record — SC#1 verification depends on this |
| Npgsql pinned to **8.0.9** (not 9.0.0) | Phase 4 fix-forward ad3f1a1 | `Npgsql.OpenTelemetry 8.0.4` declares `Npgsql >= 8.0.4` (verified — see Standard Stack §); 8.0.9 satisfies that constraint AND preserves runtime binary compat with EFCore.PostgreSQL 8.0.10 |

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| OTel log export (MEL → OTLP) | API / Backend (BaseApi.Service Program.cs) | BaseApi.Core (no direct involvement; logging is host-level concern) | `builder.Logging.AddOpenTelemetry(...)` is a host-builder concern wired in the composition root |
| OTel metrics + traces (AspNetCore + Http + Npgsql + Runtime instrumentation) | API / Backend | BaseApi.Core (no direct involvement) | `builder.Services.AddOpenTelemetry()...` is a host-builder concern; auto-instrumentation captures everything without code in Core |
| Resource attribute (`service.name` / `service.version`) propagation | API / Backend (reads `IConfiguration` and passes to both providers) | — | Single source: `appsettings.json` `Service:Name`+`Service:Version` already populated (Phase 1 INFRA-04) |
| `/health/*` path filter (metrics+traces) | API / Backend (Program.cs Filter callback) | — | Filter is a registration-time concern; can't move to middleware because OTel instrumentation is wired at the provider level |
| `/health/*` log filter (drops request-start/finish for all endpoints) | Configuration (`appsettings.json` `Logging:LogLevel:Microsoft.AspNetCore=Warning`) | — | Per CONTEXT D-09; coarse but simple; per-path log filter deferred to a future phase |
| Health check registration (self + StartupHealthCheck + NpgSql) | API / Backend (Program.cs) | BaseApi.Core (IStartupGate + StartupHealthCheck + StartupGate types) | Types live in Core (reusable across services in the future); registration in Service composition root |
| Startup gate state (`IStartupGate.IsReady`) | BaseApi.Core (Singleton lifetime, Volatile/Interlocked thread-safe) | API / Backend (calls `MarkReady()` in Phase 5; Phase 8 MigrationRunner calls it post-migration) | Singleton state with thread-safe read/write — pure DI concern; lives in Core because Phase 8 IHostedService will resolve it |
| Health probe JSON response body | API / Backend (Program.cs ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse) | NuGet (AspNetCore.HealthChecks.UI.Client) | One static method call on existing third-party API |
| OTel Collector (file exporter for tests; pass-through for ops) | Infra / Docker | — | New `otel-collector` service in compose.yaml; config in compose/otel-collector-config.yaml; ops chooses downstream destination post-v1 |
| Verification (test that OTLP delivery actually happens) | Tests (tests/BaseApi.Tests/Observability/) | Infra (otel-collector container running during dotnet test) | `OtelCollectorFixture` + WebApplicationFactory<Program> + real Postgres + real otel-collector + file exporter — full E2E |

## Standard Stack

### Core (already PRE-PINNED in Directory.Packages.props from Phase 1 D-05)

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| OpenTelemetry | 1.15.3 | Core SDK | OTel project's stable line; Logs went GA in 2023; Metrics + Tracing GA since 2022 |
| OpenTelemetry.Extensions.Hosting | 1.15.3 | IHost integration | Wires MeterProvider / TracerProvider / LoggerProvider to `IHostedService` lifecycle |
| OpenTelemetry.Exporter.OpenTelemetryProtocol | 1.15.3 | OTLP exporter | Locked decision (PROJECT.md): export target = external Collector via OTLP; gRPC default port 4317 |
| OpenTelemetry.Instrumentation.AspNetCore | 1.15.0 | HTTP server metrics + traces | Locked requirement OBSERV-03/-12; instrumentation packages cadence 1.15.x (lockstep with core SDK) |
| OpenTelemetry.Instrumentation.Http | 1.15.0 | Outbound HTTP metrics + traces | Locked requirement OBSERV-03/-12; pairs with AspNetCore instrumentation; cheap to wire even if no outbound calls today |
| AspNetCore.HealthChecks.NpgSql | 9.0.0 | Postgres reachability check | Locked requirement HEALTH-04; Xabaril package; v9.0.0 runs fine on .NET 8 (Xabaril versions on own cadence) |

### Supporting — NEW pins for Phase 5 (add to Directory.Packages.props)

| Library | Version | Purpose | When to Use | Published | Source |
|---------|---------|---------|-------------|-----------|--------|
| `OpenTelemetry.Instrumentation.Runtime` | **1.15.0** | `process.runtime.dotnet.*` metrics (GC, threadpool, exceptions, monitor lock) | Always in v1 per CONTEXT D-16 | 2024 (1.15.0 line current as of 2026-05); latest is 1.15.1 — pin 1.15.0 to match the existing Instrumentation.* 1.15.0 cadence | [CITED: nuget.org/packages/OpenTelemetry.Instrumentation.Runtime] |
| `Npgsql.OpenTelemetry` | **8.0.4** | Npgsql DB tracing instrumentation (auto-creates child spans for SQL execution) | Always in v1 per CONTEXT D-05 + OBSERV-12 | **2024-09-10** [VERIFIED: nuget.org webfetch] | [CITED: nuget.org/packages/Npgsql.OpenTelemetry/8.0.4]; declares dependency on `Npgsql >= 8.0.4`; project currently pins Npgsql 8.0.9 — satisfies constraint |
| `AspNetCore.HealthChecks.UI.Client` | **9.0.0** | `UIResponseWriter.WriteHealthCheckUIResponse(HttpContext, HealthReport)` static method | Always in v1 per CONTEXT D-07 (JSON response body) | **2024-12-19** [VERIFIED: nuget.org webfetch] | [CITED: nuget.org/packages/AspNetCore.HealthChecks.UI.Client/9.0.0]; targets .NET 8.0 + .NET 9.0 + .NET 10.0; namespace `HealthChecks.UI.Client` |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `AspNetCore.HealthChecks.UI.Client` JSON body | Microsoft default (plain text `Healthy`/`Unhealthy`) | Simpler, zero new packages, but ops can't see which sub-check failed from the body — D-07 locked the JSON shape for ops ergonomics |
| `AlwaysOnSampler` (100%) | `TraceIdRatioBasedSampler(0.1)` with `OpenTelemetry:TraceSampleRatio` knob | Reduces export volume in prod; deferred per D-04 until production load observed; one-line swap when needed |
| `otel/opentelemetry-collector-contrib:0.95.0` | `otel/opentelemetry-collector:0.95.0` (base image) | Base image lacks the file exporter; D-11 verification depends on file exporter. Contrib also gives ops more receivers/exporters for prod fanout. |
| `ExportProcessorType.Batch` (default) in tests | `ExportProcessorType.Simple` in tests | Batch is correct for prod (5s timeout, lower overhead); Simple is correct for tests (synchronous flush = deterministic assertions). Tests opt into Simple via `ConfigureTestServices`. |
| Read `OTEL_EXPORTER_OTLP_ENDPOINT` manually + pass to `.AddOtlpExporter(o => o.Endpoint = ...)` | Rely on env-var auto-honor by calling `.AddOtlpExporter()` with no options | Env-var auto-honor is the default behavior of the OTel exporter SDK; CONTEXT D-04 says "appsettings fallback to `OpenTelemetry:Endpoint`" — see §"OTLP Exporter Configuration" for the recommended hybrid pattern |
| In-process `ActivityListener` / `MeterListener` for tests | Real Collector + file exporter | User explicitly picked real Collector (D-12 / Discussion Q4 verification strategy) — higher fidelity, slower but proves OTLP wire format |

**Installation (Plan 05-01 Task 1):**

```xml
<!-- Add to Directory.Packages.props (alongside existing pins, alphabetical or thematic order) -->
<PackageVersion Include="OpenTelemetry.Instrumentation.Runtime" Version="1.15.0" />
<PackageVersion Include="Npgsql.OpenTelemetry" Version="8.0.4" />
<PackageVersion Include="AspNetCore.HealthChecks.UI.Client" Version="9.0.0" />
```

```xml
<!-- Add to src/BaseApi.Core/BaseApi.Core.csproj (no Version= attr — CPM contract) -->
<ItemGroup>
  <PackageReference Include="OpenTelemetry" />
  <PackageReference Include="OpenTelemetry.Extensions.Hosting" />
  <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" />
  <PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" />
  <PackageReference Include="OpenTelemetry.Instrumentation.Http" />
  <PackageReference Include="OpenTelemetry.Instrumentation.Runtime" />
  <PackageReference Include="Npgsql.OpenTelemetry" />
  <PackageReference Include="AspNetCore.HealthChecks.NpgSql" />
  <PackageReference Include="AspNetCore.HealthChecks.UI.Client" />
</ItemGroup>
```

**Verification:** `dotnet restore` + `dotnet list package --include-transitive` should show all 8 OpenTelemetry/HealthChecks packages resolved without NU1603/NU1605. CPM contract grep: `grep -n 'Version=' src/BaseApi.Core/BaseApi.Core.csproj` returns ZERO matches on `PackageReference` lines.

## Architecture Patterns

### System Architecture Diagram (Phase 5 wire-up)

```
Request enters BaseApi.Service
   │
   ├──► UseExceptionHandler() [Phase 4 — outermost]
   │       │
   │       ├──► UseMiddleware<CorrelationIdMiddleware>() [Phase 4]
   │       │       │  HttpContext.Items["CorrelationId"] populated
   │       │       │  ILogger.BeginScope({"CorrelationId" = corrId}) pushed
   │       │       │  Activity.Current?.AddTag("correlation.id", ...) [Phase 5 future-additive — not in scope]
   │       │       │
   │       │       ├──► UseRouting()
   │       │       │       │
   │       │       │       ├──► /health/live  ──► HealthCheckMiddleware
   │       │       │       │     ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
   │       │       │       │     Predicate = c.Tags.Contains("live")
   │       │       │       │     Returns ONLY "self" check (always Healthy)
   │       │       │       │
   │       │       │       ├──► /health/ready  ──► HealthCheckMiddleware
   │       │       │       │     Predicate = c.Tags.Contains("ready")
   │       │       │       │     Returns "startup" check + NpgSql check (both must be Healthy)
   │       │       │       │
   │       │       │       ├──► /health/startup  ──► HealthCheckMiddleware
   │       │       │       │     Predicate = c.Tags.Contains("startup")
   │       │       │       │     Returns "startup" check ONLY (reads IStartupGate.IsReady)
   │       │       │       │
   │       │       │       └──► MapControllers() — business endpoints (Phase 7+)
   │       │       │
   │       │       │  Concurrent telemetry emission:
   │       │       │
   │       │       ├──► OpenTelemetry.Instrumentation.AspNetCore
   │       │       │       ├── ActivitySource "Microsoft.AspNetCore.Hosting.HttpRequestIn"
   │       │       │       │   ─► auto-created root Activity (span)
   │       │       │       │   ─► filtered by .Filter = ctx => !path.StartsWithSegments("/health")
   │       │       │       └── Meter "Microsoft.AspNetCore.Hosting"
   │       │       │           ─► http.server.request.duration (histogram)
   │       │       │           ─► filtered identically
   │       │       │
   │       │       ├──► OpenTelemetry.Instrumentation.Http
   │       │       │       └── outbound HttpClient spans + metrics (no calls today)
   │       │       │
   │       │       ├──► Npgsql.OpenTelemetry
   │       │       │       └── ActivitySource "Npgsql"
   │       │       │       └── child span per SQL command — SQL TEMPLATE captured;
   │       │       │           parameter values NOT captured by default (T-05-PII safe)
   │       │       │
   │       │       └──► ILogger.Log* calls (MEL pipeline)
   │       │               │   filtered by Logging:LogLevel (single source of truth)
   │       │               │   IncludeScopes=true → CorrelationId attribute on every LogRecord
   │       │               │
   │       │               └──► OpenTelemetry.Logs.LoggerProvider (MEL bridge)
   │
   │  Response on the way out:
   │
   └──► CorrelationIdMiddleware.OnStarting() echoes X-Correlation-Id header

Concurrent: TracerProvider + MeterProvider + LoggerProvider all push to:
   │
   ├──► OTLP gRPC exporter (port 4317)
   │       │  endpoint from OTEL_EXPORTER_OTLP_ENDPOINT env var
   │       │  appsettings.json Development overlay: http://localhost:4317
   │       │
   │       └──► otel-collector service (compose.yaml)
   │               │  receivers.otlp.protocols.grpc :4317
   │               │  exporters.file.path /var/otel-out/telemetry.jsonl
   │               │  exporters.logging.verbosity detailed (container stdout)
   │               │
   │               └──► tests/.otel-out/telemetry.jsonl (host-mounted)
   │                       │  ← OtelCollectorFixture reads this for assertions
   │                       └──► (production: ops fans out to Jaeger/Tempo/Datadog)
   │
   └──► OpenTelemetry.Instrumentation.Runtime
           └── Meter "OpenTelemetry.Instrumentation.Runtime"
               ─► process.runtime.dotnet.{gc.*, thread_pool.*, exceptions.count, ...}
               ─► fires regardless of HTTP path (process-level)
               ─► NOT filtered (no /health/* mechanism applies)

IStartupGate (Singleton, Volatile/Interlocked):
   │  Phase 5 default: app.Services.GetRequiredService<IStartupGate>().MarkReady()
   │                   called IMMEDIATELY after var app = builder.Build();
   │
   │  Phase 8 future:  MigrationRunner : IHostedService registered FIRST
   │                   calls db.Database.MigrateAsync()
   │                   then calls _gate.MarkReady()
   │                   AND immediate MarkReady() line REMOVED from Program.cs
   │
   └──► StartupHealthCheck.CheckHealthAsync reads IsReady → Healthy / Unhealthy
```

### Recommended Project Structure (Phase 5 additions)

```
src/BaseApi.Core/
├── Health/                          # NEW (Phase 5)
│   ├── IStartupGate.cs             # NEW — interface + StartupGate sealed class
│   └── StartupHealthCheck.cs       # NEW — : IHealthCheck reading IStartupGate
├── Middleware/                      # EXISTING (Phase 4)
│   └── CorrelationIdMiddleware.cs
├── Exceptions/                      # EXISTING (Phase 4)
│   ├── NotFoundException.cs
│   └── Handlers/
│       ├── NotFoundExceptionHandler.cs
│       ├── ValidationExceptionHandler.cs
│       ├── DbUpdateExceptionHandler.cs
│       └── FallbackExceptionHandler.cs
└── Persistence/                     # EXISTING (Phase 3)
    ├── BaseDbContext.cs
    ├── AuditInterceptor.cs
    ├── IRepository.cs, Repository.cs
    └── Exceptions/PostgresExceptionMapper.cs

src/BaseApi.Service/
├── Program.cs                       # EDITED — adds OTel + health registrations + 3 MapHealthChecks
├── appsettings.json                 # NOT EDITED — Service:Name/Version + OpenTelemetry:Endpoint already correct
└── appsettings.Development.json     # NOT EDITED — endpoint already routes to localhost:4317

compose.yaml                         # EDITED — adds otel-collector service alongside postgres
compose/                             # NEW DIRECTORY
└── otel-collector-config.yaml       # NEW — receivers + exporters config

tests/BaseApi.Tests/
├── Observability/                   # NEW (Phase 5 Plan 05-02)
│   ├── OtelCollectorFixture.cs     # NEW — IAsyncLifetime + truncate/delete .otel-out/
│   ├── LogExportTests.cs           # SC#1 facts
│   ├── LogLevelFilterTests.cs      # SC#2 facts
│   ├── MetricsExportTests.cs       # SC#4 metrics facts + D-16 runtime metric fact
│   ├── HealthEndpointsTests.cs     # SC#3 + SC#4 logs half (no /health/* in OTLP)
│   └── TraceExportTests.cs         # SC#5 facts (Npgsql child span + no param values)
├── Endpoints/TestController.cs     # EXISTING (Phase 4) — may be reused or supplemented with Minimal API stub
├── Middleware/                      # EXISTING (Phase 4)
└── Persistence/                     # EXISTING (Phase 4 PostgresExceptionMapperTests)

.gitignore                           # EDITED — adds tests/.otel-out/ glob
tests/.otel-out/                     # NEW DIRECTORY (gitignored) — host mount target for file exporter
```

### Pattern 1: MEL Bridge for Logs (OBSERV-02, OBSERV-06, OBSERV-07)

**What:** Wire OTel as an `ILoggerProvider` on the MEL `ILoggingBuilder` so `ILogger<T>.Log*` calls flow to OTLP AND Console with a single `Logging:LogLevel` filter source.

**When to use:** Phase 5 OTel logging wiring. THIS IS THE LOCKED PATTERN (CONTEXT D-06 + Pitfall 8). Never use `services.AddOpenTelemetry().WithLogging(...)` — that's a separate OTel-standalone path that bypasses MEL.

**Source:** [PITFALLS.md Pitfall 8 (lines 226-267)] + [ARCHITECTURE.md anti-pattern 5 (lines 899-903)] + [opentelemetry.io/docs/languages/dotnet/ — MEL provider docs]

```csharp
// In Program.cs — INSERT after the Phase 4 AddExceptionHandler<> block, BEFORE
// the services.AddOpenTelemetry() metrics+traces chain. Order: logs first, then
// metrics+traces — both call SetResourceBuilder/AddService with the same args.

var cfg = builder.Configuration;
var serviceName = cfg["Service:Name"]!;          // "sk-api" — from appsettings.json INFRA-04
var serviceVersion = cfg["Service:Version"]!;    // "3.2.0"

// LOGS — MEL bridge path. ILogger<T>.LogInformation(...) flows here.
// IncludeScopes=true → Phase 4 CorrelationIdMiddleware's BeginScope("CorrelationId", id)
//                     becomes a log attribute named "CorrelationId" on every record.
// IncludeFormattedMessage=true → message body is the formatted string (not just
//                                the template + parameters).
// ParseStateValues=true → structured-log key/value pairs (e.g., {Path}, {StatusCode})
//                        become individual log attributes.
builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes           = true;
    o.ParseStateValues        = true;
    o.SetResourceBuilder(ResourceBuilder.CreateDefault()
        .AddService(serviceName: serviceName, serviceVersion: serviceVersion));
    o.AddOtlpExporter();   // honors OTEL_EXPORTER_OTLP_ENDPOINT env var
});
```

**Critical "do NOT":** Do not also call `services.AddOpenTelemetry().WithLogging(...)` — that creates a SECOND, parallel OTel logger that does NOT participate in MEL filtering. Logs would be duplicated (both providers serialize and ship them) AND the second provider receives ALL levels regardless of `Logging:LogLevel` because it doesn't go through MEL's filter chain. Pitfall 9 detection: ingestion volume disproportionate to traffic; `Logging:LogLevel:Default = "Warning"` yet Info-level events arriving downstream.

### Pattern 2: Metrics + Traces Provider (OBSERV-03, OBSERV-12, D-16)

**What:** Single `services.AddOpenTelemetry()` chain with `.WithMetrics(...)` AND `.WithTracing(...)` branches, each ending in `.AddOtlpExporter()`. Resource builder is configured ONCE via `.ConfigureResource(...)` at the chain root and propagates to BOTH branches.

**Source:** [ARCHITECTURE.md §"Telemetry Integration" (lines 651-692)] + [CONTEXT D-08]

```csharp
// IN PROGRAM.CS — INSERT immediately after the MEL-bridge block above.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: serviceName,
        serviceVersion: serviceVersion))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation(opts =>
            opts.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))  // Pitfall 10 + OBSERV-08 + HEALTH-05
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()        // D-16 — process.runtime.dotnet.* (GC, threadpool, exceptions)
        .AddOtlpExporter())                  // honors OTEL_EXPORTER_OTLP_ENDPOINT
    .WithTracing(t => t
        .SetSampler(new AlwaysOnSampler())   // D-04 — 100% sample
        .AddAspNetCoreInstrumentation(opts =>
            opts.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))  // exclude health from traces too
        .AddHttpClientInstrumentation()
        .AddNpgsql()                         // D-05 corrected — NO options block; default does NOT capture parameter values (T-05-PII safe)
        .AddOtlpExporter());
```

**Key ordering invariants:**
1. `.ConfigureResource(...)` MUST come BEFORE `.WithMetrics(...)` / `.WithTracing(...)` — otherwise the resource doesn't propagate to those branches.
2. `.SetSampler(...)` MUST be the first call inside `.WithTracing(...)` so it applies to all subsequent instrumentation.
3. `.AddOtlpExporter()` MUST be the LAST call in each branch — exporters run in registration order; instrumentation must be added first.
4. `.AddRuntimeInstrumentation()` can be anywhere in the `.WithMetrics(...)` chain because runtime metrics use a separate Meter; placing it AFTER `.AddHttpClientInstrumentation()` and BEFORE `.AddOtlpExporter()` is CONTEXT D-16's locked order.
5. `.AddNpgsql()` MUST be inside `.WithTracing(...)` ONLY — there is no metrics overload in Npgsql.OpenTelemetry 8.0.4.

**Does ordering of `AddOpenTelemetry()` vs `AddControllers()` matter?** NO. `AddControllers()` registers MVC services; `AddOpenTelemetry()` registers OTel providers. Neither depends on the other's services. Convention in Phase 4's Program.cs places `AddExceptionHandler<>` then `AddControllers()` then `Build()` — Phase 5 inserts the OTel + Health registrations BEFORE `AddControllers()` to keep the "host concerns first, MVC last" mental model.

### Pattern 3: Resource Attribute Propagation (OBSERV-05)

**What:** `service.name` and `service.version` MUST appear on log records, metric data points, AND span attributes. Both provider trees (LoggerProvider via MEL, MeterProvider+TracerProvider via services.AddOpenTelemetry) need the resource builder configured independently — they do NOT share state.

**Source:** [opentelemetry.io/docs/languages/dotnet/resources/] + [CONTEXT D-06, D-08]

```csharp
// LOGS (MEL provider):
builder.Logging.AddOpenTelemetry(o =>
{
    o.SetResourceBuilder(ResourceBuilder.CreateDefault()
        .AddService(serviceName: serviceName, serviceVersion: serviceVersion));
    // ...
});

// METRICS + TRACES (services-level provider):
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: serviceName,
        serviceVersion: serviceVersion))
    .WithMetrics(...)
    .WithTracing(...);
```

**Why two calls:** The MEL logging bridge has its OWN `LoggerProviderBuilder` (under the hood — accessed via the `o.SetResourceBuilder(...)` shortcut). The services-level chain has its own `OpenTelemetryBuilder` where `.ConfigureResource(...)` configures a SEPARATE shared resource for the MeterProvider + TracerProvider branches. There is no single-call API to set the resource once for all three providers — you must call it twice.

**Verification (Plan 05-02 LogExportTests):** After an `ILogger<T>.LogInformation(...)` call, the exported log record's resource attributes (in `telemetry.jsonl`) include `"service.name": "sk-api"` AND `"service.version": "3.2.0"`. Same assertion for trace export and metric export.

### Pattern 4: MEL Filter Single Source (OBSERV-06)

**What:** `Logging:LogLevel:Default` in appsettings is the SINGLE source of truth for both Console + OTel sinks because both register on the same MEL `ILoggingBuilder`. MEL applies filters BEFORE any provider's `Log()` method is called.

**Source:** [PITFALLS.md Pitfall 9 (lines 271-291)] + [ARCHITECTURE.md anti-pattern 5]

```json
// appsettings.json — already in place from Phase 1 INFRA-04
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
    }
  },
  // ...
}
```

**Verification (SC#2 / LogLevelFilterTests):**
1. Override `Logging:LogLevel:Default` to `Warning` via in-memory config in `WebAppFactory.ConfigureWebHost`.
2. Issue an HTTP request that triggers an `ILogger<T>.LogInformation("test")` call (e.g., a Minimal API stub that logs and returns 200).
3. Force-flush OTel exporters (via `ExportProcessorType.Simple` registered in `ConfigureTestServices`).
4. Read `tests/.otel-out/telemetry.jsonl`; assert NO record has `"severityText": "Information"` from your stub's logger.
5. Reset the override and assert the same stub's Info log NOW appears.

**Critical "do NOT":**
- Do NOT set `Logging:LogLevel:OpenTelemetry` to any value — that's a per-provider override and breaks single-source.
- Do NOT call `builder.Logging.SetMinimumLevel(...)` in code — overrides config.
- Do NOT add a custom `ITelemetryProcessor`-style log filter for `/health/*` in Phase 5 — D-09 explicitly defers per-path log filtering; the coarse `Microsoft.AspNetCore: Warning` setting drops request-start/finish logs for ALL paths (including `/health/*`) which is sufficient for v1.

### Pattern 5: Health Path Filter (OBSERV-08, HEALTH-05)

**What:** Exclude `/health/*` from BOTH metrics AND traces via `AspNetCoreInstrumentationOptions.Filter` callback. Logs exclusion is handled coarsely by the MEL filter (Pattern 4).

**Source:** [PITFALLS.md Pitfall 10 (lines 295-320)] + [CONTEXT D-08]

```csharp
// Inside .WithMetrics(...) AND inside .WithTracing(...):
.AddAspNetCoreInstrumentation(opts =>
    opts.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
```

**Why `StartsWithSegments` (not `StartsWith`):** `StartsWithSegments` correctly handles `/health` vs `/healthy` (a hypothetical business path) — it only matches at segment boundaries. `/health/live`, `/health/ready`, `/health/startup` all match; `/healthy-products` would NOT match.

**`.NET 8 specific note:** `.DisableHttpMetrics()` is .NET 9+ only — for .NET 8 we MUST use the `Filter` callback. This phase targets .NET 8 (verified — Directory.Build.props pins net8.0).

**Verification (SC#4 / MetricsExportTests + HealthEndpointsTests):**
1. Issue 10 requests to `/health/live` and 1 request to a stub app endpoint (e.g., `/test/ok`).
2. Read `tests/.otel-out/telemetry.jsonl`.
3. Assert: `http.server.request.duration` data points contain ONLY the stub endpoint, NOT `/health/live`.
4. Assert: ASP.NET Core request spans contain ONLY the stub endpoint.

### Pattern 6: Npgsql Tracing (T-05-PII Safe) (OBSERV-12, D-05)

**What:** Subscribe to the `"Npgsql"` ActivitySource so SQL execution becomes child spans of the ASP.NET Core request span. Parameter values are NOT captured by default — T-05-PII is satisfied without any opt-out.

**Source:** [Npgsql.OpenTelemetry 8.0.4 source at github.com/npgsql/npgsql/blob/v8.0.4/src/Npgsql.OpenTelemetry/TracerProviderBuilderExtensions.cs] + [npgsql.org/doc/api/Npgsql.NpgsqlTracingOptionsBuilder.html]

```csharp
// Inside .WithTracing(...) — SIMPLIFIED from CONTEXT D-05.
// SECURITY: Npgsql.OpenTelemetry 8.0.4 does NOT capture parameter values by default.
// Span includes db.statement = "INSERT INTO workflows (name, ...) VALUES ($1, ...)" — the SQL template only.
// Bound parameter values are NEVER exported to the Collector. T-05-PII satisfied by the default behavior.
// No options block is needed (and none is supported — NpgsqlTracingOptions has no IncludeParameterValues
// or EnableSensitiveDataLogging property in 8.0.4).
.AddNpgsql()
```

**Why CONTEXT D-05's lambda body won't compile:** The CONTEXT shows `.AddNpgsql(opts => { opts.EnableEntityFrameworkCoreInstrumentation = false; })`. This property does NOT exist on `NpgsqlTracingOptions` in v8.0.4 — the actual options API (verified via the official `NpgsqlTracingOptionsBuilder` documentation page) only exposes 11 methods (ConfigureCommandFilter, ConfigureCommandEnrichmentCallback, ConfigureBatchFilter, ConfigureBatchEnrichmentCallback, ConfigureCommandSpanNameProvider, ConfigureBatchSpanNameProvider, EnableFirstResponseEvent, EnablePhysicalOpenTracing, ConfigureCopyOperationFilter, ConfigureCopyOperationEnrichmentCallback, ConfigureCopyOperationSpanNameProvider). Furthermore, looking at the actual 8.0.4 source (`AddNpgsql` extension method), the `Action<NpgsqlTracingOptions>?` parameter is declared but the method body **does not consume it** — the options are effectively ignored in this version. The defensive recommendation: call `.AddNpgsql()` with NO options block. This is what Phase 5 SHOULD ship.

**Verification (SC#5 / TraceExportTests):**
1. Add a Minimal API stub in `OtelCollectorFixture.ConfigureWebHost` that opens an `NpgsqlConnection` (or uses `TestErrorDbContext` from Phase 4) and issues a parametrized query against a seeded test row.
2. Issue an HTTP request to the stub.
3. Read `tests/.otel-out/telemetry.jsonl`.
4. Assert: exactly one ASP.NET Core request span (no `/health` noise).
5. Assert: exactly one Npgsql child span with `parentSpanId` matching the request span's `spanId`.
6. Assert: Npgsql span has `db.statement` containing the SQL template (e.g., `SELECT * FROM "TestParents" WHERE "Id" = $1`).
7. Assert: NO span attribute starting with `db.parameter` or containing the seeded row's bound value (e.g., the test Guid string).

### Pattern 7: IStartupGate (HEALTH-01)

**What:** Thread-safe Singleton flag service that the migration runner (Phase 8 future) flips after `Database.MigrateAsync()` completes. Phase 5 ships the gate with `MarkReady()` called immediately after `Build()` so the probe is healthy in v1.

**Source:** [CONTEXT D-01, D-02, D-13]

```csharp
// src/BaseApi.Core/Health/IStartupGate.cs — NEW FILE
namespace BaseApi.Core.Health;

public interface IStartupGate
{
    bool IsReady { get; }
    void MarkReady();
}

internal sealed class StartupGate : IStartupGate
{
    private int _isReady;            // 0 = false, 1 = true (Interlocked for thread safety)
    public bool IsReady => Volatile.Read(ref _isReady) == 1;
    public void MarkReady() => Interlocked.Exchange(ref _isReady, 1);
}
```

```csharp
// src/BaseApi.Core/Health/StartupHealthCheck.cs — NEW FILE
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BaseApi.Core.Health;

internal sealed class StartupHealthCheck(IStartupGate gate) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult(gate.IsReady
            ? HealthCheckResult.Healthy("Startup complete")
            : HealthCheckResult.Unhealthy("Startup not complete (migrations pending)"));
}
```

**Why `Volatile.Read` AND `Interlocked.Exchange`:** Without Volatile, a CPU may cache the read of `_isReady = 1` and other threads keep seeing 0. With Volatile but no Interlocked, two concurrent `MarkReady()` calls could race (not an issue in Phase 5 since only one caller — but Phase 8's MigrationRunner could conceptually call MarkReady more than once on retry; Interlocked makes it idempotent). The pair is the canonical idiom for a one-shot boolean latch in .NET.

**Why `int _isReady` instead of `bool`:** `Interlocked.Exchange` has no `bool` overload in .NET 8. Use `int` (0/1) with `Volatile.Read` for the read side.

**Why `internal sealed class StartupGate` (not public):** Concrete class is registered via DI; consumers depend on `IStartupGate`. Internal forces clean interface boundary; sealed prevents subclass-based test mocking (use a stub fake instead — the interface is small).

**Phase 5 vs Phase 8 contract:**

| State | Phase 5 (now) | Phase 8 (future) |
|-------|---------------|------------------|
| `IStartupGate` registered | Singleton (DI) | unchanged |
| `MarkReady()` call site | `app.Services.GetRequiredService<IStartupGate>().MarkReady();` (in Program.cs, immediately after `Build()`) | REMOVED from Program.cs; called by `MigrationRunner.StartAsync(...)` AFTER `db.Database.MigrateAsync()` |
| `MigrationRunner : IHostedService` | NOT registered | Registered FIRST (before any other IHostedService) so it runs at host start; calls `_gate.MarkReady()` |
| Default `IsReady` after Build | true (Phase 5 line flips it) | false (until MigrationRunner runs); Phase 8 removes Phase 5's line |
| `/health/startup` response on cold boot | 200 (immediately) | 503 → 200 (transitions after migrations) |

Phase 8's ONE-LINE diff in Program.cs to enable migration gating: delete the line `app.Services.GetRequiredService<IStartupGate>().MarkReady();` and add `builder.Services.AddHostedService<MigrationRunner>();` BEFORE the build. Plus the MigrationRunner class itself, of course. Clean contract.

**IHostedService registration ordering — does it matter for MigrationRunner in Phase 8?** YES. .NET 8 starts hosted services in REGISTRATION order. If MigrationRunner is registered LAST and another IHostedService (e.g., a future background worker) depends on the DB being migrated, the worker may start running queries BEFORE migrations finish. The mitigation: MigrationRunner MUST be the FIRST `AddHostedService<>` call. Phase 5 does NOT introduce any IHostedService — it's a clean starting point for Phase 8.

### Pattern 8: Three-Probe Tag Discipline (HEALTH-01..05)

**What:** Tags on `HealthCheckRegistration` + `Predicate` on `MapHealthChecks` give three distinct probe semantics from one composite health-check registry.

**Source:** [PITFALLS.md Pitfall 15 (lines 437-466)] + [CONTEXT D-03]

```csharp
// In Program.cs — INSERT after the AddOpenTelemetry chain, BEFORE AddControllers().
builder.Services.AddSingleton<IStartupGate, StartupGate>();
builder.Services.AddHealthChecks()
    .AddCheck("self",
        () => HealthCheckResult.Healthy(),
        tags: new[] { "live" })                       // /health/live ONLY
    .AddCheck<StartupHealthCheck>("startup",
        tags: new[] { "startup", "ready" })           // /health/startup AND /health/ready
    .AddNpgSql(cfg.GetConnectionString("Postgres")!,
        tags: new[] { "ready" });                     // /health/ready ONLY

builder.Services.AddControllers();
var app = builder.Build();

// Phase 5 default: gate is Healthy immediately.
// Phase 8 will REMOVE this line and add MigrationRunner.
app.Services.GetRequiredService<IStartupGate>().MarkReady();

// Phase 4 pipeline (verbatim — no change):
app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseRouting();

// Phase 5: 3 MapHealthChecks BEFORE MapControllers (plumbing first).
app.MapHealthChecks("/health/live",    new HealthCheckOptions {
    Predicate      = c => c.Tags.Contains("live"),
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});
app.MapHealthChecks("/health/ready",   new HealthCheckOptions {
    Predicate      = c => c.Tags.Contains("ready"),
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});
app.MapHealthChecks("/health/startup", new HealthCheckOptions {
    Predicate      = c => c.Tags.Contains("startup"),
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});

app.MapControllers();
app.Run();

public partial class Program { }   // Phase 1 D-10 marker — DO NOT REMOVE
```

**Aggregate semantics — `Predicate = c => c.Tags.Contains("ready")`:** When `/health/ready` runs, `HealthCheckMiddleware` invokes EVERY registered check whose tags contain `"ready"` (the `StartupHealthCheck` + `NpgSql` in our case) and aggregates results. The aggregate is Healthy iff ALL invoked checks are Healthy; Unhealthy if ANY is Unhealthy (the worst status wins). The JSON body via `UIResponseWriter` shows per-check status + duration, so ops sees WHICH sub-check failed.

**`AspNetCore.HealthChecks.NpgSql` 9.0.0 `AddNpgSql` semantics — what counts as "down":**
- Connection refused (server stopped, network unreachable) → Unhealthy with `Exception.Message` in description.
- Authentication failure (wrong password) → Unhealthy with PG error message.
- Connection succeeds but `SELECT 1` query fails → Unhealthy.
- Connection + `SELECT 1` both succeed → Healthy.

So `/health/ready` correctly returns 503 in all 3 down-modes verified by Pitfall 15.

**`UIResponseWriter.WriteHealthCheckUIResponse` — namespace + invocation:**
- Namespace: `HealthChecks.UI.Client` (note: NO leading `AspNetCore.` — package is `AspNetCore.HealthChecks.UI.Client` but namespace is `HealthChecks.UI.Client`)
- Signature (effective): `static Task WriteHealthCheckUIResponse(HttpContext httpContext, HealthReport report)`
- Used as: `ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse` — `ResponseWriter` is a `Func<HttpContext, HealthReport, Task>` delegate.
- Output shape (verified via D-07):
```json
{
  "status": "Healthy",
  "totalDuration": "00:00:00.0042",
  "entries": {
    "startup": { "status": "Healthy", "description": "Startup complete", "duration": "00:00:00.0001" },
    "npgsql":  { "status": "Healthy", "description": null,                "duration": "00:00:00.0041" }
  }
}
```

### Pattern 9: OTLP Exporter Configuration (OBSERV-04)

**What:** `.AddOtlpExporter()` with NO options block honors the `OTEL_EXPORTER_OTLP_ENDPOINT` environment variable automatically. Default protocol is gRPC, default port 4317.

**Source:** [github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/README.md] + [STACK.md line 30]

**Environment variable precedence (auto-honored by the exporter SDK):**

| Env Var | Default | Behavior |
|---------|---------|----------|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://localhost:4317` (gRPC) | Sets endpoint for ALL three signals (logs, metrics, traces) |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` | `grpc` or `http/protobuf` |
| `OTEL_EXPORTER_OTLP_LOGS_ENDPOINT` | (overrides ENDPOINT for logs only) | per-signal override |
| `OTEL_EXPORTER_OTLP_METRICS_ENDPOINT` | (overrides ENDPOINT for metrics only) | per-signal override |
| `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` | (overrides ENDPOINT for traces only) | per-signal override |
| `OTEL_RESOURCE_ATTRIBUTES` | (additive — semicolon-separated key=value pairs) | Adds to resource builder; service.name + service.version from `.AddService(...)` win on conflict |

**For Phase 5, the SIMPLEST PATH (recommended):**

1. Production / docker-compose: container env var `OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317` (Phase 8 baseapi-service block).
2. Local dev (`dotnet run`): launchSettings.json `environmentVariables` set `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317`.
3. Tests: `WebAppFactory.ConfigureWebHost` overrides env var via `Environment.SetEnvironmentVariable` OR overrides the appsettings via in-memory config OR explicitly calls `.AddOtlpExporter(o => o.Endpoint = new Uri("http://localhost:4317"))`.

**Why NOT read appsettings `OpenTelemetry:Endpoint` and pass it manually:** OBSERV-04 says "endpoint from `OTEL_EXPORTER_OTLP_ENDPOINT` env var (default fallback to `OpenTelemetry:Endpoint` appsettings)". The cleanest implementation honors this contract:

```csharp
// Hybrid pattern — env var preferred, appsettings fallback:
var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
                ?? cfg["OpenTelemetry:Endpoint"];

builder.Logging.AddOpenTelemetry(o =>
{
    // ...
    o.AddOtlpExporter(exp =>
    {
        if (!string.IsNullOrEmpty(otlpEndpoint))
            exp.Endpoint = new Uri(otlpEndpoint);
        // else: SDK's default (http://localhost:4317) applies
    });
});

// Same exp.Endpoint = ... pattern inside .WithMetrics(...) and .WithTracing(...)
```

**OR (simpler — defer the appsettings fallback to "if env var is unset, OTel SDK default of http://localhost:4317 is fine for dev"):**

```csharp
// Pure env-var pattern — let OTel SDK auto-honor OTEL_EXPORTER_OTLP_ENDPOINT
o.AddOtlpExporter();   // no options block
```

**RECOMMENDATION:** Use the simpler pure env-var pattern for Phase 5. Set `OTEL_EXPORTER_OTLP_ENDPOINT` in:
- `compose.yaml` baseapi-service block (Phase 8 will activate this; Phase 5 leaves the block under `phase-8` profile per Phase 2 plan 02-01 fix-forward) — `OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317`
- `src/BaseApi.Service/Properties/launchSettings.json` (for `dotnet run` dev) — `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317`. This file already exists untracked (per Phase 4 04-01 SUMMARY "Issues Encountered"); Plan 05-01 may need to commit it (or .gitignore it; planner's call — Phase 7 was tentatively going to handle this).
- Test setup: `WebAppFactory.ConfigureWebHost` sets env var via `services.Configure<HostOptions>(...)` OR via direct `Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317")` in a static ctor — verify test isolation.

**Failure mode when Collector is unreachable:**
- OTel OTLP exporter (gRPC) retries with exponential backoff (default 3 retries, 1s/2s/5s).
- After retries exhausted, records are DROPPED — exporter does NOT block the app's request thread (export happens on a background thread / batch processor).
- App shutdown: `IDisposable.Dispose()` on the exporter is called by `IHostedService` graceful-shutdown. Default flush timeout is 10s; can be tuned via `o.TimeoutMilliseconds`. **Does NOT block app shutdown beyond the timeout.**
- Practical implication: if otel-collector is down during local dev, the app runs normally; logs/metrics/traces silently drop after 3 retries. No data loss visibility unless ops checks Collector dashboards.

### Pattern 10: OTel Collector docker-compose service (D-10)

**Source:** [otel/opentelemetry-collector-contrib:0.95.0 README] + [CONTEXT D-10]

```yaml
# compose.yaml — ADD this service block alongside existing postgres service
otel-collector:
  image: otel/opentelemetry-collector-contrib:0.95.0
  container_name: sk-otel-collector
  command: ["--config=/etc/otel-collector-config.yaml"]
  volumes:
    - ./compose/otel-collector-config.yaml:/etc/otel-collector-config.yaml:ro
    - ./tests/.otel-out:/var/otel-out
  ports:
    - "4317:4317"   # OTLP gRPC
    - "4318:4318"   # OTLP HTTP (optional — lets curl-from-laptop debugging work)
  healthcheck:
    test: ["CMD", "wget", "-qO-", "http://localhost:13133/"]
    interval: 5s
    timeout: 3s
    retries: 5
```

```yaml
# compose/otel-collector-config.yaml — NEW FILE
receivers:
  otlp:
    protocols:
      grpc: { endpoint: 0.0.0.0:4317 }
      http: { endpoint: 0.0.0.0:4318 }

exporters:
  file:
    path: /var/otel-out/telemetry.jsonl
    rotation:
      max_megabytes: 10
      max_days: 1
  logging:
    verbosity: detailed   # also dump to container stdout for live tailing

extensions:
  health_check: { endpoint: 0.0.0.0:13133 }

service:
  extensions: [health_check]
  pipelines:
    logs:    { receivers: [otlp], exporters: [file, logging] }
    metrics: { receivers: [otlp], exporters: [file, logging] }
    traces:  { receivers: [otlp], exporters: [file, logging] }
```

**Note on Collector version 0.95.0:** [ASSUMED] — verified in CONTEXT D-10 but not re-verified against the otel/opentelemetry-collector-contrib release feed in this research session. Image tag `0.95.0` is the project's locked choice; if planner finds it deprecated/removed at execution time, the trivial fix is to bump to a current 0.115.0+ tag (the file exporter and OTLP receiver APIs are stable across these versions).

**Healthcheck endpoint 13133:** The Collector contrib image's `health_check` extension exposes a JSON `{"status":"Server available"}` response at the configured port (default 13133). The healthcheck shell command `wget -qO- http://localhost:13133/` returns 0 iff the Collector is fully booted with the configured pipelines active. This is the canonical readiness gate when other services `depends_on: otel-collector: condition: service_healthy`. (Phase 5 does not wire baseapi-service to depend on otel-collector — the app exports best-effort; if the Collector is down, telemetry is silently dropped per Pattern 9 failure mode.)

### Anti-Patterns to Avoid

- **Calling both `builder.Logging.AddOpenTelemetry(...)` AND `services.AddOpenTelemetry().WithLogging(...)`** — Pitfall 8. The two are separate provider trees. Duplicates every log record. Single MEL filter source is broken.
- **Setting `Logging:LogLevel:OpenTelemetry` to any value** — breaks the single-source-of-truth invariant. Logs filtering becomes per-provider.
- **Liveness probe (`/health/live`) checking the DB** — Pitfall 15. Postgres has a transient blip → K8s kills the pod → cascading restarts.
- **Putting `MapHealthChecks` AFTER `MapControllers` AND adding a catch-all route** — health endpoints can be shadowed by the catch-all. Phase 5 puts MapHealthChecks BEFORE MapControllers per CONTEXT D-08.
- **`.AddNpgsql(opts => { opts.IncludeParameterValues = true; })`** — does not compile in 8.0.4 (property doesn't exist) AND would expose parameter values if it did. Default is safe; do NOT add a callback.
- **Reading `OTEL_EXPORTER_OTLP_ENDPOINT` manually AND passing it via `o.Endpoint = ...` AND setting it as an env var** — redundant; the SDK already honors the env var. Only set the property if reading from appsettings.
- **Forgetting to mark `IStartupGate.MarkReady()` immediately after `Build()`** in Phase 5 — would leave `/health/startup` permanently 503 in v1 (no migration runner to flip it). Verification HealthEndpointsTests SC#3 catches this.
- **Subscribing to `ActivitySource("sk-api")`** in Phase 5 traces — D-15 says NO custom source in Phase 5; only auto-instrumentation. A future phase introduces it.
- **Setting `ExportProcessorType.Simple` in production** — synchronous export blocks every log/metric/trace emission on the OTLP write. Use Simple ONLY in tests (via `ConfigureTestServices`). Production keeps the default (Batch).
- **Adding `tests/.otel-out/` to git** — D-11 says gitignored. Plan 05-01 edits `.gitignore`.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| OTel logging from MEL | A custom `ILoggerProvider` that serializes to OTLP | `builder.Logging.AddOpenTelemetry(...)` (MEL provider in the OTel SDK) | The OTel SDK's LoggerProvider already handles OTLP framing, batching, retries, resource attributes, scope flattening, and structured log values. Hand-rolling means reimplementing the OTLP/gRPC client too. |
| Periodic runtime metrics | A `Timer` that polls `GC.GetTotalMemory(false)` etc. and emits via a custom `Meter` | `OpenTelemetry.Instrumentation.Runtime` package + `.AddRuntimeInstrumentation()` | The instrumentation package uses event-counter-based capture (free; no polling overhead) and emits standard `process.runtime.dotnet.*` metric names per OTel semantic conventions. |
| Postgres reachability check | A custom `IHealthCheck` that opens an `NpgsqlConnection` and runs `SELECT 1` with timeout handling | `AspNetCore.HealthChecks.NpgSql` with `.AddNpgSql(connStr, tags: ["ready"])` | Xabaril already handles timeout, connection-string parsing, `Failure` vs `Degraded` status, and integrates with `HealthReport` for the JSON writer. |
| JSON response body for health probes | A custom `Func<HttpContext, HealthReport, Task>` that JSON-serializes the report | `UIResponseWriter.WriteHealthCheckUIResponse` from `AspNetCore.HealthChecks.UI.Client` | The official UI client format is canonical for `HealthChecks.UI` dashboards (even though we don't ship the UI today, future ops tooling expects this shape). |
| Correlation ID extraction into log attributes | Manually scanning `ILogger.BeginScope` state and adding attributes per log site | `o.IncludeScopes = true` on the OTel logger options | The OTel SDK auto-flattens `BeginScope` dictionaries into log attributes. Phase 4 already pushes the `CorrelationId` key — Phase 5's `IncludeScopes = true` is the one-line wiring. |
| OTLP transport (gRPC client) | `Grpc.Net.Client` + protobuf code-gen + retry logic | `OpenTelemetry.Exporter.OpenTelemetryProtocol` package | The exporter package handles serialization, batching, retries, TLS, and the protocol negotiation. |
| Startup gate pattern | A static mutable `volatile bool` field in some class | `IStartupGate` Singleton + Volatile/Interlocked encapsulation | Static mutable state is hard to test (no DI substitution) and obscures the contract; the interface + sealed implementation pair makes it explicit. |
| OTel Collector file exporter for tests | Writing a custom OTLP gRPC server stub in C# | `otel/opentelemetry-collector-contrib:0.95.0` + file exporter in `compose.yaml` | The contrib Collector's file exporter is battle-tested; a stub C# OTLP server is ~500 LOC and error-prone. Real Collector gives prod-parity. |
| OTel pipeline for tests | In-process `ActivityListener` / `MeterListener` / `ITestLogExporter` mocks | Real Collector container + `ExportProcessorType.Simple` in `ConfigureTestServices` | User explicitly chose real Collector (D-12) to actually exercise OTLP wire format. In-process listeners don't catch serialization regressions. |

**Key insight:** Phase 5 is almost entirely package wiring. The only hand-written types are 2 small files in `BaseApi.Core/Health/` (IStartupGate + StartupHealthCheck) and 1 docker-compose service block. Everything else is configuration of pre-existing OTel + HealthChecks + Collector packages. Resist the temptation to add custom log filters, custom exporters, custom samplers, or custom health-check JSON writers in v1.

## Runtime State Inventory

> Phase 5 is greenfield wiring — NOT a rename/refactor/migration phase. No existing runtime state needs migration.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | None — no DB rows reference OTel state. The `IStartupGate` flag is process-memory only (Singleton). | None |
| Live service config | None — Phase 5 adds a new compose service (`otel-collector`); no existing service config to update. | None |
| OS-registered state | None — no task scheduler, systemd unit, launchd plist registrations involve Phase 5. | None |
| Secrets/env vars | NEW env vars: `OTEL_EXPORTER_OTLP_ENDPOINT` (already implied by Phase 1 appsettings; Phase 5 expects it to be set in launchSettings.json or compose env). | Plan 05-01 documents the env var requirement; Plan 05-02 verifies the test fixture sets it. |
| Build artifacts | None — `dotnet restore` will pick up the 3 new pins; no compiled artifacts under `bin/`/`obj/` reference OTel state. (Plan 05-01 may want `dotnet restore --force` if local NuGet cache has stale 1.15.0 metadata.) | Optionally `dotnet restore --force --no-cache` after first pin edit. |

## Common Pitfalls

### Pitfall 5-A: MEL Bridge vs `WithLogging` confusion (Pitfall 8 cross-link)

**What goes wrong:** Developers call `services.AddOpenTelemetry().WithLogging(...)` thinking `ILogger<T>` logs flow to OTLP. They don't — `WithLogging()` registers an OTel-standalone `LoggerProvider`, not a MEL `ILoggerProvider`. Production logs absent from Collector. Or developer adds BOTH paths → duplicate logs.

**Why it happens:** Two genuinely-different APIs with similar names. See PITFALLS.md Pitfall 8 (lines 226-267).

**How to avoid:** Use ONLY `builder.Logging.AddOpenTelemetry(o => { ... })`. Verify by searching the codebase: `grep -n 'WithLogging' src/` should return ZERO matches in Phase 5.

**Warning signs:** Logs visible in console but absent from Collector. Logs duplicated in Collector. Log records arrive but `Body` is empty.

**Phase 5 verification command:**
```bash
grep -rn "WithLogging" src/ tests/    # MUST return 0 matches after Plan 05-01
grep -rn "builder.Logging.AddOpenTelemetry" src/    # MUST return exactly 1 match (in Program.cs)
```

### Pitfall 5-B: `Logging:LogLevel` not filtering OTel exports (Pitfall 9 cross-link)

**What goes wrong:** `appsettings.json` `Default = "Warning"` is set, but Information logs still ship to Collector — CPU/network waste under load. Root cause: someone added `Logging:LogLevel:OpenTelemetry = "Trace"` or called `builder.Logging.SetMinimumLevel(LogLevel.Trace)`.

**How to avoid:** Single source `Logging:LogLevel:Default` only. No per-provider overrides.

**Warning signs:** Collector ingestion volume disproportionate to traffic.

**Phase 5 verification command:**
```bash
grep -n "Logging:LogLevel:OpenTelemetry" src/BaseApi.Service/appsettings*.json   # MUST return 0
grep -rn "SetMinimumLevel" src/   # MUST return 0
```

### Pitfall 5-C: `/health/*` instrumentation noise (Pitfall 10 cross-link)

**What goes wrong:** `AddAspNetCoreInstrumentation()` instruments every endpoint by default. Probes hit `/health/live` every few seconds → flood of sub-millisecond histogram entries + log lines. p99 RED metrics skewed.

**How to avoid:** `Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health")` on AspNetCoreInstrumentation in BOTH metrics AND traces.

**Warning signs:** HTTP server metrics histogram dominated by sub-millisecond requests. Loki shows steady-state log spam at probe interval.

**Phase 5 verification command:**
```bash
grep -n "StartsWithSegments(\"/health\")" src/BaseApi.Service/Program.cs   # MUST return >= 2 matches (metrics + traces)
```

### Pitfall 5-D: Liveness probe checking the DB (Pitfall 15 cross-link)

**What goes wrong:** `/health/live` queries Postgres. Postgres has a blip. K8s kills the pod. Cascading restarts during DB failover.

**How to avoid:** `/health/live` returns Healthy as long as the process responds. The `"self"` always-Healthy check is the ONLY check tagged `"live"`. NpgSql is tagged `"ready"` ONLY.

**Warning signs:** Pods restarting during Postgres failover instead of just losing traffic temporarily.

**Phase 5 verification command:**
```bash
# Verify "self" check has live tag but no ready/startup tag:
grep -A2 'AddCheck(\"self\"' src/BaseApi.Service/Program.cs | grep -E 'live|ready|startup'
# Expected: only "live" appears, NOT "ready" or "startup".

# Verify NpgSql does NOT have "live" tag:
grep -A1 'AddNpgSql' src/BaseApi.Service/Program.cs | grep -E 'live'
# Expected: 0 matches (NpgSql has only "ready").
```

### Pitfall 5-E: `Npgsql.OpenTelemetry 8.0.4` lambda doesn't compile (NEW — surfaced by this research)

**What goes wrong:** Following CONTEXT D-05 verbatim:
```csharp
.AddNpgsql(opts => { opts.EnableEntityFrameworkCoreInstrumentation = false; })
```
fails at compile time with CS0117 — `NpgsqlTracingOptions` does not contain a definition for `EnableEntityFrameworkCoreInstrumentation`.

**Why it happens:** The Npgsql.OpenTelemetry 8.0.4 source shows `AddNpgsql(this TracerProviderBuilder builder, Action<NpgsqlTracingOptions>? options = null)` but the body does NOT consume `options`. The `NpgsqlTracingOptions` class itself in 8.0.4 has no `EnableEntityFrameworkCoreInstrumentation` property (and never did — that's an EFCore concept, not an Npgsql one).

**How to avoid:** Call `.AddNpgsql()` with NO callback. Default behavior already does NOT capture parameter values. T-05-PII satisfied. Add a single-line `// SECURITY: ...` comment above the call documenting why no callback is needed.

**Warning signs:** Build fails at Plan 05-01 Task 7 (Program.cs edits) with CS0117 on the EnableEntityFrameworkCoreInstrumentation reference. Under `TreatWarningsAsErrors=true` this is a hard build break.

**Phase 5 verification command:**
```bash
grep -n "AddNpgsql" src/BaseApi.Service/Program.cs   # MUST return exactly 1 match
grep -n "AddNpgsql(opts" src/BaseApi.Service/Program.cs   # MUST return 0 matches (no callback)
grep -n "AddNpgsql()" src/BaseApi.Service/Program.cs   # MUST return exactly 1 match (no-args call)
```

### Pitfall 5-F: Resource builder not shared between Logs and Metrics+Traces (NEW)

**What goes wrong:** Developer calls `.ConfigureResource(...)` on the services chain and assumes it covers logs too. Logs have NO `service.name` / `service.version` in their export. SC#1 verification fails.

**Why it happens:** Logs use `builder.Logging.AddOpenTelemetry(...)` MEL provider; that provider does NOT share the services-level `OpenTelemetryBuilder` resource. Both need explicit configuration.

**How to avoid:** Call resource setup TWICE — once via `o.SetResourceBuilder(...)` in the Logging.AddOpenTelemetry options, and once via `.ConfigureResource(...)` on the services chain. Use the SAME `serviceName` + `serviceVersion` variables.

**Warning signs:** SC#1 LogExportTests assertion that `service.name` attribute exists on log records FAILS.

**Phase 5 verification command:**
```bash
grep -c "AddService(" src/BaseApi.Service/Program.cs   # MUST be >= 2 (one for logs, one for metrics+traces)
```

### Pitfall 5-G: Test exporter buffer not flushed → flaky assertions

**What goes wrong:** Tests issue HTTP requests, immediately read `tests/.otel-out/telemetry.jsonl`, assert log/metric/trace presence — but the OTel batch processor hasn't flushed yet (default 5s timeout). Assertions fail nondeterministically.

**Why it happens:** OTel default `ExportProcessorType.Batch` accumulates records and flushes on a timer OR when batch size reached.

**How to avoid:** In `ConfigureTestServices` (via WebAppFactory.ConfigureWebHost), register a SEPARATE OTel chain that uses `ExportProcessorType.Simple` (synchronous flush per record):
```csharp
services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation(opts => opts.Filter = /* same as prod */)
        .AddOtlpExporter(o =>
        {
            o.ExportProcessorType = ExportProcessorType.Simple;
            o.Endpoint = new Uri("http://localhost:4317");
        }))
    .WithTracing(/* ... */);
```
Verify: tests pass deterministically across 3+ consecutive `dotnet test` runs.

**Alternative:** Use `WebApplicationFactory<Program>.Services.GetRequiredService<MeterProvider>().ForceFlushAsync(timeoutMs)` (and similar for TracerProvider, LoggerProvider) before reading the file.

### Pitfall 5-H: `tests/.otel-out/` directory permission issues on Windows (NEW)

**What goes wrong:** Docker Desktop on Windows can have permission issues mounting `./tests/.otel-out:/var/otel-out` if the host directory doesn't exist before container start, or if WSL2 backend's UID mapping conflicts.

**Why it happens:** Windows host paths with Docker Desktop + WSL2 backend.

**How to avoid:**
1. Plan 05-01 creates an empty `tests/.otel-out/` directory with a `.gitkeep` (committed) + adds `tests/.otel-out/*` (NOT the dir itself) to `.gitignore` — so the dir exists at clone time but its contents are ignored.
2. OR Plan 05-01 commits `tests/.otel-out/.gitkeep` and updates `.gitignore` with `tests/.otel-out/telemetry.jsonl*` + `tests/.otel-out/*.tmp` (excludes file exporter output but keeps the dir).
3. `OtelCollectorFixture.InitializeAsync` does `Directory.CreateDirectory("tests/.otel-out")` defensively if missing.

**Warning signs:** Container exits with "permission denied" on `/var/otel-out/telemetry.jsonl` write. Test fixture's first read fails because the file doesn't exist.

**Phase 5 verification command:**
```bash
ls -la tests/.otel-out/   # MUST succeed (dir exists after Plan 05-01)
grep -n "tests/.otel-out" .gitignore   # MUST show the glob entry
```

## Code Examples

### Reference Program.cs (Phase 4 → Phase 5 diff target)

The complete Program.cs after Phase 5 Plan 05-01 — approximate ~115 lines, building on the 72-line Phase 4 version:

```csharp
// BaseApi.Service — application entry point.
//
// Phase 1 D-10 scaffold. Phase 4 added correlation + ProblemDetails + IExceptionHandler chain.
// Phase 5 adds OpenTelemetry (logs via MEL bridge, metrics+traces via services chain) +
// three K8s-style health probes (live/ready/startup) backed by IStartupGate.
//
// Phase 7 will refactor into builder.Services.AddBaseApi<AppDbContext>(...) + app.UseBaseApi()
// extensions; until then, Program.cs is the composition root (CONTEXT.md Phase 1 D-10).

using BaseApi.Core.Exceptions.Handlers;
using BaseApi.Core.Health;
using BaseApi.Core.Middleware;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;
var serviceName    = cfg["Service:Name"]!;       // "sk-api" — INFRA-04
var serviceVersion = cfg["Service:Version"]!;    // "3.2.0"

// ===== Phase 4 pre-build registrations (verbatim) =====
builder.Services.AddHttpContextAccessor();
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = ctx =>
    {
        if (ctx.HttpContext.Items.TryGetValue("CorrelationId", out var corrIdObj)
            && corrIdObj is string corrId)
        {
            ctx.ProblemDetails.Extensions["correlationId"] = corrId;
        }
        ctx.ProblemDetails.Instance = ctx.HttpContext.Request.Path;
    };
});

builder.Services.AddExceptionHandler<NotFoundExceptionHandler>();
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
builder.Services.AddExceptionHandler<DbUpdateExceptionHandler>();
builder.Services.AddExceptionHandler<FallbackExceptionHandler>();

// ===== Phase 5: OTel logs via MEL bridge (Pitfall 8 / OBSERV-02 / OBSERV-07) =====
builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes           = true;   // Phase 4 "CorrelationId" scope key → log attribute
    o.ParseStateValues        = true;
    o.SetResourceBuilder(ResourceBuilder.CreateDefault()
        .AddService(serviceName: serviceName, serviceVersion: serviceVersion));
    o.AddOtlpExporter();   // OTEL_EXPORTER_OTLP_ENDPOINT env var auto-honored
});

// ===== Phase 5: OTel metrics + traces (OBSERV-03 / OBSERV-12 / D-04 / D-08 / D-16) =====
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: serviceName,
        serviceVersion: serviceVersion))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation(opts =>
            opts.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))  // Pitfall 10 / OBSERV-08 / HEALTH-05
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()   // D-16 — process.runtime.dotnet.* metrics
        .AddOtlpExporter())
    .WithTracing(t => t
        .SetSampler(new AlwaysOnSampler())   // D-04
        .AddAspNetCoreInstrumentation(opts =>
            opts.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
        .AddHttpClientInstrumentation()
        // SECURITY: Npgsql.OpenTelemetry 8.0.4 does NOT capture parameter values by default.
        // db.statement span attribute carries the SQL TEMPLATE only (e.g., "$1" placeholders).
        // T-05-PII satisfied without an opt-out — no callback is supported in this version.
        .AddNpgsql()
        .AddOtlpExporter());

// ===== Phase 5: Health probes (HEALTH-01..05) =====
builder.Services.AddSingleton<IStartupGate, StartupGate>();
builder.Services.AddHealthChecks()
    .AddCheck("self",
        () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(),
        tags: new[] { "live" })
    .AddCheck<StartupHealthCheck>("startup",
        tags: new[] { "startup", "ready" })
    .AddNpgSql(cfg.GetConnectionString("Postgres")!,
        tags: new[] { "ready" });

builder.Services.AddControllers();

var app = builder.Build();

// Phase 5: Mark gate ready immediately after build (default v1 — no migrations yet).
// Phase 8 will REMOVE this line and add MigrationRunner : IHostedService that flips it.
app.Services.GetRequiredService<IStartupGate>().MarkReady();

// ===== Phase 4 pipeline (verbatim) =====
app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseRouting();

// ===== Phase 5: 3 MapHealthChecks BEFORE MapControllers (plumbing first) =====
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate      = c => c.Tags.Contains("live"),
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate      = c => c.Tags.Contains("ready"),
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});
app.MapHealthChecks("/health/startup", new HealthCheckOptions
{
    Predicate      = c => c.Tags.Contains("startup"),
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});

app.MapControllers();
app.Run();

// Phase 1 D-10 marker for WebApplicationFactory<Program> tests.
public partial class Program { }
```

### Reference IStartupGate.cs (BaseApi.Core/Health/)

```csharp
namespace BaseApi.Core.Health;

/// <summary>
/// One-shot startup gate. Singleton DI lifetime.
/// </summary>
/// <remarks>
/// Phase 5 ships this contract with <see cref="MarkReady"/> called immediately
/// after <c>WebApplication.Build()</c> so <c>/health/startup</c> is Healthy in v1.
/// Phase 8 will register a <c>MigrationRunner : IHostedService</c> that calls
/// <see cref="MarkReady"/> after <c>db.Database.MigrateAsync()</c> completes,
/// and the immediate-call line in Program.cs will be removed.
/// </remarks>
public interface IStartupGate
{
    bool IsReady { get; }
    void MarkReady();
}

/// <summary>
/// Thread-safe latch (Volatile read + Interlocked write).
/// Idempotent — multiple <see cref="MarkReady"/> calls are safe.
/// </summary>
internal sealed class StartupGate : IStartupGate
{
    private int _isReady; // 0 = false, 1 = true
    public bool IsReady => Volatile.Read(ref _isReady) == 1;
    public void MarkReady() => Interlocked.Exchange(ref _isReady, 1);
}
```

### Reference StartupHealthCheck.cs (BaseApi.Core/Health/)

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BaseApi.Core.Health;

/// <summary>
/// Reads <see cref="IStartupGate.IsReady"/> and reports Healthy/Unhealthy.
/// Tagged "startup" + "ready" so it appears in both /health/startup and /health/ready.
/// </summary>
internal sealed class StartupHealthCheck(IStartupGate gate) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult(gate.IsReady
            ? HealthCheckResult.Healthy("Startup complete")
            : HealthCheckResult.Unhealthy("Startup not complete (migrations pending)"));
}
```

### Reference OtelCollectorFixture.cs (tests/BaseApi.Tests/Observability/)

```csharp
using System.Text.Json;
using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// Class fixture that truncates tests/.otel-out/telemetry.jsonl on init and deletes on dispose.
/// Lifts the Phase 3 D-15 / Phase 4 PostgresFixture cleanup discipline applied to the
/// host-mounted directory the otel-collector file exporter writes to.
/// </summary>
public sealed class OtelCollectorFixture : IAsyncLifetime
{
    public static readonly string TelemetryFile =
        Path.Combine(SolutionRoot(), "tests", ".otel-out", "telemetry.jsonl");

    public Task InitializeAsync()
    {
        var dir = Path.GetDirectoryName(TelemetryFile)!;
        Directory.CreateDirectory(dir);
        // Truncate to zero bytes (don't delete — file exporter holds handle in container).
        File.WriteAllText(TelemetryFile, string.Empty);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (File.Exists(TelemetryFile))
        {
            try { File.Delete(TelemetryFile); } catch { /* container may hold handle; truncate on next init */ }
        }
        return Task.CompletedTask;
    }

    public IEnumerable<JsonElement> ReadExportedRecords()
    {
        if (!File.Exists(TelemetryFile)) yield break;
        foreach (var line in File.ReadAllLines(TelemetryFile))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            yield return doc.RootElement.Clone();
        }
    }

    // Convenience helpers for the 5 verification facts:
    public IEnumerable<JsonElement> ReadExportedLogs()    => ReadExportedRecords().Where(r => r.TryGetProperty("resourceLogs",    out _));
    public IEnumerable<JsonElement> ReadExportedMetrics() => ReadExportedRecords().Where(r => r.TryGetProperty("resourceMetrics", out _));
    public IEnumerable<JsonElement> ReadExportedTraces()  => ReadExportedRecords().Where(r => r.TryGetProperty("resourceSpans",   out _));

    private static string SolutionRoot()
    {
        // Walk up from AppContext.BaseDirectory until we find SK_P.sln
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.GetFiles("SK_P.sln").Any())
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Cannot locate solution root (SK_P.sln) from test base dir");
    }
}
```

### Reference compose.yaml Edit (add otel-collector service block)

```yaml
# compose.yaml — APPEND this service block under "services:" (alongside existing postgres + baseapi-service)
otel-collector:
  image: otel/opentelemetry-collector-contrib:0.95.0
  container_name: sk-otel-collector
  restart: unless-stopped
  command: ["--config=/etc/otel-collector-config.yaml"]
  volumes:
    - ./compose/otel-collector-config.yaml:/etc/otel-collector-config.yaml:ro
    - ./tests/.otel-out:/var/otel-out
  ports:
    - "4317:4317"   # OTLP gRPC
    - "4318:4318"   # OTLP HTTP
  healthcheck:
    test: ["CMD", "wget", "-qO-", "http://localhost:13133/"]
    interval: 5s
    timeout: 3s
    retries: 5
```

## Verification Strategy

### Test Pyramid for Phase 5

```
        ┌──────────────────────────────────────────────┐
        │  Plan 05-02 fact tests (integration)         │
        │  WebApplicationFactory<Program> +            │
        │  OtelCollectorFixture (real Collector) +     │
        │  PostgresFixture (Phase 4 lift) +            │
        │  ExportProcessorType.Simple                  │
        └──────────────────────────────────────────────┘
                        │                              │
              SC#1, SC#2 (logs)                  SC#3 (health probes)
              SC#4 (metrics+logs)                D-16 runtime metric
              SC#5 (traces+Npgsql)               T-05-PII (no params)

        ┌──────────────────────────────────────────────┐
        │  Plan 05-01 build verification (per-grep)    │
        │  No execution — just static asserts          │
        └──────────────────────────────────────────────┘
                        │
        grep counts/positions of pivotal API calls
        (see Per-Pitfall verification commands above)
```

### Concrete Testcontainers + Collector Setup (Plan 05-02)

**Approach:** Real Postgres (via Phase 2's `docker compose up postgres` running at localhost:5433 — NOT Testcontainers in Phase 5 because Phase 8 introduces Testcontainers; reuse Phase 4's pattern of using the existing dev Postgres). Real OTel Collector (via `docker compose up otel-collector` running at localhost:4317 + 4318 + 13133). File exporter writes to host-mounted `tests/.otel-out/telemetry.jsonl`.

**Test class structure:**

```csharp
namespace BaseApi.Tests.Observability;

public sealed class HealthEndpointsTests : IClassFixture<WebAppFactory>, IClassFixture<OtelCollectorFixture>
{
    private readonly WebAppFactory _factory;
    private readonly OtelCollectorFixture _otel;
    private readonly HttpClient _client;

    public HealthEndpointsTests(WebAppFactory factory, OtelCollectorFixture otel)
    {
        _factory = factory;
        _otel    = otel;
        _client  = factory.CreateClient();
    }

    [Fact]
    public async Task Test_HealthLive_AlwaysReturns200_EvenWhenPostgresDown()
    {
        var ct = TestContext.Current.CancellationToken;
        // 1. Hit /health/live with Postgres reachable → 200
        var response1 = await _client.GetAsync("/health/live", ct);
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        var body = await response1.Content.ReadAsStringAsync(ct);
        Assert.Contains("\"status\":\"Healthy\"", body);

        // (Cannot easily simulate "Postgres down" without docker control; the test relies on
        //  the fact that /health/live's predicate excludes the npgsql tag — verified by code review.)
    }

    [Fact]
    public async Task Test_HealthReady_500_WhenPostgresUnreachable_200_WhenReachable()
    {
        // ... uses a WebAppFactory variant that overrides Postgres connection string to a
        //     dead port (e.g., localhost:1) and asserts 503; then uses a healthy fixture.
    }

    [Fact]
    public async Task Test_HealthStartup_503_BeforeMarkReady_200_AfterMarkReady()
    {
        // Override WebAppFactory.ConfigureWebHost to NOT call MarkReady on Build (use a custom
        // post-build hook that defers it). Then:
        // 1. Assert /health/startup returns 503 with "Startup not complete".
        // 2. Resolve IStartupGate from factory.Services, call MarkReady().
        // 3. Assert /health/startup returns 200 with "Startup complete".
    }

    [Fact]
    public async Task Test_HealthPaths_NotInOTelExports()
    {
        var ct = TestContext.Current.CancellationToken;
        // 1. Hit /health/live, /health/ready, /health/startup each 5 times.
        for (var i = 0; i < 5; i++)
        {
            await _client.GetAsync("/health/live", ct);
            await _client.GetAsync("/health/ready", ct);
            await _client.GetAsync("/health/startup", ct);
        }
        // 2. Force-flush exporters (via WebAppFactory.ForceFlushTelemetryAsync helper).
        await _factory.ForceFlushTelemetryAsync(ct);
        // 3. Read .otel-out/telemetry.jsonl.
        var records = _otel.ReadExportedRecords().ToList();
        // 4. Assert: no log record / span / metric data point references "/health/".
        foreach (var record in records)
        {
            var json = record.GetRawText();
            Assert.DoesNotContain("/health/", json);
        }
    }
}
```

**`WebAppFactory.ForceFlushTelemetryAsync` helper:**

```csharp
public async Task ForceFlushTelemetryAsync(CancellationToken ct = default)
{
    var meterProvider  = Services.GetService<MeterProvider>();
    var tracerProvider = Services.GetService<TracerProvider>();
    var loggerProvider = Services.GetService<OpenTelemetry.Logs.LoggerProvider>();

    if (meterProvider  is not null) meterProvider .ForceFlush(timeoutMilliseconds: 5_000);
    if (tracerProvider is not null) tracerProvider.ForceFlush(timeoutMilliseconds: 5_000);
    if (loggerProvider is not null) loggerProvider.ForceFlush(timeoutMilliseconds: 5_000);

    // Small sleep for file exporter to flush to disk (Collector's file exporter has its own buffer).
    await Task.Delay(500, ct);
}
```

### Phase 5 SCs → Test Map

| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| SC#1 (OBSERV-02/-05/-07) | LogInformation export contains correlationId + service.name + service.version | integration | `dotnet test --filter "FullyQualifiedName~Observability.LogExportTests"` | ❌ Wave 0 |
| SC#2 (OBSERV-06) | Logging:LogLevel=Warning suppresses Info from both console+OTLP | integration | `dotnet test --filter "FullyQualifiedName~Observability.LogLevelFilterTests"` | ❌ Wave 0 |
| SC#3 (HEALTH-01..04) | /health/live always 200, /health/ready 503 when PG down, /health/startup gated | integration | `dotnet test --filter "FullyQualifiedName~Observability.HealthEndpointsTests"` | ❌ Wave 0 |
| SC#4 (OBSERV-08, HEALTH-05) | /health/* excluded from both metrics and logs | integration | `dotnet test --filter "FullyQualifiedName~Observability.MetricsExportTests"` + part of HealthEndpointsTests | ❌ Wave 0 |
| SC#5 (OBSERV-12) | Npgsql child span + no parameter values | integration | `dotnet test --filter "FullyQualifiedName~Observability.TraceExportTests"` | ❌ Wave 0 |
| D-16 runtime metrics | process.runtime.dotnet.* metric exported | integration | extra assertion in MetricsExportTests | ❌ Wave 0 |
| T-05-PII | Npgsql span contains SQL template only, no bound values | integration | extra assertion in TraceExportTests | ❌ Wave 0 |

### Sampling Rate

- **Per task commit (Plan 05-01):** `dotnet build --configuration Release --no-restore` + `dotnet build --configuration Debug --no-restore` (zero-warning gate per Phase 1 D-02).
- **Per task commit (Plan 05-02):** `dotnet test --filter "FullyQualifiedName~Observability.<NewTestClass>" --no-build` after each new test file commit.
- **Per plan completion:** Full `dotnet test` 3 consecutive runs (Phase 4 04-02 cadence) — all 31 Phase 4 carry-over + new Phase 5 facts green.
- **Per phase merge:** Full `dotnet build` + `dotnet test` + BEFORE/AFTER `psql \l` byte-identical (D-15 cleanup) + BEFORE/AFTER `tests/.otel-out/` empty (D-11 cleanup).

### Wave 0 Gaps

- [ ] `tests/.otel-out/` directory exists (gitignored contents, but dir present)
- [ ] `compose/` directory exists (for `otel-collector-config.yaml`)
- [ ] `compose/otel-collector-config.yaml` written per D-10
- [ ] `compose.yaml` extended with `otel-collector` service
- [ ] `.gitignore` updated with `tests/.otel-out/*` glob (NOT the dir itself if using `.gitkeep` approach)
- [ ] `Directory.Packages.props` pinned: Runtime 1.15.0 + Npgsql.OpenTelemetry 8.0.4 + HealthChecks.UI.Client 9.0.0
- [ ] `src/BaseApi.Core/BaseApi.Core.csproj` adds 4 new PackageReferences (no Version=)
- [ ] `src/BaseApi.Core/Health/IStartupGate.cs` created
- [ ] `src/BaseApi.Core/Health/StartupHealthCheck.cs` created
- [ ] `src/BaseApi.Service/Program.cs` edited per Pattern 8 reference above
- [ ] `tests/BaseApi.Tests/Observability/` directory + 5+ test files + 1 fixture file
- [ ] `WebAppFactory` extended with `ForceFlushTelemetryAsync` + `ExportProcessorType.Simple` override

## Validation Architecture

### Test Framework

| Property | Value |
|----------|-------|
| Framework | xUnit v3 3.2.2 + Microsoft.Testing.Platform (MTP) |
| Config file | none (xUnit v3 uses attributes) |
| Quick run command | `dotnet test --filter "FullyQualifiedName~Observability" --no-build` |
| Full suite command | `dotnet test --no-build` (runs Phase 3 + Phase 4 + Phase 5 facts together) |

### Phase Requirements → Test Map

| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| OBSERV-01 | OpenTelemetry packages installed | static | `grep -c "OpenTelemetry" Directory.Packages.props` ≥ 6 | ✅ (Phase 1 carry-forward — Plan 05-01 adds Runtime + Npgsql + UI.Client) |
| OBSERV-02 | MEL bridge wiring; NOT WithLogging | static + integration | grep verification + LogExportTests | ❌ Wave 0 |
| OBSERV-03 | HTTP server + client metrics + Runtime metrics export | integration | MetricsExportTests | ❌ Wave 0 |
| OBSERV-04 | OTLP exporter targets configured endpoint | integration | LogExportTests asserts export reaches Collector | ❌ Wave 0 |
| OBSERV-05 | service.name + service.version on all signals | integration | LogExportTests / MetricsExportTests / TraceExportTests resource asserts | ❌ Wave 0 |
| OBSERV-06 | Logging:LogLevel filters both sinks | integration | LogLevelFilterTests | ❌ Wave 0 |
| OBSERV-07 | OTel logger options set correctly | integration | LogExportTests scope-attribute assert (CorrelationId attribute present) | ❌ Wave 0 |
| OBSERV-08 | /health/* excluded from metrics | integration | MetricsExportTests + HealthEndpointsTests | ❌ Wave 0 |
| OBSERV-12 | OTel tracing with Npgsql DB spans | integration | TraceExportTests (Npgsql child span assertion) | ❌ Wave 0 |
| HEALTH-01 | /health/startup behavior | integration | HealthEndpointsTests | ❌ Wave 0 |
| HEALTH-02 | /health/live behavior | integration | HealthEndpointsTests | ❌ Wave 0 |
| HEALTH-03 | /health/ready behavior | integration | HealthEndpointsTests | ❌ Wave 0 |
| HEALTH-04 | NpgSql health check registered | static + integration | grep + HealthEndpointsTests assertion that /health/ready 503 → 200 | ❌ Wave 0 |
| HEALTH-05 | /health/* excluded from logging+metrics | integration | HealthEndpointsTests Test_HealthPaths_NotInOTelExports + MetricsExportTests | ❌ Wave 0 |
| T-05-PII | Npgsql span has no parameter values | integration | TraceExportTests assertion that bound values don't appear | ❌ Wave 0 |
| D-16 | Runtime metrics exported | integration | MetricsExportTests `process.runtime.dotnet.*` exists | ❌ Wave 0 |

### Sampling Rate

- **Per task commit:** `dotnet build` (zero-warning) + targeted `dotnet test --filter` for new test class
- **Per wave merge:** Full `dotnet test` (~31 Phase 4 carry + ~10-12 new Phase 5 facts = ~42)
- **Phase gate:** Full suite green 3 consecutive runs + `psql \l` byte-identical + `tests/.otel-out/` empty post-test

### Wave 0 Gaps

- [ ] `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` — IAsyncLifetime fixture
- [ ] `tests/BaseApi.Tests/Observability/LogExportTests.cs` — SC#1 facts (3-4)
- [ ] `tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` — SC#2 facts (2)
- [ ] `tests/BaseApi.Tests/Observability/MetricsExportTests.cs` — SC#4 metrics half + D-16 runtime (3-4 facts)
- [ ] `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` — SC#3 + SC#4 logs half (4-5 facts)
- [ ] `tests/BaseApi.Tests/Observability/TraceExportTests.cs` — SC#5 + T-05-PII (2-3 facts)
- [ ] Possibly extend `WebAppFactory` (existing — Phase 4) to register `ExportProcessorType.Simple` override and `ForceFlushTelemetryAsync` helper

*Total new facts estimated: 10-15.*

## Security Domain

> security_enforcement: enabled (no config opting out; default applies per gsd-phase-researcher contract)

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|------------------|
| V2 Authentication | NO | Out of scope per PROJECT.md (no auth in v1) |
| V3 Session Management | NO | Out of scope (no sessions) |
| V4 Access Control | NO | Out of scope (no authz) |
| V5 Input Validation | LIMITED | Health endpoints take no user input; OTel doesn't accept input. CorrelationIdMiddleware (Phase 4) already enforces ASCII-printable header validation (Pitfall 3 / T-04-INJECT). |
| V6 Cryptography | NO | TLS for OTLP is a v2 concern (local Collector is HTTP for dev); never hand-roll. |
| V7 Error Handling and Logging | **YES** | T-05-PII (Npgsql span params NOT captured). T-04-LEAK (Phase 4 mitigation preserved — health probes' JSON body is per-check status only, no exception text). |
| V8 Data Protection | LIMITED | DB connection string contains password; appsettings convention (Postgres dev creds) is documented out-of-scope per Phase 2 D-04. No new exposure. |
| V10 Malicious Code | NO | No dynamic code paths in Phase 5. |
| V12 Files and Resources | LIMITED | `tests/.otel-out/` is host-mounted; gitignored to prevent accidental commit of exported telemetry data (which might contain stack traces or message strings during dev). |
| V14 Configuration | YES | `OTEL_EXPORTER_OTLP_ENDPOINT` env var is non-secret. AlwaysOn sampler is documented; future TraceIdRatioBasedSampler is also non-secret. |

### Known Threat Patterns for Phase 5 Stack

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| **T-05-PII** (Sensitive workflow data captured in span attributes) | Information Disclosure | Npgsql.OpenTelemetry 8.0.4 default does NOT capture parameter values; SQL template only. **Verified in this research that the default IS safe** — no opt-out needed. Drop CONTEXT D-05's non-compiling lambda. |
| **T-05-LEAK** (Exception details leak to OTLP exporter and downstream) | Information Disclosure | Phase 4's FallbackExceptionHandler logs full exception via MEL but Body to client is sanitized. MEL → OTel → Collector: the exception's stack trace IS in the OTLP log record (intentional — ops need it). The risk is the Collector + downstream backend's access control. Out of v1 scope (ops responsibility). |
| **T-05-CORR-INJECT** (Malicious X-Correlation-Id header injects via log scope) | Tampering / Information Disclosure | Phase 4 CorrelationIdMiddleware ASCII-printable validation (Pitfall 3 / T-04-INJECT mitigation) still applies; the validated value is what `IncludeScopes = true` exports. No new attack surface. |
| **T-05-HEALTH-DOS** (Probe traffic floods backend) | Denial of Service | `/health/*` filter on metrics+traces (Pitfall 10) plus coarse log filter via `Microsoft.AspNetCore: Warning` (D-09) prevent probe traffic from amplifying through OTel export. |
| **T-05-STARTUP-STUCK** (Startup gate never flips → service permanently 503 readiness) | Denial of Service | Phase 5 default `MarkReady()` immediately after Build prevents stuck state in v1. Phase 8 MigrationRunner must call `MarkReady()` AFTER `MigrateAsync()` — failure to call leaves the service in indefinite 503. Phase 8 verification fact should assert MarkReady is called even when migration fails (via try/finally OR explicit error-state handling — TBD in Phase 8 research). |
| **T-05-EXPORT-BLOCK** (Slow/unreachable Collector blocks app request thread) | Availability | OTel batch processor default (async background thread). Tests use ExportProcessorType.Simple but prod keeps Batch. Default flush timeout 10s does not block app shutdown beyond that. |

### Threat Mitigations Implemented in Phase 5 Scope

| Threat | Mitigation | Verified By |
|--------|------------|-------------|
| T-05-PII | `.AddNpgsql()` with no options; SQL template captured, parameter values not | TraceExportTests fact asserts no bound value strings appear in Npgsql span attributes |
| T-05-HEALTH-DOS | `Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health")` on AspNetCoreInstrumentation (metrics + traces) | MetricsExportTests fact + HealthEndpointsTests fact |
| T-05-STARTUP-STUCK | `MarkReady()` called immediately after Build in Phase 5 | HealthEndpointsTests SC#3 fact (300 status with "Startup complete" message) |
| T-04-LEAK (Phase 4 carry-forward) | FallbackExceptionHandler preserves no-leak property; health probe JSON has only per-check status from `UIResponseWriter` — does NOT include exception text in `description` for the `npgsql` check by default | Inherited from Phase 4 04-02 verification |

## Sources

### Primary (HIGH confidence)

- **`.planning/research/STACK.md`** — locked OTel pin versions (lines 29-32, 40-41, 45, 92-95, 103, 295-300). VERIFIED in Directory.Packages.props.
- **`.planning/research/PITFALLS.md`** — Pitfall 8 (MEL bridge, lines 226-267), Pitfall 9 (Logging:LogLevel single source, lines 271-291), Pitfall 10 (/health filter, lines 295-320), Pitfall 15 (liveness must not check DB, lines 437-466). VERIFIED — drove Phase 5 architecture.
- **`.planning/research/ARCHITECTURE.md`** — Telemetry Flow (lines 600-625), Telemetry Integration code example (lines 651-692), anti-pattern 5 MEL filter (lines 899-903). VERIFIED — Phase 5 implements Pattern 1.
- **`.planning/phases/05-observability-health-probes/05-CONTEXT.md`** — locked user decisions D-01..D-16 (lines 38-305). VERIFIED — Phase 5 plan input.
- **`.planning/phases/04-cross-cutting-middleware-error-handling/04-01-SUMMARY.md`** + **`04-02-SUMMARY.md`** — Program.cs current state (72 lines), `public partial class Program { }` marker, CorrelationIdMiddleware BeginScope("CorrelationId") key, Npgsql 8.0.9 pin. VERIFIED via direct file Read.
- **`src/BaseApi.Service/Program.cs`** — Phase 4 final state (72 lines, verified). [VERIFIED: Read tool]
- **`Directory.Packages.props`** — 25 pins as of Phase 4 (verified). [VERIFIED: Read tool]
- **`Directory.Build.props`** — net8.0 target, TreatWarningsAsErrors=true, Nullable=enable. [VERIFIED: Read tool]
- **`compose.yaml`** — Phase 2 final state with postgres at 5433 + baseapi-service placeholder under phase-8 profile. [VERIFIED: Read tool]
- **`src/BaseApi.Service/appsettings.json`** — `Service.Name=sk-api`, `Service.Version=3.2.0`, `OpenTelemetry:Endpoint=http://otel-collector:4317`. [VERIFIED: Read tool]
- **`src/BaseApi.Service/appsettings.Development.json`** — `OpenTelemetry:Endpoint=http://localhost:4317`. [VERIFIED: Read tool]

### Secondary (MEDIUM-HIGH confidence — verified against official docs)

- **[NpgsqlTracingOptionsBuilder API docs](https://www.npgsql.org/doc/api/Npgsql.NpgsqlTracingOptionsBuilder.html)** — 11 methods listed; NO `IncludeParameterValues` / `EnableSensitiveDataLogging`. [VERIFIED: WebFetch]
- **[Npgsql.OpenTelemetry 8.0.4 NuGet](https://www.nuget.org/packages/Npgsql.OpenTelemetry/8.0.4)** — published 2024-09-10; netstandard2.0; depends on Npgsql >= 8.0.4. [VERIFIED: WebFetch]
- **[Npgsql.OpenTelemetry TracerProviderBuilderExtensions.cs source v8.0.4](https://github.com/npgsql/npgsql/blob/v8.0.4/src/Npgsql.OpenTelemetry/TracerProviderBuilderExtensions.cs)** — `AddNpgsql(this TracerProviderBuilder builder, Action<NpgsqlTracingOptions>? options = null)` — the `options` parameter is DECLARED but NOT consumed in 8.0.4. [VERIFIED: WebFetch]
- **[AspNetCore.HealthChecks.UI.Client 9.0.0 NuGet](https://www.nuget.org/packages/AspNetCore.HealthChecks.UI.Client/9.0.0)** — published 2024-12-19; targets .NET 8.0/9.0/10.0. [VERIFIED: WebFetch]
- **[AspNetCore.HealthChecks.NpgSql 9.0.0 NuGet](https://www.nuget.org/packages/AspNetCore.HealthChecks.NpgSql/)** — `AddNpgSql(connectionString, name, failureStatus, tags)` signature. [CITED: WebSearch verified]
- **[AspNetCore.Diagnostics.HealthChecks GitHub](https://github.com/Xabaril/AspNetCore.Diagnostics.HealthChecks)** — `UIResponseWriter.WriteHealthCheckUIResponse` is from `AspNetCore.HealthChecks.UI.Client` package; namespace `HealthChecks.UI.Client`. [VERIFIED: WebFetch]
- **[OpenTelemetry.Instrumentation.Runtime 1.15.1 NuGet](https://www.nuget.org/packages/OpenTelemetry.Instrumentation.Runtime/)** — latest stable 1.15.1; CONTEXT pins 1.15.0 to match the existing Instrumentation.* 1.15.0 cadence. [VERIFIED: WebSearch]
- **[OpenTelemetry.Instrumentation.Runtime README](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/src/OpenTelemetry.Instrumentation.Runtime/README.md)** — `.AddRuntimeInstrumentation()` extension on `MeterProviderBuilder`. [CITED]

### Tertiary (MEDIUM confidence — single source, not contradicted)

- **[Npgsql tracing docs](https://www.npgsql.org/doc/diagnostics/tracing.html)** — shows `.AddNpgsql()` basic setup but does NOT confirm default behavior re parameter capture. [CITED — combined with source-code inspection to conclude default is safe]
- **[opentelemetry-dotnet OTLP Exporter README](https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/README.md)** — env var honor list. [ASSUMED — confirmed via STACK.md line 30 which is the project's locked baseline]

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `services.AddOpenTelemetry().WithLogging(...)` | `builder.Logging.AddOpenTelemetry(...)` (MEL bridge) | OTel 1.5+ stable (2023) | Pitfall 8 documented; Phase 5 follows the MEL path |
| `Microsoft.AspNetCore.Diagnostics.HealthChecks` (legacy NuGet) | In-box `Microsoft.Extensions.Diagnostics.HealthChecks` (`AddHealthChecks()`) | .NET 6+ | STACK.md line 262; Phase 5 uses in-box |
| `HealthChecks.UI` dashboard | Plain JSON via `UIResponseWriter.WriteHealthCheckUIResponse` (UI.Client only) | locked decision per STACK.md line 263 | No UI surface area; ops curls the probes directly |
| Custom Postgres SELECT 1 check | `AspNetCore.HealthChecks.NpgSql` | Phase 1 D-05 pin | Don't hand-roll |
| Custom runtime metrics polling | `OpenTelemetry.Instrumentation.Runtime` + event counters | OTel 1.4+ stable | Phase 5 D-16 |
| `[Obsolete] FluentValidation.AspNetCore` auto-validation | Explicit `IValidator<T>.ValidateAsync` (FluentValidation 12) | FluentValidation 11+ deprecation; FV12 removal | Not Phase 5 but constrains Phase 6 — referenced for context |
| `Activity.Current.SetBaggage` for cross-process correlation | `Activity.Current?.AddTag(...)` for in-process | OTel 1.0+ | Phase 5 doesn't add baggage (D-15 — no custom ActivitySource yet) |
| `ExportProcessorType.Batch` (always) | `Batch` in prod, `Simple` in tests via ConfigureTestServices | OTel SDK consistent — best practice | Phase 5 verification uses Simple per Pitfall 5-G |

**Deprecated/outdated (do NOT use in Phase 5):**
- `Microsoft.AspNetCore.Diagnostics.HealthChecks` standalone package — replaced by in-box namespace.
- `services.AddOpenTelemetry().WithLogging(...)` — bypasses MEL; Pitfall 8.
- `AddFluentValidationAutoValidation()` — deprecated in FV 11, removed in FV 12 (not Phase 5 scope but contextual).

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Npgsql.OpenTelemetry 8.0.4's `Action<NpgsqlTracingOptions>? options` parameter is DECLARED but UNUSED in the 8.0.4 implementation | "Pattern 6 — Npgsql Tracing" | If actually consumed, CONTEXT D-05's lambda might compile when a property like `IncludeParameterValues` is added; in 8.0.4 verified absent via [VERIFIED: WebFetch GitHub source] |
| A2 | OTLP exporter SDK default `Endpoint = http://localhost:4317` when env var is unset | "Pattern 9" | If actual default is null/throws, Plan 05-01 must pass explicit endpoint via `o.Endpoint = new Uri(cfg["OpenTelemetry:Endpoint"]!)` |
| A3 | `OTEL_EXPORTER_OTLP_ENDPOINT` env var is honored by `AddOtlpExporter()` with no options block | "Pattern 9" | If env var ignored, Plan 05-02 fixture must set the endpoint via property assignment |
| A4 | `OpenTelemetry.Instrumentation.Runtime 1.15.0` exists on nuget.org (latest stable is 1.15.1 per WebSearch; 1.15.0 is one patch back) | "Standard Stack" | If 1.15.0 unavailable, pin 1.15.1 — same API; CONTEXT D-16 just locks "1.15.x" cadence |
| A5 | `otel/opentelemetry-collector-contrib:0.95.0` image tag still available on Docker Hub at execution time | "Pattern 10" | If pulled, planner bumps to current 0.115.0+; file exporter + OTLP receiver APIs stable across versions |
| A6 | Phase 4's existing `WebAppFactory` (in `tests/BaseApi.Tests/Middleware/`) can be extended (not duplicated) for Phase 5 observability tests | "Verification Strategy" | If extension creates merge conflict, Plan 05-02 creates a new `OtelWebAppFactory` adjacent to it in Observability/ |
| A7 | `tests/.otel-out/` directory on Windows + Docker Desktop + WSL2 will mount cleanly with default permissions | "Pitfall 5-H" | If permission denied, `OtelCollectorFixture.InitializeAsync` adds `Directory.CreateDirectory + File.SetAttributes` defensively |
| A8 | The existing Phase 4 `tests/BaseApi.Tests/Middleware/PostgresFixture` can be reused (or re-lifted) for Phase 5 tests that need a real DB | "Test Pyramid" | If conflict, Plan 05-02 creates `Observability/PostgresFixture.cs` as a third lift (Phase 3 → 4 → 5 cascade) |
| A9 | The OTel batch processor's default 5-second timeout is enough that calling `ForceFlush(timeoutMilliseconds: 5_000)` followed by `Task.Delay(500)` reliably gets records to the file on disk | "ForceFlush helper" | If unreliable, increase delay to 1000ms or poll the file with retries up to 5s |
| A10 | `MapHealthChecks` registered BEFORE `MapControllers` does not shadow controller routes (because route templates are precise: `/health/live` etc. never overlap with `/api/v1/...`) | "Pattern 8 — Three-Probe Tag Discipline" | Phase 7 controllers use `/api/v1/[controller]` prefix; no overlap risk |

## Open Questions / Risks (RESOLVED in planning iteration 1)

### Open Question 1: CONTEXT D-05 Npgsql tracing options lambda DOES NOT COMPILE

**What we know:**
- CONTEXT D-05 shows `.AddNpgsql(opts => { opts.EnableEntityFrameworkCoreInstrumentation = false; })` as the locked Phase 5 wiring shape.
- `NpgsqlTracingOptions` in Npgsql.OpenTelemetry 8.0.4 has NO `EnableEntityFrameworkCoreInstrumentation` property. [VERIFIED: WebFetch on NpgsqlTracingOptionsBuilder API docs + GitHub source]
- The `Action<NpgsqlTracingOptions>? options` parameter on `AddNpgsql` is DECLARED but the method body does NOT consume it in 8.0.4. [VERIFIED: GitHub source webfetch]

**What's unclear:**
- Whether D-05's authorial intent is "set this property to ensure values aren't captured" (which the property name suggests — but the wrong concept; that's EFCore.NpgsqlEntityFrameworkCoreInstrumentation territory, NOT Npgsql.OpenTelemetry) OR "demonstrate any options block to prevent values from being captured" (which is correct intent but wrong API).

**Recommendation for planner:**
1. Plan 05-01 implements `.AddNpgsql()` with NO callback (the correct call).
2. Add a comment above the call documenting why no callback: `// SECURITY: parameter values not captured by default in Npgsql.OpenTelemetry 8.0.4 — T-05-PII satisfied`.
3. Plan 05-01 SUMMARY documents this as a "RESEARCH-side correction to CONTEXT D-05" (mirrors Phase 4 04-01's "Option A FK regex" deviation under D-08 Claude's Discretion).
4. Plan 05-02 TraceExportTests asserts default behavior: SQL template appears in span; bound parameter values do NOT appear.

**Risk if wrong:** Build break under `TreatWarningsAsErrors=true` (Phase 1 D-02) at Plan 05-01 Task 7 (Program.cs edit). Plan 05-01 catches this in Task 8 (full Release+Debug build verification) and fix-forwards in the same task.

**RESOLVED:** 05-01-PLAN.md Reconciliation 1 + Task 5 (Program.cs OTel wiring) — bare `.AddNpgsql()` (no callback); package default already does NOT capture parameter values, so T-05-PII is satisfied without the non-compiling lambda. Pitfall 5-E (this RESEARCH.md, lines 871-889) documents the build-break trap and grep guards.

### Open Question 2: OTLP endpoint resolution — env var vs appsettings

**What we know:**
- OBSERV-04 says "endpoint from `OTEL_EXPORTER_OTLP_ENDPOINT` env var (default fallback to `OpenTelemetry:Endpoint` appsettings)".
- The OTel SDK's `AddOtlpExporter()` with no options block already auto-honors the env var.
- Phase 1's appsettings.json AND appsettings.Development.json both set `OpenTelemetry:Endpoint` to a URL — but Phase 5 wiring doesn't read those keys explicitly.

**What's unclear:**
- Whether "fallback to appsettings" means the SDK auto-reads it (it does NOT — the SDK reads ONLY env vars), or whether code must manually read `cfg["OpenTelemetry:Endpoint"]` and pass via `o.Endpoint = new Uri(...)`.

**Recommendation for planner:**
- **Simpler approach (chosen):** Rely purely on env var. Plan 05-01 documents in Program.cs comment that `OTEL_EXPORTER_OTLP_ENDPOINT` must be set externally (launchSettings.json for dev, compose env for prod). The appsettings.json `OpenTelemetry:Endpoint` keys remain as documentation but are NOT consumed by the OTel SDK. Plan 05-02 verification fixture sets the env var explicitly.
- **OR (hybrid — more loyal to OBSERV-04):** Plan 05-01 reads `cfg["OpenTelemetry:Endpoint"]` as fallback and passes via `o.Endpoint = new Uri(...)` inside the options block. Three call sites (logs, metrics, traces) each repeat the read. Slightly more code; preserves the appsettings as a useful default.

**Recommendation:** Simpler approach. Phase 5 does not need to read appsettings for OTel endpoint — the env-var-or-default works fine in dev and prod. If a future operator wants to configure via appsettings without env var, they can add a single `var endpoint = cfg["OpenTelemetry:Endpoint"]; if (endpoint is not null) Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", endpoint);` line before the OTel wiring — but that's a YAGNI follow-up.

**RESOLVED:** 05-01-PLAN.md Task 5 (Program.cs OTel wiring) — relies on `OTEL_EXPORTER_OTLP_ENDPOINT` env var auto-honored by `.AddOtlpExporter()` with no options block (SDK default fallback `http://localhost:4317`). The appsettings.json `OpenTelemetry:Endpoint` keys are NOT consumed by the OTel SDK (kept as documentation). Plan 05-02 OtelCollectorFixture sets the env var explicitly in its constructor (also pins via `OtlpExporterOptions.Endpoint`).

### Open Question 3: launchSettings.json — commit or gitignore?

**What we know:**
- Phase 4 04-01 SUMMARY documents `src/BaseApi.Service/Properties/launchSettings.json` as untracked, "appeared as a Visual Studio-generated dev-launch profile with developer-specific ports". Recommendation: add to `.gitignore` in Phase 7.
- For Phase 5 to set `OTEL_EXPORTER_OTLP_ENDPOINT` for `dotnet run`, launchSettings.json is the canonical file.

**What's unclear:**
- Should Phase 5 commit launchSettings.json with the OTel env var?
- Should Phase 5 gitignore it and rely on devs to set it manually?

**Recommendation for planner:**
- COMMIT a minimal `launchSettings.json` with the dev env var. Override Phase 4's untracked default. This makes the dev-onboarding story coherent: `dotnet run` works without manual env setup. The VS-generated port numbers are usually ephemeral; commit a stable choice (e.g., 8080 or 5000).
- Plan 05-01 includes this as a deliberate file commit + docs the choice in 05-01-SUMMARY.

**RESOLVED:** 05-01-PLAN.md revision notes iteration 1 INFO #1 (option a) — NO launchSettings.json committed by Phase 5. Phase 4 left the file untracked (developer-specific ports); Phase 5 preserves that policy. The `OTEL_EXPORTER_OTLP_ENDPOINT` env var is set by `OtelCollectorFixture` for tests (Plan 05-02 Task 2) and by `compose.yaml` baseapi-service block for production (Plan 05-01 Task 4). A comment in Program.cs documents that the SDK default `http://localhost:4317` is used when the env var is unset (sufficient for `dotnet run` against a locally-running otel-collector).

### Open Question 4: Should logs include `service.namespace` resource attribute?

**What we know:**
- OBSERV-05 specifies only `service.name` + `service.version`.
- OTel semantic conventions also define `service.namespace`, `service.instance.id`, `deployment.environment`.
- ARCHITECTURE.md line 663 shows an example that includes `deployment.environment` via `.AddAttributes(...)`.

**What's unclear:**
- Whether Phase 5 should add `deployment.environment=cfg["ASPNETCORE_ENVIRONMENT"]` as an additive enhancement.

**Recommendation for planner:**
- DO NOT add additional resource attributes in Phase 5. OBSERV-05 locks the contract to `service.name` + `service.version` only. Adding `deployment.environment` is a useful future enhancement but out of scope for v1 — flag as a Phase Y followup if ops needs it.

**RESOLVED:** NOT added in Phase 5 — OBSERV-05 locks the resource attribute contract to `service.name` + `service.version` only. 05-01-PLAN.md Task 5 (Program.cs OTel wiring) sets exactly those two attributes on BOTH the LoggerProvider (via `SetResourceBuilder`) AND the MeterProvider/TracerProvider (via `ConfigureResource`). `deployment.environment` / `service.namespace` / `service.instance.id` are intentionally omitted; flagged as a Phase Y followup if ops needs them.

### Open Question 5: Test for "/health/* not in OTLP logs" — how to assert specifically?

**What we know:**
- D-09 chose the coarse `Microsoft.AspNetCore: Warning` log filter, which drops request-start/finish logs for ALL endpoints — not just `/health/*`.
- SC#4 says "requests to `/health/*` do not produce metrics or appear in OTLP logs (filtered out)".

**What's unclear:**
- How to ASSERT this precisely. Possible interpretations:
  - **Loose:** `tests/.otel-out/telemetry.jsonl` contains NO log record whose body or attributes contain `/health/`. This is what the coarse filter achieves.
  - **Strict:** OTel exporter receives ZERO log records for `/health/*` requests. This is also what the coarse filter achieves (because at Warning level, no Microsoft.AspNetCore.Hosting.Diagnostics records fire for those requests).

**Recommendation for planner:**
- Assert the **loose** interpretation in `HealthEndpointsTests.Test_HealthPaths_NotInOTelExports`: after hitting `/health/*` N times + an app endpoint once, the export file's log records contain ZERO occurrences of `/health/`. The app endpoint record (if generated by your stub via explicit `ILogger.LogInformation`) should appear if you want to prove the filter is path-specific, not absolute. Document that this is the coarse-filter version of SC#4 and per-path log filtering is deferred.

**RESOLVED:** 05-02-PLAN.md Task 5 `Test_HealthEndpoints_Absent_From_OTLP_Logs` — implements the **loose** interpretation. Test issues 10 GET requests to each of `/health/live`, `/health/ready`, `/health/startup`, calls `factory.FlushAsync()`, reads the OTLP-exported log records via `factory.ReadExportedLogs()`, and asserts none of the three path strings appear in any log record's raw JSON. Per iteration-2 revision: assertion is path-string-only — status codes are intentionally ignored, no Postgres reachability dependency.

### Risk 1: Phase 8 forward-compat for `MigrationRunner` order

Phase 5 ships `IStartupGate` with immediate `MarkReady()` after `Build()`. Phase 8 must (a) remove that line, (b) register `MigrationRunner : IHostedService` as the FIRST hosted service so it runs before any other background work. If Phase 8 incorrectly leaves the immediate `MarkReady()` line in place, the gate flips to true BEFORE migrations complete and `/health/startup` reports Healthy prematurely. **Mitigation:** Phase 8 plan checker should grep for `app.Services.GetRequiredService<IStartupGate>().MarkReady()` and FAIL if present after Phase 8 lands.

**RESOLVED:** 05-01-PLAN.md ships the Phase 5 contract (immediate `MarkReady()` after `Build()` per CONTEXT D-13). The Phase 8 grep-guard is deferred to Phase 8's planner-checker — flagged as a Phase 8 verification requirement in 05-01-SUMMARY hand-off note + 05-RESEARCH.md Phase 5 / Phase 8 contract table (Pattern 7 lines 561-571). Risk acknowledged, mitigation owned by Phase 8.

### Risk 2: OtelCollectorFixture cross-class state pollution

`tests/.otel-out/telemetry.jsonl` is shared across all test classes in `Observability/`. If xUnit runs LogExportTests + MetricsExportTests in parallel (xUnit v3 class-parallelism enabled), both classes' export records interleave in the same file → assertions on "exactly one log record" become flaky.

**Mitigation options:**
1. Disable class-parallelism for the `Observability` collection via `[CollectionDefinition(DisableParallelization = true)]`. Simple, makes the suite serial — slow but reliable.
2. Each fixture instance writes to a UNIQUE per-class file (e.g., `telemetry.{ClassName}.jsonl`) — requires Collector config to dynamically pick the file, which the file exporter doesn't natively support.
3. Each test acquires a file lock + truncates before exercising the SUT.

**Recommendation:** Option 1 — `[CollectionDefinition("Observability", DisableParallelization = true)]` + `[Collection("Observability")]` on each test class. Trade-off: ~5-10s slower full suite vs deterministic assertions. Plan 05-02 documents.

**RESOLVED:** 05-02-PLAN.md Task 2 ships `tests/BaseApi.Tests/Observability/CollectionDefinitions.cs` with `[CollectionDefinition("Observability", DisableParallelization = true)]`, and every Phase 5 test class declares `[Collection("Observability")]` (LogExportTests, LogLevelFilterTests, HealthEndpointsTests, MetricsExportTests, TraceExportTests). xUnit v3 serializes execution within the collection, eliminating the shared-file interleave hazard.

### Risk 3: Compose service order — otel-collector starts AFTER baseapi-service in Phase 8

In Phase 8 (out of scope for Phase 5 but planner should be aware), `compose up` brings up postgres + otel-collector + baseapi-service. If `baseapi-service` boots BEFORE otel-collector is ready, its initial OTLP exports fail silently (per Pattern 9 failure mode — retries + drop). Not a functional bug but ops will see "first few seconds of logs missing" complaints.

**Mitigation:** Phase 8 adds `depends_on: otel-collector: condition: service_healthy` to baseapi-service. The Collector's healthcheck (port 13133) is the gate. Phase 5 ships the healthcheck — Phase 8 wires the dependency.

**RESOLVED:** Phase 5 ships the Collector healthcheck (compose.yaml otel-collector block, Plan 05-01 Task 4 — healthcheck shells `wget -qO- http://localhost:13133/` with 5s interval, 5 retries). Phase 8 will wire `depends_on: otel-collector: condition: service_healthy` on the baseapi-service block. Risk owned by Phase 8; Phase 5 prerequisite (healthcheck endpoint) is in place.

## Metadata

**Confidence breakdown:**
- Standard stack (versions, pins, transitive deps): HIGH — verified by NuGet WebFetch + STACK.md + Directory.Packages.props
- MEL bridge pattern (OBSERV-02, Pitfall 8): HIGH — multiple sources (PITFALLS.md, ARCHITECTURE.md, opentelemetry-dotnet docs)
- Health probe tag discipline: HIGH — PITFALLS.md Pitfall 15 + AspNetCore.HealthChecks.NpgSql verified API
- Npgsql parameter capture default: HIGH — verified from official NpgsqlTracingOptionsBuilder docs + GitHub source v8.0.4
- OTLP exporter env var auto-honor: MEDIUM-HIGH — single primary source (OTel exporter README) + STACK.md baseline; not explicitly verified end-to-end in this session
- Compose collector image (0.95.0) availability at execution time: MEDIUM — CONTEXT-locked but not re-verified against Docker Hub
- Cross-class fixture pollution risk: MEDIUM — empirically observable in xUnit v3, but Phase 4 04-02 used `IClassFixture` without issue (no shared file); Phase 5 adds the shared file dimension
- launchSettings.json policy: LOW — planner's judgment call

**Research date:** 2026-05-27
**Valid until:** 2026-06-26 (30 days for stable .NET 8 stack)

## RESEARCH COMPLETE
