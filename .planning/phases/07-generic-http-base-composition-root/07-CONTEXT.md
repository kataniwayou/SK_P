# Phase 7: Generic HTTP Base + Composition Root - Context

**Gathered:** 2026-05-27
**Status:** Ready for planning

<domain>
## Phase Boundary

Build the four pieces that make `BaseApi.Service` runnable with one inheritance step per future entity:

1. **`BaseController<TEntity, TCreate, TUpdate, TRead>`** — abstract generic controller in `BaseApi.Core/Controllers/`, `[ApiController]` + URL-segment versioned route. Exposes 5 verbs (GET list, GET by id, POST, PUT, DELETE). Concrete `TestsController` (Phase 7) and 5 entity controllers (Phase 8) inherit and add nothing.
2. **`BaseService<TEntity, TCreate, TUpdate, TRead>`** — orchestration layer in `BaseApi.Core/Services/`. Owns the locked verb order: validate → ToEntity → repo.Add → SyncJunctionsAsync → SaveChangesAsync → ToRead. Exposes `protected virtual Task SyncJunctionsAsync(...)` for Phase 8's M2M sync.
3. **`AddBaseApi<TDbContext>(IConfiguration)`** — composition root extension in `BaseApi.Core/DependencyInjection/`. Absorbs all Phase 3/4/5/6 wiring as a chain of `internal` sub-extensions. Phase 8's `Program.cs` becomes ~3 lines.
4. **`UseBaseApi()`** — middleware pipeline extension in `BaseApi.Core/DependencyInjection/`. Composes Phase 4/5 middleware + new Asp.Versioning + Swashbuckle in the locked order from HTTP-14.

Out of scope:
- The 5 concrete entity controllers/services/mappers (Phase 8 HTTP-04..07, HTTP-11..12).
- Migrations (Phase 8 owns first migration; Phase 3 D-16 still binding).
- List filtering / pagination / sorting (Phase 8 HTTP-04..07).
- Authentication / CORS / rate limiting (no REQ-IDs in v1).
- ETag / If-Match concurrency over HTTP (xmin runs at SaveChanges; HTTP-layer concurrency deferred).

</domain>

<decisions>
## Implementation Decisions

### BaseController HTTP semantics (Area 1)

- **D-01:** `POST /api/v{version:apiVersion}/{entity}` returns **201 Created** + `Location` header `/api/v1/{entity}/{newId}` + `TRead` body. Uses `CreatedAtAction(nameof(GetById), new { id = read.Id }, read)`. REST-idiomatic; Swagger renders cleanly; Phase 4 D-04 ProblemDetails customizer keeps correlationId injection symmetric on the error path.
- **D-02:** `PUT /api/v{version:apiVersion}/{entity}/{id}` returns **200 OK** + `TRead` body. Clients get the canonical post-update state (audit fields stamped by AuditInterceptor, xmin advanced) without a follow-up GET. Symmetric with POST. ETag/`If-Match` over HTTP is deferred (no current REQ-ID).
- **D-03:** `DELETE /api/v{version:apiVersion}/{entity}/{id}` returns **204 No Content**. Repository.DeleteAsync is load-then-remove (Phase 3 D-08) — missing id throws `NotFoundException` → Phase 4 `NotFoundExceptionHandler` → 404 ProblemDetails. Success body is empty.
- **D-04:** `GET /api/v{version:apiVersion}/{entity}` returns bare **`IReadOnlyList<TRead>`** (JSON array). Phase 8's HTTP-04/05/06 (filter/page/sort) may introduce a `Paged<TRead>` envelope later — Phase 7 baseline must not lock that contract.

### BaseService contract + error signaling (Area 2)

