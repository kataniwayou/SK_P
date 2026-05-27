# Phase 5: Observability + Health Probes - Pattern Map

**Mapped:** 2026-05-27
**Files analyzed:** 17 (8 CREATE + 5 MODIFY + 4 infra/test files)
**Analogs found:** 16 / 17 (one infra-as-code file has no analog — `compose/otel-collector-config.yaml`)

> Reads consumed: `05-CONTEXT.md`, `05-RESEARCH.md` (pages 1-644 of 1645 — file-creation manifest sections; later pages contain Pitfall details + Open Questions, not pattern-shape material — early-stop justified), `src/BaseApi.Service/Program.cs`, `src/BaseApi.Core/Middleware/CorrelationIdMiddleware.cs`, `src/BaseApi.Core/Persistence/BaseDbContext.cs`, `src/BaseApi.Core/Persistence/Interceptors/AuditInterceptor.cs`, `src/BaseApi.Core/Persistence/Exceptions/PostgresExceptionMapper.cs` (header only), `src/BaseApi.Core/Exceptions/Handlers/NotFoundExceptionHandler.cs`, `src/BaseApi.Core/Exceptions/Handlers/FallbackExceptionHandler.cs`, `src/BaseApi.Core/Exceptions/NotFoundException.cs`, `src/BaseApi.Core/BaseApi.Core.csproj`, `src/BaseApi.Service/BaseApi.Service.csproj`, `tests/BaseApi.Tests/BaseApi.Tests.csproj`, `tests/BaseApi.Tests/Persistence/PostgresFixture.cs`, `tests/BaseApi.Tests/Middleware/PostgresFixture.cs`, `tests/BaseApi.Tests/Middleware/WebAppFactory.cs`, `tests/BaseApi.Tests/Middleware/TestErrorDbContext.cs`, `tests/BaseApi.Tests/Middleware/CorrelationIdTests.cs`, `tests/BaseApi.Tests/Middleware/SqlStateMappingTests.cs` (top 80 lines), `tests/BaseApi.Tests/Endpoints/TestController.cs`, `tests/BaseApi.Tests/Persistence/DiLifetimeTests.cs` (top 60 lines), `tests/BaseApi.Tests/Persistence/StubHttpContextAccessor.cs`, `Directory.Packages.props`, `Directory.Build.props`, `compose.yaml`, `src/BaseApi.Service/appsettings.json`, `src/BaseApi.Service/appsettings.Development.json`, `.editorconfig`, `.gitignore`.
>
> Phase 1-4 SUMMARY files (`01-SUMMARY.md`, `03-SUMMARY.md`, `04-SUMMARY.md`) requested by the prompt do NOT exist on disk — only per-plan SUMMARYs exist (`01-01-SUMMARY.md` ... `04-02-SUMMARY.md`). The phase-level invariants the prompt expected to extract from them are already documented inline in CONTEXT D-08/D-13/D-14 and verified directly from the source files above.

---

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/BaseApi.Core/Health/IStartupGate.cs` (CREATE) | domain/contract — interface + sealed impl | event-driven (one-shot latch) | `src/BaseApi.Core/Exceptions/NotFoundException.cs` (file-header + sealed-class shape) PLUS `src/BaseApi.Core/Middleware/CorrelationIdMiddleware.cs` (`public sealed class` + namespace conv.) | role-match (no existing latch/gate type — closest is sealed pure-CLR type w/ doc-comment header) |
| `src/BaseApi.Core/Health/StartupHealthCheck.cs` (CREATE) | domain/contract — `IHealthCheck` impl with primary-constructor ctor | request-response (CheckHealthAsync invoked per probe hit) | `src/BaseApi.Core/Exceptions/Handlers/NotFoundExceptionHandler.cs` (`sealed class : IXxxHandler` + ctor-injected dep + async result) | role-match (closest existing "tiny single-interface impl in Core" pattern) |
| `src/BaseApi.Core/Health/StartupCompletionService.cs` (CREATE — Phase 5 temporary) | host/composition — `IHostedService` that flips gate on start | event-driven (StartAsync fires once) | NONE in this codebase — no `IHostedService` exists yet. Closest shape is the `AuditInterceptor : SaveChangesInterceptor` pattern (ctor DI of two services + async-only override + `internal sealed`) | role-match (lifecycle hook, not interceptor — but injection shape + sealed/internal conv. transfers) |
| `src/BaseApi.Service/Program.cs` (MODIFY) | host/composition root | request-response (composition-time DI graph build) | `src/BaseApi.Service/Program.cs` itself (Phase 4 final state) | exact — same file, additive insert |
| `Directory.Packages.props` (MODIFY — 3 new pins) | config — CPM single-source-of-truth | n/a (build-time graph) | `Directory.Packages.props` itself (Phase 1-4 final state) | exact — same file, additive insert |
| `src/BaseApi.Core/BaseApi.Core.csproj` (MODIFY — 3 new `<PackageReference>`s) | config | n/a | `src/BaseApi.Core/BaseApi.Core.csproj` (Phase 4 final state) | exact — same file, additive insert |
| `compose.yaml` (MODIFY — new `otel-collector` service) | infra-as-code — Docker Compose service definition | streaming (OTLP gRPC + container stdout) | `compose.yaml` `postgres` service (same file, same compose schema, same healthcheck + volume + ports pattern) | role-match (different image + different pipelines; same compose conventions: image+ports+healthcheck+volumes+depends_on shape) |
| `compose/otel-collector-config.yaml` (CREATE — new file) | infra-as-code — Collector pipeline | streaming (OTLP receivers → file+logging exporters) | NONE — no `.yaml` config file exists in this repo apart from `compose.yaml` itself | NO ANALOG (use RESEARCH.md D-10 verbatim shape) |
| `appsettings.json` (NO CHANGE — verified) | config | n/a | already in place from Phase 1 D-11 / INFRA-04 | exact — file untouched |
| `.gitignore` (MODIFY — one new line `tests/.otel-out/`) | config | n/a | `.gitignore` final state (Phase 1 D-15 pattern: trailing `# SK_P project additions (CONTEXT.md D-15)` block) | exact — additive line in established marker block |
| `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` (CREATE) | test fixture — `IAsyncLifetime` + `WebApplicationFactory<Program>` host config | event-driven (truncate-on-init / delete-on-dispose) + composition (`ConfigureWebHost(...)`) | `tests/BaseApi.Tests/Middleware/PostgresFixture.cs` (truncate-on-init / drop-on-dispose `IAsyncLifetime`) **fused with** `tests/BaseApi.Tests/Middleware/WebAppFactory.cs` (`WebApplicationFactory<Program>` + `ConfigureTestServices`) | role-match (closest analogs cover the two halves of the fused responsibility; the Phase 5 fixture combines them) |
| `tests/BaseApi.Tests/Observability/LogExportTests.cs` (CREATE) | test class — fact battery using fixture | request-response (HTTP via WebApplicationFactory) | `tests/BaseApi.Tests/Middleware/CorrelationIdTests.cs` (`public sealed class XxxTests` with `[Fact]` methods + `TestContext.Current.CancellationToken` + `WebAppFactory` create/dispose) | exact (header pattern) — only assertion targets differ |
| `tests/BaseApi.Tests/Observability/LogLevelFilterTests.cs` (CREATE) | test class | request-response | same as `LogExportTests.cs` analog | exact |
| `tests/BaseApi.Tests/Observability/MetricsExportTests.cs` (CREATE) | test class | request-response | same as `LogExportTests.cs` analog | exact |
| `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` (CREATE) | test class | request-response | same as `LogExportTests.cs` analog | exact |
| `tests/BaseApi.Tests/Observability/TraceExportTests.cs` (CREATE — uses fixture + real Postgres) | test class with `IClassFixture<>` + real DB | request-response w/ child span | `tests/BaseApi.Tests/Middleware/SqlStateMappingTests.cs` (`IClassFixture<PostgresFixture>` + `WebAppFactory(_fixture.ConnectionString)` + post-call body assertions) | exact — same orchestration pattern, different assertion stream (JSONL file instead of HTTP body) |
| (implicit: tests/.otel-out/ host-mount dir) | runtime artifact directory (gitignored) | file-I/O | n/a | n/a |

