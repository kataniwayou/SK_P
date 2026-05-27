# Phase 4: Cross-Cutting Middleware + Error Handling - Context

**Gathered:** 2026-05-27
**Status:** Ready for planning

<domain>
## Phase Boundary

Wire the cross-cutting HTTP plumbing in `BaseApi.Core` + `BaseApi.Service` so that:
1. Every request carries an `X-Correlation-Id` (read from header if present, else generated as `Guid.NewGuid().ToString("N")`) — propagated to `HttpContext.Items["CorrelationId"]`, ASP.NET Core log scope via `ILogger.BeginScope`, and echoed on every response header (2xx / 4xx / 5xx).
2. Every non-2xx response returns RFC 7807 Problem Details JSON (`application/problem+json`) with `correlationId` and `instance` extensions populated.
3. Postgres `SQLSTATE 23503` (FK violation) maps to HTTP 422 with the offending FK column name in `detail`; `SQLSTATE 23505` (unique violation) maps to HTTP 409 with the offending field name.
4. EF Core `DbUpdateConcurrencyException` (the `xmin` lost-update path from Phase 3 D-03 / D-03a) maps to HTTP 409 with a generic "resource was modified" message (no `xmin` value leaked).
5. Custom `NotFoundException` thrown by Services maps to HTTP 404 with `resourceType` + `id` surfaced in `detail` and `Extensions`.
6. `FluentValidation.ValidationException` maps to HTTP 400 with a field-level `errors` map.
7. `[ApiController]`'s built-in model-binding 400 returns the SAME Problem Details shape (no divergent error formats — closes ERROR-10).
8. Any other unhandled exception maps to HTTP 500 with a generic body; the full stack trace is logged only (never leaked to the client).

Phase 4 lands `Program.cs` middleware wiring + DI registration + the handler/middleware/mapper types in `BaseApi.Core`. Verification proves the contracts via `WebApplicationFactory<Program>` + per-test-class throwaway Postgres DBs (D-15 pattern from Phase 3), exercising all 5 ROADMAP SCs plus the D-03a lost-update test.