- **D-05:** Every `BaseService` public method takes `CancellationToken ct`. `BaseController` bridges from action signature `(..., CancellationToken ct = default)` — ASP.NET Core binds `HttpContext.RequestAborted` automatically when the parameter is `CancellationToken`. Propagated to `IValidator.ValidateAsync(dto, ct)`, every `IRepository` call (already has CT per Phase 3), `dbContext.SaveChangesAsync(ct)`, and `SyncJunctionsAsync(..., ct)`. xUnit v3 facts continue to flow `TestContext.Current.CancellationToken` (Phase 3 pattern).
- **D-06:** `NotFound` surfaces as `throw new NotFoundException(entityName, id)`. The `NotFoundException` type already exists from Phase 4 ERROR-01. Phase 4 `NotFoundExceptionHandler` catches and emits 404 ProblemDetails with correlationId. Controllers stay branch-free: `return Ok(await _service.GetByIdAsync(id, ct))`. Same path for `UpdateAsync` and `DeleteAsync` when the id is missing.
- **D-07:** Validation runs **inside** `BaseService.CreateAsync`/`UpdateAsync` as the first step (VALID-03 — explicit `ValidateAsync` from service layer, NOT MVC auto-validation). On failure, BaseService throws `FluentValidation.ValidationException(result.Errors)`. Phase 4 `ValidationExceptionHandler` maps to 400 + ProblemDetails + `errors` map + `correlationId`. `[ApiController]`'s ModelState 400 (ERROR-10 — Phase 4 D-04 customizer) fires **only** for `[FromBody]` deserialization failures (malformed JSON / missing required JSON members); validator failures never reach ModelState. Both paths emit the same ProblemDetails shape.
- **D-08:** `DbUpdateConcurrencyException` (Phase 3 xmin shadow token) is **not** caught in BaseService — it bubbles to Phase 4 `DbUpdateExceptionHandler`, which handles concurrency FIRST (Pitfall 7) before SQLSTATE mapping, emitting 409 Conflict ProblemDetails. Same for `DbUpdateException` (constraint violations) — Phase 4 PostgresExceptionMapper handles 23503→422 and 23505→409. No service-side branching; existing 31 Phase 4 facts cover the behavior.

### SyncJunctionsAsync hook design (Area 3)