---

## Pattern Assignments

### `src/BaseApi.Core/Health/IStartupGate.cs` (domain/contract, event-driven)

**Analog:** `src/BaseApi.Core/Exceptions/NotFoundException.cs` (file header + sealed-class doc comment shape) + `src/BaseApi.Core/Middleware/CorrelationIdMiddleware.cs` (namespace/access shape).

**File header pattern** (analog: `NotFoundException.cs` lines 1-18 — XML `<summary>` + `<para>` block referencing decision IDs):

```csharp
namespace BaseApi.Core.Exceptions;

/// <summary>
/// Thrown by service-layer code when a lookup by ID returns no row.
///
/// <para>
/// <b>Mapping (D-06 #1 / ERROR-06):</b> claimed by <c>NotFoundExceptionHandler</c>
/// in the IExceptionHandler chain — produces HTTP 404 with
/// <c>detail = Message</c> AND <c>ProblemDetails.Extensions["resourceType"]</c>
/// + <c>ProblemDetails.Extensions["resourceId"]</c> for clients that want to
/// branch programmatically.
/// </para>
/// ...
/// </summary>
public sealed class NotFoundException : Exception
```

Mirror in `IStartupGate.cs`:
- `namespace BaseApi.Core.Health;` (file-scoped per `.editorconfig` line 76 `csharp_style_namespace_declarations = file_scoped:warning`).
- XML `<summary>` + `<para>` block referencing decision IDs (`D-01`, `D-13`, Phase 8 contract).
- `public interface IStartupGate` followed by `internal sealed class StartupGate : IStartupGate` per CONTEXT D-01 (the implementation is `internal sealed` — registered via DI; consumers depend on the interface).

**Sealed thread-safe latch pattern** (no exact analog — closest shape is the `AuditInterceptor` field+ctor structure):

```csharp
// Verbatim CONTEXT D-01 + RESEARCH Pattern 7 — no analog in codebase
public interface IStartupGate
{
    bool IsReady { get; }
    void MarkReady();
}

internal sealed class StartupGate : IStartupGate
{
    private int _isReady;            // 0 = false, 1 = true (Interlocked has no bool overload in .NET 8)
    public bool IsReady => Volatile.Read(ref _isReady) == 1;
    public void MarkReady() => Interlocked.Exchange(ref _isReady, 1);
}
```

**Outside-namespace usings rule** (analog: `src/BaseApi.Core/Middleware/CorrelationIdMiddleware.cs` lines 1-4):

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BaseApi.Core.Middleware;
```

Mirror in `IStartupGate.cs`: NO `using` directives needed (only `System.Threading.Volatile` + `Interlocked` — both pulled in by `<ImplicitUsings>enable</ImplicitUsings>` per `Directory.Build.props` line 29).

**Divergence vs analog:** `NotFoundException` is `public sealed`; `StartupGate` is `internal sealed` because callers depend on `IStartupGate` (not the concrete). The interface itself is `public` (must cross assembly boundary — `BaseApi.Service.Program.cs` resolves it).

---

### `src/BaseApi.Core/Health/StartupHealthCheck.cs` (domain/contract, request-response)

**Analog:** `src/BaseApi.Core/Exceptions/Handlers/NotFoundExceptionHandler.cs` (sealed-class `: IXxx` + ctor-injected dep + async result). The shape is identical: tiny class that takes one dep, implements one async method from a framework interface, returns a typed result.

**Imports pattern** (analog lines 1-5):

```csharp
using BaseApi.Core.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BaseApi.Core.Exceptions.Handlers;
```

Mirror in `StartupHealthCheck.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BaseApi.Core.Health;
```

**Sealed-class + DI ctor pattern** (analog lines 19-23):

```csharp
public sealed class NotFoundExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _pdSvc;

    public NotFoundExceptionHandler(IProblemDetailsService pdSvc) => _pdSvc = pdSvc;
```

Mirror — but the `StartupHealthCheck` uses C# 12 **primary constructor** per CONTEXT D-02 (verbatim):

```csharp
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

This matches `.editorconfig` line 79 `csharp_style_prefer_primary_constructors = true:suggestion` — the project encourages primary constructors but the analog (`NotFoundExceptionHandler`) was written before adoption became consistent. **NEW Core types in Phase 5 SHOULD use primary constructors** since they are the codified style preference and the type is trivial (no field-level XML doc needed, no logger validation).

**Async result pattern** (analog lines 25-50): `async ValueTask<bool>` + ctor reads + state mutation. `StartupHealthCheck` is even simpler — `Task.FromResult(...)` because there is no `await` inside; no `async` keyword needed.

**Divergence vs analog:**
- `NotFoundExceptionHandler` is `public sealed` (cross-assembly resolution needed — registered in `Program.cs`); `StartupHealthCheck` is `internal sealed` because it's resolved via the generic type parameter `AddCheck<StartupHealthCheck>(...)` which only requires the type to be visible to the `BaseApi.Service` consumer assembly — and `BaseApi.Service` references `BaseApi.Core` via `<ProjectReference>` which makes `internal` types via `InternalsVisibleTo` STILL invisible. **CORRECTION:** The CONTEXT D-02 lock specifies `internal sealed` but `AddCheck<T>()` requires `T` to be public OR `InternalsVisibleTo("BaseApi.Service")` must be declared on `BaseApi.Core`. **Planner decision needed:** either (a) make `StartupHealthCheck` `public sealed` (deviates from CONTEXT D-02 wording but is the simpler path), or (b) add `[assembly: InternalsVisibleTo("BaseApi.Service")]` to `BaseApi.Core` (preserves D-02 wording, adds one assembly-attr file). Recommendation: **(a)** — `public sealed`. Phase 4's `NotFoundExceptionHandler` is `public sealed` for the same reason; consistency wins.

---

### `src/BaseApi.Core/Health/StartupCompletionService.cs` (host/composition, event-driven — Phase 5 only, Phase 8 removes)

**Analog:** No `IHostedService` exists in this codebase. Closest shape: `src/BaseApi.Core/Persistence/Interceptors/AuditInterceptor.cs` (ctor DI of two services + async-only override + `public sealed`).

**NOTE on naming:** The CONTEXT lock (D-08 line 165-166) shows the Phase 5 default-ready behavior as a single line in `Program.cs`:

```csharp
// Phase 5: Mark gate ready immediately after build (Phase 8 will replace this with MigrationRunner)
app.Services.GetRequiredService<IStartupGate>().MarkReady();
```

