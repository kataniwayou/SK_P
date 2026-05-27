# Phase 5: Observability + Health Probes - Context

**Gathered:** 2026-05-27
**Status:** Ready for planning

<domain>
## Phase Boundary

Wire OpenTelemetry (logs + metrics + traces) and three K8s-style health probes into `BaseApi.Service` so the service is observable end-to-end and orchestrators can target each probe distinctly. Specifically:

1. **OTel logs** via MEL bridge (`builder.Logging.AddOpenTelemetry(...)`, NOT `services.AddOpenTelemetry().WithLogging()` — Pitfall 8). Correlation ID propagates via `IncludeScopes = true` from the Phase 4 `BeginScope("CorrelationId")` key.
2. **OTel metrics** via `services.AddOpenTelemetry().WithMetrics().AddAspNetCoreInstrumentation().AddHttpClientInstrumentation()`.
3. **OTel traces** via `WithTracing().AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddNpgsql()` (Npgsql DB spans as children of ASP.NET Core request spans — SC#5).
4. **OTLP exporter** to external Collector — endpoint from `OTEL_EXPORTER_OTLP_ENDPOINT` env var with `OpenTelemetry:Endpoint` appsettings fallback; protocol gRPC by default (OBSERV-04).
5. **Resource attributes** `service.name=sk-api`, `service.version=3.2.0` from appsettings `Service:Name` / `Service:Version` (OBSERV-05).
6. **Filter** — `/health/*` requests excluded from BOTH metrics and logs and traces (OBSERV-08 + HEALTH-05 + Pitfall 10).
7. **Three health endpoints** (HEALTH-01..05):
   - `GET /health/live` — self-check, no DB. Always Healthy as long as the process responds (Pitfall 15).
   - `GET /health/ready` — Postgres reachable AND startup gate flipped.
   - `GET /health/startup` — startup gate flipped (Phase 5 default: true; Phase 8 migration runner will flip it after `Database.MigrateAsync()` completes).
8. **JSON response body** on all 3 endpoints via `AspNetCore.HealthChecks.UI.Client.UIResponseWriter.WriteHealthCheckUIResponse` — per-check status + duration in body.

Phase 5 lands `Program.cs` registrations + DI wiring + the `IStartupGate`/`StartupHealthCheck`/`StartupCompletionService` types in `BaseApi.Core` + the `otel-collector` service in `compose.yaml` + verification battery via `WebApplicationFactory<Program>` against real Postgres AND a real OTel Collector (file exporter to host-mounted dir).

**Out of this phase (cross-phase to-do tracking):**
- Migration runner (`Database.MigrateAsync()` + `IStartupGate.MarkReady()` call) — Phase 8. Phase 5 ships the `IStartupGate` contract with `IsReady = true` default; Phase 8 changes the default to false and adds `MigrationRunner : IHostedService` that flips it.
- `AddBaseApi(...)` / `UseBaseApi(...)` extensions — Phase 7. Phase 5 wires directly in `Program.cs` (mirrors Phase 4 D-01 pattern); Phase 7 refactors without behavior change.
- Concrete `AppDbContext`, controllers, migrations — Phase 7/8. Phase 5's traces are end-to-end the moment Phase 7 adds the first controller + Phase 8 adds the first migration.
- Production OTel Collector deployment (cloud-side) — out of v1. Phase 5 ships the local `otel-collector` docker-compose service; ops chooses where the Collector forwards (Jaeger, Tempo, Datadog, etc.).

</domain>

<decisions>
## Implementation Decisions

### Startup probe gate

- **D-01:** Phase 5 ships `BaseApi.Core/Health/IStartupGate.cs`:
  ```csharp
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
  Registered Singleton: `services.AddSingleton<IStartupGate, StartupGate>();`. Default initial state in Phase 5: **`MarkReady()` is called immediately after build (no migration runner yet) so the probe is Healthy by default**. Phase 8 will (a) remove the immediate `MarkReady()` call, (b) register a `MigrationRunner : IHostedService` BEFORE other IHostedServices, and (c) the runner calls `_gate.MarkReady()` after `Database.MigrateAsync()` completes. Clean Phase 5 ↔ Phase 8 contract — Phase 5's contract is correct; Phase 8 just adds the gate flip and removes the default.

- **D-02:** `BaseApi.Core/Health/StartupHealthCheck.cs` implements `IHealthCheck`:
  ```csharp
  internal sealed class StartupHealthCheck(IStartupGate gate) : IHealthCheck
  {
      public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
          => Task.FromResult(gate.IsReady
              ? HealthCheckResult.Healthy("Startup complete")
              : HealthCheckResult.Unhealthy("Startup not complete (migrations pending)"));
  }
  ```
  Registered with tag `"startup"` so it's filterable from the `/health/ready` aggregate query. NpgSql check (HEALTH-04) gets tag `"ready"`; the implicit `"self"` check gets tag `"live"`.

- **D-03:** Health endpoint registration in `Program.cs`:
  ```csharp
  services.AddHealthChecks()
      .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
      .AddCheck<StartupHealthCheck>("startup", tags: new[] { "startup", "ready" })
      .AddNpgSql(connStr, tags: new[] { "ready" });

  app.MapHealthChecks("/health/live",    new HealthCheckOptions { Predicate = c => c.Tags.Contains("live"),    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse });
  app.MapHealthChecks("/health/ready",   new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready"),   ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse });
  app.MapHealthChecks("/health/startup", new HealthCheckOptions { Predicate = c => c.Tags.Contains("startup"), ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse });
  ```
  `/health/startup` Predicate matches only "startup" tag; `/health/ready` matches "ready" (startup + npgsql together). `/health/live` is intentionally process-only — does NOT include "startup" or "ready" tags (Pitfall 15: liveness must not check DB).

### Telemetry sampling + PII safety

- **D-04:** Tracing sampler = `AlwaysOnSampler` (100% sample rate). No configurable knob in v1. PROJECT.md doesn't specify load expectations; AlwaysOn matches the dev-first posture. If production volume becomes a concern, switching to `TraceIdRatioBasedSampler` is a one-line change behind an `OpenTelemetry:TraceSampleRatio` config knob — deferred until needed.

- **D-05:** Npgsql tracing instrumentation runs with **parameter values DISABLED**:
  ```csharp
  .WithTracing(t => t
      .AddNpgsql(opts =>
      {
          // SECURITY: never capture parameter values — workflow data is the product surface
          // (workflow names, schema definitions, assignment targets may contain sensitive
          // or business-confidential data). The SQL template is captured; values are not.
          opts.EnableEntityFrameworkCoreInstrumentation = false; // explicit; default is false anyway
      }))
  ```
  Span shows the SQL template (e.g., `INSERT INTO workflows (name, ...) VALUES ($1, ...)`) — bound parameter values are NOT exported to the Collector. T-05-PII threat mitigation. If a future phase wants richer DB telemetry for a specific entity, that's a deliberate decision documented separately.

- **D-06:** OTel logger options exactly per OBSERV-07:
  ```csharp
  builder.Logging.AddOpenTelemetry(o =>
  {
      o.IncludeFormattedMessage = true;
      o.IncludeScopes           = true;   // <-- propagates Phase 4 "CorrelationId" scope key to log attributes
      o.ParseStateValues        = true;
      o.SetResourceBuilder(ResourceBuilder.CreateDefault()
          .AddService(serviceName: cfg["Service:Name"]!, serviceVersion: cfg["Service:Version"]!));
      o.AddOtlpExporter();   // env var OTEL_EXPORTER_OTLP_ENDPOINT honored, fallback to OpenTelemetry:Endpoint
  });
  ```

### Health probe response format

- **D-07:** All 3 probes use JSON body via `AspNetCore.HealthChecks.UI.Client.UIResponseWriter.WriteHealthCheckUIResponse`. Sample response from `/health/ready`:
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
  Adds one new pin: `<PackageVersion Include="AspNetCore.HealthChecks.UI.Client" Version="9.0.0" />` (matches existing AspNetCore.HealthChecks.NpgSql 9.0.0 — same publisher/version line). Added to `BaseApi.Core.csproj` (PackageReference, no Version=, CPM contract). Devs/ops can curl any probe locally and see which sub-check failed.

### Pipeline + Program.cs wiring

- **D-08:** Phase 5 inserts OTel + health registrations into the EXISTING Phase 4 `Program.cs` (D-01 pipeline order preserved). Insertion targets:
  ```csharp
  // ===== existing Phase 4 pre-build registrations =====
  builder.Services.AddHttpContextAccessor();
  builder.Services.AddProblemDetails(... customizer ...);
  builder.Services.AddExceptionHandler<NotFoundExceptionHandler>();
  builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
  builder.Services.AddExceptionHandler<DbUpdateExceptionHandler>();
  builder.Services.AddExceptionHandler<FallbackExceptionHandler>();

  // ===== Phase 5: OTel logs (MEL bridge — Pitfall 8) =====
  builder.Logging.AddOpenTelemetry(o => { /* D-06 */ });

  // ===== Phase 5: OTel metrics + traces =====
  builder.Services.AddOpenTelemetry()
      .ConfigureResource(r => r.AddService(serviceName: cfg["Service:Name"]!, serviceVersion: cfg["Service:Version"]!))
      .WithMetrics(m => m
          .AddAspNetCoreInstrumentation(opts => opts.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))  // OBSERV-08 + HEALTH-05
          .AddHttpClientInstrumentation()
          .AddRuntimeInstrumentation()   // D-16: process.runtime.dotnet.* (GC, threadpool, exceptions)
          .AddOtlpExporter())
      .WithTracing(t => t
          .SetSampler(new AlwaysOnSampler())   // D-04
          .AddAspNetCoreInstrumentation(opts => opts.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))  // exclude health from traces too
          .AddHttpClientInstrumentation()
          .AddNpgsql(opts => { /* D-05 — params disabled */ })
          .AddOtlpExporter());

  // ===== Phase 5: Health gate + checks =====
  builder.Services.AddSingleton<IStartupGate, StartupGate>();
  builder.Services.AddHealthChecks()
      .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
      .AddCheck<StartupHealthCheck>("startup", tags: new[] { "startup", "ready" })
      .AddNpgSql(cfg.GetConnectionString("Postgres")!, tags: new[] { "ready" });

  builder.Services.AddControllers();
  var app = builder.Build();

  // Phase 5: Mark gate ready immediately after build (Phase 8 will replace this with MigrationRunner)
  app.Services.GetRequiredService<IStartupGate>().MarkReady();

  // ===== existing Phase 4 pipeline =====
  app.UseExceptionHandler();
  app.UseMiddleware<CorrelationIdMiddleware>();
  app.UseRouting();

  // ===== Phase 5: health endpoints (before MapControllers — health is plumbing, not business) =====
  app.MapHealthChecks("/health/live",    new HealthCheckOptions { Predicate = c => c.Tags.Contains("live"),    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse });
  app.MapHealthChecks("/health/ready",   new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready"),   ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse });
  app.MapHealthChecks("/health/startup", new HealthCheckOptions { Predicate = c => c.Tags.Contains("startup"), ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse });

  app.MapControllers();
  app.Run();
  public partial class Program { }
  ```

- **D-09:** Excluding `/health/*` from logs uses the MEL filter API rather than a custom log processor (Pitfall 9 / OBSERV-08). Add to `appsettings.json`:
  ```json
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
    }
  }
  ```
  AspNetCoreInstrumentation's `Filter` callback (D-08) handles metric+trace exclusion. For logs, the per-request `Microsoft.AspNetCore.Hosting.Diagnostics` logger emits at `Information` for normal requests; setting `Microsoft.AspNetCore` to `Warning` already filters request-start/request-finish logs from `/health/*` and all other endpoints alike. **Alternative considered:** a custom `ITelemetryProcessor`-style filter that drops only `/health/*` logs while keeping `Information` for other endpoints. Deferred — current filter is coarser but simpler; if ops wants per-request logs for non-health paths in dev, a follow-up phase can add path-scoped filtering.

### otel-collector docker-compose service

- **D-10:** `compose.yaml` gains a new `otel-collector` service:
  ```yaml
  otel-collector:
    image: otel/opentelemetry-collector-contrib:0.95.0
    container_name: sk-otel-collector
    command: ["--config=/etc/otel-collector-config.yaml"]
    volumes:
      - ./compose/otel-collector-config.yaml:/etc/otel-collector-config.yaml:ro
      - ./tests/.otel-out:/var/otel-out
    ports:
      - "4317:4317"   # OTLP gRPC
      - "4318:4318"   # OTLP HTTP (optional; lets curl-from-laptop debugging work)
    healthcheck:
      test: ["CMD", "wget", "-qO-", "http://localhost:13133/"]
      interval: 5s
      timeout: 3s
      retries: 5
  ```
  Config file `compose/otel-collector-config.yaml`:
  ```yaml
  receivers:
    otlp:
      protocols:
        grpc:  { endpoint: 0.0.0.0:4317 }
        http:  { endpoint: 0.0.0.0:4318 }

  exporters:
    file:
      path: /var/otel-out/telemetry.jsonl   # one JSON-lines file (logs + metrics + traces interleaved)
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
  `tests/.otel-out/` is gitignored (added to `.gitignore` in Plan 05-01 alongside the compose change). Tests read `tests/.otel-out/telemetry.jsonl` and assert shape (D-11).

- **D-11:** Verification fixture `OtelCollectorFixture` (lifts Phase 3 `PostgresFixture` pattern):
  - `IAsyncLifetime.InitializeAsync`: truncates `tests/.otel-out/telemetry.jsonl` to zero bytes (so each test class starts with a clean slate).
  - `IAsyncLifetime.DisposeAsync`: deletes the file (mirrors Phase 3 D-15 "BEFORE/AFTER byte-identical" discipline — verification reports zero leak by asserting the file is gone post-test).
  - Helper `ReadExportedLogs()` / `ReadExportedMetrics()` / `ReadExportedTraces()` parses the JSON-lines file and returns typed records. Tests assert against these.
  - **Wait for export discipline:** OTel batches by default with 5-second timeout. Tests must either wait for batch flush OR force-flush via `services.AddOpenTelemetry().WithMetrics(m => m.AddOtlpExporter(o => o.ExportProcessorType = ExportProcessorType.Simple))` in `ConfigureTestServices`. Use `ExportProcessorType.Simple` in tests (synchronous export — slower in prod but deterministic in tests).

### Verification strategy (concrete plan structure)

- **D-12:** Plans split per ROADMAP "Parallelizable: yes" hint. Three plans:
  - **05-01-PLAN.md** (autonomous: true) — OTel + Health build + wire:
    - Confirm/add PackageVersions in Directory.Packages.props: OpenTelemetry 1.15.3 (existing), OpenTelemetry.Exporter.OpenTelemetryProtocol 1.15.3 (existing), OpenTelemetry.Instrumentation.AspNetCore 1.15.0 (existing), OpenTelemetry.Instrumentation.Http 1.15.0 (existing), OpenTelemetry.Extensions.Hosting 1.15.3 (existing), **OpenTelemetry.Instrumentation.Runtime 1.15.0** (new pin — D-16), **Npgsql.OpenTelemetry 8.0.4** (new pin — matches Npgsql 8.x), AspNetCore.HealthChecks.NpgSql 9.0.0 (existing), **AspNetCore.HealthChecks.UI.Client 9.0.0** (new pin)
    - Add `<PackageReference>` (no Version=) for all of the above to `src/BaseApi.Core/BaseApi.Core.csproj`
    - Create `src/BaseApi.Core/Health/IStartupGate.cs` (D-01)
    - Create `src/BaseApi.Core/Health/StartupHealthCheck.cs` (D-02)
    - Edit `src/BaseApi.Service/Program.cs` (D-08 — inserts go between Phase 4 registrations and the `Build()` call; pipeline mods go between `UseRouting()` and `MapControllers()`)
    - Edit `compose.yaml` + create `compose/otel-collector-config.yaml` (D-10)
    - Edit `.gitignore` — add `tests/.otel-out/`
    - Build green Release + Debug 0/0 warnings under TreatWarningsAsErrors=true (Phase 1 D-02)
  - **05-02-PLAN.md** (autonomous: false, checkpoint per Phase 3 D-18 / Phase 4 D-14) — Verification battery:
    - Create `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` (D-11)
    - Create ~5 fact-test files under `tests/BaseApi.Tests/Observability/`:
      - `LogExportTests.cs` — SC#1 (correlationId attribute + service.name + service.version on a sample LogInformation call)
      - `LogLevelFilterTests.cs` — SC#2 (set `Logging:LogLevel:Default` to `Warning` via in-memory IConfiguration override; assert no `Information` logs in `.otel-out/telemetry.jsonl`)
      - `MetricsExportTests.cs` — SC#4 metrics half (HTTP server metrics present for app endpoints, ABSENT for `/health/*`)
      - `HealthEndpointsTests.cs` — SC#3 (live always 200; ready 503 when Postgres down — uses `PostgresFixture` to stop/start container OR uses a connection string that points to a non-routable port; startup probe behavior with `IStartupGate.MarkReady()` flipped in-test); SC#4 logs half (no `/health/*` requests appear in `.otel-out/telemetry.jsonl`)
      - `TraceExportTests.cs` — SC#5 (POST to a test endpoint that issues a Postgres query — assert exported trace JSON contains an ASP.NET Core request span AND a Npgsql child span; assert NO parameter values present in span attributes)
    - Run `dotnet test`, BEFORE/AFTER `psql \l` snapshots byte-identical (Phase 3 D-15) AND `tests/.otel-out/` empty post-test (D-11 dispose discipline)
    - Single SUMMARY commit `docs(05-02): ...` with GREEN/RED grid for all 5 ROADMAP SCs + T-05-PII regression coverage
  - **05-03-PLAN.md** (NOT planned in this CONTEXT — Phase 5 SCs are achievable in 05-01 + 05-02 alone). ROADMAP marks Phase 5 as parallelizable but Plan 05-01's `Program.cs` wiring touches everything in one place; splitting OTel and health into separate plans would create merge conflicts. Single build plan + single verify plan matches the Phase 3 / Phase 4 cadence the team has validated.

### Cross-phase shape (defensive landings)

- **D-13:** Phase 5 does NOT introduce migration runner — that's Phase 8. `IStartupGate.IsReady = true` by default in Phase 5 means `/health/startup` returns Healthy on every Phase 5 boot. Phase 8 will (a) register `MigrationRunner : IHostedService` that calls `db.Database.MigrateAsync()` then `_gate.MarkReady()`, (b) change `IStartupGate` default state to false (or remove the `MarkReady()` call from `Program.cs` in Plan 05-01). Phase 8 owns one csproj edit to remove the line `app.Services.GetRequiredService<IStartupGate>().MarkReady();` from Program.cs after the migration runner is registered.

- **D-14:** Phase 5 does NOT change `appsettings.Development.json`. The Phase 4-vintage OTel endpoint at `http://localhost:4317` already routes to the new `otel-collector` compose service (port mapping `4317:4317`). The Phase 2 Postgres on `localhost:5433` is unaffected.

- **D-15:** Phase 5 does NOT add any new ActivitySource. ROADMAP / FEATURES mention a custom `ActivitySource("sk-api")` for future feature use; Phase 5 ships only auto-instrumentation (AspNetCore + HttpClient + Npgsql). The custom source can be added by any future phase that needs custom span boundaries — Phase 5 doesn't pre-empt that.

- **D-16:** Phase 5 ADDS runtime metrics instrumentation (additive to REQUIREMENTS OBSERV-01/-03 — runtime instrumentation not explicitly listed but unanimously high-value for ops). Concretely:
  - Pin `<PackageVersion Include="OpenTelemetry.Instrumentation.Runtime" Version="1.15.0" />` in `Directory.Packages.props`
  - Add `<PackageReference Include="OpenTelemetry.Instrumentation.Runtime" />` (no Version=, CPM contract) to `src/BaseApi.Core/BaseApi.Core.csproj`
  - Add `.AddRuntimeInstrumentation()` to the `.WithMetrics(...)` chain in Program.cs (D-08), AFTER `.AddHttpClientInstrumentation()` and BEFORE `.AddOtlpExporter()`:
    ```csharp
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation(opts => opts.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()   // D-16: process.runtime.dotnet.* metrics — GC, threadpool, exceptions
        .AddOtlpExporter())
    ```
  
  Runtime metrics exported (sample, not exhaustive — see https://github.com/open-telemetry/opentelemetry-dotnet-contrib/tree/main/src/OpenTelemetry.Instrumentation.Runtime):
  - `process.runtime.dotnet.gc.collections.count` (gen0 / gen1 / gen2)
  - `process.runtime.dotnet.gc.heap.size`
  - `process.runtime.dotnet.gc.pause.time`
  - `process.runtime.dotnet.thread_pool.threads.count`
  - `process.runtime.dotnet.thread_pool.queue.length`
  - `process.runtime.dotnet.exceptions.count`
  - `process.runtime.dotnet.monitor.lock_contention.count`
  
  These are process-level (not per-request), so Pitfall 10's `/health/*` filter (the `AspNetCoreInstrumentation.Filter` callback in D-08) does NOT apply — runtime metrics fire regardless of any HTTP path. No additional filter logic needed.
  
  Verification: Plan 05-02's `MetricsExportTests.cs` adds one assertion that the exported metric stream contains at least one `process.runtime.dotnet.*` metric after letting the process run for a few seconds (e.g., after issuing a few HTTP requests through `WebApplicationFactory<Program>`). Force-flush via `ExportProcessorType.Simple` keeps the assertion deterministic (D-11).

### Claude's Discretion (small choices deferred to research / planning)

- Exact NpgsqlInstrumentation Options API surface for "names only, no values" — research validates the actual property name (one of `IncludeFormattedMessage`, `EnableSensitiveDataLogging`, `IncludeParameterValues`). RESEARCH will confirm against Npgsql.OpenTelemetry 8.0.4 docs.
- `OtelCollectorFixture` location: `tests/BaseApi.Tests/Observability/` mirrors the SUT layout (`BaseApi.Service` is the OTel-emitting subject). Planner may relocate to `tests/BaseApi.Tests/Fixtures/` if the team prefers grouping all fixtures together.
- Health endpoint MapHealthChecks placement in Program.cs — current D-08 puts them after `UseRouting()` but before `MapControllers()`. Placement before/after MapControllers is functionally equivalent; choosing "before MapControllers" matches the "plumbing first, business last" mental model.
- Test endpoint mechanism for `TraceExportTests` — could use a real controller (when Phase 7 lands its first one) or a Minimal API stub registered via `ConfigureTestServices`. Phase 4 uses the assembly-part controller pattern; Phase 5 can do the same OR a Minimal API stub directly in `OtelCollectorFixture.ConfigureWebHost`. Planner picks the cleaner option.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Project-level locks
- `.planning/PROJECT.md` — "Tech stack — observability" section, "Locked decisions" table (OTel exporter target = external Collector via OTLP; `Logging:LogLevel` single source of truth)
- `.planning/REQUIREMENTS.md` § Observability (OBSERV-01..08, OBSERV-12), § Health Probes (HEALTH-01..05), § INFRA-04 (appsettings sections — `Service:Name=sk-api`, `Service:Version=3.2.0`)
- `.planning/ROADMAP.md` § Phase 5 — goal, dependencies (Phase 4), success criteria SC#1-5, "Parallelizable: yes" note

### Phase 4 carry-forward (correlation propagation)
- `.planning/phases/04-cross-cutting-middleware-error-handling/04-CONTEXT.md` D-03 — `CorrelationIdMiddleware.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = corrId })` uses the literal PascalCase key `"CorrelationId"`. Phase 5's `IncludeScopes = true` (D-06 / OBSERV-07) surfaces this key as a log attribute on every OTel-exported log record — SC#1 verification depends on this.
- `.planning/phases/04-cross-cutting-middleware-error-handling/04-01-SUMMARY.md` — Npgsql pinned at 8.0.9 (NOT 8.0.10 — Npgsql skipped that version; runtime binary compat with EFCore.PostgreSQL 8.0.10). Phase 5's `Npgsql.OpenTelemetry` pin must match this version line (Npgsql 8.0.4 OTel package is the one paired with Npgsql 8.x).
- `.planning/phases/04-cross-cutting-middleware-error-handling/04-VERIFICATION.md` — Phase 5 inserts into the SAME Program.cs that Phase 4 wired; pipeline order from Phase 4 D-01 is preserved verbatim.

### Phase 3 carry-forward (test infrastructure)
- `.planning/phases/03-ef-core-persistence-base/03-CONTEXT.md` D-15 — Per-class throwaway Postgres DB pattern. Phase 5 `OtelCollectorFixture` lifts the same `IAsyncLifetime` cadence + cleanup discipline applied to `tests/.otel-out/` files.
- `.planning/phases/03-ef-core-persistence-base/03-CONTEXT.md` D-18 — `autonomous: false` checkpoint convention for verification plans (Phase 1 01-03, Phase 2 02-02, Phase 3 03-02, Phase 4 04-02). Phase 5 Plan 05-02 follows.

### Phase 1 + 2 carry-forward (build + container hygiene)
- `.planning/phases/01-repository-scaffold/01-CONTEXT.md` D-02 — `TreatWarningsAsErrors=true` globally; D-05/D-06 — CPM contract (zero `Version=` attributes on `<PackageReference>`); D-10 — `Program.cs` is composition root (Phase 7 will refactor into `AddBaseApi()`/`UseBaseApi()`)
- `.planning/phases/02-postgres-docker-compose/02-CONTEXT.md` D-01 — host port 5433; D-04 — dev credentials (out of scope auth); compose.yaml architecture (Phase 5 inserts `otel-collector` service alongside `postgres`)

### Research baselines
- `.planning/research/STACK.md` — OTel 1.15.x pin alignment (OpenTelemetry 1.15.3, Instrumentation.* 1.15.0, Npgsql.OpenTelemetry 8.0.4 paired to Npgsql 8.x)
- `.planning/research/PITFALLS.md` — Pitfall 8 (MEL bridge, not WithLogging), Pitfall 9 (Logging:LogLevel filter before sink), Pitfall 10 (exclude health from AspNetCore instrumentation), Pitfall 15 (liveness must not check DB)
- `.planning/research/ARCHITECTURE.md` Pattern 1 (Composition Root — Phase 7 will refactor Phase 5's Program.cs wiring into `AddBaseApi()`/`UseBaseApi()` extensions); §"OpenTelemetry export tree" code example (Phase 5 implements this in `Program.cs`, Phase 7 moves it to an extension)
- `.planning/research/FEATURES.md` — locked feature list: OTLP exporter, single Logging:LogLevel source, service resource attrs from config

### External specs (researcher to confirm current shape against)
- **OpenTelemetry .NET docs** — https://opentelemetry.io/docs/languages/dotnet/instrumentation/ (MEL bridge vs WithLogging, instrumentation Filter options, exporter configuration)
- **AspNetCore.HealthChecks.NpgSql 9.0** — https://github.com/Xabaril/AspNetCore.Diagnostics.HealthChecks (NpgSql probe registration, options)
- **AspNetCore.HealthChecks.UI.Client 9.0** — same repo (UIResponseWriter.WriteHealthCheckUIResponse JSON shape)
- **Npgsql.OpenTelemetry 8.0** — https://www.npgsql.org/doc/diagnostics/tracing.html (`AddNpgsql` instrumentation options, parameter capture controls)
- **OpenTelemetry Collector Contrib config** — https://github.com/open-telemetry/opentelemetry-collector-contrib (file exporter, logging exporter, health_check extension)

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`src/BaseApi.Service/Program.cs`** (Phase 4 final state) — Phase 5 inserts ~25 lines of OTel + health registrations between the existing Phase 4 `AddExceptionHandler<>` block and the `AddControllers()` call; adds 3 `MapHealthChecks` lines between `UseRouting()` and `MapControllers()`. The `public partial class Program { }` marker (Phase 1 D-10) is preserved for Phase 4/5 `WebApplicationFactory<Program>` tests.
- **`src/BaseApi.Core` directory layout** — Phase 4 created `Middleware/`, `Exceptions/Handlers/`, `Persistence/Exceptions/`. Phase 5 adds `Health/` (anticipated by Phase 1 D-10 ARCHITECTURE.md tree).
- **`appsettings.json` `OpenTelemetry:Endpoint`** (Phase 1 + 2) — `http://otel-collector:4317` (prod compose hostname) and `http://localhost:4317` (Development overlay). Both route to the Phase 5 `otel-collector` compose service.
- **`appsettings.json` `Service:Name=sk-api`, `Service:Version=3.2.0`** — Phase 1 D-11 / INFRA-04 / OBSERV-05. Phase 5 reads these via `IConfiguration` and emits as OTel resource attributes.
- **`WebApplicationFactory<Program>` infrastructure** — Phase 4 wired `Microsoft.AspNetCore.Mvc.Testing` PackageRef + ProjectReference. Phase 5 reuses for `OtelCollectorFixture`.
- **`compose.yaml`** (Phase 2 D-01..D-13) — `postgres:17-alpine` at `localhost:5433`. Phase 5 adds `otel-collector` at `localhost:4317` + `localhost:4318` alongside.
- **Phase 3 `PostgresFixture` cleanup discipline** (Phase 3 D-15) — Phase 5 `OtelCollectorFixture` lifts the IAsyncLifetime + truncate-on-init + delete-on-dispose pattern applied to `tests/.otel-out/`.

### Established Patterns
- **CPM contract** — `<PackageReference Include="…" />` only; zero `Version=` attributes. New pins go to `Directory.Packages.props` (currently 24 pins after Phase 4: Npgsql at 8.0.9 fix-forward). Phase 5 adds Npgsql.OpenTelemetry 8.0.4 + AspNetCore.HealthChecks.UI.Client 9.0.0.
- **Zero-warning build** — `TreatWarningsAsErrors=true` (Phase 1 D-02). Plan 05-01 builds Release AND Debug per Phase 3 W-02.
- **File-scoped namespaces + outside-namespace usings** (Phase 1 .editorconfig with `:warning` severities). Every new .cs file follows.
- **`autonomous: false` checkpoint plan for verification** (Phase 1 01-03, Phase 2 02-02, Phase 3 03-02, Phase 4 04-02). Plan 05-02 follows.
- **`docs(05-NN): …` for SUMMARY commits; `fix(05-NN): …` for fix-forwards** (Phase 1-4 convention).
- **Real backends for fact tests** (Phase 3 D-15, Phase 4 verification) — Phase 5 spins up real otel-collector container alongside real Postgres.
- **xUnit1051 `var ct = TestContext.Current.CancellationToken;` invariant** (Phase 3 PATTERNS.md) — every async test in Plan 05-02 threads it through every awaitable.

### Integration Points
- **`Program.cs`** (currently ~70 lines, Phase 4 final state) — Phase 5 edits add ~25 lines of registration + 3 `MapHealthChecks` lines. Pipeline order from Phase 4 D-01 is PRESERVED. The `IStartupGate.MarkReady()` call after `Build()` is the temporary "Phase 5 default healthy" hook that Phase 8 will remove when MigrationRunner lands.
- **`tests/BaseApi.Tests/` layout** — already has `Endpoints/`, `Middleware/`, `Persistence/` (Phase 3 + 4). Phase 5 adds `Observability/` (parallel to Persistence layout, matches SUT subject area).
- **`compose.yaml` → `compose/otel-collector-config.yaml`** — new file path. The `compose/` directory is currently empty (only `compose.yaml` at repo root). Plan 05-01 creates it.
- **`.gitignore`** — needs one new line: `tests/.otel-out/` (excludes the file exporter's JSON output dir).

</code_context>

<specifics>
## Specific Ideas

- **JSON body via UIResponseWriter** (D-07) — explicitly chosen so ops can curl any probe locally and see per-check breakdown without parsing the response stream or hitting logs. Critical when the ready probe is failing intermittently and the operator needs to know "is it Postgres or is it the startup gate".
- **Npgsql parameter values DISABLED by default** (D-05) — the workflow domain (Schemas / Processors / Steps / Assignments / Workflows) is user-content-heavy; workflow names, schema definitions, and assignment targets may contain sensitive business data. PROJECT.md says auth/authz is out of scope; PII guard is still mandatory because the Collector backend (and any forwarded backend like Jaeger/Tempo) becomes a queryable surface for that data. Treat parameter values as Tier-2 confidential by default.
- **otel-collector adopts `:contrib` image** (D-10) — `otel/opentelemetry-collector-contrib:0.95.0` includes the file exporter (D-11 verification depends on this) and the wider receiver/exporter set ops may need at runtime. The base `otel/opentelemetry-collector:0.95.0` image lacks the file exporter and would force a verification approach change.
- **AlwaysOn sampler over probability** (D-04) — chosen explicitly because v1 has no production load data. Probability sampling adds a config knob and a debugging tax (some traces missing) without addressing a measured pain. If/when load is observed, the change is a one-line sampler swap behind an appsettings knob — deferred to a follow-up phase, not pre-empted in v1.

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within Phase 5 scope.

The following are Phase-Y items that came up implicitly but already have their own phase home (NOT deferred from Phase 5 — they were never in scope to begin with; surfaced here for cross-phase awareness):
- Migration runner (`Database.MigrateAsync()` + `IStartupGate.MarkReady()` call) — Phase 8. Phase 5 ships `IStartupGate` contract + default `MarkReady()` immediately after build (D-01, D-13).
- `AddBaseApi(...)` / `UseBaseApi(...)` composition root extensions — Phase 7. Phase 5 wires directly in Program.cs (D-08).
- Custom `ActivitySource("sk-api")` for feature-specific spans — any future phase that needs custom span boundaries (D-15).
- Production OTel Collector destination (Jaeger / Tempo / Datadog / etc.) — ops decision, post-v1.
- Sampling tuning (`TraceIdRatioBasedSampler` with `OpenTelemetry:TraceSampleRatio` knob) — deferred until production load observed (D-04).
- Per-path log filtering (more granular than current `Microsoft.AspNetCore: Warning` setting) — deferred until a use case requires `Information` logs for non-health paths in production (D-09).
- Richer DB telemetry (parameter values enabled per-entity, query plan capture, etc.) — deferred; default is names-only (D-05).

</deferred>

---

*Phase: 05-observability-health-probes*
*Context gathered: 2026-05-27*
