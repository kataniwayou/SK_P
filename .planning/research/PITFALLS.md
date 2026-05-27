# Pitfalls Research

**Domain:** .NET 8 Web API modular monolith — EF Core 8 + Npgsql + OpenTelemetry + FluentValidation + Mapperly + Postgres + RFC 7807
**Researched:** 2026-05-26
**Confidence:** HIGH — Most pitfalls verified against official docs, GitHub issues, and recent ecosystem write-ups. A few items (compose-v2 nuances, Cronos/NCrontab edge cases) are MEDIUM where ecosystem patterns are well-known but version-specific behavior may shift.

Pitfalls are organized by subsystem to map cleanly onto roadmap phases. Each pitfall lists the failure mode, root cause, prevention strategy with concrete code/config, detection signal, and target phase. Suggested phase names referenced below:

- **P0 — Repository scaffold & solution structure** (`BaseApi.Core` + `BaseApi.Service` projects)
- **P1 — Postgres + Docker Compose + connection plumbing**
- **P2 — EF Core base infra** (`BaseDbContext`, conventions, audit, migrations on startup)
- **P3 — Cross-cutting middleware** (correlation, problem details, error handling)
- **P4 — Observability** (OTel logs + metrics, health checks)
- **P5 — Validation & mapping base** (FluentValidation + Mapperly wiring + base validators)
- **P6 — Abstract generic controllers + service/repository base + DI extension**
- **P7 — Entity build-out** (Schema → Processor → Step → Assignment → Workflow)
- **P8 — Test stack** (Testcontainers + WebApplicationFactory + Respawn)

---

## Critical Pitfalls

### Pitfall 1: Writing non-UTC DateTime to Postgres `timestamptz` columns

**What goes wrong:**
At runtime, the first INSERT/UPDATE that touches `CreatedAt` or `UpdatedAt` throws `InvalidCastException: Cannot write DateTime with Kind=Unspecified to PostgreSQL type 'timestamp with time zone', only UTC is supported.` Audit columns either save successfully but are wrong, or every write blows up.

**Why it happens:**
Starting in Npgsql 6.0 (and unchanged through Npgsql 8 / EF Core 8 used here), `DateTime` properties map to `timestamptz` by default and Npgsql refuses any `DateTime` whose `Kind` is `Local` or `Unspecified`. Reading back returns `Kind=Utc`. Developers default-construct `DateTime.Now` or hydrate from JSON (which parses to `Kind=Unspecified`) and don't realize the kind matters.

**How to avoid:**
- In `AuditInterceptor`, **always** stamp with `DateTime.UtcNow`, never `DateTime.Now`.
- Decide deliberately whether `BaseEntity.CreatedAt`/`UpdatedAt` are `DateTime` (UTC-only by convention) or `DateTimeOffset` (forced to offset 0). Recommend `DateTime` UTC for simplicity since Npgsql 8 reads `timestamptz` as `Kind=Utc`.
- Do **NOT** flip the legacy switch (`AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true)`) — it papers over the bug and will be removed in a future Npgsql.
- For any DTOs containing dates, document that callers must send UTC ISO-8601 (`...Z`); validate at the validator layer if needed.
- Configure `System.Text.Json` to round-trip with `JsonSerializerOptions.PropertyNamingPolicy` and ensure date deserialization preserves UTC.

**Warning signs:**
- Any string like `DateTime.Now`, `DateTime.Today`, or `new DateTime(...)` (no `DateTimeKind`) appearing in domain code, interceptors, seeders, or migrations.
- Tests that pass on Windows dev box but fail in containers (timezone mismatches).
- Audit columns reading back at wrong hour offset.

**Phase to address:**
**P2 — EF Core base infra.** Encode in `AuditInterceptor` and document the rule in `BaseEntity` XML doc. Add a unit test that round-trips an entity through `AppDbContext` and asserts `Kind == Utc`.

---

### Pitfall 2: `DbContext` lifetime mistakes in DI

**What goes wrong:**
Registering `AppDbContext` as `Singleton` (e.g., to "cache" it) corrupts state across requests, causes random `InvalidOperationException: A second operation was started on this context instance before a previous operation completed`, and leaks change tracker entries forever. Registering as `Transient` leaks connections and breaks unit-of-work semantics.

**Why it happens:**
`AddDbContext<T>()` defaults to `Scoped` for a reason: one context per HTTP request matches EF Core's unit-of-work model. Developers who came from `IDbConnection`/Dapper sometimes set it to `Transient`; developers worried about "startup cost" sometimes set it to `Singleton`.

**How to avoid:**
- Use `services.AddDbContext<AppDbContext>(...)` (scoped by default) — do not override the lifetime.
- For the `AuditInterceptor`, register it as `Singleton` only if it has no scoped dependencies; otherwise use `AddDbContext` overload that takes `IServiceProvider` and resolve scoped services per request.
- Inject `AppDbContext` directly into scoped services (`BaseService<T>` / `Repository<T>`); never store it in a static field, singleton, or background hosted service without `IServiceScopeFactory`.

**Warning signs:**
- "A second operation was started on this context instance" exceptions under load.
- Memory profile shows change-tracker entries growing without bound.
- `IServiceScope` references in singletons (a smell, but sometimes correct — verify).

**Phase to address:**
**P2 — EF Core base infra** (registration) and **P6 — Abstract generic controllers + service/repository base** (consumption).

---

### Pitfall 3: Migrations on startup with multiple service instances — race + restart loop

**What goes wrong:**
Two instances boot simultaneously, both call `dbContext.Database.MigrateAsync()`, the second fails midway (or hits a duplicate constraint), exits non-zero, the orchestrator restarts it, and the service flaps. Even when EF Core's migration advisory lock prevents corruption, the failed instance still terminates and loops.