The prompt's file-list mentions `StartupCompletionService.cs` (the immediate-MarkReady IHostedService for Phase 5 default; Phase 8 will remove). **The CONTEXT does NOT require this file** — D-08 uses an inline `app.Services.GetRequiredService<IStartupGate>().MarkReady()` call. The prompt-mandated `StartupCompletionService.cs` is an INTERPRETATION of D-13 (which describes Phase 8 introducing `MigrationRunner : IHostedService`). Two viable readings:

1. **Inline (CONTEXT D-08 literal):** Phase 5 has ZERO `IHostedService` types; the `MarkReady()` call is one line in `Program.cs`. Phase 8's `MigrationRunner` becomes the FIRST `IHostedService`.
2. **Hosted-service (prompt literal):** Phase 5 ships `StartupCompletionService : IHostedService` whose `StartAsync` calls `_gate.MarkReady()`. Phase 8 replaces it with `MigrationRunner`.

**Planner decision:** Reading (2) is what the prompt asks for — and it is cleaner for Phase 8 substitution (delete one registration, add another; vs. delete one inline line, add a registration). Pattern below assumes reading (2). **Planner must reconcile with CONTEXT D-08 explicitly.**

**Imports + sealed-class + DI ctor pattern** (analog: `AuditInterceptor.cs` lines 1-7):

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using BaseApi.Core.Entities;

namespace BaseApi.Core.Persistence.Interceptors;

public sealed class AuditInterceptor : SaveChangesInterceptor
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly TimeProvider _clock;

    public AuditInterceptor(IHttpContextAccessor httpContextAccessor, TimeProvider clock)
    {
        _httpContextAccessor = httpContextAccessor;
        _clock = clock;
    }
```

Mirror in `StartupCompletionService.cs` (primary-ctor variant per `.editorconfig` preference for new code):

```csharp
using Microsoft.Extensions.Hosting;

namespace BaseApi.Core.Health;

/// <summary>
/// Phase 5 default-ready hook. Flips <see cref="IStartupGate.MarkReady"/> on host
/// start so <c>/health/startup</c> reports Healthy in v1 (no migrations yet).
///
/// <para>
/// <b>Phase 8 contract:</b> this type is REMOVED. A new
/// <c>MigrationRunner : IHostedService</c> registered FIRST runs
/// <c>db.Database.MigrateAsync()</c> THEN calls <c>_gate.MarkReady()</c>.
/// Clean one-file deletion + one new registration in Phase 8.
/// </para>
/// </summary>
internal sealed class StartupCompletionService(IStartupGate gate) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        gate.MarkReady();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

**Async-only-override pattern** (analog lines 49-58): the interceptor explicitly does NOT override the sync `SavingChanges` — production must use async path. `StartupCompletionService` similarly returns `Task.CompletedTask` synchronously because there is no actual work; `IHostedService.StartAsync` is the canonical async hook.

**Divergence vs analog:** `AuditInterceptor` is `public sealed` (Phase 7 composition root + Phase 3 test fixture both wire it via `AddInterceptors(...)` — must cross assembly). `StartupCompletionService` is `internal sealed` BUT must be registered via `AddHostedService<T>()` which (per same constraint as `AddCheck<T>`) requires `T` to be accessible to the registering assembly — **same `InternalsVisibleTo` decision applies as `StartupHealthCheck`**. Planner recommendation: `public sealed` for consistency.

---

### `src/BaseApi.Service/Program.cs` (host/composition, modifications)

**Analog:** Itself — Phase 4 final state. Phase 5 is purely ADDITIVE; pipeline order from Phase 4 D-01 is preserved verbatim.

**Phase 4 final state (lines 13-72 — verbatim baseline):**

```csharp
using BaseApi.Core.Exceptions.Handlers;
using BaseApi.Core.Middleware;

var builder = WebApplication.CreateBuilder(args);

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

// IExceptionHandler chain — REGISTRATION ORDER IS LOAD-BEARING (D-06).
builder.Services.AddExceptionHandler<NotFoundExceptionHandler>();
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
builder.Services.AddExceptionHandler<DbUpdateExceptionHandler>();
builder.Services.AddExceptionHandler<FallbackExceptionHandler>();

builder.Services.AddControllers();

var app = builder.Build();

// PIPELINE ORDER (D-01): UseExceptionHandler FIRST ...
app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseRouting();
app.MapControllers();

app.Run();

// Marker type for WebApplicationFactory<Program> ...
public partial class Program { }
```

**Insertion points (CONTEXT D-08 — three of them):**

**(A) After exception-handler registrations, BEFORE `AddControllers()`:** insert OTel + Health registrations.

**(B) Immediately after `var app = builder.Build();`, BEFORE `app.UseExceptionHandler();`:** insert either the inline `MarkReady()` line OR the `AddHostedService<StartupCompletionService>()` registration (the latter goes ABOVE the `Build()` call in block (A); only the inline reading needs (B)).

**(C) After `app.UseRouting();`, BEFORE `app.MapControllers();`:** insert 3 × `app.MapHealthChecks(...)`.

**Insertion (A) — OTel + Health registration block** (verbatim from RESEARCH Pattern 1 + 2 + 8 + CONTEXT D-08 with the D-05 correction from RESEARCH):

```csharp
// ============================================================================
// Phase 5: Observability (OTel logs via MEL bridge — Pitfall 8 — + metrics + traces)
// ============================================================================
var cfg = builder.Configuration;
var serviceName    = cfg["Service:Name"]!;      // "sk-api" — INFRA-04
var serviceVersion = cfg["Service:Version"]!;   // "3.2.0"

// Logs — MEL bridge. ILogger<T>.LogInformation flows here.
// IncludeScopes=true exports Phase 4 CorrelationIdMiddleware's BeginScope("CorrelationId", id)
// as a log attribute named "CorrelationId" on every OTLP-exported record.
builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes           = true;
    o.ParseStateValues        = true;
    o.SetResourceBuilder(ResourceBuilder.CreateDefault()
        .AddService(serviceName: serviceName, serviceVersion: serviceVersion));
    o.AddOtlpExporter();   // env var OTEL_EXPORTER_OTLP_ENDPOINT honored
});

// Metrics + traces — services-level provider chain. .ConfigureResource MUST come first.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(serviceName: serviceName, serviceVersion: serviceVersion))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation(opts =>
            opts.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))  // Pitfall 10
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()                                                // D-16
        .AddOtlpExporter())
    .WithTracing(t => t
        .SetSampler(new AlwaysOnSampler())                                          // D-04
        .AddAspNetCoreInstrumentation(opts =>
            opts.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
        .AddHttpClientInstrumentation()
        .AddNpgsql()                                                                 // D-05 corrected — no opts block (RESEARCH §"Pattern 6")
        .AddOtlpExporter());

// ============================================================================
// Phase 5: Health gate + checks
// ============================================================================
builder.Services.AddSingleton<IStartupGate, StartupGate>();
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
    .AddCheck<StartupHealthCheck>("startup", tags: new[] { "startup", "ready" })
    .AddNpgSql(cfg.GetConnectionString("Postgres")!, tags: new[] { "ready" });

// Phase 5 default-ready: register the IHostedService that flips the gate on host start.
// Phase 8 will REMOVE this line and register MigrationRunner instead.
builder.Services.AddHostedService<StartupCompletionService>();
```

**Insertion (B) — only used if the planner adopts the inline-MarkReady reading of D-08 INSTEAD of `StartupCompletionService`:**