**Out of this phase (cross-phase to-do tracking):**
- OpenTelemetry wiring (`AddOpenTelemetry`, OTLP exporter, `WithLogging` etc.) — Phase 5 (`OBSERV-01..08, OBSERV-12`)
- Health probes (`/health/live`, `/health/ready`, `/health/startup`) and `AspNetCore.HealthChecks.NpgSql` — Phase 5 (`HEALTH-01..05`)
- `services.AddFluentValidation()` and per-entity validators — Phase 6 (Phase 4 only ships the `ValidationException` → 400 mapping defensively + adds the package reference; nothing constructs a validator yet)
- Concrete `BaseController<…>`, `AppDbContext`, controllers, migrations — Phase 7/8 (Phase 4's Program.cs wiring uses an inert `app.MapControllers()` against a still-empty controller set; the wiring works the moment Phase 7 adds the first controller)
- `AddBaseApi(...)` / `UseBaseApi(...)` composition-root extensions — Phase 7 (Phase 4 wires middleware directly in `Program.cs`; Phase 7 will refactor into the extension methods without changing behavior)

</domain>

<decisions>
## Implementation Decisions

### Middleware pipeline + correlation generation

- **D-01:** Pipeline order in `Program.cs`:
  ```
  app.UseExceptionHandler();   // catches everything below
  app.UseCorrelationId();      // populates HttpContext.Items["CorrelationId"] + ILogger.BeginScope
  app.UseRouting();
  app.MapControllers();
  app.Run();
  ```
  Rationale: `UseExceptionHandler` wraps a try/catch around the rest of the pipeline. `CorrelationIdMiddleware` runs *inside* that wrapper so when an endpoint throws, the `IExceptionHandler` chain sees the already-populated `HttpContext.Items["CorrelationId"]`. `CorrelationIdMiddleware` itself only calls `Guid.NewGuid()` + a TryGetValue on the request header — it cannot throw under any realistic condition, so the small "outside the safety net" window is acceptable. This is the .NET 8 community-standard ordering (matches Microsoft Learn samples).

- **D-02:** Correlation ID format = `Guid.NewGuid().ToString("N")` — 32-char lowercase hex, no dashes (e.g. `5f3e8c4a9b2d4e1f8c7b6a5d4e3f2a1b`). Rationale chosen at discuss time: keeps correlation IDs visually distinct from entity Ids (which always render in canonical `"D"` 8-4-4-4-12 dash format via System.Text.Json's hardcoded Guid serialization). Easier log grep; catches accidental cross-paste of correlation ID into an entity Id field. Inbound `X-Correlation-Id` values are echoed verbatim regardless of format (only validated as non-empty, length < 128, ASCII-printable to prevent log injection).

- **D-03:** `CorrelationIdMiddleware` lives at `src/BaseApi.Core/Middleware/CorrelationIdMiddleware.cs`. Behavior per request:
  1. `var corrId = Request.Headers.TryGetValue("X-Correlation-Id", out var h) && IsValid(h) ? h.ToString() : Guid.NewGuid().ToString("N");`
  2. `HttpContext.Items["CorrelationId"] = corrId;`
  3. `Response.OnStarting(() => { Response.Headers["X-Correlation-Id"] = corrId; return Task.CompletedTask; });`
  4. `using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = corrId });` then `await _next(context);`
  
  `IsValid(h)` = non-empty, length ≤ 128, ASCII-printable only. BeginScope key name is the literal `"CorrelationId"` (PascalCase) — matches what Phase 5's OTel `IncludeScopes = true` will surface as a log attribute without renaming.
  
  Registered via `app.UseMiddleware<CorrelationIdMiddleware>()` (no extension method in Phase 4 — Phase 7 may wrap in `app.UseBaseApi()`).

### Problem Details shaping

- **D-04:** ProblemDetails extensions injection uses the .NET 8 modern pattern:
  ```csharp
  services.AddProblemDetails(opts => opts.CustomizeProblemDetails = ctx =>
  {
      var corrId = ctx.HttpContext.Items["CorrelationId"] as string;
      if (corrId is not null) ctx.ProblemDetails.Extensions["correlationId"] = corrId;
      ctx.ProblemDetails.Instance = ctx.HttpContext.Request.Path;
  });
  ```
  This callback runs for ProblemDetails emitted by:
  - Our `IExceptionHandler` chain (when they call `IProblemDetailsService.WriteAsync`)
  - The framework's built-in 400 (model binding) / 404 (no route match) / 500 paths when `AddProblemDetails` is registered
  - The `[ApiController]` `InvalidModelStateResponseFactory` (closes ERROR-10) — provided we use the default factory (which emits ProblemDetails through the same service) instead of a custom one
  
  No per-handler boilerplate. Single source of truth for the `correlationId` + `instance` extension fields. Closes ERROR-08 + ERROR-09.

- **D-05:** Final Problem Details JSON shape on a typical error response:
  ```json
  {
    "type": "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.21",
    "title": "Unprocessable Entity",
    "status": 422,
    "detail": "Foreign key violation: input_schema_id references a non-existent Schema.",
    "instance": "/processors",
    "correlationId": "5f3e8c4a9b2d4e1f8c7b6a5d4e3f2a1b",
    "errors": { "Version": ["Version must be SemVer..."] }   // present only on 400 (FluentValidation / model binding)
  }
  ```
  Field-level `errors` map is set only by ValidationExceptionHandler + the [ApiController] model-binding path. All other handlers emit `type` / `title` / `status` / `detail` + the customizer's `instance` + `correlationId`.

### IExceptionHandler chain (.NET 8 modern pattern)

- **D-06:** Multiple `IExceptionHandler` classes registered via `services.AddExceptionHandler<T>()` called N times — walked in registration order, each returns `true` to claim or `false` to pass. Registration order (this IS load-bearing — same reason ROADMAP marks Phase 4 non-parallelizable):
  1. `NotFoundExceptionHandler` — claims `NotFoundException` → 404
  2. `ValidationExceptionHandler` — claims `FluentValidation.ValidationException` → 400
  3. `DbUpdateExceptionHandler` — claims `DbUpdateException` and all subtypes including `DbUpdateConcurrencyException`; uses `PostgresExceptionMapper` (D-08) for SQLSTATE branching
  4. `FallbackExceptionHandler` — claims everything else → 500, logs full exception (incl. stack) at `LogLevel.Error`, body never carries stack trace (ERROR-07)
  
  Each handler lives at `src/BaseApi.Core/Exceptions/Handlers/{Name}.cs`. Each is < 50 lines and independently unit-testable. When Phase 6 / Phase 8 add new handlers (e.g., schema-validation errors for `Schema.Definition` jsonb), they slot in at the correct precedence point without editing existing files.

- **D-07:** `NotFoundException` lives at `src/BaseApi.Core/Exceptions/NotFoundException.cs`. Constructor shape:
  ```csharp
  public sealed class NotFoundException : Exception
  {
      public string ResourceType { get; }
      public object Id { get; }
      public NotFoundException(string resourceType, object id)
          : base($"{resourceType} with id '{id}' was not found.")
      {
          ResourceType = resourceType;
          Id = id;
      }
  }
  ```
  Services throw `throw new NotFoundException("Schema", id)` (Phase 7/8 service code). `NotFoundExceptionHandler` reads both properties and shapes `ProblemDetails.detail` from `Message` AND surfaces `Extensions["resourceType"]` + `Extensions["resourceId"]` for clients that want to branch on them programmatically.

### Postgres exception mapping

- **D-08:** Static helper at `src/BaseApi.Core/Persistence/Exceptions/PostgresExceptionMapper.cs`:
  ```csharp
  public static bool TryMap(DbUpdateException ex, out int httpStatus, out string detail, out string? columnName);
  ```
  Behavior:
  1. Walks `ex.InnerException as PostgresException` (Npgsql.PostgresException). Returns `false` if the inner isn't a PostgresException (caller falls back to FallbackExceptionHandler 500).
  2. Switches on `pgEx.SqlState`:
     - `23503` (FK violation) → `httpStatus = 422`, extracts FK column name from `pgEx.ConstraintName` via the ERROR-11 convention regex `^fk_[a-z0-9]+_(?<col>[a-z0-9_]+?)(_id)?$` (column name is the named group), `detail = $"Foreign key violation: {col} references a non-existent {target}."`
     - `23505` (unique violation) → `httpStatus = 409`, extracts column name from `pgEx.ConstraintName` via `^uq_[a-z0-9]+_(?<col>[a-z0-9_]+)$`, `detail = $"Unique constraint violation: {col} already exists."`
     - any other SqlState → `false` (caller falls back to FallbackExceptionHandler 500; the stack is still logged so we discover unmapped SQLSTATEs naturally)
  3. Returns the extracted `columnName` so the handler can also stash it in `ProblemDetails.Extensions["field"]` for client convenience.
  
  The regex tolerates the missing `_id` suffix because not all FKs end in `_id` (some FK columns reference natural keys). Unit-testable in isolation without HTTP / DI / DbContext.

- **D-09:** `DbUpdateExceptionHandler` early-return branch for the D-03a / xmin path:
  ```csharp
  if (ex is DbUpdateConcurrencyException)
  {
      var problem = new ProblemDetails {
          Status = 409, Title = "Conflict",
          Detail = "The resource was modified by another request; reload and retry."
      };
      await problemDetailsService.WriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem });
      return true;
  }
  ```
  `DbUpdateConcurrencyException` IS-A `DbUpdateException` (EF Core inheritance), so the single handler catches both. `xmin` value is NOT exposed (internal Postgres detail — information-disclosure guard). One handler, one test class, zero chain-order dependency. Phase 3 → Phase 4 D-03a closure.

### Cross-phase shape (defensive landings)

- **D-10:** `FluentValidation.ValidationException` mapping ships in Phase 4 even though FluentValidation isn't wired into request pipelines until Phase 6. Concretely:
  - Add `<PackageReference Include="FluentValidation" />` to `BaseApi.Core.csproj` (CPM pin lands in `Directory.Packages.props` per the Phase 1 D-05 convention)
  - `ValidationExceptionHandler` claims the typed exception and shapes ProblemDetails with `errors` map built from `ex.Errors.GroupBy(e => e.PropertyName).ToDictionary(...)`
  - Phase 4 verification throws a `ValidationException` directly from a test endpoint (`app.MapGet("/test/validation-error", () => throw new ValidationException(...))`) — no validator construction needed
  - Phase 6 just calls `.AddFluentValidation()` and adds per-entity validators; the mapping is already live
  
  Closes ERROR-03 in Phase 4 without scope creep into Phase 6.

- **D-11:** `[ApiController]` `InvalidModelStateResponseFactory` is configured in `Program.cs` in Phase 4. Concretely: leave the default factory in place (do NOT override) but ensure `AddProblemDetails` is registered so the default factory emits Problem Details (it auto-detects). Verification adds a test controller in `BaseApi.Tests/Endpoints/TestController.cs` with a `[Required]` DTO field and asserts a malformed body produces the same Problem Details shape (with `errors` map + `correlationId`) as the FluentValidation 400 path. Closes ERROR-10 in Phase 4 — no defer-debt for Phase 7.
  
  Subtlety: the default factory uses status 400 by default for model-binding errors. We do NOT customize it; we just ride on the default + `AddProblemDetails`. This means the Phase 4 verification can use a real `[ApiController]` test controller because by then `app.MapControllers()` is already wired.

- **D-12:** Stack-trace logging (ERROR-07): `FallbackExceptionHandler` calls `logger.LogError(ex, "Unhandled exception on {Path}", httpContext.Request.Path)`. The exception object passed as the first argument auto-serializes the full stack into the structured log via MEL. The response body has only `title = "Internal Server Error"` + `status = 500` + the customizer's `correlationId` + `instance` — never the exception type, message, or stack. Phase 5's OTel export inherits this automatically since MEL is the single sink path (OBSERV-06).

- **D-13:** Phase 4 does NOT introduce OpenTelemetry. `CorrelationIdMiddleware` uses plain `ILogger.BeginScope` and `Response.OnStarting` — no `Activity` tagging, no `ActivitySource`. Phase 5 will add `Activity.Current?.AddTag("correlation.id", corrId)` inside the same middleware (small additive change) when OTel lands. Phase 4 keeps the diff small and avoids half-instrumenting OTel.

### Verification strategy

- **D-14:** Plan structure (Phase 3 pattern, matches user preference):
  - **04-01-PLAN.md** (autonomous: true) — Build + wire:
    - Add `FluentValidation` + `Npgsql` (already referenced via `Npgsql.EntityFrameworkCore.PostgreSQL` in Phase 3 — confirm `Npgsql` types are reachable; if not, add explicit pin) to `Directory.Packages.props` and `BaseApi.Core.csproj` (CPM contract: zero `Version=` attributes)
    - Create `CorrelationIdMiddleware.cs` (D-03)
    - Create `NotFoundException.cs` (D-07)
    - Create `PostgresExceptionMapper.cs` (D-08) — pure helper, unit-testable
    - Create 4 `IExceptionHandler` classes (D-06): NotFound / Validation / DbUpdate / Fallback
    - Edit `src/BaseApi.Service/Program.cs`: register handlers (in order D-06), `AddProblemDetails` (D-04), `UseExceptionHandler` + `UseMiddleware<CorrelationIdMiddleware>` (D-01)
    - Build green Release + Debug 0/0 warnings (Phase 1 D-02 enforced)
  - **04-02-PLAN.md** (autonomous: false, checkpoint per Phase 3 D-18) — Verification battery:
    - Add 2 test fixture files: `WebAppFactory.cs` (wraps `WebApplicationFactory<Program>` with seam to register test endpoints) + `PostgresFixture.cs` (lifts Phase 3's IAsyncLifetime throwaway DB pattern; D-15 cleanup discipline preserved)
    - Add 1 test controller: `tests/BaseApi.Tests/Endpoints/TestController.cs` — `[ApiController] [Route("test")]` with deliberately-throwing endpoints for each handler path (`GET /test/not-found`, `POST /test/validation-error` with `[Required]` DTO, `POST /test/fk-violation`, `POST /test/unique-violation`, `POST /test/concurrency`, `GET /test/unhandled`)
    - Add ~6 fact-test files (one per error category) under `tests/BaseApi.Tests/Middleware/`:
      - `CorrelationIdTests.cs` — 2xx / 4xx / 5xx all echo header; supplied header is echoed verbatim; missing header generates valid 32-char hex (SC#1)
      - `ValidationErrorTests.cs` — `ValidationException` AND `[ApiController]` model-binding both produce Problem Details with `errors` map + matching `correlationId` (SC#2 + SC#5)
      - `SqlStateMappingTests.cs` — FK violation against a real Postgres table → 422 with column in detail; unique violation → 409 with column (SC#3)
      - `NotFoundAndUnhandledTests.cs` — `NotFoundException` → 404 with resource type + id; unhandled exception → 500 with generic message and NO stack in body, full stack in logs (SC#4)
      - `ConcurrencyTokenTests.cs` — Two PUTs racing on same row → second returns 409 with the D-09 detail string (D-03a Phase 3 carry-forward)
      - `ProblemDetailsExtensionsTests.cs` — every error response includes `correlationId` AND `instance` extension fields populated correctly (ERROR-08 / ERROR-09 regression guard)
    - Run `dotnet test`, take BEFORE/AFTER `psql \l` snapshots (D-15), commit one `docs(04-02): ...` SUMMARY with GREEN/RED grid for all 5 SCs + D-03a + ERROR-08/09 regression coverage
    - Source code in `src/BaseApi.Core/` and `src/BaseApi.Service/` is NOT touched here unless a verification surfaces a defect (then `fix(04-01): ...` fix-forward per Phase 1/2/3 convention)
  
  Phase 4 ROADMAP says non-parallelizable; that applies WITHIN a plan (middleware order is load-bearing) — splitting into a build plan and a verify plan is orthogonal and lifts the proven Phase 3 cadence (checkpoint catches deviations like the Phase 3 xUnit1051 fix).

### Claude's Discretion (small choices deferred to research / planning)

- BeginScope key naming and shape: `Dictionary<string, object> { ["CorrelationId"] = ... }` is the recommended default (D-03), but planner may use a `KeyValuePair<string, object>` or a typed log-scope helper if research surfaces a cleaner .NET 8 idiom that still surfaces `CorrelationId` as an OTel log attribute under Phase 5's `IncludeScopes = true`.
- Test endpoint location: Phase 4 verification adds `TestController.cs` under `tests/BaseApi.Tests/Endpoints/`. If WebApplicationFactory's `ConfigureWebHost` + `services.AddMvc().AddApplicationPart(...)` is cleaner for picking up the test controller assembly, planner may relocate.
- Exact regex for ERROR-11 constraint name parsing (D-08): the recommended patterns above (`^fk_[a-z0-9]+_(?<col>[a-z0-9_]+?)(_id)?$` and `^uq_[a-z0-9]+_(?<col>[a-z0-9_]+)$`) are a starting point. Researcher should validate against the EXACT Phase 8 constraint names locked in REQUIREMENTS.md PERSIST and ENTITY sections; adjust the regex if Phase 8's conventions differ.
- Stack-trace logging level: `LogError` (D-12) is the default; planner may downgrade specific exception types (e.g., expected `OperationCanceledException` from client disconnects) to `LogDebug` if research surfaces standard practice. The 500 ProblemDetails body must stay clean either way.
- Test endpoint mechanism inside `WebApplicationFactory<Program>` (test controller via assembly part, vs. Minimal API stubs in `ConfigureTestServices`): pick whichever the .NET 8 testing docs recommend; both satisfy the verification contract.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Project-level locks
- `.planning/PROJECT.md` — "Out of Scope" (auth/authz), "Error responses" section (RFC 7807, SQLSTATE 23503/23505 mappings, correlationId requirement), Key Decisions table
- `.planning/REQUIREMENTS.md` § Error Handling (ERROR-01..11), § Observability (OBSERV-09, OBSERV-10, OBSERV-11) — the 14 in-scope requirement IDs verbatim
- `.planning/ROADMAP.md` § Phase 4 — goal, dependencies (Phase 3), success criteria SC#1-5, "Parallelizable: no" note explaining middleware-order load-bearing

### Phase 3 carry-forward
- `.planning/phases/03-ef-core-persistence-base/03-CONTEXT.md` D-03a — `DbUpdateConcurrencyException` → HTTP 409 with detail "The resource was modified by another request; reload and retry." Phase 4 verification must add a lost-update test (two PUTs racing on the same row → second returns 409 with `correlationId`). xmin shadow concurrency token is wired in Phase 3 `BaseDbContext.OnModelCreating`; Phase 4 maps the resulting EF-layer exception to HTTP.
- `.planning/phases/03-ef-core-persistence-base/03-CONTEXT.md` D-11 — Phase 3 did NOT touch `Program.cs`; the current scaffold is Phase 1 minimal (`CreateBuilder` + `AddControllers` + `MapControllers` + `Run`). Phase 4 OWNS the first non-trivial `Program.cs` edits (middleware + handler registration).
- `.planning/phases/03-ef-core-persistence-base/03-CONTEXT.md` D-15 — Per-class throwaway Postgres DB pattern (`stepsdb_test_{Guid:N}` + `ClearAllPools` + `DROP DATABASE WITH FORCE`); verified byte-identical BEFORE/AFTER `psql \l` snapshots. Phase 4 verification reuses this exact pattern for the SQLSTATE + concurrency tests.
- `.planning/phases/03-ef-core-persistence-base/03-CONTEXT.md` D-18 — `autonomous: false` checkpoint convention for verification plans (matches Phase 1 Plan 01-03 + Phase 2 Plan 02-02). Phase 4 Plan 04-02 follows.

### Research baselines (Phase 0 research)
- `.planning/research/STACK.md` — .NET 8 + ASP.NET Core 8.0.27 versions, Npgsql 8.0.10, EF Core 8.0.27. FluentValidation version not yet pinned (Phase 4 adds the pin).
- `.planning/research/PITFALLS.md` — Pitfall list (Phase 4 phase-researcher will surface middleware-specific pitfalls during research)
- `.planning/research/ARCHITECTURE.md` — Pattern 1 (Composition Root: `AddBaseApi`/`UseBaseApi` extensions are Phase 7). Phase 4 wires middleware directly in `Program.cs`; Phase 7 will refactor into extensions without changing behavior.

### Phase 1 + 2 carry-forward (build hygiene)
- `.planning/phases/01-repository-scaffold/01-CONTEXT.md` D-02 — `TreatWarningsAsErrors=true` globally; D-05/D-06 — CPM contract (zero `Version=` attributes on `<PackageReference>`); D-10 — `Program.cs` is the composition root, kept minimal in Phase 1 (Phase 4 takes the first real edits)
- `.planning/phases/02-postgres-docker-compose/02-CONTEXT.md` D-01 — host port 5433; D-02 — `appsettings.Development.json` ConnectionString `Host=localhost;Port=5433;Database=stepsdb;Username=postgres;Password=postgres`; D-04 — dev credentials (out of scope auth)

### External specs
- **RFC 7807** — Problem Details for HTTP APIs. https://datatracker.ietf.org/doc/html/rfc7807 (planner should NOT inline the spec; reference by URL in code comments where ProblemDetails shape is non-obvious).
- **RFC 9110 §15** — HTTP Semantics status code references (used as `ProblemDetails.type` URIs by the framework default).
- **Postgres SQLSTATE reference** — https://www.postgresql.org/docs/17/errcodes-appendix.html — 23503 (foreign_key_violation), 23505 (unique_violation) are the two Phase 4 maps; full table referenced for future-proofing the FallbackExceptionHandler logs.
- **Npgsql PostgresException** — https://www.npgsql.org/doc/api/Npgsql.PostgresException.html — `.SqlState`, `.ConstraintName`, `.TableName`, `.ColumnName` are the fields D-08 relies on.

### .NET 8 modern patterns (researcher to confirm current shape)
- `services.AddExceptionHandler<T>()` + `services.AddProblemDetails()` — the .NET 8 IExceptionHandler chain replacing the older `UseExceptionHandler(builder => ...)` lambda style. Microsoft Learn: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling
- `WebApplicationFactory<Program>` for integration tests + the `partial class Program { }` marker Phase 1 already added.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`BaseApi.Core`** class library exists (Phase 1) with `FrameworkReference Include="Microsoft.AspNetCore.App"` already wired (Phase 3 D-12). All Phase 4 middleware + handler types land here directly — no new csproj wiring beyond adding the FluentValidation package reference.
- **`Microsoft.Extensions.Logging`** is reachable transitively via the AspNetCore framework reference. `ILogger<T>.BeginScope` + `LogError(ex, ...)` work out of the box.
- **`Npgsql.EntityFrameworkCore.PostgreSQL`** (Phase 3 D-12) transitively brings `Npgsql.PostgresException` — D-08's mapper consumes this type. Confirm during research whether `Npgsql` standalone needs explicit pin or is reachable transitively.
- **`WebApplicationFactory<Program>`** infrastructure: Phase 1's `Program.cs` has `public partial class Program { }` (line 25) explicitly added as the marker type for integration tests. Phase 4 verification consumes this.
- **`PostgresFixture` pattern** (Phase 3 Plan 03-02) — `IAsyncLifetime` per-class throwaway DB lifecycle with `ClearAllPools()` + `DROP DATABASE WITH FORCE`. Phase 4 lifts this verbatim (separate file under `tests/BaseApi.Tests/Middleware/` rather than re-using Persistence/ to keep test layout aligned with the SUT layout).
- **`StubHttpContextAccessor`** (Phase 3 Plan 03-02) — handwritten test double, no Moq/NSubstitute (PROJECT.md Out of Scope). Phase 4 doesn't strictly need this (WebApplicationFactory provides a real HttpContext through the test server) but planner may reuse it for any unit-test-level mapper coverage.

### Established Patterns
- **CPM contract** — `<PackageReference Include="…" />` only; zero `Version=` attributes. New pins go to `Directory.Packages.props` (currently 23 pins after Phase 3). FluentValidation pin lands here.
- **Zero-warning build** — `TreatWarningsAsErrors=true` (Phase 1 D-02). Any new analyzer warning is build-fatal. Plan 04-01 builds Release AND Debug per W-01 + W-02 (Phase 3 unconditional convention).
- **File-scoped namespaces + outside-namespace usings** (Phase 1 .editorconfig with `:warning` severities). Every new .cs file follows this shape.
- **`autonomous: false` checkpoint plan for verification** (Phase 1 01-03, Phase 2 02-02, Phase 3 03-02) — 04-02 follows.
- **`docs(04-02): …` prefix for evidence/scaffold/SUMMARY commits; `fix(04-01): …` for source fix-forwards** (Phase 1/2/3 D-18 convention).
- **Real Postgres for fact tests, not InMemory** (Phase 3 D-15, Pitfall 9). Plan 04-02 reuses the running Phase 2 docker compose container at `localhost:5433`.

### Integration Points
- **`Program.cs`** (currently 27 lines, Phase 1 scaffold) — Phase 4 edits add ~15 lines of registration + middleware wiring. Layout target (illustrative — planner finalizes):
  ```csharp
  var builder = WebApplication.CreateBuilder(args);
  builder.Services.AddControllers();
  builder.Services.AddProblemDetails(opts => opts.CustomizeProblemDetails = ctx => { … });
  builder.Services.AddExceptionHandler<NotFoundExceptionHandler>();
  builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
  builder.Services.AddExceptionHandler<DbUpdateExceptionHandler>();
  builder.Services.AddExceptionHandler<FallbackExceptionHandler>();
  var app = builder.Build();
  app.UseExceptionHandler();
  app.UseMiddleware<CorrelationIdMiddleware>();
  app.UseRouting();   // implicit when MapControllers is called, but explicit is clearer for ordering
  app.MapControllers();
  app.Run();
  public partial class Program { }   // Phase 1 marker, retained
  ```
- **`tests/BaseApi.Tests/`** — already has `Microsoft.AspNetCore.Mvc.Testing` (Phase 1 D-13). `WebApplicationFactory<Program>` is reachable. New test files land under `tests/BaseApi.Tests/Middleware/` for the verification suite (parallel to Phase 3's `tests/BaseApi.Tests/Persistence/` layout). Test controller lands at `tests/BaseApi.Tests/Endpoints/TestController.cs`.
- **`BaseApi.Core` folder skeleton** — Phase 1 D-10 created `Middleware/.gitkeep` and (per ARCHITECTURE.md) anticipates `Exceptions/` and `Persistence/Exceptions/`. Plan 04-01 creates these directories implicitly when the first file lands.

</code_context>

<specifics>
## Specific Ideas

- **`ToString("N")` correlation format** locked at user's clarification — chosen specifically because correlation IDs (32 hex chars no dashes) look visually distinct from entity Ids (canonical 36-char `"D"` format via System.Text.Json). Mitigates accidental cross-paste between log lines and entity Id query parameters. Entity Id serialization across webapi GET responses, Postgres, and any future Elasticsearch indexing stays at `"D"` format unchanged — no editing needed for entity-Id copy-paste.
- **Generic "resource was modified" message on concurrency conflict** (D-09) — deliberately does NOT expose the `xmin` value, the row Id, or the conflicting field set. Treat as internal Postgres detail (information-disclosure guard). If a future phase wants a richer conflict response, that's a deliberate decision documented separately, not a Phase 4 accident.
- **`UseExceptionHandler` placement before `UseCorrelationId`** (D-01) is the .NET community standard but the rationale is documented inline so future readers understand why a `Guid.NewGuid()`-only middleware sits "outside the safety net" — it cannot realistically throw.
- **`[ApiController]` default `InvalidModelStateResponseFactory` is RIDDEN, not overridden** (D-11) — we just register `AddProblemDetails` and the factory auto-detects + emits Problem Details with the correct shape. Simpler than implementing a custom factory and matches Microsoft's intended .NET 8 design.

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within Phase 4 scope.

The following are Phase-Y items that came up implicitly but already have their own phase home (NOT deferred from Phase 4 — they were never in scope to begin with; surfaced here for cross-phase awareness):
- OpenTelemetry / OTLP exporter / Activity tagging on correlation middleware — Phase 5 (OBSERV-01..08, OBSERV-12) — D-13 explicitly defers
- Health probes + `AspNetCore.HealthChecks.NpgSql` — Phase 5 (HEALTH-01..05)
- `services.AddFluentValidation()` + per-entity validator registration — Phase 6 (Phase 4 ships only the typed-exception mapping per D-10)
- `AddBaseApi(...)` / `UseBaseApi(...)` extension methods — Phase 7 (Phase 4 wires `Program.cs` directly; Phase 7 will refactor without behavior change)
- Concrete `AppDbContext`, controllers, migrations — Phase 7/8 (Phase 4's `app.MapControllers()` is inert until Phase 7's first controller lands)

</deferred>

---

*Phase: 04-cross-cutting-middleware-error-handling*
*Context gathered: 2026-05-27*