**Why it happens:**
EF Core takes a Postgres advisory lock around migrations, so corruption is unlikely — but a non-acquiring instance does not gracefully wait by default in some failure modes (see efcore#34439). Beyond that, a *failed* migration (bad SQL, constraint conflict) leaves the service unable to start, with no operator signal beyond a restart loop.

**How to avoid:**
- Run migrations from `Program.cs` in a clearly bracketed block with explicit start/finish log lines so it shows up in OTel logs:
  ```csharp
  using (var scope = app.Services.CreateScope())
  {
      var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      logger.LogInformation("migrations.start");
      await db.Database.MigrateAsync();
      logger.LogInformation("migrations.complete");
  }
  ```
- For v1 (single-instance deployment), document that horizontal scaling requires either (a) a pre-deploy migration job, or (b) wrapping the migration call in a Postgres `pg_advisory_lock(<bigint>)` explicitly so non-winners wait instead of racing.
- **Do not** swallow exceptions during migration — let the process exit so the operator sees the failure; but make sure the exit is logged via OTel before shutdown.
- The startup health probe must report "not ready" until migrations complete (not just "DI built").

**Warning signs:**
- Restart-loop in container logs with no clear error.
- `__EFMigrationsHistory` shows partial rows after a failed deploy.
- Two instances logging "Applying migration X" in the same second.

**Phase to address:**
**P2 — EF Core base infra** (the on-startup runner with logging) and **P4 — Observability** (startup probe gating).

---

### Pitfall 4: `EFCore.NamingConventions` mangles `__EFMigrationsHistory`

**What goes wrong:**
After adding `UseSnakeCaseNamingConvention()`, the first migration succeeds against an empty DB but a second deployment to a DB created without the convention fails because the package snake_cases the `__EFMigrationsHistory` table's columns (`MigrationId` → `migration_id`, `ProductVersion` → `product_version`) — EF can't find its history rows and tries to re-apply migrations.

**Why it happens:**
The package applies snake_case to all schema artifacts, including the history table columns. If migrations were ever generated/applied with the convention off, upgrading is destructive. (efcore/EFCore.NamingConventions#1)

**How to avoid:**
- Add `UseSnakeCaseNamingConvention()` **before the first migration is ever generated** — bake it into `BaseDbContext`'s `OnConfiguring`/`OnModelCreating` from day one.
- Document this in `BaseApi.Core` README: "If you swap convention later, you own a manual ALTER script."
- Avoid PascalCase column names > 55 characters (Postgres truncates at 63; snake_casing pushes you over silently — see efcore/EFCore.NamingConventions#289). Keep `BaseEntity` field names short.
- Never use ASP.NET Identity tables with the snake convention without overriding the Identity DbContext mappings (efcore/EFCore.NamingConventions#300). Not relevant here (auth out of scope), but document it.

**Warning signs:**
- Migrations reapply on every startup.
- Errors like `column "migration_id" does not exist`.

**Phase to address:**
**P2 — EF Core base infra.** Add the snake_case convention and generate the **first** initial migration in the same commit.

---

### Pitfall 5: Many-to-many "skip navigation" misconfiguration on junction tables

**What goes wrong:**
The roadmap demands no nav properties between bounded contexts but does require junction tables (`StepNextSteps`, `WorkflowEntrySteps`, `WorkflowAssignments`). Configuring these with `HasMany().WithMany()` skip navigations creates "shadow" join entities EF can't see in the model. Updates either silently no-op, or EF generates DELETE+INSERT on every save instead of diffing.

**Why it happens:**
EF Core 8's default many-to-many uses a generated join entity. The project explicitly wants **explicit junction tables** with their own configuration (since FK columns are scalar `Guid`s, no nav properties). Mixing these models leads to two different join behaviors in the same DbContext.

**How to avoid:**
- Define each junction explicitly as a non-keyed entity (or compound-keyed) and configure in `OnModelCreating`:
  ```csharp
  modelBuilder.Entity<StepNextSteps>(b =>
  {
      b.ToTable("step_next_steps");
      b.HasKey(x => new { x.StepId, x.NextStepId });
      b.HasOne<StepEntity>().WithMany().HasForeignKey(x => x.StepId)
          .OnDelete(DeleteBehavior.Cascade);
      b.HasOne<StepEntity>().WithMany().HasForeignKey(x => x.NextStepId)
          .OnDelete(DeleteBehavior.Restrict); // avoid multiple cascade paths
  });
  ```
- Use `DeleteBehavior.Restrict` (or `NoAction`) on the second FK to avoid Postgres "multiple cascade paths" errors.
- M2M sync (add/remove) lives in the entity-specific `Service`, not the generic `Repository<T>`.
- Write an explicit unit test for each junction: add, remove, re-add, ensure single row in DB after `SaveChanges`.

**Warning signs:**
- "Introducing FOREIGN KEY constraint ... may cause cycles or multiple cascade paths" at migration time.
- M2M updates issue many DELETE+INSERT statements (visible in SQL log).
- "Cannot insert duplicate key" on junction rows.

**Phase to address:**
**P2 — EF Core base infra** (the cascade pattern) and **P7 — Entity build-out** (per-entity junction wiring).

---

### Pitfall 6: `xmin` concurrency token configured wrong (or not at all)

**What goes wrong:**
Concurrent PUTs to the same entity both succeed silently (last-write-wins), corrupting state. Or `xmin` is configured but as a regular column, so EF tries to SET it on UPDATE and Postgres errors out (`xmin` is a system column).

**Why it happens:**
Postgres's `xmin` is a hidden system column that changes on every UPDATE. Npgsql supports it as an EF concurrency token via `.IsRowVersion()` on a `uint` property mapped to `"xmin"`, but it needs `ValueGeneratedOnAddOrUpdate()` and to be treated as readonly.

**How to avoid:**
- In `BaseEntity` (or `BaseDbContext.OnModelCreating`), wire xmin generically for every entity:
  ```csharp
  modelBuilder.Entity<T>()
      .Property<uint>("xmin")
      .HasColumnName("xmin")
      .HasColumnType("xid")
      .ValueGeneratedOnAddOrUpdate()
      .IsConcurrencyToken();
  ```
- Use a shadow property (no CLR field on `BaseEntity`) so the model stays clean.
- Map `DbUpdateConcurrencyException` to HTTP 409 in the problem-details middleware, with a useful detail message.
- For v1 (CRUD only, no expected high contention) document that 409 is the expected behavior for concurrent edits; do not silently retry.

**Warning signs:**
- "Database operation expected to affect 1 row(s) but actually affected 0 row(s)" — but only intermittently.
- Two PUT requests in close succession with the second's data being silently dropped (no 409).

**Phase to address:**
**P2 — EF Core base infra** (the shadow xmin convention) and **P3 — Cross-cutting middleware** (concurrency exception → 409 mapping).

---

### Pitfall 7: Using deprecated `FluentValidation.AspNetCore` auto-validation

**What goes wrong:**
Devs `dotnet add package FluentValidation.AspNetCore` and call `services.AddFluentValidationAutoValidation()`. It "works" in v1 but:
- The package is **deprecated** and removed in FluentValidation 12.
- Auto-validation runs in MVC's sync model-binding pipeline, so `ValidateAsync` rules silently won't run async logic correctly.
- Hides where validators actually execute, making debugging painful.
- Locks the project to MVC controller projects (won't work for Minimal APIs if ever migrated).

**Why it happens:**
Every tutorial older than 2024 still shows the auto-validation flow. It's the most ergonomic-looking option.

**How to avoid:**
- Install `FluentValidation` and `FluentValidation.DependencyInjectionExtensions` only — **not** `FluentValidation.AspNetCore`.
- Register: `services.AddValidatorsFromAssembly(typeof(BaseEntityValidator<>).Assembly);` (and per-entity assemblies).
- Validate **inside `BaseController<>`** (or a shared service method) explicitly:
  ```csharp
  var validationResult = await validator.ValidateAsync(dto, ct);
  if (!validationResult.IsValid)
      return ValidationProblem(validationResult.ToValidationProblemDetails());
  ```
- This gives you control over status code (RFC 7807 says 400 for validation by ASP.NET convention; don't use 422 here — it's a different category).
- Validator lifetime: register as **transient** (default) — they are stateless; making them scoped/singleton causes captured-state bugs with `ChildRules` and `When` closures.

**Warning signs:**
- `FluentValidation.AspNetCore` in `.csproj`.
- `AddFluentValidationAutoValidation()` in `Program.cs`.
- Validators that "don't run" on `IFormFile`, query parameters, or minimal API endpoints.

**Phase to address:**
**P5 — Validation & mapping base.** Wire FluentValidation correctly from the start. Add an ADR ("we do explicit validation, not auto-validation") in the repo so the deprecated path doesn't get reintroduced.

---

### Pitfall 8: OTel logging provider wired wrong — MEL pipeline doesn't reach the OTLP exporter

**What goes wrong:**
Developers call `builder.Services.AddOpenTelemetry().WithLogging(...)` and assume `ILogger<T>` logs flow to OTLP. They don't — because that overload registers OpenTelemetry's *standalone* `LoggerProvider`, not a MEL `ILoggerProvider`. Production traffic logs do not appear in the Collector. Or the inverse: they wire **both** paths, get duplicate log entries, and burn CPU/network.

**Why it happens:**
There are genuinely two APIs:
1. `builder.Logging.AddOpenTelemetry(o => { o.AddOtlpExporter(); })` — hooks into MEL, so `ILogger<T>.LogInformation(...)` flows out to OTLP. **This is what you want.**
2. `builder.Services.AddOpenTelemetry().WithLogging(...)` — sets up OTel's own logging API (`LoggerProvider`) for direct calls; useful for non-MEL emitters, often misunderstood.

The naming is genuinely confusing (see open-telemetry/opentelemetry-dotnet#4653).

**How to avoid:**
- Use exactly this shape for MEL → OTLP:
  ```csharp
  builder.Logging.AddOpenTelemetry(o =>
  {
      o.IncludeFormattedMessage = true;
      o.IncludeScopes = true;
      o.ParseStateValues = true;
      o.SetResourceBuilder(ResourceBuilder.CreateDefault()
          .AddService(serviceName: "sk-api", serviceVersion: "3.2.0"));
      o.AddOtlpExporter();
  });

  builder.Services.AddOpenTelemetry()
      .ConfigureResource(r => r.AddService("sk-api", serviceVersion: "3.2.0"))
      .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddOtlpExporter());
      // NOTE: NOT calling .WithLogging() here — MEL path above handles it.
  ```
- Set `IncludeFormattedMessage = true` (default is `false`, which sends only the template + parameters; many backends render this poorly).
- Set `IncludeScopes = true` so the correlation-id scope gets exported.
- Verify by sending a known log line and checking the Collector — do this in P4 as an explicit acceptance test.

**Warning signs:**
- Logs visible in console but absent from the Collector.
- Logs duplicated in the Collector (both paths active).
- Log records arrive but `Body` is empty or just the template string.

**Phase to address:**
**P4 — Observability.** Add an integration smoke test that emits a log line via `ILogger<T>` and asserts the OTLP exporter received it.

---

### Pitfall 9: `Logging:LogLevel` not filtering before OTel exporter — wasted CPU/network

**What goes wrong:**
`appsettings.json` `Logging:LogLevel:Default = "Warning"` is set, but Debug/Information logs are still serialized, batched, and sent to the Collector — because `AddOpenTelemetry` on the logging builder runs *after* MEL filters by default, but devs sometimes override the level on the provider or build filters incorrectly. CPU and network costs balloon under load.

**Why it happens:**
MEL filtering is per-provider. The OTel provider name is `"OpenTelemetry"`. If `Logging:LogLevel:OpenTelemetry` is set to `"Trace"` (or anyone calls `builder.Logging.SetMinimumLevel(LogLevel.Trace)` without thinking), the exporter receives everything.

**How to avoid:**
- Single source of truth: `Logging:LogLevel:Default` only. Do **not** set per-provider overrides under `Logging:LogLevel:OpenTelemetry` or `Logging:LogLevel:Console`.
- Do not call `builder.Logging.SetMinimumLevel(...)` in code; rely entirely on configuration.
- Add a startup log line that emits the effective minimum level so you can verify in production logs.
- Document in `appsettings.json` comments (yes, the .NET config system allows comments in `appsettings.json` despite STJ defaults — see Pitfall 30).

**Warning signs:**
- Collector ingestion volume disproportionate to traffic.
- `Logging:LogLevel:Default = "Information"` yet trace-level events arriving downstream.

**Phase to address:**
**P4 — Observability.** Add an acceptance test asserting that with `Default = "Warning"`, an Information log does not appear in a captured OTLP export.

---

### Pitfall 10: AspNetCoreInstrumentation reporting health probe traffic

**What goes wrong:**
Kubernetes / Docker compose hits `/health/live` and `/health/ready` every few seconds. Every probe creates a span and an HTTP metrics histogram entry. The Collector and downstream Loki/Prometheus are flooded with noise; the p99 RED metrics get skewed by the cheap health endpoints.

**Why it happens:**
`AddAspNetCoreInstrumentation()` instruments **every** endpoint by default. Devs forget to filter.

**How to avoid:**
- Filter at the instrumentation level:
  ```csharp
  .WithMetrics(m => m.AddAspNetCoreInstrumentation(o =>
  {
      o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health");
  }))
  ```
- For tracing (if added later), same filter pattern.
- Also exclude `/metrics`, `/swagger`, `/openapi`, and the Problem Details endpoint if you have one.
- Note: `.DisableHttpMetrics()` on the endpoint is .NET 9+; for .NET 8, use the filter.

**Warning signs:**
- HTTP server metrics histogram dominated by very-fast (sub-millisecond) requests.
- Loki shows steady-state log spam at probe interval.

**Phase to address:**
**P4 — Observability.** Wire the filter when adding instrumentation and verify by hitting `/health/live` 10 times and checking export.

---

### Pitfall 11: Correlation-Id scope lost across async boundaries

**What goes wrong:**
Middleware reads `X-Correlation-Id`, calls `_logger.BeginScope(new { CorrelationId = id })`, and then everything logged inside the same request shows the id — until code goes through `Task.Run`, a captured lambda, a background `IHostedService`, or any code that doesn't flow `AsyncLocal`. Now logs from the actual work have no correlation id.

**Why it happens:**
`ILogger.BeginScope` stores in `AsyncLocal<T>` and flows with the execution context. `Activity.Current` (the OTel/W3C tracing context) is separate. Code that fires-and-forgets work, or libraries that use `ThreadPool.UnsafeQueueUserWorkItem`, can drop both.

**How to avoid:**
- Set correlation id on **both** the log scope **and** `Activity.Current?.SetBaggage("correlation.id", id)` (so tracing propagation carries it too).
- The middleware must:
  1. Read `X-Correlation-Id` header; if missing, generate `Guid.NewGuid().ToString("N")`.
  2. Set `HttpContext.TraceIdentifier = id` (so default ASP.NET logs pick it up too).
  3. Push `_logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id })` for the rest of the pipeline.
  4. Set `Activity.Current?.AddTag("correlation.id", id)`.
  5. Write the header to the **response** before any other middleware can short-circuit (use `httpContext.Response.OnStarting(...)` or set it immediately).
- Header name: standardize as `X-Correlation-Id` (case-insensitive in HTTP, but always emit in that exact form for grep-ability).
- Add the correlation id into every Problem Details response body explicitly — see Pitfall 14.

**Warning signs:**
- Half of logs for a single request have a correlation id, the other half don't.
- Logs from background `IHostedService` work or `Task.Run(...)` paths never have ids.
- The id in the response header differs from the id in the logs.

**Phase to address:**
**P3 — Cross-cutting middleware.**

---

### Pitfall 12: Generating correlation id too late (after another middleware logged)

**What goes wrong:**
Correlation middleware is registered after `UseExceptionHandler` / `UseRouting` / etc. A request fails in earlier middleware → the exception is logged without a correlation id → the response body shows id `X`, but logs show no id at all.

**Why it happens:**
ASP.NET Core middleware order matters; `app.Use*` is sequential. Devs paste correlation middleware near where they put logging or auth, which is usually too deep.

**How to avoid:**
- Register correlation-id middleware **as the first** app-level middleware (after `UseForwardedHeaders` if used, before everything else):
  ```csharp
  app.UseMiddleware<CorrelationIdMiddleware>();   // FIRST
  app.UseExceptionHandler("/error");              // problem-details handler
  app.UseRouting();
  app.UseAuthorization(); // when added later
  app.MapControllers();
  ```
- Document the order in `BaseApi.Core`'s `AddBaseApi(...)` extension method via the order of `Use*` calls inside it.
- The `BaseApi.Core` extension method should bundle the order so consumers can't get it wrong.

**Warning signs:**
- Exception logs without correlation ids while response bodies have them.
- First few log entries of a request lack the id.

**Phase to address:**
**P3 — Cross-cutting middleware.** Bake the order into `AddBaseApi(...)`.

---

### Pitfall 13: Leaking stack traces / internal details into Problem Details responses

**What goes wrong:**
A `NullReferenceException` from the service layer surfaces in the JSON response body's `detail` field complete with source paths, environment usernames, and (in worst cases) connection-string fragments. Anyone with cURL has a roadmap of your internals.

**Why it happens:**
ASP.NET Core's `DeveloperExceptionPage` is on in `Development`; the `UseExceptionHandler` lambda may pass `Exception.ToString()` or `Exception.Message` into `ProblemDetails.Detail` without thinking about prod.

**How to avoid:**
- The exception handler middleware (custom or `UseExceptionHandler`):
  - In **all** environments, never put `Exception.ToString()` into the response.
  - Set `ProblemDetails.Detail = "An unexpected error occurred."`
  - Set `ProblemDetails.Extensions["correlationId"] = correlationId` so the operator can grep logs.
  - Log the full exception (with stack) via `ILogger.LogError(ex, "unhandled.exception")`.
- For known exceptions (DbUpdateException, FluentValidationException, KeyNotFoundException, DbUpdateConcurrencyException), map to specific status codes and safe detail messages.
- Test in P3 with an endpoint that throws — assert the response body does not contain the word "Exception" or any path fragment.

**Warning signs:**
- `Detail` field contains file paths, line numbers, or class names in 500 responses.
- `application/problem+json` body grows > 2KB for a simple error.

**Phase to address:**
**P3 — Cross-cutting middleware.** Add a "no internals leaked" test in P3.

---

### Pitfall 14: Postgres SQLSTATE → HTTP mapping miss-mapping or incomplete coverage

**What goes wrong:**
The team maps `23503` (FK violation) and `23505` (unique violation), and forgets `23502` (NOT NULL violation) or `23514` (check constraint). Or maps `23503` to 409 (it's actually 422 per the spec). Or doesn't extract the offending field name from the constraint name, so the response says only "FK violation" with no actionable detail.

**Why it happens:**
SQLSTATE codes are not memorized; the mapping is invisible until production traffic hits an edge case.

**How to avoid:**
- Centralize the mapping in a single class (`PostgresErrorMapper`) in `BaseApi.Core`:
  - `23503` (foreign_key_violation) → **422 Unprocessable Entity**, detail mentions the referenced table/column from `PostgresException.ConstraintName`.
  - `23505` (unique_violation) → **409 Conflict**, detail names the unique column(s) from the constraint.
  - `23502` (not_null_violation) → **400 Bad Request**, detail names the column from `PostgresException.ColumnName`.
  - `23514` (check_violation) → **400 Bad Request**, detail names the constraint.
  - `23P01` (exclusion_violation) → **409 Conflict**.
  - Any other `23xxx` → **400 Bad Request** with generic message and `sqlstate` in `extensions`.
- Catch `DbUpdateException` in the error middleware, unwrap the `InnerException as PostgresException`, route through the mapper.
- Add `sqlstate` to Problem Details `extensions` so operators can cross-reference.

**Warning signs:**
- Unique-constraint hits returning 500 instead of 409.
- FK violation messages saying "duplicate key" (you mapped the wrong code).
- Postgres-specific text appearing verbatim in client responses.

**Phase to address:**
**P3 — Cross-cutting middleware.** Verify against the project's explicit table of mappings.

---

### Pitfall 15: Liveness probe checking the database

**What goes wrong:**
The liveness probe queries Postgres. Postgres has a transient blip. Kubernetes kills the pod. The pod restarts. Postgres is still blippy. The pod is killed again. Cascading restarts; the service oscillates while Postgres is fine 5 seconds later.

**Why it happens:**
Devs treat "is this thing working" as one question. It's two: *can this process accept work* (liveness) vs *can this process serve traffic* (readiness).

**How to avoid:**
- **Liveness** (`/health/live`): only the process being alive — return 200 always once started. **Do not** check DB.
- **Readiness** (`/health/ready`): check DB connectivity, OTLP endpoint reachability (optional), any other deps. If a dep is down, return 503 — orchestrator stops routing traffic but does **not** kill the pod.
- **Startup** (`/health/startup`): completes once DI is built and migrations have run; never re-evaluates after that.
- Use `Microsoft.Extensions.Diagnostics.HealthChecks` with tags:
  ```csharp
  services.AddHealthChecks()
      .AddNpgSql(connStr, tags: new[] { "ready" })
      .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" });

  app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = c => c.Tags.Contains("live") });
  app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
  app.MapHealthChecks("/health/startup", new HealthCheckOptions { Predicate = c => c.Tags.Contains("startup") });
  ```
- Tag the startup check separately and flip it healthy from the migration runner (Pitfall 3).

**Warning signs:**
- Pods restarting during Postgres failover instead of just losing traffic temporarily.
- Liveness probe latency > 100ms (it's calling the DB).

**Phase to address:**
**P4 — Observability.** Wire all three probes with correct tag predicates.

---

### Pitfall 16: Mapperly compile-time pitfalls — wrong project setup or partial class shape

**What goes wrong:**
Build succeeds but `Mapper.MapToDto(entity)` throws `NullReferenceException` at runtime because the source generator didn't run. Or the build fails with `MP0001: Mapping not possible` because nullability mismatches. Or the mapper class is `public partial class Mapper` (not `static partial class`) and source-gen produces methods on an instance type that's never registered in DI.

**Why it happens:**
- The `Riok.Mapperly` package must be referenced with `PrivateAssets="all"` and `OutputItemType="Analyzer"` (or via the `Riok.Mapperly` PackageReference using its `analyzers` asset by default — but some templates strip it).
- C# language version must be 11+ (default in .NET 8: yes).
- The class must be marked `[Mapper]` and be `partial`. For dependency-free mappers, mark it `static partial class` for least surprise; otherwise, the generated methods are instance methods needing DI registration.

**How to avoid:**
- Standard template in `BaseApi.Service` for entity mappers:
  ```csharp
  [Mapper]
  public static partial class SchemaMapper
  {
      public static partial SchemaReadDto ToReadDto(SchemaEntity entity);
      public static partial SchemaEntity ToEntity(SchemaCreateDto dto);
      public static partial void ApplyUpdate(SchemaUpdateDto dto, SchemaEntity entity); // [MappingTarget] inferred from 2nd param? — see Pitfall 17
  }
  ```
- Verify by inspecting `obj/.../generated/Riok.Mapperly` for the actual generated code on first build.
- CI should fail the build on Mapperly diagnostics: `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` or specifically `<WarningsAsErrors>MP0001;MP0011;MP0020;MP0021</WarningsAsErrors>`.

**Warning signs:**
- Build "succeeds" but no generated files in `obj/`.
- Methods declared `partial` but never implemented at runtime.
- Suppressed warnings about unmapped properties.

**Phase to address:**
**P5 — Validation & mapping base.** Establish the template and CI warning-promotion rule in P5; entity-specific mappers in P7 follow it.

---

### Pitfall 17: Mapperly Update mapping — overwriting server-controlled fields

**What goes wrong:**
The Update mapper takes `UpdateDto` and writes to the existing entity. If the dto accidentally has an `Id` field (e.g., devs copy `CreateDto` to make `UpdateDto`), Mapperly happily overwrites the entity's `Id`. Worse, if `UpdateDto` includes `CreatedAt`, `CreatedBy`, or `Version` (when not intended as user-settable), the user can rewrite audit fields and primary keys.

**Why it happens:**
Mapperly maps by property name. If the DTO has a field with the same name as a server-controlled field, it gets mapped. Source generators don't know "this is audit data."

**How to avoid:**
- `UpdateDto` shape must *intentionally* omit `Id`, `CreatedAt`, `CreatedBy`, `UpdatedAt`, `UpdatedBy`, and any system-managed field. Treat the DTO as the security boundary.
- Use `[MapperIgnoreSource]` or `[MapperIgnoreTarget]` to be explicit in the mapper when there's ever a question:
  ```csharp
  [Mapper]
  public static partial class SchemaMapper
  {
      [MapperIgnoreTarget(nameof(SchemaEntity.Id))]
      [MapperIgnoreTarget(nameof(SchemaEntity.CreatedAt))]
      [MapperIgnoreTarget(nameof(SchemaEntity.CreatedBy))]
      public static partial void ApplyUpdate(SchemaUpdateDto dto, [MappingTarget] SchemaEntity entity);
  }
  ```
- Add a code review checklist item: "Does the UpdateDto contain any server-controlled fields?"
- Mapperly emits `MP0011` for unmapped target properties — promote it to error in CI; it's your safety net for missing fields, but not for server-controlled fields.

**Warning signs:**
- Audit columns reset to default values after PUT.
- `Id` columns changing under PUT (catastrophic — but actually possible if DTOs are wrong).

**Phase to address:**
**P5 — Validation & mapping base** (set the pattern + add the linter) and **P7 — Entity build-out** (apply per entity).

---

### Pitfall 18: SemVer regex too loose or too strict for `BaseEntity.Version`

**What goes wrong:**
The project locks in `^\d+\.\d+\.\d+$`. This accepts:
- `0.0.0` ✅ (allowed — but is that valid?)
- `01.02.03` ✅ (technically allowed by regex; **not valid SemVer 2.0** — leading zeros forbidden)
- It rejects `1.0.0-alpha`, `1.0.0+build.1`, `1.0.0-rc.1+build.42` ❌

Documentation says "SemVer" but the regex only accepts the major.minor.patch subset. Users send `1.0.0-beta`, get 400, file bug reports.

**Why it happens:**
The regex was a quick sanity-check, not a SemVer 2.0 implementation. The full SemVer 2.0 regex is much longer (see semver.org).

**How to avoid:**
- **Pick a position and document it.** Two valid choices:
  - **Option A — "Strict numeric triple":** `^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$` (rejects leading zeros, rejects prerelease/build metadata). Document as "We use the SemVer 2.0 *core* version only."
  - **Option B — "Full SemVer 2.0":** Use the official regex from semver.org/#is-there-a-suggested-regular-expression-regex-to-check-a-semver-string.
- Put the validator + regex literal in `BaseEntityValidator<T>` with a unit test table covering both accepted and rejected inputs.
- Document the choice in `BaseEntity.Version` XML doc.

**Warning signs:**
- Bug reports about `1.0.0-alpha` being rejected.
- Devs writing case-by-case workarounds in entity-specific validators.

**Phase to address:**
**P5 — Validation & mapping base.** Lock the regex in `BaseEntityValidator<T>` and document.

---

### Pitfall 19: SHA-256 regex case-sensitivity for `Processor.SourceHash`

**What goes wrong:**
The validator uses `^[0-9a-f]{64}$`. A caller computes the hash and converts to uppercase hex (Microsoft's `BitConverter.ToString(...).Replace("-","")` returns uppercase). The string is a valid SHA-256 hex; the validator says 400. Bug.

**Why it happens:**
SHA-256 is binary; "hex" representation has no canonical case. Different stdlibs/languages emit different cases.

**How to avoid:**
- Validator regex: `^[0-9A-Fa-f]{64}$` (accept both cases) **and** normalize to lowercase before persisting / before uniqueness check (otherwise `ABC...` and `abc...` are two rows for the same hash).
- Normalization happens in the mapper / service before save: `entity.SourceHash = dto.SourceHash.ToLowerInvariant();`.
- Unique index on the column is case-sensitive by default in Postgres — that's fine since you normalized.

**Warning signs:**
- 400 errors on hashes that "look right."
- Two rows with hashes differing only in case (uniqueness check failed because you didn't normalize before insert).

**Phase to address:**
**P5 — Validation & mapping base** (regex + normalization helper) and **P7 — Entity build-out** (ProcessorEntity specifically).

---

### Pitfall 20: JSON Schema validator: draft mismatch for `Schema.Definition`

**What goes wrong:**
The validator uses `JsonSchema.Net` or `NJsonSchema` configured to assume `draft-07`. A user posts a schema with `"$schema": "https://json-schema.org/draft/2020-12/schema"`. The validator either ignores the `$schema` keyword and forces draft-07 (passing invalid 2020-12 constructs), or fails on legitimate 2020-12 idioms (`$defs` vs `definitions`).

**Why it happens:**
JSON Schema has been through several drafts; libraries default to whatever was current at their last update. Drafts vary in keyword sets meaningfully.

**How to avoid:**
- **Pick a single draft and document it.** Recommend **2020-12** (current spec, broader keyword support).
  - Library: `JsonSchema.Net` (a.k.a. `Json.Schema` from `gregsdennis/json-everything`) — actively maintained, supports 2020-12.
  - Alternative: `NJsonSchema` if generating C# from schemas later.
- Validator code:
  ```csharp
  var schemaJson = JsonNode.Parse(dto.Definition);
  var schema = JsonSchema.FromText(dto.Definition);
  // If user supplied $schema, validate against that; else against 2020-12.
  ```
- Reject schemas whose `$schema` doesn't match the project's supported draft, with a clear error.
- Add unit tests against a known-good and known-bad schema for each draft.

**Warning signs:**
- Schemas containing `$defs` failing where they should pass (or vice versa).
- Validators silently passing malformed schemas.

**Phase to address:**
**P5 — Validation & mapping base** (decide draft + library) and **P7 — Entity build-out** (SchemaEntity specifically).

---

### Pitfall 21: Cron expression validation — Cronos vs NCrontab differ on edge cases

**What goes wrong:**
The validator uses NCrontab and accepts `0 0 * * * *` (six fields). Cronos (used elsewhere, or in a future scheduler) rejects it because it expects 5 or 6 fields with a different convention. Or vice versa: Cronos accepts `0 0 12 ? * MON` (Quartz-style `?`) and NCrontab doesn't.

**Why it happens:**
"Cron" is not a standard. Implementations differ on:
- 5-field (POSIX) vs 6-field (seconds) cron
- `?` placeholder (Quartz) vs `*`
- Day-of-week numbering (0=Sun vs 1=Mon)
- Special tokens (`@yearly`, `L`, `W`, `#`)

**How to avoid:**
- **Pick the library that matches the consumer.** Since the orchestrator/scheduler is external and out of scope, this project must:
  - Either: standardize on **Cronos** (stricter, predictable, widely used in .NET) and document "5-field UTC cron, no Quartz extensions."
  - Or: standardize on whatever the external scheduler uses, and validate using the same library it does.
- Validator code (Cronos):
  ```csharp
  RuleFor(x => x.CronExpression)
      .Must(BeValidCron)
      .When(x => !string.IsNullOrEmpty(x.CronExpression))
      .WithMessage("CronExpression must be a valid 5-field cron expression.");

  static bool BeValidCron(string? expr)
      => Cronos.CronExpression.TryParse(expr, out _);
  ```
- Document the chosen format in `WorkflowEntity.CronExpression` XML doc and the OpenAPI description.

**Warning signs:**
- Workflows that "validate fine" but the external scheduler rejects.
- Bug reports about `?` or `@hourly` not working.

**Phase to address:**
**P5 — Validation & mapping base** (library choice) and **P7 — Entity build-out** (WorkflowEntity specifically).

---

### Pitfall 22: `Assignment.Payload` JSON validation — STJ default rejects comments / trailing commas

**What goes wrong:**
The validator parses `Assignment.Payload` as JSON to confirm syntactic validity. `JsonDocument.Parse(payload)` with default options throws on `// comment`, trailing commas, or `NaN`. Users (or upstream tools) send "JSON" that has these conveniences; the validator says 400; the user is confused because Postman accepts it.

**Why it happens:**
`System.Text.Json` is strict by default. Newtonsoft.Json is lenient. Some upstream systems emit JSON5-style or have comments.

**How to avoid:**
- **Decide and document the JSON dialect.** Recommend **strict RFC 8259** (no comments, no trailing commas) — that's what's actually portable.
- Validator code:
  ```csharp
  RuleFor(x => x.Payload)
      .Must(BeValidJson)
      .WithMessage("Payload must be syntactically valid JSON (RFC 8259, no comments).");

  static bool BeValidJson(string payload)
  {
      try { using var _ = JsonDocument.Parse(payload); return true; }
      catch (JsonException) { return false; }
  }
  ```
- Do **not** set `JsonDocumentOptions.CommentHandling = Skip` or `AllowTrailingCommas = true` unless explicitly required.
- For `Schema.Definition` (JSON Schema): same rule — must be strict JSON.

**Warning signs:**
- Validation errors for "obviously valid" JSON (it had a comment).
- Users requesting comment support.

**Phase to address:**
**P5 — Validation & mapping base** (helper + rule) and **P7 — Entity build-out** (AssignmentEntity + SchemaEntity).

---

### Pitfall 23: Postgres connection pool starvation — long-running operations holding connections

**What goes wrong:**
A request opens a transaction, calls an external API mid-transaction, the external API takes 30 seconds, the Postgres connection sits idle inside the pool's `MaxPoolSize` budget. A burst of requests exhausts the pool. New requests block on `NpgsqlConnection.Open()` until timeout, returning 500s. Postgres itself is fine; the connection pool is the bottleneck.

**Why it happens:**
Npgsql's default `Maximum Pool Size = 100` per process, with `Connection Idle Lifetime = 300s`. Holding connections across `await` boundaries that involve non-DB I/O burns the budget.

**How to avoid:**
- **Architectural rule:** never call non-DB I/O (HTTP, file system, message queue) inside a `using` of `IDbContextTransaction` or with an open `DbContext` in scope.
- Document the rule in `BaseApi.Core` repository pattern: services do single-unit-of-work transactions only.
- For v1 (CRUD only), this is naturally enforced by the controller → service → repository shape. Add the rule to `CONTRIBUTING.md` so it doesn't drift later.
- Configure conservative pool size in connection string and document it: `Maximum Pool Size=20;Timeout=15;` for the API service.

**Warning signs:**
- 500s under load with `NpgsqlException: The connection pool has been exhausted` or `Timeout while getting a connection from pool`.
- Postgres `pg_stat_activity` shows many `idle in transaction` connections.

**Phase to address:**
**P1 — Postgres + connection plumbing** (sensible pool size) and **P6 — Abstract generic controllers + service/repository base** (transaction shape).

---

### Pitfall 24: Docker compose v2 — `depends_on` without `condition: service_healthy`

**What goes wrong:**
The API container starts before Postgres is accepting connections. Migrations fail. Container exits. Compose restarts it. After a few retries it works (or doesn't). On CI, race-condition test failures.

**Why it happens:**
Compose v1's `depends_on: [postgres]` only waits for the container to **start**, not to be **ready**. Compose v2 added the `condition` syntax but devs often copy old examples.

**How to avoid:**
- Postgres service needs a `healthcheck`:
  ```yaml
  postgres:
    image: postgres:16
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U $$POSTGRES_USER -d $$POSTGRES_DB"]
      interval: 5s
      timeout: 5s
      retries: 10
      start_period: 5s
    environment:
      POSTGRES_DB: steps
      POSTGRES_USER: steps
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:?required}
    volumes:
      - pgdata:/var/lib/postgresql/data
    ports:
      - "5433:5432"  # avoid 5432 host conflict — see Pitfall 25
  ```
- API service:
  ```yaml
  api:
    depends_on:
      postgres:
        condition: service_healthy
  ```
- Use `pg_isready` (not `nc` or `psql -c "SELECT 1"` — `pg_isready` is the canonical readiness check shipped with the image).
- Double-`$$` escapes compose's variable interpolation so the env var is expanded inside the container.

**Warning signs:**
- Migration errors on first `docker compose up` after `down`.
- CI flakes only on the first run of a job.

**Phase to address:**
**P1 — Postgres + Docker Compose.**

---

### Pitfall 25: Host port 5432 already taken — silent dev confusion

**What goes wrong:**
Developer has a local Postgres (or another project's Postgres) bound to 5432. Compose starts; the port binding silently maps to the wrong service or fails. Developer's `psql -h localhost -p 5432` connects to the wrong database; they "see" data that isn't really there or destroy data they didn't expect.

**Why it happens:**
Postgres on the dev host defaults to 5432. Compose's port binding gives a clear error if 5432 is busy, but it's a common surprise.

**How to avoid:**
- In `docker-compose.yml`, bind to a non-default host port: `ports: ["5433:5432"]`.
- Document the choice in the repo README: "Connect locally via `psql -h localhost -p 5433`."
- The API's connection string (in `appsettings.Development.json`) uses `Host=localhost;Port=5433` for direct dev runs, **but** when running inside Docker Compose, the API container reaches Postgres via the service name and the **internal** port: `Host=postgres;Port=5432`. Two configs.

**Warning signs:**
- "It works for me" but the dev was looking at a different DB.
- `docker compose up` errors with "bind: address already in use."

**Phase to address:**
**P1 — Postgres + Docker Compose.**

---

### Pitfall 26: Volume persistence — destroying dev data on `docker compose down`

**What goes wrong:**
Developer runs `docker compose down -v` (or even `down` if volumes are anonymous), all dev data vanishes, migrations replay, seeded test data is gone.

**Why it happens:**
Volumes are named explicitly (good) or anonymous (bad). `-v` removes all of them. Anonymous volumes for Postgres data are easy to lose.

**How to avoid:**
- Always use a **named** volume for Postgres data:
  ```yaml
  volumes:
    pgdata:
  services:
    postgres:
      volumes:
        - pgdata:/var/lib/postgresql/data
  ```
- Document in README: "`docker compose down -v` wipes the dev database. Use `docker compose down` (without -v) for normal restarts."
- For CI/test runs, use throwaway databases (Testcontainers — Pitfall 33).

**Warning signs:**
- Dev complaints about losing data after restart.
- Migration runs every startup as if from scratch.

**Phase to address:**
**P1 — Postgres + Docker Compose.**

---

### Pitfall 27: Postgres locale / encoding mismatch on volume reuse

**What goes wrong:**
First run created the database with the image's default locale (`en_US.utf8`). Dev re-creates the volume from a different OS/image and the locale differs. Migrations succeed but text comparisons sort differently in tests; `ORDER BY name` returns different orders on dev vs CI.

**Why it happens:**
`initdb` runs once when the volume is empty. The collation is baked in at that point.

**How to avoid:**
- Pin in compose: `POSTGRES_INITDB_ARGS: "--encoding=UTF8 --locale=C.UTF-8"` (or `en_US.UTF-8` if collation matters).
- Pin the image tag, not `:latest`: `image: postgres:16.4` (or whatever LTS is current).
- For text comparison/sorting determinism, prefer `COLLATE "C"` on columns that need it (e.g., `Name`).

**Warning signs:**
- CI sort-order flakes that don't reproduce locally.
- Different Postgres versions in dev vs prod.

**Phase to address:**
**P1 — Postgres + Docker Compose.**

---

### Pitfall 28: Tests slow because Testcontainers starts a new container per test

**What goes wrong:**
Each test in an integration test suite spins up a new Postgres container. 50 tests × 5 seconds container startup = 4+ minutes per run. CI times out; devs run tests less; bugs accumulate.

**Why it happens:**
Default xUnit creates a new test class instance per test method. If `Testcontainers` startup is in the constructor or `IAsyncLifetime.InitializeAsync` per-test, you pay the cost N times.

**How to avoid:**
- Use **xUnit class fixtures** or **collection fixtures** to share one container across many tests:
  ```csharp
  public class PostgresFixture : IAsyncLifetime
  {
      public PostgreSqlContainer Container { get; } =
          new PostgreSqlBuilder().WithImage("postgres:16.4").Build();

      public async Task InitializeAsync() => await Container.StartAsync();
      public async Task DisposeAsync() => await Container.DisposeAsync();
  }

  [CollectionDefinition("Database")]
  public class DatabaseCollection : ICollectionFixture<PostgresFixture> { }

  [Collection("Database")]
  public class SchemaTests
  {
      private readonly PostgresFixture _fixture;
      public SchemaTests(PostgresFixture fixture) => _fixture = fixture;
  }
  ```
- Reset DB state **between tests** with **Respawn** (delete table rows; ~10ms) instead of restarting the container.
- For WebApplicationFactory: subclass and override the connection string to point at the container's exposed port.

**Warning signs:**
- Test suite takes > 1 minute for < 100 tests.
- `docker ps` during test run shows dozens of `postgres:...` containers.

**Phase to address:**
**P8 — Test stack.**

---

### Pitfall 29: WebApplicationFactory not honoring Testcontainers connection string

**What goes wrong:**
Test runs `WebApplicationFactory<Program>` but the API still connects to the dev `appsettings.Development.json` Postgres (or fails because no DB is up). The factory builds `Program.cs`'s DI which reads `IConfiguration` at startup time — the override happens too late.

**Why it happens:**
`WebApplicationFactory.ConfigureWebHost` runs after `Program.cs` builds the host. To override config used by `AddDbContext`, you must intercept `IConfiguration` before it's bound, or replace the registered DbContextOptions after.

**How to avoid:**
- Use `WithWebHostBuilder` and `ConfigureAppConfiguration` to **add an in-memory config source** that overrides the connection string **before** `AddDbContext` sees it:
  ```csharp
  public class TestWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
  {
      private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().Build();

      public async Task InitializeAsync() => await _container.StartAsync();
      public new async Task DisposeAsync() { await _container.DisposeAsync(); await base.DisposeAsync(); }

      protected override void ConfigureWebHost(IWebHostBuilder builder)
      {
          builder.ConfigureAppConfiguration(cfg =>
          {
              cfg.AddInMemoryCollection(new Dictionary<string, string?>
              {
                  ["ConnectionStrings:Default"] = _container.GetConnectionString()
              });
          });
      }
  }
  ```
- Alternative: replace the DbContextOptions registration in `ConfigureTestServices`, but the config-override approach is cleaner and exercises the real `AddDbContext` path.

**Warning signs:**
- Tests succeed locally only because dev Postgres is up.
- Tests connect to the dev database (and modify it!).

**Phase to address:**
**P8 — Test stack.**

---

### Pitfall 30: `appsettings.json` comments and `Logging:LogLevel` discoverability

**What goes wrong:**
A developer adds a comment to `appsettings.json` explaining a setting. The default `ConfigurationBuilder` in .NET 8 parses JSON strictly; `//` is rejected; the service fails to start with an unhelpful "JSON parse error at line N."

**Why it happens:**
The default `Json.NET`-era convention allowed comments; .NET 8 uses `System.Text.Json` which is strict.

**How to avoid:**
- Don't put comments in `appsettings.json`. If documentation is needed, put it in a sibling `appsettings.README.md` or in the loader (`Program.cs` comments).
- Alternative: use `AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)` with the `JsonConfigurationProvider` configured to allow comments — but this requires plumbing and is more trouble than it's worth.
- Don't rely on environment-variable-only config in dev (harder to debug).

**Warning signs:**
- Service fails on startup with "JSON" errors after a config edit.
- Operators add `# explanation` thinking it's like YAML.

**Phase to address:**
**P0 — Repository scaffold.** Set the convention and document.

---

### Pitfall 31: OTLP exporter timeouts/batching defaults mask Collector downtime

**What goes wrong:**
The Collector is down for 5 minutes. The default OTLP batch exporter buffers up to 2048 spans/log records and retries with backoff. Buffer fills, oldest get dropped silently. When the Collector comes back, logs/metrics for that window are gone — no warning in the app's own logs.

**Why it happens:**
The exporter is intentionally non-blocking; failures don't propagate to the app to avoid taking down the service. But the silence is deceiving.

**How to avoid:**
- Set explicit exporter options and log internal failures:
  ```csharp
  o.AddOtlpExporter((exporterOpts, processorOpts) =>
  {
      exporterOpts.Endpoint = new Uri(otlpEndpoint);
      exporterOpts.TimeoutMilliseconds = 5000;
      processorOpts.BatchExportProcessorOptions.MaxQueueSize = 4096;
      processorOpts.BatchExportProcessorOptions.MaxExportBatchSize = 512;
      processorOpts.BatchExportProcessorOptions.ScheduledDelayMilliseconds = 5000;
  });
  ```
- Enable OTel's **self-diagnostics** via env var (`OTEL_DOTNET_AUTO_LOG_DIRECTORY` or `OTEL_LOG_LEVEL`) so internal exporter errors are visible.
- Add a Collector reachability check to the readiness probe (optional; some teams treat it as non-critical).
- Document expected backpressure: "If OTLP is down for >N minutes, logs from that window will be dropped."

**Warning signs:**
- Gaps in the Collector that don't correspond to gaps in console logs.
- No app-side warning when OTLP exporter fails.

**Phase to address:**
**P4 — Observability.**

---

### Pitfall 32: Unbounded `ActivitySource` / counters causing memory growth

**What goes wrong:**
A counter or histogram is tagged with high-cardinality values (e.g., the entity `Id` itself, or the raw request path including `Guid`s). Each unique tag combination creates a new metric series. Memory grows linearly; Prometheus/Collector ingestion explodes.

**Why it happens:**
Devs tag metrics with "useful" identifiers that are unbounded in cardinality.

**How to avoid:**
- Hard rule: **never tag metrics with `Guid` / user-supplied strings / full request paths**.
- For HTTP server metrics, the AspNetCore instrumentation uses **route templates** (e.g., `/api/schemas/{id}`), not raw paths — this is correct by default. Verify by checking exported metrics.
- For custom counters added later, document the allowed tag set. Aim for tags with bounded cardinality (entity type, status code, error code) — not entity-specific.
- For high-cardinality dimensions, use logs/traces (where each event is its own unit), not metrics.

**Warning signs:**
- Collector memory growing without bound.
- Prometheus `series count` growing linearly with traffic.

**Phase to address:**
**P4 — Observability.** Document the rule when wiring instrumentation.

---

### Pitfall 33: Server-controlled fields in `CreateDto` — silent privilege escalation

**What goes wrong:**
`SchemaCreateDto` is generated by copy-paste from `SchemaEntity` and accidentally includes `Id`, `CreatedAt`, `CreatedBy`. A client POSTs `{"id": "00000000-...", "createdBy": "admin", ...}` — the mapper happily assigns those values, and the entity is created with a chosen ID and forged `CreatedBy`.

**Why it happens:**
DTOs aren't intentionally minimal. Devs lazily mirror the entity shape.

**How to avoid:**
- **CreateDto rule:** never include `Id`, `CreatedAt`, `CreatedBy`, `UpdatedAt`, `UpdatedBy`. The service / interceptor / framework owns these.
- **UpdateDto rule:** never include the above either; also never include immutable fields (the entity's identity).
- **ReadDto rule:** include all audit fields (it's read-only, observability matters).
- Add a unit test per entity: deserialize a `CreateDto` from a JSON containing `id`, `createdAt`, `createdBy` — assert these fields are NOT settable (either ignored by STJ or rejected by the validator).
- The Mapperly mapper from `CreateDto` → `Entity` simply cannot map `Id` if `CreateDto` doesn't have it — Mapperly's `MP0020`/`MP0021` warnings flag unmapped target properties; promote to errors so missing fields are visible.

**Warning signs:**
- `CreateDto` and `ReadDto` are structurally identical (suspicious).
- Tests don't cover "client sends id" case.

**Phase to address:**
**P5 — Validation & mapping base** (DTO conventions) and **P7 — Entity build-out** (per-entity DTOs).

---

### Pitfall 34: `[ApiController]` model validation triggering before FluentValidation runs

**What goes wrong:**
The base controller has `[ApiController]` (gives automatic 400 on model-binding errors, useful). But `[ApiController]` also runs DataAnnotations and reports its own ValidationProblemDetails — bypassing the project's custom error shape and FluentValidation results. The two coexist confusingly: model-binding errors → ASP.NET's shape; FluentValidation errors → project's shape.

**Why it happens:**
`[ApiController]` adds automatic 400 handling for invalid `ModelState`. FluentValidation in the project is invoked manually inside actions. The two paths produce different response bodies.

**How to avoid:**
- Keep `[ApiController]` (still want auto-400 for model-binding / JSON-shape errors).
- Customize the response factory to match the project's RFC 7807 + correlationId shape:
  ```csharp
  services.Configure<ApiBehaviorOptions>(options =>
  {
      options.InvalidModelStateResponseFactory = context =>
      {
          var problem = new ValidationProblemDetails(context.ModelState)
          {
              Status = StatusCodes.Status400BadRequest,
              Type = "https://example.com/probs/validation",
          };
          problem.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
          return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
      };
  });
  ```
- Document: model-binding errors and FluentValidation errors share the same response contract (status, type, errors map, correlationId).

**Warning signs:**
- "Same" validation failure returns different JSON shapes depending on which code path triggered.
- Missing `correlationId` in 400s from model-binding (vs present in FluentValidation 400s).

**Phase to address:**
**P5 — Validation & mapping base.**

---

### Pitfall 35: Hard delete cascading unexpectedly through junctions

**What goes wrong:**
DELETE `/steps/{id}` succeeds. Postgres cascades to `step_next_steps` (good) and to `workflow_entry_steps` (bad — silently removing the step from workflows that reference it, possibly invalidating workflows). No application-level check that the step is unreferenced.

**Why it happens:**
DB-level cascade was set to `Cascade` on every FK by EF Core's default for required relationships, or by a copy-paste in `OnModelCreating`.

**How to avoid:**
- Be explicit per FK about delete behavior. Default to `Restrict` (DB raises FK violation if referenced; surfaces as 422 per Pitfall 14):
  ```csharp
  modelBuilder.Entity<WorkflowEntrySteps>()
      .HasOne<StepEntity>().WithMany().HasForeignKey(x => x.StepId)
      .OnDelete(DeleteBehavior.Restrict);
  ```
- Only use `Cascade` where the relationship semantically owns (e.g., `step_next_steps` rows when their parent step is deleted — that's housekeeping, not data loss).
- Document the cascade table per junction in `BaseDbContext`.

**Warning signs:**
- DELETE on a referenced entity returns 200 instead of 422.
- Workflows mysteriously "lose" entry steps after step deletion.

**Phase to address:**
**P7 — Entity build-out** (per-relationship cascade decisions).

---

### Pitfall 36: Empty `BaseEntity.Description` round-tripping as `null` vs `""`

**What goes wrong:**
A user POSTs without `description`. The entity stores `null`. GET returns `"description": null`. Next caller PUTs back the same object with `null`; mapper or validator complains because `null` is treated differently from missing-from-payload by STJ. Or worse: the validator says "Description must not be empty," which conflicts with the documented "Description is optional."

**Why it happens:**
STJ JSON `null` and missing-property are not distinguished in the deserialized object (both yield `null`). Validators conflate "missing" and "explicitly null."

**How to avoid:**
- `Description` is `string?` in `BaseEntity` (nullable).
- Validator rule: `RuleFor(x => x.Description).MaximumLength(2000).When(x => x.Description != null);` — no `.NotEmpty()`.
- Mapper: nullable property maps as-is; no transformation.
- Document: `null` and missing are equivalent for `Description`. Empty string `""` is invalid (validator should reject it; or normalize empty → null in service).

**Warning signs:**
- Round-trip GET → PUT with the same body returns 400.
- Database has both `NULL` and `''` values in the same column.

**Phase to address:**
**P5 — Validation & mapping base** (description rule consistent everywhere).

---

### Pitfall 37: Decimal precision in jsonb columns (future-proofing)

**What goes wrong:**
Not directly applicable to v1 (the project's only jsonb fields are `Schema.Definition` and `Assignment.Payload`, both arbitrary user JSON). But if/when domain DTOs are stored as jsonb later, .NET `decimal` round-trips through System.Text.Json as `Number` and Postgres jsonb stores it as JSON number (`numeric` precision via JSONB) — Postgres `numeric` has effectively unlimited precision, but Postgres jsonb stores as numeric internally and precision is preserved; but JSON parsers downstream may downgrade to IEEE-754 double, losing precision for >15-digit decimals.

**Why it happens:**
Cross-system serialization of numbers is hostile. Postgres preserves precision in jsonb; JavaScript clients silently truncate.

**How to avoid:**
- For v1, the user-supplied JSON in `Payload`/`Definition` is stored verbatim — no concern.
- **Document the rule**: if domain numeric data is ever added to jsonb-backed fields, serialize as strings, not numbers, to preserve precision.
- For Postgres `numeric` columns (none in v1), use EF Core's `HasPrecision(18, 6)` on `decimal` properties.

**Warning signs:**
- N/A for v1; flag for future milestones if money/quantity fields appear.

**Phase to address:**
**P7 — Entity build-out** (no current entity needs this; flag in PROJECT.md if added later).

---

### Pitfall 38: Async exception handling — `ExceptionDispatchInfo` swallowing original stack

**What goes wrong:**
Custom error middleware catches an exception, logs it, then either rethrows (losing original stack info if done via `throw ex;` instead of `throw;`) or wraps it in a new exception (losing inner context). Investigations later have stack traces pointing at the middleware, not the original failure site.

**Why it happens:**
`throw ex;` resets the stack. The fix is `throw;` or `ExceptionDispatchInfo.Capture(ex).Throw();` for cross-method preservation.

**How to avoid:**
- In the error middleware, **never** rethrow — handle and return a Problem Details response.
- Log with the full exception: `_logger.LogError(ex, "unhandled exception in {Path}", context.Request.Path);` — the `ex` parameter preserves the stack.
- If exceptions need to cross async boundaries while preserving stack, use `ExceptionDispatchInfo.Capture(ex)`.

**Warning signs:**
- Stack traces in logs all point to the middleware file/line.
- Inner exceptions missing or set to the wrapping exception.

**Phase to address:**
**P3 — Cross-cutting middleware.**

---

### Pitfall 39: Connection string secrets in `appsettings.json`

**What goes wrong:**
`appsettings.json` contains `"ConnectionStrings:Default": "Host=localhost;Username=steps;Password=devpassword"`. Committed to git. The dev password becomes the prod password through "we just changed the value but the structure stayed." Pen test finds it.

**Why it happens:**
The path of least resistance is to put the connection string in JSON. Many tutorials do this.

**How to avoid:**
- `appsettings.json` contains the connection string **shape** (host, db name) but **not** the password.
- Password supplied via:
  - Local dev: User Secrets (`dotnet user-secrets set "ConnectionStrings:Default" "..."`)
  - CI / prod: environment variable `ConnectionStrings__Default` (double underscore = config-section separator)
  - Or via `OTEL_EXPORTER_OTLP_ENDPOINT`-style explicit env vars
- Document the supply chain in `README.md`. Reject PRs containing secrets in committed JSON.
- Compose file uses `${POSTGRES_PASSWORD:?required}` so a missing var fails compose-up loudly.

**Warning signs:**
- `git log -p | grep -i password` finds anything.
- The same password works in dev and prod.

**Phase to address:**
**P0 — Repository scaffold** (User Secrets + .gitignore rules) and **P1 — Postgres + compose**.

---

## Technical Debt Patterns

Shortcuts that look reasonable in v1 but cost later.

| Shortcut | Immediate Benefit | Long-term Cost | When Acceptable |
|----------|-------------------|----------------|-----------------|
| `MigrateAsync()` on startup as the only deploy mechanism | Zero ops setup; single artifact | Multi-instance race risk; failed migrations cause restart loop; no rollback path | v1 single-instance only; document the upgrade path |
| Generic `Repository<T>` with no compile-time per-entity hooks | Less code per entity | Hides entity-specific logic in services; can leak `IQueryable<T>` shape to controllers if not careful | Always — but pair with explicit `Service<T>` per entity |
| 5 entities in one `AppDbContext` | Simpler DI; cross-entity FKs trivially | If a bounded context ever splits into its own service, every migration touches the shared schema | v1 by design; split via "AppDbContext partial classes" pattern if scaling |
| Hard delete only | Simpler to implement | Audit trail loss; "deleted" records can't be recovered; FK cascades are scarier | v1 explicitly out of scope (PROJECT.md); add soft-delete in `BaseEntity` later as a column + filter |
| No pagination on list endpoints | Simpler controllers | Will OOM the service when a table has 100k rows | v1 explicitly out of scope; add when first table approaches 1k rows |
| `(Name, Version)` not unique | Matches business rules; no schema work | Future searches by `(Name, Version)` may return multiples; consumers must handle | Per PROJECT.md decision; document for consumers |
| FK pre-validation deferred to DB | One round-trip; reliable | Error messages are less actionable to clients without good SQLSTATE mapping (Pitfall 14) | Per PROJECT.md decision; depends on the SQLSTATE mapping being thorough |
| No `Payload`-vs-`Schema` conformance check | Saves a JSON Schema dynamic-validation pass per Assignment write | Garbage payloads accepted silently | Per PROJECT.md decision (N2); add as opt-in feature later |
| Snake_case convention added later | "We'll convert when we need to" | Migration nightmare (Pitfall 4) | **Never** — add at first migration or never |
| Single shared `appsettings.json` with envs as overrides | One config to grok | Easy to leak prod values into dev or vice versa | Always, but enforce secrets-out via User Secrets / env vars |

---

## Integration Gotchas

| Integration | Common Mistake | Correct Approach |
|-------------|----------------|------------------|
| Postgres (Npgsql) | Storing local-time `DateTime` in `timestamptz` columns | Always UTC; document in `BaseEntity` |
| Postgres (Npgsql) | Pool size = 100 default, holding connections across non-DB I/O | Pool size ≤ 20 for API service; no I/O inside transactions |
| Postgres (Npgsql) | SQLSTATE codes mapped incompletely | Central `PostgresErrorMapper`; cover 23502/23503/23505/23514 explicitly |
| OTel Collector (OTLP) | Wiring `WithLogging` instead of `Logging.AddOpenTelemetry` | Use `builder.Logging.AddOpenTelemetry(...)` for MEL flow |
| OTel Collector (OTLP) | No filter on AspNetCore instrumentation → health probes flood metrics | Filter `/health*`, `/metrics`, `/swagger` paths |
| OTel Collector (OTLP) | Silent buffer drops when Collector is down | Enable self-diagnostics; document the buffer behavior |
| Docker Compose v2 | `depends_on` without `condition: service_healthy` | Use `pg_isready` healthcheck + `service_healthy` |
| Docker Compose | Anonymous volumes; data lost on `down -v` | Named volume `pgdata`; document `-v` warning |
| Docker Compose | Host port 5432 conflicts with local Postgres | Use `5433:5432` host mapping; document |
| FluentValidation | Using deprecated `FluentValidation.AspNetCore` auto-validation | Explicit `ValidateAsync` in controllers |
| Mapperly | `partial class` without `[Mapper]` attribute → no generated code | Standard template: `[Mapper] static partial class` |
| Mapperly | `UpdateDto` writes server fields silently | `[MapperIgnoreTarget]` + warnings-as-errors |
| JSON Schema validation | Draft mismatch | Pin draft 2020-12; reject mismatched `$schema` |
| Cron validation | Library disagreement with downstream consumer | Standardize on Cronos 5-field; document |
| Testcontainers | New container per test | Class/collection fixture + Respawn between tests |
| WebApplicationFactory | Config override too late for `AddDbContext` | Override `ConfigureAppConfiguration` with in-memory source |

---

## Performance Traps

Patterns that work for v1's expected scale but degrade with growth.

| Trap | Symptoms | Prevention | When It Breaks |
|------|----------|------------|----------------|
| `GET /entity` returns all rows | Slow list responses, OOM on huge tables | Pagination (deferred per PROJECT.md, but **add the pagination interface to base controller now** to defer impl, not interface) | ~10k rows |
| EF Core change tracker keeping every queried entity | Memory growth per request | Use `AsNoTracking()` in `Repository<T>.GetById` for read-only paths; tracking only when needed for updates | ~100 entities per request, persistently held |
| OTel batch buffer too large in memory-constrained containers | Container OOM-killed | Set explicit `MaxQueueSize` (Pitfall 31) | When Collector latency rises under load |
| Health probe hitting DB every second | Connection pool partly burned by readiness checks | Cache readiness result for 5s (`AddCheck` with `failureStatus + caching` pattern) | Probe frequency × replicas > available connections |
| Validator instantiation per request | GC pressure | Transient lifetime is fine; FluentValidation validators are cheap to construct | Doesn't break; non-issue |
| `_dbContext.Set<T>().Where(...).ToList()` without `.AsNoTracking()` on hot paths | Slow reads, memory growth | Audit each read path; tracking off by default in `Repository.GetAll` | ~1k entities per request |
| Hot-loop calls to `AppDbContext` in the same request without `await using` | Connection leaks | Standard EF patterns with `using` block; never inject `AppDbContext` into singletons | Any | 

---

## Security Mistakes

Domain-specific issues beyond OWASP basics. (Auth itself is out of scope per PROJECT.md, but other security concerns apply.)

| Mistake | Risk | Prevention |
|---------|------|------------|
| Server-controlled fields in `CreateDto`/`UpdateDto` (Pitfall 33) | Forged audit trail; client-chosen IDs | Strict DTO conventions; Mapperly warnings as errors |
| Stack traces in error responses (Pitfall 13) | Information disclosure | Always sanitize `Detail`; log full stack server-side only |
| Connection-string secrets in committed `appsettings.json` (Pitfall 39) | Credential exposure in git history | User Secrets in dev; env vars in prod; CI scans for secrets |
| `Schema.Definition` being executed by a downstream tool | If any consumer evaluates the JSON Schema as code (e.g., loading remote `$ref`), SSRF possible | Document: this service does **not** dereference remote `$ref`s; validator config disables network access |
| `Assignment.Payload` size unbounded | DoS via 1 GB payload | Set `Kestrel.Limits.MaxRequestBodySize` + explicit max length validator on `Payload` (e.g., 1 MB) |
| `CronExpression` evaluated by external scheduler | A malicious cron could starve the scheduler if scheduler doesn't validate | Validate cron at the API boundary even though the scheduler is the executor; cap "max iterations per second" downstream |
| `X-Correlation-Id` accepted unfiltered | Log injection via newlines / huge ids | Validate as `^[A-Za-z0-9\-]{1,64}$` in the correlation middleware; reject malformed and generate fresh |
| Postgres user has too many privileges | Migrations succeed because the API user has DDL; runtime SQLi (theoretical) could DROP tables | Document migration-user vs runtime-user split as a future enhancement; v1 single user is acceptable but flag for prod hardening |
| Verbose Problem Details leaking entity field names client doesn't own | Schema enumeration | Map errors using stable, public field names — not raw DB column names |

---

## UX Pitfalls

Domain UX concerns (for API consumers — the orchestrator/scheduler is the "user").

| Pitfall | User Impact | Better Approach |
|---------|-------------|-----------------|
| Inconsistent error shape between model-binding and FluentValidation (Pitfall 34) | Two ways to parse errors | Unified RFC 7807 with `errors` map + `correlationId` for all 400s |
| Missing `correlationId` in error responses | Operators can't grep logs from a client-reported issue | Mandatory in every error body (Pitfall 13) |
| Vague Postgres-derived error details ("FK violation") | Consumer can't identify which FK failed | Parse `PostgresException.ConstraintName` → human-readable field (Pitfall 14) |
| OpenAPI spec mismatches actual response shape | Consumer code-gen breaks | Generate OpenAPI from the API itself (built-in in .NET 8) and snapshot in CI |
| `null` vs missing `Description` causing 400 (Pitfall 36) | Round-trip GET → PUT fails | Validator treats `null` and missing as equivalent |
| `CronExpression` validation differs from external scheduler's | Workflows accepted by API fail at scheduler time | Validate using the same library the scheduler uses (Pitfall 21) |
| List endpoint returns 10k items in one response | Slow / OOM clients | Pagination interface in base; per PROJECT.md, full impl deferred but `GET ?limit=100` returning first N is a cheap precaution |

---

## "Looks Done But Isn't" Checklist

Things that appear complete but are missing critical pieces.

- [ ] **Audit interceptor:** Verify `UpdatedAt` actually updates on UPDATE (only `CreatedAt` is set on insert — many implementations get this wrong).
- [ ] **Audit interceptor:** Verify `CreatedBy`/`UpdatedBy` are populated when `HttpContext.User.Identity.Name` is available (auth out of scope, but the wiring should exist for when auth is added).
- [ ] **Audit interceptor:** Verify `Kind == Utc` on every `DateTime` it sets (Pitfall 1).
- [ ] **Correlation middleware:** Verify the id appears in **error** responses, not just success responses.
- [ ] **Correlation middleware:** Verify the id flows through `Task.Run`-style fire-and-forget (Pitfall 11).
- [ ] **Health probes:** Verify three distinct endpoints respond correctly under DB-down, DB-blip, and DB-up scenarios (Pitfall 15).
- [ ] **OTel logs:** Verify `IncludeFormattedMessage = true` and that a real log line arrives at the Collector with the formatted text (Pitfall 8).
- [ ] **OTel logs:** Verify `Logging:LogLevel:Default = "Warning"` actually filters out Information at the exporter (Pitfall 9).
- [ ] **OTel metrics:** Verify `/health` traffic is filtered out (Pitfall 10).
- [ ] **Problem Details:** Verify 500 responses do not contain stack traces, file paths, or "Exception" text (Pitfall 13).
- [ ] **Problem Details:** Verify FK violation → 422, unique → 409, not-null → 400 with correct field names (Pitfall 14).
- [ ] **Migrations:** Verify the startup log lines (`migrations.start` / `migrations.complete`) appear in OTel logs.
- [ ] **Migrations:** Verify `__EFMigrationsHistory` columns are intact after first migration (Pitfall 4).
- [ ] **FluentValidation:** Verify `FluentValidation.AspNetCore` is **NOT** in the dependency tree (Pitfall 7).
- [ ] **Mapperly:** Verify the build emits no `MP0001`/`MP0011`/`MP0020`/`MP0021` warnings (or fails on them per Pitfall 16).
- [ ] **DTOs:** Verify `CreateDto` and `UpdateDto` do NOT contain `Id`/`CreatedAt`/`CreatedBy`/`UpdatedAt`/`UpdatedBy` (Pitfall 33).
- [ ] **DTOs:** Verify a POST with `{"id": ..., "createdBy": ...}` ignores those fields (test).
- [ ] **Postgres compose:** Verify `pg_isready` healthcheck passes before the API tries to connect (Pitfall 24).
- [ ] **Postgres compose:** Verify the API container connects to `Host=postgres;Port=5432` (internal), not the host port (Pitfall 25).
- [ ] **Postgres compose:** Verify named volume `pgdata` persists across `docker compose down` (Pitfall 26).
- [ ] **Concurrency (xmin):** Verify a concurrent PUT actually returns 409 (Pitfall 6).
- [ ] **Validators:** Verify SemVer regex matches the documented draft (strict triple vs full SemVer 2.0) (Pitfall 18).
- [ ] **Validators:** Verify SHA-256 regex accepts mixed case AND normalization happens before persist (Pitfall 19).
- [ ] **Validators:** Verify JSON Schema validation correctly identifies draft (Pitfall 20).
- [ ] **Validators:** Verify cron validation matches the format documented to consumers (Pitfall 21).
- [ ] **Tests:** Verify Testcontainers + WebApplicationFactory + Respawn pattern is used (Pitfalls 28, 29).
- [ ] **Tests:** Verify the test suite runs in under 60s for the v1 entity count.
- [ ] **Secrets:** Verify `appsettings.json` contains no passwords; verify CI fails if it does (Pitfall 39).
- [ ] **Junctions:** Verify cascade behavior per FK is intentional, not defaulted (Pitfall 35).

---

## Recovery Strategies

When pitfalls occur despite prevention, the cost of recovery.

| Pitfall | Recovery Cost | Recovery Steps |
|---------|---------------|----------------|
| Snake_case convention added after first migration (Pitfall 4) | HIGH | Manual ALTER scripts to snake_case existing columns; re-baseline migrations history; coordinate downtime |
| Non-UTC DateTime stored in `timestamptz` (Pitfall 1) | MEDIUM | UPDATE columns to `AT TIME ZONE 'UTC'`; deploy fixed AuditInterceptor; verify in next migration |
| FluentValidation.AspNetCore in dependency tree (Pitfall 7) | LOW | Remove package; convert auto-validation sites to explicit `ValidateAsync`; usually < 1 day |
| Mapperly generates nothing (Pitfall 16) | LOW | Fix project setup (`PrivateAssets="all"` or correct package reference); rebuild |
| Server-controlled fields in DTOs (Pitfall 33) | MEDIUM | Audit all DTOs; deploy fix; any data created with forged audits stays corrupted unless re-derived from logs |
| Stack traces leaking (Pitfall 13) | LOW | Patch error middleware to sanitize; deploy; assess any exploit window |
| Connection pool exhaustion (Pitfall 23) | MEDIUM | Identify the holding code path; refactor to release connections; tune pool size; document the rule |
| Liveness probe killing pods during DB blip (Pitfall 15) | LOW | Change probe wiring to use `live` tag only; redeploy |
| Migrations race-loop across instances (Pitfall 3) | MEDIUM | Move migrations to a pre-deploy job; remove from API startup; ship as separate container |
| Hard-coded passwords in git (Pitfall 39) | HIGH | Rotate credentials in all environments; rewrite git history (controversial); audit downstream systems; assume compromise |
| Wrong cron library accepting strings the scheduler rejects (Pitfall 21) | LOW | Replace validator library; reject existing bad records via background reconciliation if scheduler can't handle them |
| Cascading delete destroying workflow references (Pitfall 35) | HIGH | Backfill from backups or audit logs; switch DELETE policy to `Restrict`; introduce soft-delete |

---

## Pitfall-to-Phase Mapping

How roadmap phases should address these pitfalls.

| # | Pitfall | Prevention Phase | Verification |
|---|---------|------------------|--------------|
| 1 | Non-UTC DateTime → timestamptz | **P2** EF Core base infra | Unit test: round-trip `BaseEntity` through DbContext, assert `Kind == Utc` |
| 2 | DbContext lifetime | **P2** + **P6** | DI registration review; load test for "second operation" exception |
| 3 | Migrations race / restart loop | **P2** + **P4** | Log lines `migrations.start/complete` visible in OTel; startup probe gated on completion |
| 4 | NamingConventions mangles `__EFMigrationsHistory` | **P2** | First migration generated after convention is wired; manual inspection of `__EFMigrationsHistory` schema |
| 5 | M2M junction misconfig | **P2** + **P7** | Per-junction unit test: add/remove/re-add yields single row |
| 6 | xmin concurrency token | **P2** + **P3** | Integration test: concurrent PUT returns 409 |
| 7 | FluentValidation.AspNetCore deprecated | **P5** | Dependency tree contains only `FluentValidation` + `FluentValidation.DependencyInjectionExtensions` |
| 8 | OTel logger provider wiring | **P4** | Smoke test: `ILogger<T>` log appears in OTLP export |
| 9 | LogLevel filter not effective | **P4** | Test with `Default = "Warning"`; assert Information log not exported |
| 10 | Health probes spam metrics | **P4** | After 100 probe hits, exported HTTP metric count for `/health` is 0 |
| 11 | Correlation id lost across async | **P3** | Test: id present in logs from a `Task.Run` continuation |
| 12 | Correlation middleware ordering | **P3** | `AddBaseApi(...)` enforces order; integration test of exception path includes id |
| 13 | Stack traces in 500 responses | **P3** | Endpoint that throws; assert response body has no "Exception"/path text |
| 14 | Postgres SQLSTATE mapping | **P3** | Per-code integration test (23503, 23505, 23502, 23514) |
| 15 | Liveness probe checks DB | **P4** | Three-endpoint test under DB-up / DB-blip / DB-down scenarios |
| 16 | Mapperly project setup | **P5** | Inspect `obj/.../generated`; CI fails on MP* warnings |
| 17 | Mapper writes server-controlled fields | **P5** + **P7** | Unit test: `ApplyUpdate` does not modify `Id`/`CreatedAt`/`CreatedBy` |
| 18 | SemVer regex | **P5** | Validator table-test covering accepted/rejected SemVer strings |
| 19 | SHA-256 case | **P5** + **P7** | Unit test: uppercase and lowercase both accepted; uniqueness on lowercase only |
| 20 | JSON Schema draft | **P5** + **P7** | Validator test against known-good draft-2020-12 schemas; rejects draft-07-only constructs (or vice versa per choice) |
| 21 | Cron library mismatch | **P5** + **P7** | Cron validation matches documentation; cross-check against scheduler's library if known |
| 22 | STJ strict JSON | **P5** + **P7** | Validator rejects payloads with `//` comments and trailing commas |
| 23 | Connection pool starvation | **P1** + **P6** | Pool size set in connection string; code review forbids non-DB I/O inside transactions |
| 24 | Compose `depends_on` healthcheck | **P1** | `docker compose up` from cold start: API doesn't error on connection |
| 25 | Host port 5432 conflict | **P1** | README documents `5433:5432`; compose uses non-default port |
| 26 | Volume persistence | **P1** | Named `pgdata` volume; README documents `-v` semantics |
| 27 | Locale/encoding | **P1** | `POSTGRES_INITDB_ARGS` pinned; image tag pinned |
| 28 | Testcontainers per-test | **P8** | Test suite runs in <60s; `docker ps` shows ≤1 container during run |
| 29 | WebAppFactory config override | **P8** | Tests connect to Testcontainers DB, not dev DB |
| 30 | `appsettings.json` comments | **P0** | Convention documented; no comments in JSON; CI lints |
| 31 | OTLP exporter silent drops | **P4** | Self-diagnostics enabled; explicit batch options |
| 32 | Unbounded metric cardinality | **P4** | Documented rule; AspNetCore instrumentation uses route templates |
| 33 | DTO server-controlled fields | **P5** + **P7** | Per-entity unit test: POSTing `{id, createdBy}` ignores those fields |
| 34 | `[ApiController]` vs FluentValidation error shape | **P5** | `InvalidModelStateResponseFactory` aligned with custom shape; correlation id present in both |
| 35 | Cascade delete unexpected | **P7** | Per-FK explicit `OnDelete()` call; integration test of DELETE under FK references |
| 36 | `null` Description round-trip | **P5** | Round-trip test: GET → PUT same body succeeds |
| 37 | Decimal precision in jsonb | (future) | Flag in PROJECT.md if numeric data is added to jsonb |
| 38 | `throw ex` losing stack | **P3** | Code review rule; static analyzer rule (CA2200) enabled |
| 39 | Secrets in `appsettings.json` | **P0** + **P1** | User Secrets in dev; compose uses `${POSTGRES_PASSWORD:?required}`; CI secret scan |

---

## Sources

- Npgsql DateTime/UTC breaking change: [Date and Time Handling — Npgsql Documentation](https://www.npgsql.org/doc/types/datetime.html) — HIGH confidence (official docs)
- Npgsql DateTimeOffset breaking change: [Issue #2108 — npgsql/efcore.pg](https://github.com/npgsql/efcore.pg/issues/2108) — HIGH confidence
- FluentValidation.AspNetCore deprecation: [Issue #1960 — Deprecation of the FluentValidation.AspNetCore package](https://github.com/FluentValidation/FluentValidation/issues/1960) and [FluentValidation 12 Upgrade Guide](https://docs.fluentvalidation.net/en/latest/upgrading-to-12.html) — HIGH confidence
- OpenTelemetry .NET logging wiring: [Getting started with logs — ASP.NET Core](https://opentelemetry.io/docs/languages/dotnet/logs/getting-started-aspnetcore/) and [WithLogging() discussion #4653](https://github.com/open-telemetry/opentelemetry-dotnet/discussions/4653) — HIGH confidence
- Mapperly existing target object: [Existing target object — Mapperly docs](https://mapperly.riok.app/docs/configuration/existing-target/) and [Mapper configuration](https://mapperly.riok.app/docs/configuration/mapper/) — HIGH confidence
- EF Core migration race + advisory locks: [Issue #34439 — efcore migration lock](https://github.com/dotnet/efcore/issues/34439) and [The Reformed Programmer — safely apply EF Core migrate on startup](https://www.thereformedprogrammer.net/how-to-safely-apply-an-ef-core-migrate-on-asp-net-core-startup/) — MEDIUM confidence (well-known pattern, version-specific details may shift)
- EFCore.NamingConventions pitfalls: [Issue #1](https://github.com/efcore/EFCore.NamingConventions/issues/1), [#289](https://github.com/efcore/EFCore.NamingConventions/issues/289), [#300](https://github.com/efcore/EFCore.NamingConventions/issues/300) — HIGH confidence
- Testcontainers + WebApplicationFactory + Respawn: [Testcontainers + Respawn for integration testing](https://medium.com/@hiddenhenry/simple-integration-testing-in-net-with-respawn-testcontainers-39f5de21740c) and [Testing an ASP.NET Core web app](https://testcontainers.com/guides/testing-an-aspnet-core-web-app/) — HIGH confidence
- OTel health-check filtering: [Filtering Telemetry in .NET](https://blog.marcelmichau.dev/filtering-telemetry-in-net), [Issue #3420 — open-telemetry/opentelemetry-dotnet](https://github.com/open-telemetry/opentelemetry-dotnet/issues/3420) — HIGH confidence
- RFC 7807 Problem Details, ASP.NET Core: training-data + verified pattern against ASP.NET Core 8 docs — HIGH confidence

---
*Pitfalls research for: .NET 8 Web API + EF Core 8 + Npgsql + OTel + FluentValidation + Mapperly + Postgres + RFC 7807*
*Researched: 2026-05-26*