```csharp
var app = builder.Build();

// Phase 5: Mark gate ready immediately after build (Phase 8 will replace with MigrationRunner)
app.Services.GetRequiredService<IStartupGate>().MarkReady();
```

**Insertion (C) — 3 × MapHealthChecks (between `UseRouting()` and `MapControllers()`):**

```csharp
app.UseRouting();

// Phase 5: health endpoints (BEFORE MapControllers — plumbing first, business last)
app.MapHealthChecks("/health/live",    new HealthCheckOptions {
    Predicate      = c => c.Tags.Contains("live"),
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse,
});
app.MapHealthChecks("/health/ready",   new HealthCheckOptions {
    Predicate      = c => c.Tags.Contains("ready"),
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse,
});
app.MapHealthChecks("/health/startup", new HealthCheckOptions {
    Predicate      = c => c.Tags.Contains("startup"),
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse,
});

app.MapControllers();
```

**New using directives at top of file** (alphabetical, outside-namespace per `.editorconfig` line 119):

```csharp
using BaseApi.Core.Exceptions.Handlers;
using BaseApi.Core.Health;                                  // NEW (Phase 5)
using BaseApi.Core.Middleware;
using HealthChecks.UI.Client;                               // NEW — UIResponseWriter (note: no AspNetCore. prefix in namespace)
using Microsoft.AspNetCore.Diagnostics.HealthChecks;        // NEW — HealthCheckOptions
using Microsoft.Extensions.Diagnostics.HealthChecks;        // NEW — HealthCheckResult
using OpenTelemetry.Logs;                                   // NEW — AddOtlpExporter (logs)
using OpenTelemetry.Metrics;                                // NEW — WithMetrics chain extensions
using OpenTelemetry.Resources;                              // NEW — ResourceBuilder, AddService
using OpenTelemetry.Trace;                                  // NEW — AlwaysOnSampler, WithTracing chain
```

**Invariant preserved:** The `public partial class Program { }` marker (Phase 1 D-10, line 72 of current Program.cs) MUST remain at the bottom. `WebApplicationFactory<Program>` (Phase 4 + Phase 5 fixtures) and the planner's `OtelCollectorFixture` both depend on it.

**Divergence vs analog (Phase 4 Program.cs):**
- Phase 4 introduced top-of-file comment "Phase 1 scaffold per CONTEXT.md D-10..." Phase 5's additive edits SHOULD NOT remove that block — it remains accurate (Phase 7 will refactor everything to `AddBaseApi(...)` extension methods). Plan 05-01 MAY append one new paragraph to the comment block describing the OTel + Health inserts; the planner picks the wording.
- Phase 4's order was `Configuration → AddHttpContextAccessor → AddProblemDetails → AddExceptionHandler×4 → AddControllers → Build → UseExceptionHandler → UseMiddleware → UseRouting → MapControllers → Run`. Phase 5 inserts OTel + Health REGISTRATIONS between `AddExceptionHandler×4` and `AddControllers`; HEALTH-PIPELINE inserts between `UseRouting` and `MapControllers`. Nothing existing moves.

---

### `Directory.Packages.props` (config, MODIFY — 3 new pins)

**Analog:** Itself (current state, lines 71-84). Adding entries follows the same `<PackageVersion Include="…" Version="…" />` shape with thematic grouping comments.

**Existing thematic blocks** (lines 71-84):

```xml
<!-- Observability — core + hosting + OTLP exporter pinned together -->
<PackageVersion Include="OpenTelemetry" Version="1.15.3" />
<PackageVersion Include="OpenTelemetry.Extensions.Hosting" Version="1.15.3" />
<PackageVersion Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.15.3" />
<!-- Instrumentation packages version on their own cadence (currently 1.15.0) -->
<PackageVersion Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.15.0" />
<PackageVersion Include="OpenTelemetry.Instrumentation.Http" Version="1.15.0" />
...
<!-- Health checks -->
<PackageVersion Include="AspNetCore.HealthChecks.NpgSql" Version="9.0.0" />
```

**Phase 5 additions — place each in its thematic block:**

```xml
<!-- Append to Observability block (after Instrumentation.Http line 77) -->
<PackageVersion Include="OpenTelemetry.Instrumentation.Runtime" Version="1.15.0" />
<PackageVersion Include="Npgsql.OpenTelemetry" Version="8.0.4" />

<!-- Append to Health checks block (after AspNetCore.HealthChecks.NpgSql line 84) -->
<PackageVersion Include="AspNetCore.HealthChecks.UI.Client" Version="9.0.0" />
```

**Convention check (Phase 1 D-06 / file-header lines 13-14):** "Front-load ALL STACK-verified pins in Phase 1. Each subsequent phase only adds `<PackageReference>` entries." — Phase 5 is a documented exception (3 new pins surfaced by RESEARCH §"Standard Stack — NEW pins"). Plan 05-01 SUMMARY should call out the additions explicitly.

**Divergence vs convention:** Adding `<PackageVersion>` lines in subsequent phases is documented as expected for later-phase research finds (e.g., Phase 4 added Npgsql 8.0.9 as a fix-forward per lines 52-60 inline comment block). Phase 5's 3 additions follow the same precedent.

---

### `src/BaseApi.Core/BaseApi.Core.csproj` (config, MODIFY — 3 new `<PackageReference>` entries)

**Analog:** Itself (current state). Phase 5 adds 3 `<PackageReference>` lines following the CPM convention (zero `Version=` attrs).

**Existing pattern** (lines 37-55, two thematic `<ItemGroup>` blocks):

```xml
<ItemGroup>
  <!-- EF Core 8 + Npgsql + snake_case convention (Phase 3 CONTEXT.md D-12). ...
       Versions resolve from Directory.Packages.props (CPM — no Version= per Phase 1 D-05/D-06). -->
  <PackageReference Include="Microsoft.EntityFrameworkCore" />
  <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" />
  <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
  <PackageReference Include="Npgsql" />
  <PackageReference Include="EFCore.NamingConventions" />
</ItemGroup>

<ItemGroup>
  <!-- FluentValidation 12 — ... -->
  <PackageReference Include="FluentValidation" />
</ItemGroup>
```

**Phase 5 addition — append new `<ItemGroup>` block** (RESEARCH §"Standard Stack" Plan 05-01 Task 1):

```xml
<ItemGroup>
  <!-- Phase 5 Observability (CONTEXT.md D-08 + D-16). OTel core/exporter/hosting +
       AspNetCore/Http auto-instrumentation already pinned in Directory.Packages.props
       from Phase 1 D-05. Phase 5 ADDS:
         - Runtime (D-16, process.runtime.dotnet.*)
         - Npgsql.OpenTelemetry (D-05, DB child spans)
       Versions resolve from Directory.Packages.props (CPM — no Version= per Phase 1 D-05/D-06). -->
  <PackageReference Include="OpenTelemetry" />
  <PackageReference Include="OpenTelemetry.Extensions.Hosting" />
  <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" />
  <PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" />
  <PackageReference Include="OpenTelemetry.Instrumentation.Http" />
  <PackageReference Include="OpenTelemetry.Instrumentation.Runtime" />
  <PackageReference Include="Npgsql.OpenTelemetry" />
</ItemGroup>

<ItemGroup>
  <!-- Phase 5 Health Checks (CONTEXT.md D-03 + D-07). NpgSql probe + UI.Client JSON writer. -->
  <PackageReference Include="AspNetCore.HealthChecks.NpgSql" />
  <PackageReference Include="AspNetCore.HealthChecks.UI.Client" />
</ItemGroup>
```