- **D-09:** Signature: `protected virtual Task SyncJunctionsAsync(TEntity entity, TCreate? createDto, TUpdate? updateDto, CancellationToken ct)`. One virtual covers both paths — `CreateAsync` passes `(entity, createDto, null, ct)`, `UpdateAsync` passes `(entity, null, updateDto, ct)`. Phase 8 overrides switch on whichever DTO is non-null (or override one of two sibling protected methods that the base delegates to — that's a planner-side implementation detail).
- **D-10:** Default body: `=> Task.CompletedTask;` (no-op). 4 of 5 v1 entities (TaskEntity, SchemaEntity, AssignmentEntity, and TestEntity from Phase 6) inherit the no-op and write zero override code. StepEntity (NextStepIds → StepNextSteps junction) and WorkflowEntity (EntrySteps → WorkflowEntrySteps; Assignments → WorkflowAssignments) override in Phase 8. Matches the ROADMAP description "one inheritance step away from working."
- **D-11:** Call site (locked in CreateAsync, mirrored in UpdateAsync minus the `repo.Add`):
  ```
  await _validator.ValidateAsync(createDto, ct);  // throw on fail (D-07)
  var entity = _mapper.ToEntity(createDto);       // Mapperly; Guid Id assigned client-side (Phase 3 BaseEntity)
  await _repo.AddAsync(entity, ct);               // EF tracker state: Added
  await SyncJunctionsAsync(entity, createDto, null, ct);  // entity still Added; junctions become Added too
  await _dbContext.SaveChangesAsync(ct);          // one transaction — atomic insert (entity + junction rows)
  return _mapper.ToRead(entity);                  // audit fields stamped by AuditInterceptor pre-Save
  ```
  Atomic on transaction boundary; if junction insert fails (SQLSTATE 23503 from a stale id ref), entity also rolls back — Phase 4 DbUpdateExceptionHandler still handles it.
- **D-12:** SC#2 verification (proving the 6-step order without a real junction entity in Phase 7): Phase 7's test seam uses a `TestService : BaseService<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>` (TestEntity / DTOs already exist from Phase 6 Plan 06-02) that overrides `SyncJunctionsAsync` to record `(DateTime.UtcNow, dbContext.ChangeTracker.Entries<TestEntity>().Single().State)`. The fact asserts:
  1. The recorded ChangeTracker state at SyncJunctionsAsync entry is `EntityState.Added` (proves we run AFTER repo.Add).
  2. SyncJunctionsAsync timestamp < SaveChanges timestamp (proves we run BEFORE SaveChanges).
  3. Mocks of `IValidator<TestCreateDto>` and `IEntityMapper<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>` (Mapperly + Moq/NSubstitute — planner picks) record their invocation timestamps; full assertion is the 6-step sequence.
  4. Pair with an end-to-end fact through TestsController to prove the controller→service→repo wiring actually invokes this path (not just the unit-level recorder).

### AddBaseApi composition root structure (Area 4)

- **D-13:** `AddBaseApi<TDbContext>(IConfiguration)` is a thin public method that calls **`internal`** sub-extensions in order:
  ```csharp
  public static IServiceCollection AddBaseApi<TDbContext>(this IServiceCollection services, IConfiguration cfg)
      where TDbContext : DbContext
      => services
          .AddBaseApiPersistence<TDbContext>(cfg)       // DbContext + naming + AuditInterceptor (Phase 3)
          .AddBaseApiObservability(cfg)                 // OTel logs/metrics/traces (Phase 5)
          .AddBaseApiHealth()                           // 3 probes + IStartupGate + StartupCompletionService (Phase 5)
          .AddBaseApiErrorHandling()                    // 4 IExceptionHandler + AddProblemDetails customizer (Phase 4)
          .AddBaseApiHttp(cfg)                          // AddControllers + Asp.Versioning + Swashbuckle (Phase 7)
          .AddBaseApiValidation(typeof(TDbContext).Assembly)   // ALREADY SHIPPED in Phase 6 D-14
          .AddBaseApiMapping(typeof(TDbContext).Assembly);     // ALREADY SHIPPED in Phase 6 D-15
  ```
  Each sub-extension is `internal static` (not part of public surface). Phase 6's `AddBaseApiValidation` + `AddBaseApiMapping` stay **public** (already shipped that way) — they get absorbed by AddBaseApi but remain individually callable (no breaking change, no behavior change).
  - **Why `typeof(TDbContext).Assembly` for validator/mapper scan:** Phase 8's `AppDbContext` lives in `BaseApi.Service` alongside the concrete validators and Mapperly partial classes. The closed-generic constraint already forced `TDbContext` to be supplied; reusing its assembly avoids an extra `params Assembly[]` parameter on `AddBaseApi`. Tests still override via `ConfigureTestServices` to scan additional assemblies (D-16).
- **D-14:** `AddBaseApiPersistence<TDbContext>(IConfiguration)` registers the DbContext + connection string + naming convention + AuditInterceptor inline:
  ```csharp
  services.AddScoped<AuditInterceptor>();
  services.AddDbContext<TDbContext>((sp, opts) =>
  {
      opts.UseNpgsql(cfg.GetConnectionString("Postgres"))
          .UseSnakeCaseNamingConvention()
          .AddInterceptors(sp.GetRequiredService<AuditInterceptor>());
  });
  ```
  Connection string key is `"Postgres"` (Phase 2 D-04 / appsettings.json — already locked). Phase 8's `AppDbContext` class becomes minimal (just `DbSet<>`s + `OnModelCreating` overrides; no `OnConfiguring`). Matches HTTP-13 verbatim ("registers DbContext + naming convention + interceptors").
- **D-15:** Migration plan: `AddBaseApi` ABSORBS all Phase 4+5+6 wiring currently in `Program.cs`. After migration, `Program.cs` is approximately:
  ```csharp
  var builder = WebApplication.CreateBuilder(args);
  builder.Services.AddBaseApi<AppDbContext>(builder.Configuration);
  var app = builder.Build();
  app.UseBaseApi();
  app.MapControllers();
  app.Run();
  ```
  All 47 existing Phase 1-5 facts MUST still pass after this migration — verification step replays them through the new composition. Phase 6 already proved Phase 4+5 behavior is regression-stable (47/47 GREEN × 3 runs). Phase 7 inherits the regression discipline: 3 consecutive GREEN runs over the full suite (Phase 3 D-18 cadence) after AddBaseApi lands.
- **D-16:** Test seam: `WebAppFactory<Program>` (Phase 4, unsealed by Phase 6) and its subclasses (`ValidationWebAppFactory`, Phase 5 OTel/Health subclasses) keep their existing shape. WebApplicationFactory boots `Program.Main`; tests override via `ConfigureTestServices` for per-test customization (StubHttpContextAccessor, connection string swap, additional validator assembly, etc.). Every fact exercises the real AddBaseApi composition — strong integration coverage, no drift between test and prod wiring.

### API versioning (Area 5)

- **D-17:** `Asp.Versioning.Http` (the framework-independent variant per HTTP-15) + `Asp.Versioning.Mvc.ApiExplorer` (Swagger integration). URL-segment versioning:
  ```csharp
  services.AddApiVersioning(opts =>
  {
      opts.DefaultApiVersion = new ApiVersion(1, 0);
      opts.AssumeDefaultVersionWhenUnspecified = true;
      opts.ReportApiVersions = true;             // emits api-supported-versions response header
  })
  .AddApiExplorer(opts =>
  {
      opts.GroupNameFormat = "'v'VVV";           // "v1"
      opts.SubstituteApiVersionInUrl = true;     // {version:apiVersion} → "1.0" → "1"
  });
  ```
  BaseController carries the templated route + version:
  ```csharp
  [ApiController]
  [ApiVersion("1.0")]
  [Route("api/v{version:apiVersion}/[controller]")]
  public abstract class BaseController<TEntity, TCreate, TUpdate, TRead> : ControllerBase { ... }
  ```
  Concrete controllers add `[ApiController]` (inherited from BaseController is enough — but planner verifies) and nothing else. Adding v2 in a later milestone = one `[ApiVersion("2.0")]` attribute on a sibling controller. HTTP-15's "/api/v1/" URL prefix is honored via the URL segment substitution.

### Swagger / OpenAPI (Area 6)

- **D-18:** `Swashbuckle.AspNetCore` minimal config:
  - `SwaggerDoc("v1", new OpenApiInfo { Title = cfg["Service:Name"], Version = "v1", Description = "...from PROJECT.md..." })`.
  - `OperationFilter<CorrelationIdHeaderOperationFilter>` documents `X-Correlation-Id` as an optional `string` header on every operation (Phase 4 OBSERV-09/10 visibility — clients see they can pass it in OR will get one back).
  - `DocumentFilter<HideHealthEndpointsDocumentFilter>` removes `/health/live`, `/health/ready`, `/health/startup` from the spec (operational endpoints, not API). Phase 5 `ASP.NET Core/HttpClient instrumentation Filter` callback already excludes them from OTel traces (Pitfall 10) — same separation principle here.
  - No XML doc inclusion in v1 (`<GenerateDocumentationFile>` stays off — turning it on would surface CS1591 on every public type and TreatWarningsAsErrors=true (Phase 1 D-02) would block the build). Logged as deferred idea.
  - No security definitions (no auth in v1).
  - UI mapped at `/swagger` only inside `app.Environment.IsDevelopment()`. Production returns 404 (HTTP-16 + SC#4).

### UseBaseApi middleware pipeline (Area 7)

- **D-19:** Pipeline order (matches HTTP-14 verbal spec; CORS omitted because no REQ-ID):
  ```csharp
  public static WebApplication UseBaseApi(this WebApplication app)
  {
      app.UseExceptionHandler();                 // FIRST — catches everything below (Phase 4 D-01)
      app.UseMiddleware<CorrelationIdMiddleware>(); // Phase 4 — must precede MapControllers
      app.UseRouting();                          // Phase 5 OTel uses this for HTTP route attribute
      // CORS omitted in v1 — no REQ-ID; AllowAnyOrigin would introduce auth/sec surface
      if (app.Environment.IsDevelopment())
      {
          app.UseSwagger();
          app.UseSwaggerUI(); // serves at /swagger
      }
      app.MapHealthChecks("/health/live",    new HealthCheckOptions { Predicate = c => c.Tags.Contains("live"),    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse });
      app.MapHealthChecks("/health/ready",   new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready"),   ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse });
      app.MapHealthChecks("/health/startup", new HealthCheckOptions { Predicate = c => c.Tags.Contains("startup"), ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse });
      // MapControllers is called from Program.cs (not UseBaseApi) — tests can still .MapControllers themselves without forcing a re-entry into UseBaseApi
      return app;
  }
  ```
  Health endpoints continue to emit JSON via Phase 5 `UIResponseWriter.WriteHealthCheckUIResponse` (T-05-READY-DB-EXPOSE sanitization). Tag predicates copy Phase 5 D-09 verbatim.

### Claude's Discretion

- Constructor injection plumbing for `BaseService` (`IValidator<TCreate>`, `IValidator<TUpdate>`, `IEntityMapper<,,,>`, `IRepository<TEntity>`, `TDbContext`, `ILogger<BaseService<...>>`) — planner picks DI parameter order and any guard clauses. Validator nullability check is interesting (FluentValidation registers `IValidator<T>` only if a concrete `AbstractValidator<T>` is found; a missing validator at runtime would null-ref) — planner decides between (a) throw `InvalidOperationException` in ctor if `IValidator<TCreate>` is missing, (b) take `IValidator<TCreate>?` and skip validation when null. Phase 8 always ships concrete validators, so the safer (a) is preferable.
- BaseController action signatures: planner picks between `[HttpGet]` and `[HttpGet, ProducesResponseType(typeof(IReadOnlyList<TRead>), 200)]`. Swagger schema is nicer with explicit `ProducesResponseType`; planner picks what reads cleanest in the generic context.
- Sealed vs non-sealed on `BaseController` / `BaseService`: must be abstract per HTTP-02. Planner decides whether to seal `BaseService` against further inheritance once Phase 8 ships (likely no — keep extensible).
- Sub-extension method visibility (`internal static`) vs nested-class organization. Planner picks file layout.
- Test framework for SC#2 ordering proof (Moq vs NSubstitute vs raw recording wrapper). Project has no mocking library yet; planner picks based on Phase 8's anticipated needs.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase 7 boundary + success criteria
- `.planning/ROADMAP.md` §"Phase 7: Generic HTTP Base + Composition Root" — Goal, dependencies, 9 REQ-IDs, 4 SCs.
- `.planning/REQUIREMENTS.md` HTTP-01..03, HTTP-08..09, HTTP-13..16 — locked acceptance criteria.

### Cross-phase decisions that constrain Phase 7
- `.planning/phases/03-ef-core-persistence-base/03-CONTEXT.md` — BaseEntity (Guid Id assigned client-side, xmin shadow concurrency token, audit fields owned by AuditInterceptor), IRepository/Repository (5-method surface, load-then-remove DeleteAsync).
- `.planning/phases/04-cross-cutting-middleware-error-handling/04-CONTEXT.md` — IExceptionHandler chain order (D-06), AddProblemDetails customizer (D-04), CorrelationIdMiddleware pipeline order (D-01), NotFoundException type (ERROR-01).
- `.planning/phases/05-observability-health-probes/05-CONTEXT.md` — OTel wiring (logs via MEL bridge, traces+metrics, /health/* filter), 3 health probes + tag predicates (D-09), IStartupGate + StartupCompletionService, UIResponseWriter for JSON health bodies.
- `.planning/phases/06-validation-mapping-base/06-CONTEXT.md` — AddBaseApiValidation (D-14) + AddBaseApiMapping (D-15) already shipped + already wired in Program.cs (D-18). IBaseDto / BaseDtoValidator<T> / IEntityMapper<,,,> contracts. TestEntity/TestDtos/TestController already exist for Phase 7 verification.
- `.planning/PROJECT.md` — single-API modular-monolith pattern, Service.Name="sk-api", Service.Version="3.2.0", controller-based ASP.NET Core (HTTP-01 — NOT Minimal APIs).
- `.planning/phases/01-repository-scaffold/01-CONTEXT.md` D-02 + Directory.Build.props — TreatWarningsAsErrors=true globally; XML doc would surface CS1591 (blocks build) — pinned constraint for Swagger config D-18.
- `.planning/phases/02-postgres-docker-compose/02-CONTEXT.md` — Connection string key "Postgres" in appsettings (locks D-14 lookup key).

### No external specs
- All requirements fully captured in REQUIREMENTS.md HTTP-01..16 and prior CONTEXT files. No ADRs or external design docs.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `BaseApi.Core/Controllers/` — empty folder (`.gitkeep` from Phase 1 D-08). Phase 7 lands `BaseController.cs` as the first file.
- `BaseApi.Core/Services/` — empty folder. Phase 7 lands `BaseService.cs`.
- `BaseApi.Core/DependencyInjection/` — already contains `ValidationServiceCollectionExtensions.cs` + `MappingServiceCollectionExtensions.cs` (Phase 6). Phase 7 adds the remaining sub-extensions + `BaseApiServiceCollectionExtensions.cs` (public `AddBaseApi`) + `BaseApiApplicationBuilderExtensions.cs` (public `UseBaseApi`).
- `BaseApi.Core/Persistence/` — `BaseEntity`, `BaseDbContext`, `Repository<T>`, `AuditInterceptor` already exist (Phase 3). `AddBaseApiPersistence` wraps them.
- `BaseApi.Core/Middleware/` — `CorrelationIdMiddleware` already exists (Phase 4). `UseBaseApi` consumes it.
- `BaseApi.Core/ErrorHandling/` (or `Exceptions/Handlers/`) — 4 IExceptionHandlers + `PostgresExceptionMapper` already exist (Phase 4). `AddBaseApiErrorHandling` wraps them.
- `BaseApi.Core/Health/` — `IStartupGate`, `StartupHealthCheck`, `StartupCompletionService` already exist (Phase 5). `AddBaseApiHealth` wraps them.
- `BaseApi.Core/Telemetry/` — OTel wiring artifacts from Phase 5. `AddBaseApiObservability` wraps them.
- `BaseApi.Core/Validation/` + `BaseApi.Core/Mapping/` — Phase 6 seam types. Public DI extensions already callable.
- `tests/BaseApi.Tests/Middleware/WebAppFactory.cs` — unsealed in Phase 6 (`public class`). Phase 7 tests can subclass for Dev-environment Swagger fact or Production-environment 404 fact.
- `tests/BaseApi.Tests/Validation/{TestEntity, TestDtos, TestDtoValidator, TestEntityMapper, ValidationWebAppFactory}.cs` — Phase 6 scaffolds. Phase 7 builds `TestsController : BaseController<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>` + `TestService : BaseService<...>` on top of these.

### Established Patterns
- DI extension naming: `AddBaseApi*` for service-side, `UseBaseApi*` for builder-side. Visibility `public static` for the top-level entry, `internal static` for sub-extensions (D-13).
- File layout: one type per file, namespace matches folder path.
- Sealed concretes by default (Phase 3/4/5 precedent); abstract bases for `BaseController` / `BaseService` (HTTP-02, ROADMAP wording).
- Constructor parameter validation: ASP.NET Core DI guarantees non-null for registered services — no defensive null checks except where intentional (e.g., optional `IValidator<T>` if planner picks Discretion path b in BaseService).
- Test convention: xUnit v3 facts, `WebApplicationFactory<Program>` integration tests, per-class throwaway databases (Phase 3 D-15), `TestContext.Current.CancellationToken` flowed through every async call site (Phase 3 deviation #4).
- Source-gen Mapperly conventions: `PrivateAssets="all"` + `ExcludeAssets="runtime"` on any csproj referencing Riok.Mapperly (Phase 6 D-13). BaseApi.Core does NOT reference Mapperly — only consumers (BaseApi.Service / BaseApi.Tests).

### Integration Points
- `Program.cs` at `src/BaseApi.Service/Program.cs` — Phase 7 rewrites the body to ~3 lines (D-15). The current ~150-line wiring moves into AddBaseApi sub-extensions.
- `appsettings.json` ConnectionStrings:Postgres — already exists; AddBaseApiPersistence reads it via `cfg.GetConnectionString("Postgres")` (D-14).
- `Directory.Packages.props` — Phase 7 adds 3 NEW package pins for `Asp.Versioning.Http`, `Asp.Versioning.Mvc.ApiExplorer`, `Swashbuckle.AspNetCore` (none currently pinned per Phase 1 D-05 "front-load all 23 NuGet pins" — Phase 7 packages were deferred to this phase). Planner picks specific versions during research.
- `Directory.Build.props` — no edits anticipated (already covers Mapperly RMG codes via Phase 6 D-11). XML doc generation explicitly NOT enabled (D-18).
- WebAppFactory + ConfigureTestServices override pattern (D-16) keeps existing 47 facts wired without modification.

</code_context>

<specifics>
## Specific Ideas

- BaseController is `[ApiController]` + `[ApiVersion("1.0")]` + `[Route("api/v{version:apiVersion}/[controller]")]` decorated on the abstract base — concrete controllers inherit and add nothing for the v1 verbs.
- The `[controller]` token resolves against the concrete controller name minus "Controller" suffix — so `TestsController` → `tests`, `StepsController` → `steps`, etc. ASP.NET Core's default convention; matches HTTP-03's URL examples.
- The Guid Id assigned client-side at BaseEntity construction (Phase 3) is what makes D-11's "after repo.Add but before SaveChanges" tracker state work — the entity already has a non-empty Id when SyncJunctionsAsync sees it.
- `TestService : BaseService<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>` for the SC#2 fact uses TestEntity from Phase 6 — no new entity needed in Phase 7. Phase 6's `TestValidationService` is a different shape (validation-only); Phase 7 builds the full orchestration variant.
- `TestsController` (plural — matches `[controller]` → "tests" URL convention) inherits from `BaseController<TestEntity, ...>` with an EMPTY body. Verification asserts the 5 routes are exposed (SC#1) via the WebApplicationFactory route table inspection or live HTTP probes.

</specifics>

<deferred>
## Deferred Ideas

- **ETag / If-Match optimistic concurrency over HTTP** — surfaces xmin via response header on GET/PUT, lets clients send `If-Match: <xmin>` on PUT. No REQ-ID in v1. Worth a backlog ticket — Phase 4 facts already cover the DB-side concurrency; HTTP-layer is the missing half.
- **XML doc inclusion in Swagger** — `<GenerateDocumentationFile>true</GenerateDocumentationFile>` + `IncludeXmlComments(...)` in Swashbuckle config. Blocked by TreatWarningsAsErrors=true + CS1591 on every public type — requires either editorial sweep (write triple-slash on every public type) OR a `<NoWarn>$(NoWarn);1591</NoWarn>` per-project relaxation. Decision deferred to a later doc/polish phase.
- **CORS configuration** — `AddCors` + `UseCors` placement is locked by HTTP-14 (between routing and endpoints) but no REQ-ID specifies the policy. Browser-tooling devs may want a permissive Dev-only policy. Backlog candidate.
- **Bearer JWT auth + Security Definitions in Swagger** — no auth backend in v1. Phase 9+ when an identity provider is selected.
- **Paged<TRead> envelope for GET list** — Phase 8's HTTP-04..06 will surface filter/page/sort; the bare-list contract in D-04 leaves the door open. If Phase 8 picks the paged shape, it's a breaking change to D-04 — flag for Phase 8 CONTEXT.
- **Result<T, TError> envelope pattern** — rejected in D-06 in favor of exception-based NotFound signaling. If complexity grows (multiple non-success outcomes per operation), revisit.
- **DispatchProxy / Castle interceptor for ordering proof** — D-12 picked the simpler recording-override approach. Logged here because it might be useful for cross-cutting audits later.

</deferred>

---

*Phase: 07-generic-http-base-composition-root*
*Context gathered: 2026-05-27*