**CPM contract verification** (RESEARCH §"Installation"): `grep -n 'Version=' src/BaseApi.Core/BaseApi.Core.csproj` after edit MUST still return ZERO matches on `<PackageReference>` lines.

**Divergence vs analog:** none — additive following the established two-ItemGroup-per-feature pattern (EF + Validation today → EF + Validation + Observability + Health after Phase 5).

---

### `compose.yaml` (infra-as-code, MODIFY — new `otel-collector` service)

**Analog:** `compose.yaml` `postgres` service (lines 6-22). Same compose v2 schema, same fields (image, restart, environment OR command, ports, volumes, healthcheck).

**Existing pattern** (lines 6-22):

```yaml
services:
  postgres:
    image: postgres:17-alpine
    restart: unless-stopped
    environment:
      POSTGRES_DB: ${POSTGRES_DB}
      POSTGRES_USER: ${POSTGRES_USER}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
    ports:
      - "5433:5432"
    volumes:
      - pgdata:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U $$POSTGRES_USER -d $$POSTGRES_DB"]
      interval: 5s
      timeout: 5s
      retries: 10
      start_period: 5s
```

**Phase 5 addition — `otel-collector` service** (CONTEXT D-10 + RESEARCH §"otel-collector docker-compose service"). Mirror the field order from `postgres`:

```yaml
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
      - "4318:4318"   # OTLP HTTP (curl-from-laptop debugging)
    healthcheck:
      test: ["CMD", "wget", "-qO-", "http://localhost:13133/"]
      interval: 5s
      timeout: 3s
      retries: 5
```

**Convention discipline (mirrored from `postgres`):**
- `restart: unless-stopped` — same as `postgres`.
- Container-relative volume paths (`./compose/...`, `./tests/.otel-out`) — same relative-path convention as `pgdata:` (named-volume) variant. The Collector needs a host bind-mount (test reads files); `postgres` uses a named volume (data persists across restarts). Both styles are acceptable per Compose v2.
- `healthcheck` block fields: `test`, `interval`, `timeout`, `retries` — match `postgres` shape; omit `start_period` (Collector boots faster than Postgres).
- 2-space indentation — matches `.editorconfig` line 27 `[*.{json,yml,yaml}] indent_size = 2`.

**Divergence vs analog (intentional):**
- `image: otel/opentelemetry-collector-contrib:0.95.0` (CONTEXT D-10 explicitly: contrib variant required for the file exporter that Plan 05-02 verification depends on).
- `command:` field present (Postgres uses the default entrypoint; Collector needs `--config=...`).
- Two `ports` lines (Postgres has one). Both are bound to localhost in Linux dev.
- `container_name` set explicitly (Postgres doesn't set one) — Compose auto-generates `sk_p-postgres-1`; Collector wants `sk-otel-collector` for ops-friendly `docker logs sk-otel-collector` invocations. Optional; planner may omit for consistency with Postgres.
- NO `profiles: ["phase-8"]` (Postgres doesn't have one either; only `baseapi-service` does, lines 31-32). Collector is always-on for Phase 5.
- NO `environment:` block (Postgres needs DB credentials; Collector reads everything from the host-mounted config file).
- NO `depends_on:` (Postgres doesn't depend on Collector and vice versa).

---

### `compose/otel-collector-config.yaml` (infra-as-code, CREATE — NO ANALOG)

**Analog:** NONE in this codebase. Only `compose.yaml` exists; no `.yaml` config files for any service.

**Pattern source:** RESEARCH §"otel-collector docker-compose service" + CONTEXT D-10 (literal verbatim shape). External canonical reference: https://github.com/open-telemetry/opentelemetry-collector-contrib (referenced in CONTEXT line 350).

**Verbatim content** (use CONTEXT D-10's exact shape):

```yaml
receivers:
  otlp:
    protocols:
      grpc:  { endpoint: 0.0.0.0:4317 }
      http:  { endpoint: 0.0.0.0:4318 }

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

**Convention discipline (no analog — derive from `.editorconfig`):**
- 2-space indentation per `.editorconfig` line 27 `[*.{json,yml,yaml}] indent_size = 2`.
- `end_of_line = lf`, `charset = utf-8`, `insert_final_newline = true` per `.editorconfig` lines 13-15.
- File location: `compose/otel-collector-config.yaml` at repo root (new directory). Mirrors the host-path declared in `compose.yaml` volumes block.

**Planner verification:** `docker compose config` (no flags) MUST parse the merged file without error. `docker compose up otel-collector` MUST bring the container Healthy (the `health_check` extension on port 13133 is what `compose.yaml`'s `healthcheck.test: wget http://localhost:13133/` queries).

---

### `.gitignore` (config, MODIFY — one new line)

**Analog:** `.gitignore` current state (lines 402-413). Phase 5 appends one line to the existing "SK_P project additions" marker block.

**Existing marker block** (lines 402-413):

```
# SK_P project additions (CONTEXT.md D-15)
*.received.*

# Build output (explicit lowercase reinforcement of [Bb]in/ and [Oo]bj/ above)
bin/
obj/

# Local environment overrides (CONTEXT.md D-06) - Compose auto-loads .env but NOT .env.local;
# developers wanting per-machine overrides use: docker compose --env-file .env.local up
.env.local
*.env.local
```

**Phase 5 addition** (CONTEXT D-10 + integration-points note line 379):

```
# Phase 5 (CONTEXT.md D-10) — otel-collector file exporter host-mount target.
# Each test class truncates telemetry.jsonl on InitializeAsync and deletes on DisposeAsync.
tests/.otel-out/
```

**Convention discipline:** Each block has a comment header naming the source decision (`D-15`, `D-06`, `D-10`). Mirror the convention with a `D-10` reference.

**Divergence vs analog:** none — purely additive in the established block-with-header pattern.

---

### `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` (test fixture, CREATE)

**Analog (split responsibility):**
- `tests/BaseApi.Tests/Middleware/PostgresFixture.cs` — `IAsyncLifetime` truncate-on-init + cleanup-on-dispose pattern.
- `tests/BaseApi.Tests/Middleware/WebAppFactory.cs` — `WebApplicationFactory<Program>` + `ConfigureTestServices` + optional connection-string param.

The Phase 5 fixture fuses both: it is a `WebApplicationFactory<Program>` subclass that ALSO implements `IAsyncLifetime` for the `.otel-out/` truncate/delete cadence. (Alternatively the planner may split into two types — but a single fused fixture is closer to the WebAppFactory analog and the IClassFixture pattern that SqlStateMappingTests already uses.)

**Imports + sealed class + namespace pattern** (analog: `WebAppFactory.cs` lines 1-7):

```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BaseApi.Tests.Middleware;

public sealed class WebAppFactory : WebApplicationFactory<Program>
```

Mirror in `OtelCollectorFixture.cs`:

```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;          // ExportProcessorType
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace BaseApi.Tests.Observability;

public sealed class OtelCollectorFixture : WebApplicationFactory<Program>, IAsyncLifetime
```

**Truncate-on-init / cleanup-on-dispose pattern** (analog: `PostgresFixture.cs` lines 39-74):

```csharp
public async ValueTask InitializeAsync()
{
    // [analog creates the test DB]
    await using var adminConn = new NpgsqlConnection(AdminConnectionString);
    await adminConn.OpenAsync();
    await using var createCmd = adminConn.CreateCommand();
    createCmd.CommandText = $"CREATE DATABASE \"{DatabaseName}\"";
    await createCmd.ExecuteNonQueryAsync();
    // ...
}

public async ValueTask DisposeAsync()
{
    NpgsqlConnection.ClearAllPools();
    await using var adminConn = new NpgsqlConnection(AdminConnectionString);
    await adminConn.OpenAsync();
    await using var dropCmd = adminConn.CreateCommand();
    dropCmd.CommandText = $"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE)";
    await dropCmd.ExecuteNonQueryAsync();
}
```

Mirror for `tests/.otel-out/telemetry.jsonl`:

```csharp
private static readonly string TelemetryFile =
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", ".otel-out", "telemetry.jsonl");
// Or — preferred — resolve via an env var the launchSettings/test runner can set;
// planner picks. The path MUST resolve to the same file the otel-collector container
// has volume-mounted at /var/otel-out/telemetry.jsonl.

public ValueTask InitializeAsync()
{
    // Truncate to zero bytes (or create empty if missing). Mirrors PostgresFixture
    // "CREATE DATABASE" — start each test class with a clean slate.
    Directory.CreateDirectory(Path.GetDirectoryName(TelemetryFile)!);
    File.WriteAllText(TelemetryFile, string.Empty);
    return ValueTask.CompletedTask;
}

public ValueTask DisposeAsync()
{
    // Delete (file may not exist if exporter never wrote — that's fine).
    // Mirrors PostgresFixture DROP DATABASE discipline (CONTEXT D-11 "BEFORE/AFTER byte-identical").
    if (File.Exists(TelemetryFile))
    {
        File.Delete(TelemetryFile);
    }
    return ValueTask.CompletedTask;
}
```

**WebHost configuration pattern** (analog: `WebAppFactory.cs` lines 37-52):

```csharp
protected override void ConfigureWebHost(IWebHostBuilder builder)
{
    builder.ConfigureTestServices(services =>
    {
        // Register the test assembly's [ApiController] types via assembly part discovery.
        services.AddControllers()
            .AddApplicationPart(typeof(WebAppFactory).Assembly);

        if (_connectionString is not null)
        {
            services.AddDbContext<TestErrorDbContext>(opts =>
                opts.UseNpgsql(_connectionString)
                    .UseSnakeCaseNamingConvention());
        }
    });
}
```

Mirror in `OtelCollectorFixture.cs` — add the same TestController assembly part (so Plan 05-02 can hit `/test/ok`, `/test/not-found`, etc., to emit traces) PLUS swap OTel exporters to `ExportProcessorType.Simple` for deterministic flush (CONTEXT D-11 line 248):

```csharp
protected override void ConfigureWebHost(IWebHostBuilder builder)
{
    builder.ConfigureTestServices(services =>
    {
        // Re-register the test assembly's [ApiController] types (Phase 4 pattern).
        services.AddControllers()
            .AddApplicationPart(typeof(OtelCollectorFixture).Assembly);

        // Phase 5 D-11: force-flush via ExportProcessorType.Simple so tests can assert
        // against the .otel-out/telemetry.jsonl file synchronously. The OTel chain is
        // already wired in Program.cs (AddOpenTelemetry().WithMetrics().WithTracing());
        // here we re-configure the exporters' processor type. The MEL-bridge logger
        // similarly needs Simple processor; planner picks the API shape from RESEARCH §"D-11"
        // and the OTel docs.
        services.Configure<OtlpExporterOptions>(o =>
        {
            o.ExportProcessorType = ExportProcessorType.Simple;
            o.BatchExportProcessorOptions = new();   // unused when Simple
        });
    });
}
```

**Helper methods for log/metric/trace readers** (CONTEXT D-11 lines 246-247):

```csharp
public IReadOnlyList<JsonElement> ReadExportedLogs()
    => ReadJsonLines().Where(e => HasField(e, "logRecords")).ToList();

public IReadOnlyList<JsonElement> ReadExportedMetrics()
    => ReadJsonLines().Where(e => HasField(e, "metrics")).ToList();

public IReadOnlyList<JsonElement> ReadExportedTraces()
    => ReadJsonLines().Where(e => HasField(e, "spans")).ToList();

// (planner picks exact JSON-path shape from OTLP file-exporter docs)
```

**Divergence vs analogs (acknowledged):**
- `PostgresFixture` uses an admin connection to CREATE/DROP a database; `OtelCollectorFixture` uses `File.WriteAllText("")` + `File.Delete(...)` for the same purpose against a file. The shape (init+dispose, clean slate, no leaks) is the same.
- `WebAppFactory` takes an optional `string? connectionString` constructor arg for opt-in DB wiring; `OtelCollectorFixture` may take a similar opt-in for Postgres DB wiring used by `TraceExportTests` (Pattern 6 — Npgsql child spans).
- `WebAppFactory` is NOT an `IAsyncLifetime` (caller does `using var factory = new WebAppFactory();` inline per test). `OtelCollectorFixture` IS an `IAsyncLifetime` because the `.otel-out/` discipline mandates per-class lifecycle. Tests use `IClassFixture<OtelCollectorFixture>` (same as `SqlStateMappingTests : IClassFixture<PostgresFixture>` — see Pattern Assignments below).

---

### `tests/BaseApi.Tests/Observability/{LogExportTests,LogLevelFilterTests,MetricsExportTests,HealthEndpointsTests,TraceExportTests}.cs` (test classes, CREATE)

**Analog:** `tests/BaseApi.Tests/Middleware/CorrelationIdTests.cs` (header + `[Fact]` + `TestContext.Current.CancellationToken` invariant) and `tests/BaseApi.Tests/Middleware/SqlStateMappingTests.cs` (`IClassFixture<PostgresFixture>` + ctor injection + `_fixture.ConnectionString` + body assertions).

**Header + sealed class + `[Fact]` pattern** (analog `CorrelationIdTests.cs` lines 1-27):

```csharp
using System.Net;
using Xunit;

namespace BaseApi.Tests.Middleware;

/// <summary>
/// SC#1 (OBSERV-09/10/11) — verifies the CorrelationIdMiddleware behaviors: ...
/// </summary>
public sealed class CorrelationIdTests
{
    [Fact]
    public async Task Test_Missing_Header_Generates_32CharHex_On2xx()
    {
        var ct = TestContext.Current.CancellationToken;     // xUnit1051 invariant
        using var factory = new WebAppFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/test/ok", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Correlation-Id", out var values));
        Assert.Matches("^[a-f0-9]{32}$", values!.First());
    }
}
```

Mirror per Phase 5 test class — header points at the relevant ROADMAP SC#, body uses `OtelCollectorFixture` via `IClassFixture<>`. Example for `LogExportTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// SC#1 (OBSERV-02 / OBSERV-05 / OBSERV-07) — Phase 4 CorrelationIdMiddleware's
/// BeginScope("CorrelationId", id) propagates through MEL → OTel LoggerProvider
/// (IncludeScopes=true) to the OTLP-exported log record as a "CorrelationId"
/// log attribute. Service resource attributes service.name=sk-api,
/// service.version=3.2.0 present on every record.
/// </summary>
public sealed class LogExportTests : IClassFixture<OtelCollectorFixture>
{
    private readonly OtelCollectorFixture _fixture;

    public LogExportTests(OtelCollectorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Test_LogRecord_Has_CorrelationId_And_ServiceResource()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _fixture.CreateClient();

        var response = await client.GetAsync("/test/ok", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // ExportProcessorType.Simple → file write is synchronous; no sleep needed.
        var logs = _fixture.ReadExportedLogs();
        Assert.NotEmpty(logs);
        // ... assertions on CorrelationId attribute + service.name + service.version
    }
}
```

**IClassFixture pattern** (analog `SqlStateMappingTests.cs` lines 14-18):

```csharp
public sealed class SqlStateMappingTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public SqlStateMappingTests(PostgresFixture fixture) => _fixture = fixture;
```

Mirror verbatim — replace `PostgresFixture` with `OtelCollectorFixture`. For `TraceExportTests` which needs BOTH the OTel fixture AND a real Postgres connection, use **TWO fixtures** (xUnit composes via `IClassFixture<TFixture1>, IClassFixture<TFixture2>`):

```csharp
public sealed class TraceExportTests : IClassFixture<OtelCollectorFixture>, IClassFixture<PostgresFixture>
{
    private readonly OtelCollectorFixture _otel;
    private readonly PostgresFixture _pg;

    public TraceExportTests(OtelCollectorFixture otel, PostgresFixture pg)
    {
        _otel = otel;
        _pg = pg;
    }
    // ...
}
```

Alternative: have `OtelCollectorFixture` take an optional `PostgresFixture` collaborator in its ctor (xUnit collection fixtures pattern, `ICollectionFixture<>` + `[CollectionDefinition]`). Planner picks; the dual-`IClassFixture` shape is the simpler path and mirrors existing PostgresFixture-usage patterns.

**Test naming pattern** (analog: `Test_Missing_Header_Generates_32CharHex_On2xx`, `Test_FK_Violation_23503_Maps_To_422_WithColumnInDetail`):

```
Test_<Subject>_<Behavior>_<Expected>
```

Phase 5 examples per RESEARCH §"Verification strategy":
- `Test_LogRecord_Has_CorrelationId_And_ServiceResource` (SC#1)
- `Test_LogRecord_Suppressed_When_LogLevel_Filter_Set_To_Warning` (SC#2)
- `Test_HealthLive_Returns_200_Always_NoDbCheck` (SC#3)
- `Test_HealthReady_Returns_503_When_Postgres_Down` (SC#3)
- `Test_HealthStartup_Returns_503_Before_GateFlipped_200_After` (SC#3)
- `Test_HealthEndpoints_Absent_From_OTLP_Metrics` (SC#4 metrics half)
- `Test_HealthEndpoints_Absent_From_OTLP_Logs` (SC#4 logs half)
- `Test_NpgsqlChildSpan_Has_DbStatement_Without_ParameterValues` (SC#5 — T-05-PII regression)
- `Test_RuntimeMetric_ProcessRuntimeDotnet_Gc_Collections_Exported` (D-16)

**Divergence vs analogs:**
- `CorrelationIdTests` does NOT use `IClassFixture` because it doesn't need persistent state; Phase 5 tests DO because of the `.otel-out/` truncate/dispose discipline.
- Phase 4 tests construct `using var factory = new WebAppFactory();` per test; Phase 5 tests use `_fixture.CreateClient()` because the fixture IS the WebApplicationFactory (xUnit disposes the fixture once per class, after all tests run).

---

## Shared Patterns

### Authentication / Authorization

NOT APPLICABLE — Phase 5 introduces no auth. CONTEXT line 9-23 + Phase 1-4 invariants explicitly defer authN/authZ to post-v1. Health probes are intentionally unauthenticated (HEALTH-01 / HEALTH-02 / HEALTH-03 — K8s probes hit them anonymously).

### File header / doc-comment convention

**Source:** Every Phase 1-4 `.cs` file under `src/BaseApi.Core/` follows this shape. Best exemplar: `src/BaseApi.Core/Middleware/CorrelationIdMiddleware.cs` lines 1-48 (long-form `<summary>` + `<para>` blocks tagged with decision IDs).

**Apply to:** All NEW `src/BaseApi.Core/Health/*.cs` files (`IStartupGate.cs`, `StartupHealthCheck.cs`, `StartupCompletionService.cs`) AND `tests/BaseApi.Tests/Observability/*.cs` test files.

```csharp
namespace BaseApi.Core.Health;

/// <summary>
/// [One-sentence purpose. Subject + verb + key consumer.]
///
/// <para>
/// <b>[Decision-ID-tagged behavioral note]:</b> [explanation, ideally
/// referencing the originating decision and any pitfall it mitigates.]
/// </para>
///
/// <para>
/// <b>[Cross-phase contract or invariant]:</b> [what later phases will/won't do.]
/// </para>
/// </summary>
public {sealed | abstract | static} class Xxx
```

### File-scoped namespace + outside-namespace usings

**Source:** `.editorconfig` lines 76 (`csharp_style_namespace_declarations = file_scoped:warning`) + 119 (`csharp_using_directive_placement = outside_namespace:warning`). Every Phase 1-4 file conforms (e.g., `NotFoundExceptionHandler.cs` lines 1-6).

**Apply to:** All NEW `.cs` files (Core + tests).

```csharp
using System.X;
using ProjectName.Y;     // alphabetical within block

namespace BaseApi.Core.Health;   // file-scoped, semicolon-terminated

public sealed class Xxx { /* ... */ }
```

### `sealed` + `internal sealed` access modifier convention

**Source:** Every `.cs` file in `src/BaseApi.Core/` uses `sealed` on the concrete class. Visibility split:
- `public sealed` — type crosses assembly boundary (resolved via DI from `BaseApi.Service.Program.cs` by concrete type OR is part of the contract surface). Examples: `NotFoundExceptionHandler`, `FallbackExceptionHandler`, `CorrelationIdMiddleware`, `AuditInterceptor`, `NotFoundException`.
- `internal sealed` — type used only within `BaseApi.Core` (and any future internals-visible test project). No current Core type uses `internal sealed` — Phase 4 chose `public sealed` for all handlers + middleware + interceptors.

**Apply to (CONTEXT D-01/D-02 + accessibility reconciliation):** Phase 5's `StartupGate`, `StartupHealthCheck`, `StartupCompletionService` are described as `internal sealed` in CONTEXT D-01/D-02. **CAVEAT (covered above):** `AddCheck<T>()` and `AddHostedService<T>()` resolve `T` from the registering assembly; both require `T` to be accessible. Recommend planner override CONTEXT D-01/D-02 wording and use `public sealed` for consistency with all other Core sealed types. The `IStartupGate` interface remains `public` either way.

### DI registration style

**Source:** Phase 4 `Program.cs` lines 23, 30, 46-49, 51. Consistent pattern: `builder.Services.AddX<T>();` with one statement per registration, blank-line groupings, comment per group naming the decision ID.

**Apply to:** Phase 5 OTel + Health blocks in `Program.cs` — see "Insertion (A)" above.

```csharp
// ============================================================================
// Phase 5: <Block name> (CONTEXT.md D-NN)
// ============================================================================
builder.Services.AddSingleton<IInterface, Impl>();
builder.Services.AddXxxChain()
    .AddSubItem(...)
    .AddSubItem(...);
```

### TreatWarningsAsErrors / zero-warning build

**Source:** `Directory.Build.props` line 33 — `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` (Phase 1 D-02).

**Apply to:** All Phase 5 code. Build MUST pass Release AND Debug with 0/0 warnings (Phase 3 W-02 convention reaffirmed by Phase 4 04-01 SUMMARY per CONTEXT line 261). Plan 05-01 Task N: `dotnet build -c Release` + `dotnet build -c Debug`, no `#pragma warning disable`.

### CPM contract (zero `Version=` on `<PackageReference>`)

**Source:** `Directory.Packages.props` lines 5-14 (D-05/D-06) + `BaseApi.Core.csproj` lines 38-55 (verified no `Version=` attrs).

**Apply to:** `BaseApi.Core.csproj` new `<PackageReference>` block (above). Verification grep: `grep -n 'Version=' src/BaseApi.Core/BaseApi.Core.csproj` returns 0 matches on `PackageReference` lines after Plan 05-01.

### xUnit invariant `var ct = TestContext.Current.CancellationToken;`

**Source:** `tests/BaseApi.Tests/Middleware/CorrelationIdTests.cs` — every `[Fact]` opens with this (lines 17, 32, 46, 60, 81, 101). xUnit v3 3.2.2 raises xUnit1051 if async tests don't propagate a cancellation token (Phase 3 PATTERNS.md per CONTEXT line 373).

**Apply to:** Every `[Fact]` in Phase 5 test classes (`LogExportTests`, `LogLevelFilterTests`, `MetricsExportTests`, `HealthEndpointsTests`, `TraceExportTests`).

```csharp
[Fact]
public async Task Test_Xxx()
{
    var ct = TestContext.Current.CancellationToken;  // first line of every async [Fact]
    using var client = _fixture.CreateClient();

    var response = await client.GetAsync("/...", ct);  // thread ct through every awaitable
    // ...
}
```

### IAsyncLifetime cleanup discipline (Phase 3 D-15 lift)

**Source:** `tests/BaseApi.Tests/Persistence/PostgresFixture.cs` lines 30-55 + `tests/BaseApi.Tests/Middleware/PostgresFixture.cs` lines 39-74 (Phase 4 lift). Pattern: create resource on `InitializeAsync`, dispose on `DisposeAsync`, BEFORE/AFTER snapshot byte-identical (Phase 3 D-15).

**Apply to:** `OtelCollectorFixture.cs` — see Pattern Assignment above. The discipline gives Plan 05-02 SUMMARY a clean BEFORE/AFTER (Postgres `\l` snapshot + `tests/.otel-out/` directory listing). After `dotnet test` completes, `tests/.otel-out/telemetry.jsonl` MUST NOT exist; the directory itself MAY remain (gitignored).

### Real backends for fact tests (Phase 3 D-15 / Phase 4 verification)

**Source:** Every fact test in `tests/BaseApi.Tests/Middleware/` uses a real Postgres container (Phase 2 `compose.yaml` postgres at localhost:5433) via PostgresFixture; `WebAppFactory` spins up a real Kestrel via WebApplicationFactory<Program>.

**Apply to:** Phase 5 Plan 05-02 — uses BOTH (a) the new `otel-collector` container (via the augmented `compose.yaml`) AND (b) the existing Postgres container. No in-process ActivityListener / MeterListener mocking; the file exporter is the truth.

### Commit message convention

**Source:** Phase 1-4 SUMMARY files (verified via Glob — `01-01-SUMMARY.md` ... `04-02-SUMMARY.md`). Convention per CONTEXT line 371: `docs(05-NN): ...` for SUMMARY commits, `fix(05-NN): ...` for fix-forwards.

**Apply to:** Plan 05-01 and 05-02 SUMMARY commits — `docs(05-01): observability + health probes wired` / `docs(05-02): verification battery green`.

---

## No Analog Found

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| `compose/otel-collector-config.yaml` | infra-as-code | streaming | Only existing `.yaml` is `compose.yaml` itself; no Collector config has been written. Use RESEARCH §"otel-collector docker-compose service" + CONTEXT D-10 literal shape. Verify via `docker compose config` + the container's `health_check` extension on port 13133. |

(All other files have at least a role-match analog in the codebase.)

---

## Key Patterns Identified

1. **`public sealed class Xxx : IInterface` + ctor-injected dep** — every Core type written in Phase 3-4 uses this shape (handlers, middleware, interceptor). Phase 5's `StartupHealthCheck` follows it with C# 12 primary-constructor syntax (verbatim CONTEXT D-02).
2. **Long-form XML doc-comment header tagged with decision IDs** — the convention is enforced socially (not by analyzer); every Core file documents which CONTEXT decision it implements. Phase 5 health files must do likewise (CONTEXT D-01/D-02/D-13 references).
3. **Program.cs additive composition** — Phase 4 left `Program.cs` ~72 lines; Phase 5 inserts ~30 lines without reordering anything. The `public partial class Program { }` marker (line 72) is load-bearing — multiple test fixtures resolve `WebApplicationFactory<Program>` against it.
4. **CPM additive pattern** — `Directory.Packages.props` adds new `<PackageVersion>` lines in their thematic block; `Directory.Packages.props` is the SINGLE source of versions; consumer csprojs add `<PackageReference Include="…" />` with zero `Version=` attrs. Phase 5 adds 3 pins + 3 references.
5. **PostgresFixture truncate-on-init / cleanup-on-dispose IAsyncLifetime** — Phase 5's `OtelCollectorFixture` mirrors this for `tests/.otel-out/telemetry.jsonl`. Cleanup discipline produces a BEFORE/AFTER byte-identical SUMMARY (zero leaks).
6. **WebApplicationFactory<Program> + ConfigureTestServices + assembly part discovery** — `WebAppFactory.cs` registers the test assembly's `TestController` via `AddApplicationPart`. Phase 5's fixture reuses the pattern to drive HTTP requests that emit telemetry.

---

## Metadata

**Analog search scope:** `src/BaseApi.Core/**/*.cs`, `src/BaseApi.Service/**/*.cs`, `tests/BaseApi.Tests/**/*.cs`, `*.csproj`, `Directory.{Build,Packages}.props`, `compose.yaml`, `.editorconfig`, `.gitignore`, `appsettings*.json`.
**Files scanned (via Glob/Read):** 27 files actually read (header-level via Read; full content for the high-leverage analogs). Three further candidate analogs (`AuditInterceptorTests.cs`, `SchemaTests.cs`, `XminConcurrencyTokenTests.cs`) discovered via Glob but not read — early-stop justified per gsd-pattern-mapper "Stop at 3–5 analogs" rule; the four analogs read (`PostgresFixture`, `WebAppFactory`, `CorrelationIdTests`, `SqlStateMappingTests`) cover every shape Phase 5 needs.
**Phase summary files requested but missing:** `.planning/phases/01-repository-scaffold/01-SUMMARY.md`, `.planning/phases/03-ef-core-persistence-base/03-SUMMARY.md`, `.planning/phases/04-cross-cutting-middleware-error-handling/04-SUMMARY.md` (only per-plan SUMMARYs exist, e.g., `01-01-SUMMARY.md`, `04-02-SUMMARY.md`). The phase-level invariants the prompt cited (Program.cs Logging pipeline, IncludeScopes wiring, AddNpgSql conn string convention, csproj/CPM repo conventions) are already documented inline in CONTEXT D-08/D-13/D-14 and verified directly against the source files read above.
**`./CLAUDE.md`:** does not exist (also confirmed in RESEARCH line 102: "No `./CLAUDE.md` file exists at repo root — verified by Glob").
**Pattern extraction date:** 2026-05-27.

## PATTERN MAPPING COMPLETE
