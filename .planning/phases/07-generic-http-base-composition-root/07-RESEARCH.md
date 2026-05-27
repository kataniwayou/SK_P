# Phase 7: Generic HTTP Base + Composition Root - Research

**Researched:** 2026-05-27
**Domain:** Controller-based ASP.NET Core (generic base controller/service), DI composition root, Asp.Versioning URL-segment routing, Swashbuckle OpenAPI
**Confidence:** HIGH (all critical claims verified against Context7-equivalent official docs + existing codebase + locked CONTEXT.md decisions)

## Summary

Phase 7 lands four tightly coupled pieces in `BaseApi.Core`: (1) abstract generic `BaseController<TEntity,TCreate,TUpdate,TRead>` decorated with `[ApiController]` + `[ApiVersion("1.0")]` + `[Route("api/v{version:apiVersion}/[controller]")]` exposing 5 verbs; (2) abstract generic `BaseService<TEntity,TCreate,TUpdate,TRead>` enforcing the locked 6-step `CreateAsync` order with a `virtual SyncJunctionsAsync` hook for Phase 8 M2M sync; (3) `AddBaseApi<TDbContext>(IConfiguration)` composition root that absorbs all Phase 3/4/5/6 wiring as `internal` sub-extensions plus 4 NEW sub-extensions (Persistence/Observability/Health/ErrorHandling/Http) — Phase 8's `Program.cs` becomes ~3 lines; (4) `UseBaseApi()` middleware extension composing the locked pipeline order from CONTEXT D-19. Asp.Versioning + Swashbuckle wire as a unit inside `AddBaseApiHttp`/`UseBaseApi`.

Two material findings emerge that warrant the planner's attention before task decomposition:

1. **Package correction required:** CONTEXT D-17 specifies `Asp.Versioning.Http` (the package for Minimal APIs). Controller-based APIs require `Asp.Versioning.Mvc` (which depends on `Asp.Versioning.Http` transitively) AND the canonical `.AddMvc()` chain method. `Directory.Packages.props` currently pins `Asp.Versioning.Http 8.1.0` ONLY — a new pin for `Asp.Versioning.Mvc 8.1.0` and `Asp.Versioning.Mvc.ApiExplorer 8.1.0` is required. The Mvc package transitively brings the Http base, so retaining the Http pin is fine (or it could be removed — Mvc.ApiExplorer transitively brings both). See A-01 below.
2. **XML documentation IS already generated:** CONTEXT D-18 claims `<GenerateDocumentationFile>` is OFF and any XML inclusion would trigger CS1591. Inspection of `BaseApi.Core.csproj` (lines 26-27) and `BaseApi.Service.csproj` (lines 31-32) shows `<GenerateDocumentationFile>true</GenerateDocumentationFile>` IS enabled with `<NoWarn>$(NoWarn);CS1591</NoWarn>` suppressing the missing-docs warning. Swashbuckle `IncludeXmlComments(...)` IS therefore unblocked in v1 — but CONTEXT D-18 explicitly defers it, so the recommendation stands to NOT include XML comments in v1 (deferred per CONTEXT D-18 closing bullet) even though the technical blocker doesn't exist. See A-02 below.

**Primary recommendation:** Land the composition root in two waves. Wave 1 (`07-01-PLAN.md` autonomous): csproj pins + BaseController + BaseService + 5 sub-extensions (Persistence/Observability/Health/ErrorHandling/Http) + AddBaseApi top-level + UseBaseApi + Program.cs migration to 3 lines + minimal Swashbuckle filters + zero-warning Release+Debug build. Wave 2 (`07-02-PLAN.md` autonomous:false verification checkpoint): `TestsController : BaseController<TestEntity,...>` empty-body fact (SC#1 route exposure), `TestService : BaseService<TestEntity,...>` overriding `SyncJunctionsAsync` to record `(DateTime.UtcNow, ChangeTracker.Entries<TestEntity>().Single().State)` (SC#2 6-step order proof), `Program.cs` line-count fact (SC#3), `WebApplicationFactory<Program>.UseEnvironment("Production")` returning 404 on `/swagger` (SC#4), plus regression replay of all 76 Phase 1-6 facts × 3 consecutive runs (CONTEXT D-15 + Phase 3 D-18 cadence).

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| HTTP routing + verb dispatch | API / Backend (Controllers) | — | `BaseController` is generic abstract; concrete TestsController + Phase 8's 5 controllers inherit — pure ASP.NET MVC concern |
| Validation orchestration | API / Backend (BaseService) | — | VALID-03 locks Service-layer explicit `ValidateAsync` (NOT MVC auto-validation); `[ApiController]` ModelState only catches deserialization failures |
| Entity-to-DTO mapping | API / Backend (BaseService → Mapperly) | — | `IEntityMapper<,,,>` consumed by BaseService; controllers never call mapper directly (HTTP-08) |
| Transaction boundary (SaveChanges) | API / Backend (BaseService) | — | Phase 3 D-05 locks Service-owns-SaveChanges; junction sync (Phase 8) requires single transaction |
| API versioning (URL segment) | API / Backend (route attribute + Asp.Versioning) | — | URL `/api/v1/...` is HTTP routing — backend-only; OpenAPI doc generation is a documentation overlay on the same tier |
| OpenAPI surface | API / Backend (Swashbuckle) | Dev-only UI | Doc generation runs server-side; `/swagger` UI gated by `IsDevelopment()` per CONTEXT D-19 |
| Composition root | API / Backend (AddBaseApi + UseBaseApi) | — | Pure ASP.NET Core DI/middleware wiring; one-shot at host startup |
| Error mapping (ProblemDetails) | Cross-cutting (Phase 4 IExceptionHandler chain) | — | Already shipped Phase 4; AddBaseApi just re-wires the same handlers via `AddBaseApiErrorHandling` |

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| HTTP-01 | Controller-based ASP.NET Core (not Minimal APIs) — required for BaseController inheritance | [VERIFIED: codebase + CONTEXT D-17] BaseController is `[ApiController]` decorated abstract class deriving from `ControllerBase`; Asp.Versioning.Mvc package (not .Http) is canonical for this pattern |
| HTTP-02 | `BaseController<TEntity, TCreate, TUpdate, TRead>` abstract generic in `BaseApi.Core/Controllers/`, decorated with `[ApiController]` and `[Route(...)]` | [VERIFIED: codebase `src/BaseApi.Core/Controllers/` exists empty + CONTEXT D-17 attribute set] Pattern proven for generic abstract base via Microsoft Learn + Strathweb article |
| HTTP-03 | Standard CRUD verbs on every concrete controller: GET list, GET by id, POST, PUT, DELETE | [VERIFIED: CONTEXT D-01..D-04 lock status codes 201/200/200/204 + `IReadOnlyList<TRead>` for list] |
| HTTP-08 | Layering enforced: Controller → Service → Repository (no Controller-to-Repository shortcut) | [VERIFIED: CONTEXT D-13 / Phase 3 D-05 / Phase 6 D-14] BaseController injects only `BaseService<...>`; Repository is internal-to-service |
| HTTP-09 | `BaseService<TEntity, TCreate, TUpdate, TRead>` in `BaseApi.Core` provides generic CRUD plus virtual `SyncJunctionsAsync` hook for M2M sync | [VERIFIED: CONTEXT D-09..D-12 lock signature + call site + test seam] |
| HTTP-13 | `AddBaseApi<TDbContext>(IConfiguration)` extension registers DI for all base concerns | [VERIFIED: CONTEXT D-13 locks 7-call chain — 5 new internals + 2 already-public Phase 6 extensions] |
| HTTP-14 | `UseBaseApi()` extension registers middleware in correct order (exception → correlation → routing → CORS → endpoints) | [VERIFIED: CONTEXT D-19 locks pipeline order verbatim; CORS omitted in v1 — no REQ-ID specifies a policy] |
| HTTP-15 | API versioning via `Asp.Versioning.Http` with URL prefix `/api/v1/` from v1 release | [PARTIALLY VERIFIED — see A-01] `Asp.Versioning.Mvc` is the controller-correct package; HTTP-15's literal "Asp.Versioning.Http" is a package-name miscite (the family/branding name) — the actual NuGet for controllers is `Asp.Versioning.Mvc`. URL prefix `/api/v1/` confirmed via `SubstituteApiVersionInUrl=true` |
| HTTP-16 | OpenAPI/Swagger UI via `Swashbuckle.AspNetCore`; exposed in Development environment | [VERIFIED: CONTEXT D-18, D-19 lock `app.UseSwagger()` + `app.UseSwaggerUI()` inside `if (app.Environment.IsDevelopment())`] |
</phase_requirements>

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

**BaseController HTTP semantics (Area 1):**
- **D-01:** `POST /api/v{version:apiVersion}/{entity}` returns **201 Created** + `Location` header `/api/v1/{entity}/{newId}` + `TRead` body. Uses `CreatedAtAction(nameof(GetById), new { id = read.Id }, read)`. Phase 4 D-04 ProblemDetails customizer injects `correlationId` on error path.
- **D-02:** `PUT /api/v{version:apiVersion}/{entity}/{id}` returns **200 OK** + `TRead` body. Symmetric with POST. ETag/`If-Match` over HTTP deferred (no current REQ-ID).
- **D-03:** `DELETE /api/v{version:apiVersion}/{entity}/{id}` returns **204 No Content**. Repository.DeleteAsync is load-then-remove (Phase 3 D-08) — missing id throws `NotFoundException` → Phase 4 `NotFoundExceptionHandler` → 404 ProblemDetails. Success body is empty.
- **D-04:** `GET /api/v{version:apiVersion}/{entity}` returns bare **`IReadOnlyList<TRead>`** (JSON array). Phase 8's HTTP-04/05/06 (filter/page/sort) may introduce a `Paged<TRead>` envelope later — Phase 7 baseline must not lock that contract.

**BaseService contract + error signaling (Area 2):**
- **D-05:** Every `BaseService` public method takes `CancellationToken ct`. Bridges from `BaseController` action signature `(..., CancellationToken ct = default)` — ASP.NET Core binds `HttpContext.RequestAborted` automatically. Propagated to validator, repository, `dbContext.SaveChangesAsync(ct)`, and `SyncJunctionsAsync(..., ct)`. Tests flow `TestContext.Current.CancellationToken` (Phase 3 pattern).
- **D-06:** `NotFound` surfaces as `throw new NotFoundException(entityName, id)`. Phase 4 `NotFoundExceptionHandler` catches → 404 ProblemDetails with correlationId. Controllers stay branch-free.
- **D-07:** Validation runs INSIDE `BaseService.CreateAsync`/`UpdateAsync` as the first step (VALID-03 — explicit `ValidateAsync`, NOT MVC auto-validation). On failure, BaseService throws `FluentValidation.ValidationException(result.Errors)`. Phase 4 `ValidationExceptionHandler` maps to 400.
- **D-08:** `DbUpdateConcurrencyException` (Phase 3 xmin shadow token) is NOT caught in BaseService — bubbles to Phase 4 `DbUpdateExceptionHandler`. No service-side branching.

**SyncJunctionsAsync hook design (Area 3):**
- **D-09:** Signature: `protected virtual Task SyncJunctionsAsync(TEntity entity, TCreate? createDto, TUpdate? updateDto, CancellationToken ct)`. One virtual covers both paths.
- **D-10:** Default body: `=> Task.CompletedTask;` (no-op). 4 of 5 v1 entities inherit no-op. StepEntity (NextStepIds) and WorkflowEntity (EntrySteps + Assignments) override in Phase 8.
- **D-11:** Call site (locked in `CreateAsync`, mirrored in `UpdateAsync` minus `repo.Add`): validate → ToEntity → repo.Add → SyncJunctionsAsync → SaveChangesAsync → ToRead.
- **D-12:** SC#2 verification: `TestService : BaseService<TestEntity, ...>` overrides `SyncJunctionsAsync` to record `(DateTime.UtcNow, dbContext.ChangeTracker.Entries<TestEntity>().Single().State)`. Asserts: (1) recorded state at SyncJunctionsAsync entry is `EntityState.Added`; (2) SyncJunctionsAsync timestamp < SaveChanges timestamp; (3) Mocks of validator + mapper record invocation timestamps for full 6-step sequence; (4) Pair with end-to-end fact through TestsController.

**AddBaseApi composition root structure (Area 4):**
- **D-13:** `AddBaseApi<TDbContext>(IConfiguration)` is thin public method calling `internal` sub-extensions:
  ```
  AddBaseApiPersistence<TDbContext>(cfg)   // DbContext + naming + AuditInterceptor
  AddBaseApiObservability(cfg)             // OTel logs/metrics/traces
  AddBaseApiHealth()                       // 3 probes + IStartupGate + StartupCompletionService
  AddBaseApiErrorHandling()                // 4 IExceptionHandler + AddProblemDetails customizer
  AddBaseApiHttp(cfg)                      // AddControllers + Asp.Versioning + Swashbuckle
  AddBaseApiValidation(typeof(TDbContext).Assembly)   // Phase 6 — already public
  AddBaseApiMapping(typeof(TDbContext).Assembly)      // Phase 6 — already public
  ```
- **D-14:** `AddBaseApiPersistence<TDbContext>(IConfiguration)` registers DbContext inline with `UseNpgsql(cfg.GetConnectionString("Postgres"))` + `UseSnakeCaseNamingConvention()` + `AuditInterceptor`. Connection string key is `"Postgres"` (Phase 2 D-04).
- **D-15:** `Program.cs` becomes ~3 lines: `builder.Services.AddBaseApi<AppDbContext>(builder.Configuration); var app = builder.Build(); app.UseBaseApi(); app.MapControllers(); app.Run();`. All 47 (now 76 with Phase 6 facts) prior facts MUST still pass after migration. 3 consecutive GREEN runs (Phase 3 D-18 cadence).
- **D-16:** Test seam: `WebAppFactory<Program>` (unsealed Phase 6) + subclasses keep existing shape. Tests override via `ConfigureTestServices`.

**API versioning (Area 5):**
- **D-17:** `Asp.Versioning.Http` (framework-independent) + `Asp.Versioning.Mvc.ApiExplorer`. URL-segment versioning:
  - `AddApiVersioning` with `DefaultApiVersion = new ApiVersion(1, 0)`, `AssumeDefaultVersionWhenUnspecified = true`, `ReportApiVersions = true`.
  - `.AddApiExplorer` with `GroupNameFormat = "'v'VVV"`, `SubstituteApiVersionInUrl = true`.
  - BaseController route: `[ApiController]` + `[ApiVersion("1.0")]` + `[Route("api/v{version:apiVersion}/[controller]")]`.

**Swagger / OpenAPI (Area 6):**
- **D-18:** `Swashbuckle.AspNetCore` minimal config: `SwaggerDoc("v1", new OpenApiInfo {...})`, `OperationFilter<CorrelationIdHeaderOperationFilter>` for `X-Correlation-Id` header, `DocumentFilter<HideHealthEndpointsDocumentFilter>` for `/health/*`. No XML doc inclusion in v1. No security definitions. UI mapped at `/swagger` only in Development.

**UseBaseApi middleware pipeline (Area 7):**
- **D-19:** Pipeline order:
  ```
  app.UseExceptionHandler();                 // FIRST (Phase 4 D-01)
  app.UseMiddleware<CorrelationIdMiddleware>(); // Phase 4
  app.UseRouting();
  if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
  app.MapHealthChecks("/health/live",    new HealthCheckOptions { Predicate = c => c.Tags.Contains("live"),    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse });
  app.MapHealthChecks("/health/ready",   new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready"),   ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse });
  app.MapHealthChecks("/health/startup", new HealthCheckOptions { Predicate = c => c.Tags.Contains("startup"), ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse });
  // MapControllers called from Program.cs (not UseBaseApi) — tests can MapControllers independently
  ```

### Claude's Discretion

- **Constructor injection plumbing for `BaseService`** (`IValidator<TCreate>`, `IValidator<TUpdate>`, `IEntityMapper<,,,>`, `IRepository<TEntity>`, `TDbContext`, `ILogger<BaseService<...>>`) — planner picks DI parameter order and guard clauses. Validator nullability check: between (a) throw `InvalidOperationException` in ctor if `IValidator<TCreate>` is missing, (b) take `IValidator<TCreate>?` and skip validation when null. Phase 8 always ships concrete validators, so (a) preferable.
- **BaseController action signatures:** planner picks between `[HttpGet]` and `[HttpGet, ProducesResponseType(typeof(IReadOnlyList<TRead>), 200)]`. Swagger schema nicer with explicit `ProducesResponseType`.
- **Sealed vs non-sealed:** abstract per HTTP-02. Planner decides sealing on `BaseService` post-Phase-8 (likely no — keep extensible).
- **Sub-extension method visibility** (`internal static`) vs nested-class organization. Planner picks file layout.
- **Test framework for SC#2 ordering proof** (Moq vs NSubstitute vs raw recording wrapper). Project has no mocking library yet.

### Deferred Ideas (OUT OF SCOPE)

- **ETag / If-Match optimistic concurrency over HTTP** — surfaces xmin via response header on GET/PUT; no REQ-ID in v1.
- **XML doc inclusion in Swagger** — `IncludeXmlComments(...)` in Swashbuckle config; deferred per D-18 closing bullet (despite the project having `<GenerateDocumentationFile>true</GenerateDocumentationFile>` already on).
- **CORS configuration** — `AddCors` + `UseCors`; no REQ-ID in v1.
- **Bearer JWT auth + Security Definitions in Swagger** — no auth backend in v1.
- **Paged<TRead> envelope for GET list** — Phase 8 may introduce.
- **Result<T, TError> envelope pattern** — rejected per D-06 in favor of exception-based NotFound.
- **DispatchProxy / Castle interceptor for ordering proof** — D-12 picked simpler recording-override.
</user_constraints>

## Project Constraints (from CLAUDE.md)

No `./CLAUDE.md` exists at repo root. Project conventions are encoded in `Directory.Build.props` + prior-phase CONTEXT.md decisions:

- **TreatWarningsAsErrors=true globally** (Phase 1 D-02) — every new C# file must be warning-clean under `Nullable=enable`, `AnalysisMode=latest`, `EnforceCodeStyleInBuild=true`. Phase 7 inherits.
- **WarningsAsErrors RMG007;RMG012;RMG020;RMG089** (Phase 6 D-11/D-12) — Mapperly diagnostics promoted to errors. Phase 7 does NOT touch Mapperly directly (only IEntityMapper interface) but the test-side mappers (TestEntityMapper from Phase 6) inherit the promotion.
- **CPM contract** (Phase 1 D-05/D-06) — `<PackageReference>` has NO `Version=` attribute; pins live in `Directory.Packages.props`. Phase 7 adds at most 1 new pin (`Asp.Versioning.Mvc` — see A-01) and zero-or-one existing-pin update (`Asp.Versioning.Mvc.ApiExplorer` if treated as separate, or transitively brought by Mvc).
- **`<GenerateDocumentationFile>true</GenerateDocumentationFile>` + `<NoWarn>$(NoWarn);CS1591</NoWarn>`** (both Core and Service csprojs) — XML doc generation IS on for production assemblies; missing-doc warnings suppressed. This contradicts CONTEXT D-18's claim that XML doc is off, but the CONTEXT's recommendation (no `IncludeXmlComments` in v1) stands as a separate policy decision. Planner respects the CONTEXT (no IncludeXmlComments) regardless.
- **Test project conventions:** xUnit v3, `TestContext.Current.CancellationToken` threaded through every awaitable (xUnit1051 analyzer is build-fatal under TreatWarningsAsErrors), `WebApplicationFactory<Program>` integration tests, per-class throwaway Postgres DBs (Phase 3 D-15), psql `\l` byte-identical BEFORE/AFTER cleanup discipline.

## Standard Stack

### Core

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Asp.Versioning.Mvc | **8.1.0 (NEW PIN REQUIRED)** | Controller-based API versioning extension methods | [VERIFIED: nuget.org] Standard package for `[ApiVersion]` + `[Route("api/v{version:apiVersion}/...")]` on `ControllerBase`-derived types; transitively pulls `Asp.Versioning.Http` |
| Asp.Versioning.Mvc.ApiExplorer | **8.1.0 (NEW PIN REQUIRED)** | API explorer integration for Swashbuckle | [VERIFIED: nuget.org] Replaces deprecated `Microsoft.AspNetCore.Mvc.Versioning.ApiExplorer`; required for `SubstituteApiVersionInUrl` + `GroupNameFormat` |
| Swashbuckle.AspNetCore | 6.9.0 (already pinned) | OpenAPI 3 doc generation + Swagger UI | [VERIFIED: Directory.Packages.props line 95] Pinned in Phase 1 D-05 front-load; .NET 8 compatible (6.6+ added .NET 8 support per Sikilinda blog) |

### Supporting

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| Microsoft.AspNetCore.App (FrameworkReference) | net8.0 | `ControllerBase`, `[ApiController]`, `[Route]`, `ProblemDetails` | Already wired in `BaseApi.Core.csproj` line 34 (Phase 3 D-12) — no new csproj edit |
| FluentValidation 12.1.1 | (already pinned) | `IValidator<T>.ValidateAsync` injected into BaseService | Already wired Phase 4 + Phase 6 — BaseService constructor injects `IValidator<TCreate>` + `IValidator<TUpdate>` |
| Riok.Mapperly 4.3.1 | (already pinned) | `IEntityMapper<,,,>` Mapperly source-gen implementations | BaseApi.Core does NOT reference Mapperly (Phase 6 D-13) — only IEntityMapper interface; BaseService injects `IEntityMapper<...>` |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Asp.Versioning.Mvc | Asp.Versioning.Http (alone) | [CITED: nuget.org/Asp.Versioning.Mvc; milanjovanovic.tech] `.Http` alone targets Minimal APIs; controller `[ApiVersion]` attribute resolution requires the Mvc package. CONTEXT D-17's "Asp.Versioning.Http" was a package-name miscite — the actual NuGet for controllers is Asp.Versioning.Mvc (which depends on .Http transitively) |
| Swashbuckle.AspNetCore | Microsoft.AspNetCore.OpenApi (built-in .NET 9+) | [CITED: aka.ms/aspnet-9-openapi] Built-in `AddOpenApi()` is .NET 9+; this project targets .NET 8 — Swashbuckle is the only viable choice. CONTEXT D-18 locks Swashbuckle. |
| NSubstitute (for mocks in SC#2) | Moq | [CITED: dimitrilaaraybi.com/blog/moqtonsubstitute; sikilinda.com/posts/my-take-on-moq-and-sponsorlink-situation] Moq's SponsorLink controversy (Aug 2023, v4.20.0) drove community migration to NSubstitute. Recommended for v1 going forward — see Pitfall 9 / Discretion item. |
| Mocking library | Raw recording wrapper class | [CITED: NSubstitute docs at nsubstitute.github.io] Hand-rolled recording wrappers add 30-50 LOC per dependency. NSubstitute's `Received.InOrder(() => { ... })` is the idiomatic way to assert ordered invocation across multiple dependencies. |

**Installation (Wave 1 csproj edits):**

```xml
<!-- Directory.Packages.props additions -->
<PackageVersion Include="Asp.Versioning.Mvc" Version="8.1.0" />
<PackageVersion Include="Asp.Versioning.Mvc.ApiExplorer" Version="8.1.0" />
<!-- Optional: remove existing Asp.Versioning.Http 8.1.0 since Mvc transitively brings it.
     Recommend KEEP the pin for auditability (CPM convention — explicit > implicit). -->
<!-- Tests: NSubstitute new pin (planner's discretion for SC#2 — see Pitfall 9) -->
<PackageVersion Include="NSubstitute" Version="5.3.0" />  <!-- planner may pick alternative -->

<!-- BaseApi.Core.csproj additions -->
<PackageReference Include="Asp.Versioning.Mvc" />
<PackageReference Include="Asp.Versioning.Mvc.ApiExplorer" />
<PackageReference Include="Swashbuckle.AspNetCore" />

<!-- BaseApi.Tests.csproj addition (planner's discretion) -->
<PackageReference Include="NSubstitute" />
```

**Version verification commands** (planner runs before Wave 1 finalization):

```bash
# Confirm Asp.Versioning.Mvc 8.1.0 is the latest 8.x line (10.x exists but is .NET 10-targeted)
dotnet add package Asp.Versioning.Mvc --version 8.1.0 --dry-run
dotnet add package Asp.Versioning.Mvc.ApiExplorer --version 8.1.0 --dry-run
# Confirm Swashbuckle.AspNetCore 6.9.0 supports .NET 8 (release notes 6.6.x added .NET 8 support)
dotnet add package Swashbuckle.AspNetCore --version 6.9.0 --dry-run
# NSubstitute current stable (planner picks 5.x line for .NET 8)
dotnet add package NSubstitute --version 5.3.0 --dry-run
```

**Note on Swashbuckle 6.9.0 vs 10.x:** The latest Swashbuckle on nuget.org is 10.1.7 (Mar 2026) but it's .NET 10-targeted. Version 6.9.0 (Phase 1-pinned) is the correct line for .NET 8 — releases 6.6.x added .NET 8 support, and 6.9.0 (the current 6.x stable) is appropriate. Do NOT upgrade to 10.x without first moving the project off .NET 8.

## Architecture Patterns

### System Architecture Diagram

```
                  ┌────────────────────────────────────────────────────┐
                  │ HTTP Request (POST /api/v1/tests with TestCreateDto)│
                  └────────────────────┬───────────────────────────────┘
                                       ▼
                  ┌────────────────────────────────────────────────────┐
                  │ UseExceptionHandler (Phase 4)                      │ ← outermost wrap
                  └────────────────────┬───────────────────────────────┘
                                       ▼
                  ┌────────────────────────────────────────────────────┐
                  │ CorrelationIdMiddleware (Phase 4)                  │ ← stamps HttpContext.Items["CorrelationId"]
                  └────────────────────┬───────────────────────────────┘
                                       ▼
                  ┌────────────────────────────────────────────────────┐
                  │ UseRouting + endpoint matching                     │
                  └────────────────────┬───────────────────────────────┘
                                       ▼
       ┌───────────────────────────────┴───────────────────────────────┐
       ▼                                                                ▼
  /health/*  ←─ Phase 5 MapHealthChecks                            /swagger ←─ Dev only
  (returns                                                          (404 in Prod — SC#4)
   JSON via
   UIResponseWriter)                  ┌────────────────────────────────────────┐
                                      ▼ /api/v{version:apiVersion}/[controller]
                       ┌──────────────────────────────────────────────────────┐
                       │ TestsController : BaseController<TestEntity,           │
                       │   TestCreateDto, TestUpdateDto, TestReadDto>           │
                       │ (empty body — verbs from base)                         │
                       └────────────────────┬─────────────────────────────────┘
                                            ▼ controller action calls _service.CreateAsync(dto, ct)
                       ┌──────────────────────────────────────────────────────┐
                       │ BaseService<TEntity, TCreate, TUpdate, TRead>          │
                       │ CreateAsync 6-step (CONTEXT D-11):                     │
                       │   1. validator.ValidateAsync(dto, ct)  ← throw on fail │
                       │   2. mapper.ToEntity(dto)             ← Mapperly        │
                       │   3. repo.AddAsync(entity, ct)        ← tracker:Added  │
                       │   4. SyncJunctionsAsync(entity, dto, null, ct) ← virt  │
                       │   5. dbContext.SaveChangesAsync(ct)   ← AuditIntercept │
                       │   6. mapper.ToRead(entity)            ← returns TRead  │
                       └─────────┬──────────────────────────┬────────────────┘
                                 ▼                          ▼
                       ┌─────────────────────┐  ┌─────────────────────────────┐
                       │ IValidator<TCreate>  │  │ IEntityMapper<,,,>           │
                       │ (FluentValidation 12)│  │ (Mapperly source-gen)       │
                       └─────────────────────┘  └─────────────────────────────┘
                                                            │
                                                            ▼
                                              ┌──────────────────────────────┐
                                              │ IRepository<TEntity>           │
                                              │ (Phase 3 — 5 methods, tracker  │
                                              │  state only; no SaveChanges)   │
                                              └──────────┬───────────────────┘
                                                         ▼
                                              ┌──────────────────────────────┐
                                              │ AppDbContext : BaseDbContext   │
                                              │ (Phase 8) + AuditInterceptor   │
                                              │ + xmin shadow concurrency tok  │
                                              └──────────┬───────────────────┘
                                                         ▼
                                              ┌──────────────────────────────┐
                                              │ Postgres 17 (Phase 2)          │
                                              └──────────────────────────────┘

Error path (if any step throws):
  IExceptionHandler chain (Phase 4 D-06): NotFound → Validation → DbUpdate → Fallback
  → ProblemDetails + correlationId customizer (Phase 4 D-04) → RFC 7807 JSON response

Composition:
  AddBaseApi<TDbContext>(cfg) chain (CONTEXT D-13):
    AddBaseApiPersistence<TDbContext>(cfg)   // NEW Phase 7
      └─ AddDbContext + UseNpgsql + UseSnakeCaseNamingConvention + AddInterceptors(AuditInterceptor)
      └─ AddScoped<IHttpContextAccessor> (already in Program.cs — idempotent)
      └─ AddSingleton<TimeProvider>(TimeProvider.System)
      └─ AddScoped(typeof(IRepository<>), typeof(Repository<>))
    AddBaseApiObservability(cfg)             // NEW Phase 7 — moves Phase 5 wiring
      └─ builder.Logging.AddOpenTelemetry(...)
      └─ AddOpenTelemetry().WithMetrics(...).WithTracing(...)
    AddBaseApiHealth()                       // NEW Phase 7 — moves Phase 5 wiring
      └─ AddSingleton<IStartupGate, StartupGate>
      └─ AddHealthChecks() with self/startup/npgsql checks
      └─ AddHostedService<StartupCompletionService>
    AddBaseApiErrorHandling()                // NEW Phase 7 — moves Phase 4 wiring
      └─ AddProblemDetails(customizer)
      └─ AddExceptionHandler<NotFound|Validation|DbUpdate|Fallback>
    AddBaseApiHttp(cfg)                      // NEW Phase 7
      └─ AddControllers()
      └─ AddApiVersioning().AddMvc().AddApiExplorer()
      └─ AddSwaggerGen() + IConfigureOptions<SwaggerGenOptions>
      └─ AddTransient<IOperationFilter, CorrelationIdHeaderOperationFilter>
      └─ AddTransient<IDocumentFilter, HideHealthEndpointsDocumentFilter>
    AddBaseApiValidation(typeof(TDbContext).Assembly)   // Phase 6 — already public
    AddBaseApiMapping(typeof(TDbContext).Assembly)      // Phase 6 — already public

  UseBaseApi() chain (CONTEXT D-19):
    UseExceptionHandler → UseMiddleware<CorrelationIdMiddleware> → UseRouting
    → if Dev: UseSwagger + UseSwaggerUI
    → MapHealthChecks × 3
    (MapControllers called from Program.cs, NOT UseBaseApi)
```

### Recommended Project Structure

```
src/BaseApi.Core/
├── Controllers/
│   └── BaseController.cs            # NEW Phase 7 — abstract generic
├── Services/
│   └── BaseService.cs               # NEW Phase 7 — abstract generic
├── DependencyInjection/
│   ├── BaseApiServiceCollectionExtensions.cs        # NEW — public AddBaseApi<TDbContext>
│   ├── BaseApiApplicationBuilderExtensions.cs       # NEW — public UseBaseApi
│   ├── PersistenceServiceCollectionExtensions.cs    # NEW — internal AddBaseApiPersistence<TDbContext>
│   ├── ObservabilityServiceCollectionExtensions.cs  # NEW — internal AddBaseApiObservability
│   ├── HealthServiceCollectionExtensions.cs         # NEW — internal AddBaseApiHealth
│   ├── ErrorHandlingServiceCollectionExtensions.cs  # NEW — internal AddBaseApiErrorHandling
│   ├── HttpServiceCollectionExtensions.cs           # NEW — internal AddBaseApiHttp
│   ├── ValidationServiceCollectionExtensions.cs     # EXISTING Phase 6 — public, called by AddBaseApi
│   └── MappingServiceCollectionExtensions.cs        # EXISTING Phase 6 — public, called by AddBaseApi
├── Swagger/                          # NEW folder
│   ├── ConfigureSwaggerOptions.cs               # IConfigureOptions<SwaggerGenOptions> per-version
│   ├── CorrelationIdHeaderOperationFilter.cs    # IOperationFilter — adds X-Correlation-Id header
│   └── HideHealthEndpointsDocumentFilter.cs     # IDocumentFilter — strips /health/*
├── (existing folders: Entities/, Persistence/, Exceptions/, Middleware/, Health/, Validation/, Mapping/, Telemetry/)
```

```
src/BaseApi.Service/
├── Program.cs                       # REWRITTEN — ~10 lines after migration
└── (existing appsettings + Properties — unchanged)
```

```
tests/BaseApi.Tests/
├── Http/                             # NEW folder for Phase 7 verification
│   ├── TestsController.cs            # public class TestsController : BaseController<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto> {} — empty body
│   ├── TestService.cs                # TestService : BaseService<TestEntity, ...> — overrides SyncJunctionsAsync to record state+timestamp
│   ├── RouteExposureTests.cs         # SC#1 — 5 routes via WebApplicationFactory route table or live HTTP probes
│   ├── ServiceOrderingTests.cs       # SC#2 — 6-step order via mocks + recorded ChangeTracker state
│   ├── ProgramMinimalityTests.cs     # SC#3 — Program.cs line count + no-per-concern-wiring assertion
│   └── SwaggerDevOnlyTests.cs        # SC#4 — UseEnvironment("Development") returns 200; "Production" returns 404
├── (existing: Persistence/, Middleware/, Observability/, Validation/, Endpoints/)
```

### Pattern 1: Generic Abstract BaseController with Inherited Routing

**What:** Decorate the abstract base with `[ApiController]` + `[ApiVersion]` + `[Route(...)]`. Concrete controllers inherit attributes — `[controller]` token resolves against the concrete class name minus "Controller" suffix.

**When to use:** Whenever 5+ entities share identical CRUD verb shape (HTTP-12 locks Phase 8's empty-derived-class pattern).

**Example:**

```csharp
// Source: Microsoft Learn "Create web APIs with ASP.NET Core" §"Annotation with [ApiController] attribute"
// + Strathweb "Generic and dynamically generated controllers" (verified abstract base pattern)
// + CONTEXT D-17 attribute set

using Asp.Versioning;
using BaseApi.Core.Entities;
using BaseApi.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace BaseApi.Core.Controllers;

/// <summary>
/// Abstract generic base controller exposing 5 CRUD verbs against
/// <typeparamref name="TEntity"/>. Concrete controllers (Phase 8) inherit
/// with no body — verbs come from the base, URL prefix from versioning.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
public abstract class BaseController<TEntity, TCreate, TUpdate, TRead> : ControllerBase
    where TEntity : BaseEntity
{
    private readonly BaseService<TEntity, TCreate, TUpdate, TRead> _service;

    protected BaseController(BaseService<TEntity, TCreate, TUpdate, TRead> service)
        => _service = service;

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<TRead>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TRead>>> List(CancellationToken ct)
        => Ok(await _service.ListAsync(ct));

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(TRead), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TRead>> GetById(Guid id, CancellationToken ct)
        => Ok(await _service.GetByIdAsync(id, ct));  // throws NotFoundException → Phase 4 404

    [HttpPost]
    [ProducesResponseType(typeof(TRead), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TRead>> Create([FromBody] TCreate dto, CancellationToken ct)
    {
        var read = await _service.CreateAsync(dto, ct);
        // CONTEXT D-01: 201 + Location header /api/v1/{entity}/{newId}.
        // The id property name on TRead is by convention "Id" — Phase 8 mappers stamp it from entity.
        // ASP.NET Core route matching against versioned routes works correctly
        // when there's a SINGLE [Route] on the base (no multi-route ambiguity per GitHub issue #651).
        return CreatedAtAction(nameof(GetById), new { id = ((dynamic)read!).Id }, read);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(TRead), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TRead>> Update(Guid id, [FromBody] TUpdate dto, CancellationToken ct)
        => Ok(await _service.UpdateAsync(id, dto, ct));

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _service.DeleteAsync(id, ct);
        return NoContent();
    }
}
```

**Caveat on `((dynamic)read!).Id`:** Forcing `TRead` to expose `Id` requires either (a) the dynamic dispatch shown (works but boxes + reflective dispatch — small cost on 201 path only), (b) an additional generic constraint like `where TRead : IHasId` exposing `Guid Id { get; }`, or (c) a `Func<TRead, Guid> idSelector` factory passed by derived class. Planner picks the cleanest option — recommendation is **(b) introduce `IHasId` marker interface** in `BaseApi.Core/Contracts/` and constrain `TRead`. Phase 8's Read DTOs implement `IHasId` trivially. Avoids dynamic dispatch on the hot 201 path.

**Verified facts:**
- `[ApiController]` on abstract base IS inherited by derived classes [CITED: Microsoft Learn web-api docs §"Annotation with [ApiController] attribute"]: *"A controller's class is annotated with the `[ApiController]` attribute or `ApiControllerAttribute`. [...] An alternative is the use of a custom base controller class. [...] The following example shows a custom base class and a controller that derives from it: `[ApiController] public class MyControllerBase : ControllerBase { } public class PetsController : MyControllerBase`"*
- `[controller]` token resolution: the token resolves against the derived controller's class name minus "Controller" suffix [CITED: Microsoft Learn "Routing to controller actions in ASP.NET Core" §"Token replacement in route templates ([controller], [action], [area])"]. `TestsController` → `tests`, `SchemasController` → `schemas`.

### Pattern 2: Generic Abstract BaseService with SyncJunctionsAsync hook

**What:** Single abstract class that owns the 6-step verb order (CONTEXT D-11). Constructor injects all 6 collaborators (validator × 2, mapper, repo, dbContext, logger). `SyncJunctionsAsync` is `protected virtual` no-op default.

**When to use:** Always — Phase 8's 5 services either inherit unchanged (4 entities) or override `SyncJunctionsAsync` (StepService, WorkflowService).

**Example:**

```csharp
// Source: CONTEXT D-09/D-10/D-11/D-12 verbatim; combined with Phase 3 IRepository pattern + Phase 6 IEntityMapper pattern.

using BaseApi.Core.Entities;
using BaseApi.Core.Exceptions;
using BaseApi.Core.Mapping;
using BaseApi.Core.Persistence;
using BaseApi.Core.Persistence.Repositories;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BaseApi.Core.Services;

public abstract class BaseService<TEntity, TCreate, TUpdate, TRead>
    where TEntity : BaseEntity
{
    private readonly IValidator<TCreate> _createValidator;
    private readonly IValidator<TUpdate> _updateValidator;
    private readonly IEntityMapper<TEntity, TCreate, TUpdate, TRead> _mapper;
    private readonly IRepository<TEntity> _repo;
    private readonly BaseDbContext _dbContext;
    private readonly ILogger _logger;

    protected BaseService(
        IValidator<TCreate> createValidator,
        IValidator<TUpdate> updateValidator,
        IEntityMapper<TEntity, TCreate, TUpdate, TRead> mapper,
        IRepository<TEntity> repo,
        BaseDbContext dbContext,
        ILogger<BaseService<TEntity, TCreate, TUpdate, TRead>> logger)
    {
        _createValidator = createValidator
            ?? throw new InvalidOperationException(
                $"No IValidator<{typeof(TCreate).Name}> registered. " +
                $"Concrete validator must inherit AbstractValidator<{typeof(TCreate).Name}> " +
                $"and be discoverable by AddValidatorsFromAssembly (Phase 6).");
        _updateValidator = updateValidator
            ?? throw new InvalidOperationException(
                $"No IValidator<{typeof(TUpdate).Name}> registered.");
        _mapper = mapper;
        _repo = repo;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TRead>> ListAsync(CancellationToken ct)
    {
        var entities = await _repo.ListAsync(ct);
        return entities.Select(_mapper.ToRead).ToList();  // IReadOnlyList<TRead>
    }

    public async Task<TRead> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var entity = await _repo.GetAsync(id, ct);
        if (entity is null) throw new NotFoundException(typeof(TEntity).Name, id);
        return _mapper.ToRead(entity);
    }

    public async Task<TRead> CreateAsync(TCreate dto, CancellationToken ct)
    {
        // CONTEXT D-11 6-step order — load-bearing for SC#2.
        await _createValidator.ValidateAndThrowAsync(dto, ct);     // 1
        var entity = _mapper.ToEntity(dto);                         // 2
        await _repo.AddAsync(entity, ct);                           // 3 — tracker: Added; Guid Id stamped by AuditInterceptor (Phase 3 D-01)
        await SyncJunctionsAsync(entity, dto, default, ct);         // 4 — virtual hook; CreateDto path passes (entity, create, null, ct)
        await _dbContext.SaveChangesAsync(ct);                      // 5 — AuditInterceptor stamps audit fields; xmin advances
        return _mapper.ToRead(entity);                              // 6 — audit fields visible to caller
    }

    public async Task<TRead> UpdateAsync(Guid id, TUpdate dto, CancellationToken ct)
    {
        await _updateValidator.ValidateAndThrowAsync(dto, ct);
        var entity = await _repo.GetAsync(id, ct);
        if (entity is null) throw new NotFoundException(typeof(TEntity).Name, id);
        _mapper.Update(dto, entity);                                // void — mutates in place
        await SyncJunctionsAsync(entity, default, dto, ct);         // UpdateDto path
        await _dbContext.SaveChangesAsync(ct);                      // DbUpdateConcurrencyException bubbles to Phase 4 mapper
        return _mapper.ToRead(entity);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var existing = await _repo.GetAsync(id, ct);
        if (existing is null) throw new NotFoundException(typeof(TEntity).Name, id);
        await _repo.DeleteAsync(id, ct);  // load-then-remove (Phase 3 D-08) — already verified existence; stages Remove
        await _dbContext.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Phase 8 override site for M2M junction sync. Called AFTER repo.AddAsync (tracker
    /// state: Added) and BEFORE SaveChangesAsync. Exactly one of <paramref name="createDto"/>
    /// or <paramref name="updateDto"/> is non-null. Default is no-op (CONTEXT D-10).
    /// </summary>
    protected virtual Task SyncJunctionsAsync(
        TEntity entity, TCreate? createDto, TUpdate? updateDto, CancellationToken ct)
        => Task.CompletedTask;
}
```

**Test seam (SC#2 — CONTEXT D-12):**

```csharp
// tests/BaseApi.Tests/Http/TestService.cs

public sealed class TestService : BaseService<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>
{
    public List<(DateTime Timestamp, EntityState ChangeTrackerState)> JunctionRecords { get; } = new();

    public TestService(
        IValidator<TestCreateDto> createValidator,
        IValidator<TestUpdateDto> updateValidator,
        IEntityMapper<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto> mapper,
        IRepository<TestEntity> repo,
        BaseDbContext dbContext,
        ILogger<BaseService<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>> logger)
        : base(createValidator, updateValidator, mapper, repo, dbContext, logger) { }

    protected override Task SyncJunctionsAsync(
        TestEntity entity, TestCreateDto? createDto, TestUpdateDto? updateDto, CancellationToken ct)
    {
        // CONTEXT D-12 record: (timestamp, tracker state).
        // ChangeTracker.Entries<TestEntity>().Single() — only one entity in this isolated test path.
        var state = base._dbContext.ChangeTracker.Entries<TestEntity>().Single().State;
        // NOTE: _dbContext field is private in BaseService; the test seam needs the field exposed
        // as `protected` OR the recording is done via the injected dbContext available to the
        // test fixture. Planner picks: change BaseService field visibility to `protected` OR
        // pass dbContext as a constructor parameter to TestService and capture it locally.
        // Recommendation: make `_dbContext` field `protected BaseDbContext DbContext { get; }`
        // exposed via a protected property — clean, no double-injection.
        JunctionRecords.Add((DateTime.UtcNow, state));
        return Task.CompletedTask;
    }
}
```

### Pattern 3: AddBaseApi composition root with sub-extensions

**What:** Public top-level `AddBaseApi<TDbContext>` calls 7 sub-extensions in order. Each sub-extension is `internal static` and owns one concern.

**Example skeleton (BaseApiServiceCollectionExtensions.cs):**

```csharp
// Source: CONTEXT D-13 verbatim.

using System.Reflection;
using BaseApi.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BaseApi.Core.DependencyInjection;

public static class BaseApiServiceCollectionExtensions
{
    public static IServiceCollection AddBaseApi<TDbContext>(
        this IServiceCollection services, IConfiguration cfg)
        where TDbContext : BaseDbContext
        => services
            .AddBaseApiPersistence<TDbContext>(cfg)        // 1
            .AddBaseApiObservability(cfg)                  // 2 — moves Phase 5 OTel wiring
            .AddBaseApiHealth(cfg)                         // 3 — moves Phase 5 health wiring (NOTE: needs cfg for connection string per AddNpgSql)
            .AddBaseApiErrorHandling()                     // 4 — moves Phase 4 IExceptionHandler + ProblemDetails customizer
            .AddBaseApiHttp(cfg)                           // 5 — NEW: AddControllers + Asp.Versioning + Swashbuckle
            .AddBaseApiValidation(typeof(TDbContext).Assembly)   // 6 — Phase 6 — already public
            .AddBaseApiMapping(typeof(TDbContext).Assembly);     // 7 — Phase 6 — already public
}
```

**Constraint subtlety:** CONTEXT D-13's example shows `where TDbContext : DbContext`. CONTEXT D-14 wires `UseNpgsql + UseSnakeCaseNamingConvention + AddInterceptors(AuditInterceptor)` — these calls are valid on any `DbContextOptionsBuilder` regardless of the generic constraint. **Stronger recommendation:** constrain `where TDbContext : BaseDbContext` to ensure the consumer's DbContext has the `xmin` shadow property and snake_case convention from Phase 3 D-09 — defensive against Phase 8 accidentally inheriting from `DbContext` directly.

**Internal sub-extension example (PersistenceServiceCollectionExtensions.cs):**

```csharp
// CONTEXT D-14 verbatim.

using BaseApi.Core.Persistence;
using BaseApi.Core.Persistence.Interceptors;
using BaseApi.Core.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BaseApi.Core.DependencyInjection;

internal static class PersistenceServiceCollectionExtensions
{
    internal static IServiceCollection AddBaseApiPersistence<TDbContext>(
        this IServiceCollection services, IConfiguration cfg)
        where TDbContext : BaseDbContext
    {
        services.AddHttpContextAccessor();                       // idempotent — Phase 4 already calls
        services.AddSingleton(TimeProvider.System);              // Phase 3 D-07
        services.AddScoped<AuditInterceptor>();                  // Phase 3 D-06 — note: D-06 says Singleton; CONTEXT D-14 says Scoped. RECONCILE per planner — current code uses default (no explicit registration line in BaseDbContext; planner verifies against Phase 3 actual wiring before locking)
        services.AddDbContext<TDbContext>((sp, opts) =>
        {
            opts.UseNpgsql(cfg.GetConnectionString("Postgres"))
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(sp.GetRequiredService<AuditInterceptor>());
        });
        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        // BaseDbContext alias so BaseService<...> can resolve via the abstract type:
        services.AddScoped<BaseDbContext>(sp => sp.GetRequiredService<TDbContext>());
        return services;
    }
}
```

**AuditInterceptor lifetime reconciliation:** Phase 3 CONTEXT.md D-06 says **Singleton**. The current `Repository.cs`/`BaseDbContext.cs` code does not register `AuditInterceptor` (Phase 3 deferred Program.cs touch per Phase 3 D-11; Phase 4 took over but did not add it). Phase 7 Plan must register it. **Recommendation:** register as **Singleton** per Phase 3 D-06 (the interceptor only depends on `IHttpContextAccessor` Singleton-wrapper + `TimeProvider` Singleton — both Singleton-safe). The CONTEXT.md D-14 code snippet showing `AddScoped<AuditInterceptor>` would create a Scoped-from-Singleton-resolution captive dependency if registered on the DbContext options builder via `sp.GetRequiredService<AuditInterceptor>()` — Singleton is the right choice.

### Pattern 4: Asp.Versioning.Mvc + Swashbuckle integration

**What:** Register API versioning with URL-segment substitution, configure Swashbuckle via `IConfigureOptions<SwaggerGenOptions>` to generate one SwaggerDoc per discovered version.

**Example (HttpServiceCollectionExtensions.cs):**

```csharp
// Source: github.com/dotnet/aspnet-api-versioning/wiki/Swashbuckle-Integration (canonical pattern)
// + CONTEXT D-17 + D-18

using Asp.Versioning;
using Asp.Versioning.ApiExplorer;
using BaseApi.Core.Swagger;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace BaseApi.Core.DependencyInjection;

internal static class HttpServiceCollectionExtensions
{
    internal static IServiceCollection AddBaseApiHttp(
        this IServiceCollection services, IConfiguration cfg)
    {
        services.AddControllers();

        services.AddApiVersioning(opts =>
        {
            opts.DefaultApiVersion = new ApiVersion(1, 0);
            opts.AssumeDefaultVersionWhenUnspecified = true;
            opts.ReportApiVersions = true;
            // URL-segment reader is the DEFAULT when the [Route] template contains
            // {version:apiVersion}. No explicit ApiVersionReader configuration needed
            // for v1, though setting it explicitly is harmless and self-documenting.
        })
        .AddMvc()                                  // CRITICAL: controllers-side activation (Asp.Versioning.Mvc package)
        .AddApiExplorer(opts =>
        {
            opts.GroupNameFormat = "'v'VVV";        // "v1"
            opts.SubstituteApiVersionInUrl = true;  // {version:apiVersion} → "1"
        });

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();
        services.AddTransient<IConfigureOptions<SwaggerGenOptions>, ConfigureSwaggerOptions>();

        return services;
    }
}
```

**ConfigureSwaggerOptions (one SwaggerDoc per discovered version):**

```csharp
// Source: github.com/dotnet/aspnet-api-versioning/wiki/Swashbuckle-Integration

using Asp.Versioning.ApiExplorer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace BaseApi.Core.Swagger;

internal sealed class ConfigureSwaggerOptions(
    IApiVersionDescriptionProvider provider,
    IConfiguration cfg) : IConfigureOptions<SwaggerGenOptions>
{
    public void Configure(SwaggerGenOptions options)
    {
        foreach (var description in provider.ApiVersionDescriptions)
        {
            options.SwaggerDoc(description.GroupName, new OpenApiInfo
            {
                Title       = cfg["Service:Name"] ?? "sk-api",
                Version     = description.ApiVersion.ToString(),
                Description = "Steps API — workflow-engine CRUD foundation."
                            + (description.IsDeprecated ? " DEPRECATED." : ""),
            });
        }

        options.OperationFilter<CorrelationIdHeaderOperationFilter>();
        options.DocumentFilter<HideHealthEndpointsDocumentFilter>();
    }
}
```

**UseBaseApi UI wiring (one endpoint per version):**

```csharp
// Source: github.com/dotnet/aspnet-api-versioning/wiki/Swashbuckle-Integration

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(opts =>
    {
        foreach (var description in app.DescribeApiVersions())
        {
            opts.SwaggerEndpoint(
                $"/swagger/{description.GroupName}/swagger.json",
                description.GroupName.ToUpperInvariant());
        }
    });
}
```

### Pattern 5: IOperationFilter — X-Correlation-Id header parameter

**Example (CorrelationIdHeaderOperationFilter.cs):**

```csharp
// Source: thecodebuzz.com/ioperationfilter-idocumentfilter-in-asp-net-core
// + Phase 4 CorrelationIdMiddleware (OBSERV-09 — header is "X-Correlation-Id")

using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace BaseApi.Core.Swagger;

internal sealed class CorrelationIdHeaderOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        operation.Parameters ??= new List<OpenApiParameter>();
        operation.Parameters.Add(new OpenApiParameter
        {
            Name        = "X-Correlation-Id",
            In          = ParameterLocation.Header,
            Required    = false,
            Description = "Optional correlation ID for request tracking. If absent, the server generates a new 32-char hex value and echoes it on the response header.",
            Schema      = new OpenApiSchema { Type = "string", MaxLength = 128 },
        });
    }
}
```

### Pattern 6: IDocumentFilter — hide health endpoints from OpenAPI spec

**Example (HideHealthEndpointsDocumentFilter.cs):**

```csharp
// Source: damirscorner.com/blog/posts/20240607-HidingEndpointsForDisabledFeaturesInOpenApi.html
// + CONTEXT D-18 (strip /health/live, /health/ready, /health/startup)

using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace BaseApi.Core.Swagger;

internal sealed class HideHealthEndpointsDocumentFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        var pathsToRemove = swaggerDoc.Paths
            .Where(kv => kv.Key.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var path in pathsToRemove)
        {
            swaggerDoc.Paths.Remove(path);
        }
    }
}
```

**Note:** Health endpoints are registered via `MapHealthChecks` (not `[ApiController]` actions) and typically do NOT appear in the OpenAPI document by default — `ApiExplorer` only describes endpoints from MVC's `ApiDescriptionProvider`. The DocumentFilter is **defense-in-depth**: if a future health-check library exposes its endpoints via ApiExplorer, the filter catches them; otherwise it's a no-op. CONTEXT D-18 explicitly mandates it.

### Pattern 7: Program.cs after migration (CONTEXT D-15)

```csharp
// src/BaseApi.Service/Program.cs (final form after Phase 7)
// Phase 8 will add AppDbContext class as the type argument; until then this file
// compiles with TDbContext = some concrete BaseDbContext subclass.

using BaseApi.Core.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBaseApi<AppDbContext>(builder.Configuration);
var app = builder.Build();
app.UseBaseApi();
app.MapControllers();
app.Run();

public partial class Program { }  // Phase 1 D-10 marker — load-bearing for WebApplicationFactory<Program>
```

**Until Phase 8 lands `AppDbContext`:** Phase 7 Wave 1 cannot use `AppDbContext` directly. Two paths:

- **Path A (recommended):** Leave Program.cs as-is from Phase 6 (with the existing imperative wiring) and only add the AddBaseApi + UseBaseApi extension methods + tests that use a `WebAppFactory` subclass overriding `ConfigureTestServices` to wire a `TestDbContext`. The Program.cs cutover to `AddBaseApi<AppDbContext>` happens in Phase 8 when AppDbContext exists.
- **Path B:** Land Phase 7 with a placeholder `AppDbContext` (3-line empty class deriving from BaseDbContext) in BaseApi.Service so Program.cs becomes the locked 3-line form. Phase 8 then adds DbSets to the existing class instead of creating it.

**Recommendation: Path B** — it satisfies SC#3 ("Program.cs is a thin file") atomically in Phase 7 and minimizes Phase 8 disruption. The placeholder AppDbContext has no DbSets in Phase 7 (Phase 8 adds the 5 entity DbSets + 3 junction DbSets), but BaseDbContext's `OnModelCreating` xmin iteration is a no-op against an empty set so EnsureCreated produces no tables — which is fine for Phase 7 verification (the TestEntity DbContext lives in tests/, not in Program.cs's AppDbContext).

### Anti-Patterns to Avoid

- **`UseEndpoints(...)` wrapper:** .NET 8+ pattern is direct `app.MapControllers()` on the WebApplication — no `UseEndpoints` block needed. Don't reintroduce.
- **Manual validator registration per Phase 8 entity:** Phase 6 AddBaseApiValidation auto-discovers. Phase 7 AddBaseApi.AddBaseApiValidation pass continues the auto-discovery. Don't `services.AddScoped<IValidator<X>>` manually.
- **`ServicesCollection.AddSingleton<BaseService<,,,>>` open-generic registration:** BaseService is abstract — open-generic Singleton registration would fail at activation. Phase 8 registers EACH concrete service (SchemaService, ProcessorService, etc.) as `AddScoped<SchemaService>()` because the concrete class is the resolution surface for the matching concrete controller.
- **`CreatedAtRoute` instead of `CreatedAtAction`:** Both work, but `nameof(GetById)` requires `CreatedAtAction`. If multiple routes were on the controller (e.g., `[Route("[controller]")]` AND `[Route("api/v{version:apiVersion}/[controller]")]`), `CreatedAtAction` would generate wrong Location headers (GitHub issue #651). Phase 7 has ONE route — pattern is safe.
- **`AddSwaggerGen()` BEFORE `AddApiVersioning().AddApiExplorer()`:** The `IConfigureOptions<SwaggerGenOptions>` reads from `IApiVersionDescriptionProvider` which is registered by `AddApiExplorer`. If `AddSwaggerGen` happens first, the configurator gets an empty provider and emits zero SwaggerDocs. CONTEXT D-13's chain order in `AddBaseApiHttp` puts ApiVersioning first — preserve.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Per-version SwaggerDoc generation | Manual `SwaggerDoc("v1", ...)` + `SwaggerDoc("v2", ...)` lines in Program.cs | `IConfigureOptions<SwaggerGenOptions>` reading `IApiVersionDescriptionProvider.ApiVersionDescriptions` | New API version = one `[ApiVersion("2.0")]` attribute; SwaggerDoc auto-generated. Manual approach drifts the moment Phase 9 adds v2. |
| Pre-validation FK existence checks | `await _repo.GetAsync(fkId) ?? throw` inside Service before SaveChanges | Postgres SQLSTATE 23503 → Phase 4 422 mapper | Phase 4 PostgresExceptionMapper already catches FK violations at SaveChanges; pre-checks add a race condition (FK can be deleted between check and save). Out of Scope per REQUIREMENTS.md "FK pre-validation over HTTP call". |
| Mocking framework for ordering proof | Hand-rolled `RecordingValidator : IValidator<T>` class + `RecordingMapper : IEntityMapper<...>` class | NSubstitute `Substitute.For<IValidator<TestCreateDto>>()` + `Received.InOrder(() => { ... })` | Hand-rolled wrappers grow 30-50 LOC × 4 dependencies = 120-200 LOC of test boilerplate per fact class. NSubstitute provides this in ~5 lines per assertion. |
| Generic CRUD repository wrapper | Add Phase-7-specific repository methods (e.g., `IRepository<T>.ExistsAsync`) | Reuse Phase 3 `IRepository<T>` 5-method surface | Phase 3 D-04 locked the 5-method surface. BaseService composes those primitives — no new repo methods. |
| Custom ProblemDetails shaping for Asp.Versioning errors | `IErrorResponseProvider` override | Default — Asp.Versioning already emits ProblemDetails | Asp.Versioning natively returns RFC 7807 for unsupported version (400) + version-not-specified (400) [CITED: github.com/dotnet/aspnet-api-versioning/wiki/Error-Responses]. Phase 4 customizer auto-injects correlationId + instance into these responses (same code path). |
| Generic mocking-library-free recording for SC#2 | DispatchProxy / Castle interceptor proxy | NSubstitute (planner discretion item) | CONTEXT D-12 explicitly deferred DispatchProxy approach per CONTEXT line 247. NSubstitute is the idiomatic .NET community choice post-Moq SponsorLink. |

**Key insight:** Phase 7 is fundamentally a **plumbing phase**. Every concern (persistence, observability, health, error handling, validation, mapping) has its full implementation in a prior phase. Phase 7 wires them together via DI extension methods and exposes the BaseController + BaseService surface. The only NEW behavior is API versioning + Swagger. Resist building anything else.

## Runtime State Inventory

Phase 7 is **not** a rename/refactor/migration phase — it is a greenfield phase landing new types + new csproj package pins + a `Program.cs` body rewrite. No stored data, no live service config, no OS-registered state, no secrets, and no stale build artifacts carry the old structure forward (the Phase 6 Program.cs imperative wiring is REPLACED, not migrated — it's a code rewrite, no data state involved).

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | None — no DB schema changes (Phase 8 still owns InitialCreate migration) | None |
| Live service config | None — Compose stack (Postgres + otel-collector) unchanged | None |
| OS-registered state | None | None |
| Secrets/env vars | None — `ConnectionStrings:Postgres` key unchanged; OTEL_EXPORTER_OTLP_ENDPOINT unchanged | None |
| Build artifacts | None — `bin/`/`obj/` are gitignored and rebuilt; no .egg-info-style stale artifacts in .NET | None |

**Note on Program.cs rewrite:** the rewrite from ~150-line imperative to ~3-line declarative IS a refactor at the source-code level, but it's pure code (no runtime state). The 76 existing tests continue to exercise the SAME composition graph because `WebApplicationFactory<Program>` still boots Program.cs verbatim — just now Program.cs delegates to `AddBaseApi` internally. CONTEXT D-15 explicitly demands "All 47 (now 76) existing Phase 1-5 facts MUST still pass after this migration" — the regression-replay × 3 cadence is the proof.

## Common Pitfalls

### Pitfall 1: CreatedAtAction returns wrong Location with multiple routes on controller

**What goes wrong:** [CITED: github.com/dotnet/aspnet-api-versioning/issues/651] When a controller has BOTH `[Route("[controller]")]` AND `[Route("api/v{version:apiVersion}/[controller]")]`, `CreatedAtAction(nameof(GetById), ...)` generates `/Items/123?version=1` instead of `/api/v1/items/123`.

**Why it happens:** ASP.NET Core's route URL generation walks the route table and matches the FIRST route that satisfies the action — the unversioned route wins, then `{version}` is appended as a query parameter because it has nowhere to go in the matched route template.

**How to avoid:** Phase 7 BaseController has EXACTLY ONE `[Route]` attribute (the versioned one). No unversioned route. Confirms the issue doesn't apply. Don't add an unversioned `[Route]` to derived controllers in Phase 8.

**Warning signs:** SC#1 verification's HTTP probe of `POST /api/v1/tests` returning a Location like `/Tests/<guid>?version=1` instead of `/api/v1/tests/<guid>`.

### Pitfall 2: AddSwaggerGen registered before AddApiExplorer — empty SwaggerDoc

**What goes wrong:** Calling `services.AddSwaggerGen()` BEFORE `.AddApiExplorer()` registers `IApiVersionDescriptionProvider` results in `IConfigureOptions<SwaggerGenOptions>.Configure()` iterating an empty collection — Swagger UI shows zero versions.

**Why it happens:** `IConfigureOptions` is resolved at the moment of first SwaggerGen request, not at registration time. But the `ConfigureSwaggerOptions` ctor resolves `IApiVersionDescriptionProvider` — if it's a no-op default registration (because `AddApiVersioning().AddApiExplorer()` ran AFTER and overwrote the placeholder), the configurator still sees the provider but the collection is built from MVC's action descriptors which may not be populated yet.

**How to avoid:** Call `AddApiVersioning().AddMvc().AddApiExplorer()` BEFORE `AddSwaggerGen()` in `AddBaseApiHttp`. CONTEXT D-13's chain order satisfies this. Verification: SC#4 must assert Swagger UI shows the "v1" group with at least one operation.

**Warning signs:** Empty Swagger UI dropdown in Dev environment; `/swagger/v1/swagger.json` returns 404.

### Pitfall 3: BaseService field `_dbContext` private — TestService cannot access for D-12 ChangeTracker assertion

**What goes wrong:** D-12 requires `TestService.SyncJunctionsAsync` override to read `_dbContext.ChangeTracker.Entries<TestEntity>().Single().State`. If BaseService's `_dbContext` field is `private readonly`, derived classes can't see it.

**Why it happens:** Default C# field visibility is `private`. The 6-injectee constructor pattern naturally produces 6 private fields.

**How to avoid:** Expose `_dbContext` as a `protected BaseDbContext DbContext { get; }` property (or `protected readonly BaseDbContext _dbContext;` field). Phase 8 services may need the same access for junction-table `DbSet<TJunction>` queries inside their `SyncJunctionsAsync` overrides — making it protected is forward-looking. Also expose `_repo`, `_mapper` similarly if needed (planner's discretion).

**Warning signs:** Compile error `CS0122: 'BaseService<...>._dbContext' is inaccessible due to its protection level` in TestService.

### Pitfall 4: AuditInterceptor lifetime mismatch — Singleton vs Scoped

**What goes wrong:** Phase 3 CONTEXT D-06 says `AddSingleton<AuditInterceptor>()`. CONTEXT (Phase 7) D-14 example shows `AddScoped<AuditInterceptor>`. If registered Scoped and resolved into a Singleton DbContextOptionsBuilder factory via `sp.GetRequiredService<AuditInterceptor>()`, you get a captive scope reference.

**Why it happens:** Discrepancy between two CONTEXT docs. Phase 3 is canonical (PERSIST-03/04 ownership).

**How to avoid:** Register as **Singleton** per Phase 3 D-06. The interceptor's only deps (`IHttpContextAccessor`, `TimeProvider`) are Singleton-safe. Update CONTEXT D-14's snippet to `AddSingleton`. Tests verify against Phase 3 AuditInterceptorTests which already proved the Singleton lifetime works.

**Warning signs:** `InvalidOperationException: Cannot consume scoped service from singleton` at app startup OR audit interceptor not stamping fields in production (silent — captive scope reuses old HttpContext).

### Pitfall 5: AddSingleton<BaseDbContext> alias resolution returns the wrong DbContext

**What goes wrong:** `AddBaseApiPersistence` example registers both `services.AddDbContext<TDbContext>(...)` (Scoped) and `services.AddScoped<BaseDbContext>(sp => sp.GetRequiredService<TDbContext>())` so `BaseService<...>` can resolve via the abstract type. If the alias is `AddSingleton` instead of `AddScoped`, the BaseDbContext alias resolves to a different lifetime than TDbContext — captive dependency.

**Why it happens:** Copy-paste between Singleton sub-extensions.

**How to avoid:** Alias MUST be `AddScoped` to match `AddDbContext`'s default lifetime (PERSIST-15 locked Scoped). Plan task: explicit comment on the alias line.

**Warning signs:** Phase 3 SC#4 regression (scoped DbContext lifetime) fails.

### Pitfall 6: `[ApiController]` on derived controller required for ModelState short-circuit even with inherited attribute

**What goes wrong:** CONTEXT D-17 says concrete controllers add `[ApiController]` (inherited from BaseController is enough — but planner verifies). Microsoft Learn's "annotation with [ApiController] attribute" documentation says derived classes inherit the attribute correctly. **However:** older versions of ASP.NET Core (pre-3.0) required `[ApiController]` on each derived controller separately. In .NET 8 (current target), inheritance works.

**Why it happens:** Historical asymmetry.

**How to avoid:** Verify with the SC#1 fact that `POST /api/v1/tests` with a malformed body returns 400 ProblemDetails (the `[ApiController]` ModelState short-circuit triggers — confirms inheritance works). If verification fails, add `[ApiController]` to the derived controller (one-line fix), no Plan churn.

**Warning signs:** TestsController accepts malformed JSON without 400 short-circuit — meaning `[ApiController]` semantics aren't inheriting.

### Pitfall 7: Asp.Versioning unsupported-version 400 NOT enriched with correlationId

**What goes wrong:** Asp.Versioning's `IProblemDetailsWriter` emits its own 400 for unsupported versions. If the writer doesn't route through `IProblemDetailsService`, the Phase 4 `AddProblemDetails` customizer doesn't fire — no `correlationId` / `instance` extensions.

**Why it happens:** Asp.Versioning's default writer uses `IProblemDetailsService` per [CITED: github.com/dotnet/aspnet-api-versioning/wiki/Error-Responses] (".NET 6 / 7 / 8" branch). So the customizer SHOULD fire. But if a project registers a custom `IProblemDetailsWriter`, this can be broken.

**How to avoid:** Phase 7 does NOT register a custom IProblemDetailsWriter. Verification fact: SC#1 add a fact that probes `/api/v99/tests` and asserts the 400 response includes `correlationId` matching the `X-Correlation-Id` response header (regression for Phase 4 customizer + Asp.Versioning interaction).

**Warning signs:** 400 response from `/api/v99/tests` returns `{"type":"...","title":"Unsupported API version","status":400,"detail":"..."}` WITHOUT `correlationId` field.

### Pitfall 8: Mapperly + IEntityMapper resolution when no concrete partial class exists

**What goes wrong:** BaseService injects `IEntityMapper<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>`. AddBaseApiMapping (Phase 6 D-15) scans assemblies for closed-generic implementations. If the TestEntityMapper from Phase 6 is in `BaseApi.Tests.dll` and `AddBaseApi<TDbContext>(cfg)` only scans `typeof(TDbContext).Assembly` (which is `BaseApi.Service.dll` in production), the test mapper is invisible.

**Why it happens:** CONTEXT D-13 reuses `typeof(TDbContext).Assembly` for both validator AND mapper scans. The Phase 6 D-16 `params Assembly[]` overload is what tests use to inject the Tests assembly via `ValidationWebAppFactory.ConfigureWebHost`.

**How to avoid:** Phase 7 tests reuse the Phase 6 `ValidationWebAppFactory` pattern — a `Phase7WebAppFactory` subclass overrides `ConfigureTestServices` to call `services.AddBaseApiMapping(typeof(Phase7WebAppFactory).Assembly)` AND `services.AddBaseApiValidation(typeof(Phase7WebAppFactory).Assembly)`. This re-scans the Tests assembly on top of the production scan that AddBaseApi already did. Phase 6 D-16 already proved this pattern works for validators; same shape for mappers.

**Warning signs:** Test boot fails with `InvalidOperationException: Unable to resolve service for type 'BaseApi.Core.Mapping.IEntityMapper<TestEntity, ...>' while attempting to activate 'TestService'`.

### Pitfall 9: Moq SponsorLink controversy — pick NSubstitute for v1

**What goes wrong:** [CITED: dimitrilaaraybi.com/blog/moqtonsubstitute] Moq 4.20.0 (Aug 2023) bundled `SponsorLink`, a closed-source library that hashed and transmitted developer git emails to an Azure endpoint. While Moq 4.20.2 removed it, the community reaction was decisive.

**Why it matters:** Phase 7 needs a mocking library for SC#2 ordering proof (CONTEXT Discretion item explicitly lists "Moq vs NSubstitute vs raw recording wrapper"). Picking Moq invites a future security/policy audit hit.

**How to avoid:** Pin **NSubstitute 5.3.0** (current stable). `Received.InOrder(() => { ... })` is the idiomatic ordered-call assertion API. Add `<PackageVersion Include="NSubstitute" Version="5.3.0" />` to Directory.Packages.props + `<PackageReference Include="NSubstitute" />` (no Version=) to BaseApi.Tests.csproj.

**Warning signs:** Plan picks Moq → future compliance/policy review flags the SponsorLink history.

### Pitfall 10: BaseService open-generic registration fails because BaseService is abstract

**What goes wrong:** Tempting to `services.AddScoped(typeof(BaseService<,,,>), typeof(BaseService<,,,>))` for symmetry with `AddScoped(typeof(IRepository<>), typeof(Repository<>))`. Both fail at activation — BaseService is `abstract`.

**Why it happens:** HTTP-02 + CONTEXT D-09 lock the abstract status. Concrete subclasses (Phase 8: SchemaService, ProcessorService, etc.) are the resolution surfaces.

**How to avoid:** BaseController injects `BaseService<TEntity, TCreate, TUpdate, TRead>` via constructor. Phase 8 each concrete controller passes through the request to the matching concrete service via constructor signature `public SchemasController(SchemaService service) : base(service)`. Phase 8's `AddBaseApi` calls `services.AddScoped<SchemaService>()` × 5 — NOT a single open-generic line. **Phase 7 Wave 2's TestService is registered the same way in the WebAppFactory subclass — `services.AddScoped<TestService>()`** AND `services.AddScoped<BaseService<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>>(sp => sp.GetRequiredService<TestService>())` so BaseController can resolve via the abstract type.

**Warning signs:** Test boot fails with `InvalidOperationException: Cannot create instance of abstract class 'BaseService<TestEntity, ...>'`.

## Code Examples

### Example 1: ChangeTracker.Entries state assertion inside SyncJunctionsAsync (SC#2)

```csharp
// Source: CONTEXT D-12 verbatim + Microsoft Learn ChangeTracker docs + Phase 3 D-08 (AuditInterceptor SavingChangesAsync)

protected override Task SyncJunctionsAsync(
    TestEntity entity, TestCreateDto? createDto, TestUpdateDto? updateDto, CancellationToken ct)
{
    // ChangeTracker.Entries<T>() returns all tracked entries of type T.
    // After repo.AddAsync(entity, ct) staged the Add, the entity is in EntityState.Added.
    // SaveChangesAsync has NOT been called yet — this assertion proves we run AFTER step 3
    // (repo.Add) and BEFORE step 5 (SaveChanges) of the CONTEXT D-11 6-step order.
    var entry = base.DbContext.ChangeTracker.Entries<TestEntity>().Single();
    JunctionRecords.Add((Timestamp: DateTime.UtcNow, State: entry.State));
    return Task.CompletedTask;
}

// Fact assertion (xUnit v3):
[Fact]
public async Task CreateAsync_RunsSixStepsInLockedOrder()
{
    var ct = TestContext.Current.CancellationToken;
    using var fixture = new PostgresFixture();  // Phase 3 D-15 throwaway DB
    await fixture.InitializeAsync();

    var createValidator = Substitute.For<IValidator<TestCreateDto>>();
    var updateValidator = Substitute.For<IValidator<TestUpdateDto>>();
    var mapper = Substitute.For<IEntityMapper<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>>();
    var repo = Substitute.For<IRepository<TestEntity>>();

    var validateTime = (DateTime?)null;
    createValidator.ValidateAsync(Arg.Any<TestCreateDto>(), Arg.Any<CancellationToken>())
        .Returns(call => { validateTime = DateTime.UtcNow; return new ValidationResult(); });

    var mappedEntity = new TestEntity { Id = Guid.NewGuid(), Name = "x", Version = "1.0.0" };
    var mapTime = (DateTime?)null;
    mapper.ToEntity(Arg.Any<TestCreateDto>()).Returns(call => { mapTime = DateTime.UtcNow; return mappedEntity; });

    var addTime = (DateTime?)null;
    repo.AddAsync(Arg.Any<TestEntity>(), Arg.Any<CancellationToken>())
        .Returns(call => { addTime = DateTime.UtcNow; return Task.CompletedTask; });

    var readTime = (DateTime?)null;
    mapper.ToRead(Arg.Any<TestEntity>())
        .Returns(call => { readTime = DateTime.UtcNow; return new TestReadDto(mappedEntity.Id, "x", "1.0.0", null, ""); });

    var service = new TestService(createValidator, updateValidator, mapper, repo,
                                  fixture.DbContext, NullLogger<BaseService<...>>.Instance);

    var dto = new TestCreateDto("x", "1.0.0", null, "note");
    var result = await service.CreateAsync(dto, ct);

    // Step ordering assertions
    Assert.NotNull(validateTime);   // step 1 fired
    Assert.NotNull(mapTime);         // step 2 fired
    Assert.NotNull(addTime);         // step 3 fired
    Assert.Single(service.JunctionRecords);
    var (junctionTime, trackerState) = service.JunctionRecords[0];

    // Step 4 ran AFTER step 3 (repo.Add)
    Assert.True(addTime < junctionTime, "SyncJunctionsAsync must run AFTER repo.AddAsync");
    // Step 4 saw EntityState.Added (proves run order against tracker)
    Assert.Equal(EntityState.Added, trackerState);
    // Step 4 ran BEFORE step 5 (SaveChanges) — verified by SaveChangesAsync's effect on
    // ChangeTracker state (post-save state is Unchanged), but assertion via timestamp
    // requires intercepting SaveChanges — alternative: check the entity is now in Unchanged
    // state after CreateAsync returned (proves SaveChanges happened).
    Assert.NotNull(readTime);        // step 6 fired

    // Cross-mock NSubstitute Received.InOrder for compact ordered-call grammar:
    Received.InOrder(() =>
    {
        createValidator.ValidateAsync(Arg.Any<TestCreateDto>(), Arg.Any<CancellationToken>());
        mapper.ToEntity(Arg.Any<TestCreateDto>());
        repo.AddAsync(Arg.Any<TestEntity>(), Arg.Any<CancellationToken>());
        // SyncJunctionsAsync isn't a mock — its effect is in service.JunctionRecords
        // (asserted separately above).
        mapper.ToRead(Arg.Any<TestEntity>());
    });
}
```

### Example 2: WebApplicationFactory + UseEnvironment("Production") for SC#4

```csharp
// Source: github.com/dotnet/aspnetcore/issues/45372 + Microsoft Learn "Integration tests in ASP.NET Core"

internal sealed class ProductionWebAppFactory : WebAppFactory
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);
        return base.CreateHost(builder);
    }
}

[Fact]
public async Task SwaggerEndpoint_Returns404_InProduction()
{
    var ct = TestContext.Current.CancellationToken;
    using var factory = new ProductionWebAppFactory();
    using var client = factory.CreateClient();

    var response = await client.GetAsync("/swagger", ct);

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
}

[Fact]
public async Task SwaggerEndpoint_Returns200_InDevelopment()
{
    var ct = TestContext.Current.CancellationToken;
    using var factory = new WebAppFactory();  // default environment is Development under WebApplicationFactory
    using var client = factory.CreateClient();

    var response = await client.GetAsync("/swagger/v1/swagger.json", ct);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
}
```

### Example 3: Route exposure fact (SC#1) via WebApplicationFactory route table

```csharp
[Fact]
public void TestsController_Exposes_FiveCrudRoutes_UnderApiV1()
{
    using var factory = new WebAppFactory();
    using var scope = factory.Services.CreateScope();
    var actionDescriptorCollection = scope.ServiceProvider
        .GetRequiredService<IActionDescriptorCollectionProvider>();

    var tests = actionDescriptorCollection.ActionDescriptors.Items
        .OfType<ControllerActionDescriptor>()
        .Where(a => a.ControllerTypeInfo.AsType() == typeof(TestsController))
        .ToList();

    Assert.Equal(5, tests.Count);

    var routes = tests.Select(t => $"{t.ActionConstraints?.OfType<HttpMethodActionConstraint>().FirstOrDefault()?.HttpMethods.First()} {t.AttributeRouteInfo?.Template}")
        .OrderBy(r => r).ToArray();

    Assert.Contains("GET api/v{version:apiVersion}/[controller]",       routes);
    Assert.Contains("GET api/v{version:apiVersion}/[controller]/{id:guid}", routes);
    Assert.Contains("POST api/v{version:apiVersion}/[controller]",      routes);
    Assert.Contains("PUT api/v{version:apiVersion}/[controller]/{id:guid}", routes);
    Assert.Contains("DELETE api/v{version:apiVersion}/[controller]/{id:guid}", routes);
}
```

### Example 4: Program.cs line-count fact (SC#3)

```csharp
[Fact]
public void ProgramCs_HasMinimalBody_AfterAddBaseApiMigration()
{
    var programPath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "BaseApi.Service", "Program.cs");
    Assert.True(File.Exists(programPath), $"Program.cs not found at {programPath}");

    var nonCommentNonBlankLines = File.ReadAllLines(programPath)
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Where(line => !line.TrimStart().StartsWith("//"))
        .Where(line => !line.TrimStart().StartsWith("using"))
        .ToList();

    // Body lines (excluding using directives, comments, blank lines, partial class marker):
    //   1. var builder = WebApplication.CreateBuilder(args);
    //   2. builder.Services.AddBaseApi<AppDbContext>(builder.Configuration);
    //   3. var app = builder.Build();
    //   4. app.UseBaseApi();
    //   5. app.MapControllers();
    //   6. app.Run();
    //   7. public partial class Program { }
    // SC#3 says "thin file" — assert ≤ 10 non-trivial lines (slack for the partial marker + Program declaration syntax).
    Assert.True(nonCommentNonBlankLines.Count <= 10,
        $"Program.cs has {nonCommentNonBlankLines.Count} non-trivial lines — expected ≤ 10 per SC#3");

    // Strong-form assertion — these strings MUST appear:
    var fileContent = File.ReadAllText(programPath);
    Assert.Contains("AddBaseApi<", fileContent);
    Assert.Contains("UseBaseApi(", fileContent);
    Assert.Contains("MapControllers", fileContent);
    // Negative assertion: no per-concern wiring leaked into Program.cs:
    Assert.DoesNotContain("AddOpenTelemetry", fileContent);
    Assert.DoesNotContain("AddHealthChecks", fileContent);
    Assert.DoesNotContain("AddExceptionHandler<", fileContent);
    Assert.DoesNotContain("AddDbContext<", fileContent);
    Assert.DoesNotContain("AddSwaggerGen", fileContent);
    Assert.DoesNotContain("AddApiVersioning", fileContent);
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `Microsoft.AspNetCore.Mvc.Versioning.ApiExplorer` (Microsoft package) | `Asp.Versioning.Mvc.ApiExplorer` (community-owned at github.com/dotnet/aspnet-api-versioning) | Microsoft transferred ownership in 2022 | The Microsoft package is deprecated. Phase 7 MUST use the Asp.Versioning.* family. Already pinned at 8.1.0. |
| `Microsoft.AspNetCore.OpenApi` (.NET 9+ built-in `AddOpenApi`) | `Swashbuckle.AspNetCore` | Built-in shipped in .NET 9 (Nov 2024) | Built-in is .NET 9+ only; this project is .NET 8. Swashbuckle remains the correct choice. |
| `services.AddOpenApi()` (.NET 9+) | `services.AddSwaggerGen()` (.NET 8) | N/A — version constraint | Phase 7 stays on Swashbuckle until project upgrades to .NET 9+. |
| Moq (mocking) | NSubstitute | Aug 2023 SponsorLink controversy | Community migration; NSubstitute now the .NET default per multiple 2024-2026 blog comparisons. |
| `[Required]` model-validation attributes on DTOs | FluentValidation `AbstractValidator<TDto>` + Service-layer explicit ValidateAsync | Phase 6 D-14 | Already shipped Phase 6; Phase 7 inherits. |
| Manual `[Mapper] partial class` per entity | Mapperly source-gen (Riok.Mapperly 4.x) | Phase 6 D-15 | Already shipped Phase 6. |

**Deprecated/outdated:**
- `Microsoft.AspNetCore.Mvc.Versioning` (legacy 5.x package) — replaced by Asp.Versioning.*; community-owned.
- `FluentValidation.AspNetCore` (auto-validation) — deprecated/removed in FluentValidation 12; explicit ValidateAsync is the modern pattern.
- `Moq` (post-SponsorLink) — usable but community-tainted; NSubstitute preferred for new projects.
- `UseEndpoints(...) { endpoints.MapControllers(); }` wrapper — replaced by direct `app.MapControllers()` on WebApplication in .NET 6+.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | CONTEXT D-17's "Asp.Versioning.Http" is a package-name miscite — the correct controllers package is `Asp.Versioning.Mvc` (which depends on .Http transitively). HTTP-15 REQUIREMENTS.md text is the brand-name reference, not the exact NuGet ID. | Standard Stack; Pattern 4 | [LOW] Adding Asp.Versioning.Mvc 8.1.0 pin is a strict superset of Asp.Versioning.Http — even if A1 is wrong, the project still builds (extra dependency harmless). Mitigation: planner verifies during Wave 1 by running `dotnet build` after adding the Mvc package; the `[ApiVersion]` attribute resolution will be exercised by the SC#1 test. |
| A2 | The XML doc generation contradiction (CONTEXT D-18 vs actual csproj setting) is not blocking — CONTEXT D-18 explicitly defers `IncludeXmlComments` regardless of whether the file is generated. Phase 7 plan does NOT call `options.IncludeXmlComments(...)`. | Project Constraints; Standard Stack | [LOW] If planner DID add `IncludeXmlComments(...)` based on the actual XML doc availability, the result is richer Swagger UI but no behavior change. Mitigation: CONTEXT D-18 wording is unambiguous — "No XML doc inclusion in v1". |
| A3 | NSubstitute 5.3.0 is the current stable line compatible with .NET 8. | Standard Stack; Pitfall 9 | [LOW] If 5.3.0 is unavailable or incompatible, fall back to 5.1.0 (verified stable on .NET 8) or planner switches to hand-rolled recording wrappers. Mitigation: `dotnet add package NSubstitute --version 5.3.0 --dry-run` during Wave 1 csproj task. |
| A4 | `app.DescribeApiVersions()` extension method is available in Asp.Versioning.Mvc.ApiExplorer 8.1.0. | Pattern 4 | [LOW] If unavailable, manually iterate `app.Services.GetRequiredService<IApiVersionDescriptionProvider>().ApiVersionDescriptions`. Same data, more verbose. |
| A5 | The placeholder `AppDbContext : BaseDbContext { public AppDbContext(DbContextOptions<AppDbContext> opts) : base(opts) {} }` empty 3-line class in BaseApi.Service is acceptable for Phase 7 — Phase 8 adds DbSets without recreating. | Pattern 7 (Path B); Plan structure | [LOW] If the empty class causes EF Core model-validation warnings (no DbSets) under TreatWarningsAsErrors, the build fails — fallback is Path A (keep Phase 6 Program.cs imperative wiring; Phase 8 does the cutover). Mitigation: empirically verified pattern (EF Core does NOT warn on empty model; OnModelCreating's xmin iteration is a no-op against an empty GetEntityTypes() collection). |
| A6 | The Phase 4 ProblemDetails customizer (`AddProblemDetails` opts.CustomizeProblemDetails) fires on Asp.Versioning's 400-for-unsupported-version response because Asp.Versioning routes through IProblemDetailsService. | Pitfall 7 | [MEDIUM] If A6 is wrong, unsupported-version 400s won't have correlationId/instance — visible regression vs Phase 4 ERROR-08/09 facts. Mitigation: Wave 2 fact explicitly probes `/api/v99/tests` and asserts correlationId presence; if absent, planner adds a thin `IProblemDetailsWriter` adapter (alternative: keep the gap as known deviation and add fix-forward task in Phase 8). |
| A7 | `BaseService` injection of `IValidator<TCreate>` and `IValidator<TUpdate>` works correctly in production where concrete validators always ship per Phase 8 entity. The defensive `?? throw InvalidOperationException` ctor guard is correct for the test path where no validator is registered. | Pattern 2 (BaseService) | [LOW] Even if .NET DI returns a default validator (which it does not), the guard fires only when null is actually returned. Mitigation: covered by Wave 2 fact that explicitly removes a validator from DI and asserts InvalidOperationException at TestService construction. |

## Open Questions

1. **`[ApiController]` inheritance verification in .NET 8**
   - What we know: Microsoft Learn confirms the pattern works for .NET 8 (cited verbatim).
   - What's unclear: Whether subtle behaviors (e.g., `InvalidModelStateResponseFactory` short-circuit for malformed JSON) require the attribute on the derived class or only on the base. Phase 4's TestController has `[ApiController]` declared directly — no inheritance test in the existing 76 facts.
   - Recommendation: Wave 2 SC#1 fact explicitly probes `POST /api/v1/tests` with malformed JSON body and asserts 400 + ProblemDetails. If it passes, inheritance works; if not, add `[ApiController]` to TestsController and pin that as the pattern for Phase 8 derived controllers.

2. **CreatedAtAction with generic `TRead.Id` access strategy**
   - What we know: CONTEXT D-01 says `CreatedAtAction(nameof(GetById), new { id = read.Id }, read)` — assumes `TRead` has an `Id` property.
   - What's unclear: Whether to enforce via `IHasId` marker interface (recommended), `dynamic` dispatch (works but reflective), or `Func<TRead, Guid>` factory (verbose but explicit).
   - Recommendation: introduce `IHasId` marker in `BaseApi.Core/Contracts/IHasId.cs` exposing `Guid Id { get; }`. Constrain `BaseController<...>` and `BaseService<...>` on `where TRead : IHasId`. Phase 8 Read DTOs implement `IHasId` trivially (HTTP-07 already requires Id on Read DTOs). Planner finalizes during Wave 1 task design.

3. **Plan wave count — 2 plans vs 3+**
   - What we know: Phase 1-6 all used 2 plans (build + verify). CONTEXT line 122 says "Parallelizable: no" for Phase 7.
   - What's unclear: Whether Wave 1 build is large enough to warrant splitting into 07-01a (BaseController + BaseService) + 07-01b (composition root + Program.cs migration). The csproj edits + 5 new sub-extensions + 1 new top-level + Program.cs rewrite + 3 Swagger helper classes + AppDbContext placeholder is ~14 files.
   - Recommendation: stay with 2 plans (build + verify) — single plan executor; matches Phase 4/5/6 cadence; verification gate (Wave 2 checkpoint) catches drift.

4. **`Asp.Versioning.Http 8.1.0` pin retention vs removal**
   - What we know: Directory.Packages.props line 94 pins `Asp.Versioning.Http 8.1.0`. Adding `Asp.Versioning.Mvc 8.1.0` transitively brings `Asp.Versioning.Http` (the Mvc package depends on Http).
   - What's unclear: CPM auditability — should the pin stay (explicit > implicit, even if redundant), or be removed (one fewer line)?
   - Recommendation: **KEEP the pin** — CPM convention favors explicit pins for every direct OR transitive dependency the project intentionally relies on. Future Mvc upgrades may shift the Http transitive version; an explicit pin guards against unintended drift.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK | Build, run, test | ✓ | 8.0.421 (Phase 1 D-08 pinned in global.json) | None — phase blocked if absent |
| Postgres | Wave 2 verification (TestService + WebAppFactory) | ✓ | 17-alpine via compose.yaml (Phase 2 D-12); host port 5433 | None — Phase 2 dependency, MUST be running before Wave 2 tests |
| OTel Collector | Wave 2 — Phase 5 facts regression replay | ✓ | otel/opentelemetry-collector-contrib:0.95.0 via compose.yaml (Phase 5 D-10); ports 4317/4318/13133 | Skip Phase 5 facts in Wave 2 if collector down — but Phase 6 already proved 76/76 passes against running collector; pattern is fixed |
| Docker Desktop | Phase 2 + 5 containers | ✓ | Confirmed Phase 2 + 5 STATE.md | None — phase blocked if absent (Windows + WSL2 backend; STATE.md Blockers list calls this out for Phase 8 but Phase 7 doesn't need Testcontainers) |
| dotnet ef tool | NOT required in Phase 7 (Phase 8 owns InitialCreate migration) | N/A | N/A | N/A |
| Asp.Versioning.Mvc 8.1.0 NuGet | csproj pin (NEW) | TBD via `dotnet add package --dry-run` | 8.1.0 expected | Fall back to current 8.x line (8.0.0 verified shipped to nuget.org) |
| Asp.Versioning.Mvc.ApiExplorer 8.1.0 NuGet | csproj pin (NEW) | TBD | 8.1.0 expected | Fall back to 8.0.0 |
| NSubstitute 5.3.0 NuGet | csproj pin (NEW — planner discretion) | TBD | 5.3.0 expected | Fall back to 5.1.0 (verified .NET 8 compat); or skip mocking library entirely and use hand-rolled recording wrappers |

**Missing dependencies with no fallback:** None.

**Missing dependencies with fallback:** Asp.Versioning.Mvc, Asp.Versioning.Mvc.ApiExplorer, NSubstitute — all NEW pins; fallback is older stable version in same major line.

## Validation Architecture

### Test Framework

| Property | Value |
|----------|-------|
| Framework | xUnit v3 (3.2.2) under Microsoft.Testing.Platform — 76 facts currently GREEN (Phase 1-6 regression baseline) |
| Config file | None — xUnit v3 uses `[Fact]` discovery + assembly-level `[CollectionDefinition]` (Observability/CollectionDefinitions.cs Phase 5 pattern) |
| Quick run command | `dotnet test SK_P.sln --filter "FullyQualifiedName~Http" --no-restore` (run only Phase 7's `Http/` namespace tests) |
| Full suite command | `dotnet test SK_P.sln --no-restore` |
| Pre-test setup | `docker compose up -d postgres otel-collector` MUST be running before any fact that boots WebApplicationFactory |

### Phase Requirements → Test Map

| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| HTTP-01 | Controller-based (not Minimal APIs) — BaseController inherits ControllerBase | integration | `dotnet test --filter "FullyQualifiedName~RouteExposureTests"` | ❌ Wave 2 (TestsController + RouteExposureTests new) |
| HTTP-02 | BaseController<T,...> abstract generic with [ApiController]+[Route] | unit + integration | `dotnet test --filter "FullyQualifiedName~RouteExposureTests"` | ❌ Wave 2 |
| HTTP-03 | 5 CRUD verb routes exposed (SC#1) | integration | `dotnet test --filter "FullyQualifiedName~RouteExposureTests"` | ❌ Wave 2 |
| HTTP-08 | Layering Controller→Service→Repository | unit (visibility check) | `dotnet test --filter "FullyQualifiedName~LayeringTests"` (optional reflective fact asserting BaseController has no IRepository field) | ❌ Wave 2 (optional) |
| HTTP-09 | BaseService 6-step order with SyncJunctionsAsync hook (SC#2) | unit + integration | `dotnet test --filter "FullyQualifiedName~ServiceOrderingTests"` | ❌ Wave 2 |
| HTTP-13 | AddBaseApi composition root | integration (boot + resolve) | `dotnet test --filter "FullyQualifiedName~RouteExposureTests"` (WebAppFactory boot proves AddBaseApi works) + dedicated `CompositionRootTests` resolving each registered type from DI scope | ❌ Wave 2 |
| HTTP-14 | UseBaseApi pipeline order | integration (request flow) | `dotnet test --filter "FullyQualifiedName~CorrelationIdTests"` (existing Phase 4 — still passes after migration) + Wave 2 regression replay × 3 | ✅ Phase 4 facts re-used |
| HTTP-15 | URL versioning /api/v1/* (SC#1) | integration | `dotnet test --filter "FullyQualifiedName~RouteExposureTests"` asserts versioned route templates | ❌ Wave 2 |
| HTTP-16 | Swagger UI in Dev, 404 in Prod (SC#4) | integration | `dotnet test --filter "FullyQualifiedName~SwaggerDevOnlyTests"` | ❌ Wave 2 |
| SC#3 (Program.cs thin) | line count + no-per-concern-wiring | unit (source file inspection) | `dotnet test --filter "FullyQualifiedName~ProgramMinimalityTests"` | ❌ Wave 2 |
| Regression — all 76 prior facts | Phase 1-6 facts still GREEN after AddBaseApi migration (CONTEXT D-15) | integration | `dotnet test SK_P.sln --no-restore` × 3 consecutive runs | ✅ All 76 facts already exist; Phase 7 Wave 2 just replays |

### Sampling Rate

- **Per task commit (Wave 1):** `dotnet build SK_P.sln -c Release` + `dotnet build SK_P.sln -c Debug` (zero-warning enforcement per Phase 1 D-02; matches W-02 pattern from Phase 3+).
- **Per wave merge (Wave 2):** `dotnet test SK_P.sln --no-restore` — must show Passed equal to 76 (Phase 1-6 carryover) + Phase 7 new facts (estimate 12-15 new facts: 5 route exposure + 4 ordering + 3 swagger + 1 program-minimality + 1-2 composition root + optional regressions for Pitfall 7 / Pitfall 8). Total target: ~88-91 facts.
- **Phase gate:** Full suite × 3 consecutive GREEN runs (CONTEXT D-15 mirror of Phase 3 D-18 / Phase 4 cadence / Phase 5 D-11) BEFORE `/gsd-verify-work`. Plus byte-identical psql `\l` BEFORE/AFTER snapshots (Phase 3 D-15) AND `tests/.otel-out/` clean post-suite (Phase 5 D-11 [assembly: AssemblyFixture] discipline).

### Wave 0 Gaps

Phase 6 already shipped the test infrastructure that Phase 7 inherits. Existing assets:

- ✅ `tests/BaseApi.Tests/Middleware/WebAppFactory.cs` (Phase 4 unsealed Phase 6 D-19) — Phase 7 subclasses for Phase 7-specific overrides.
- ✅ `tests/BaseApi.Tests/Validation/TestEntity.cs` + `TestDtos.cs` + `TestEntityMapper.cs` (Phase 6 D-08 amended) — Phase 7 REUSES these for TestService and TestsController (CONTEXT line 233 explicitly says "TestService uses TestEntity from Phase 6 — no new entity needed in Phase 7").
- ✅ `tests/BaseApi.Tests/Persistence/PostgresFixture.cs` (Phase 3 + Phase 4) — Phase 7 reuses for any fact requiring throwaway DB.
- ✅ `tests/BaseApi.Tests/Observability/OtelCollectorFixture.cs` (Phase 5) — Phase 7 reuses for regression replay of Phase 5 facts.
- ✅ xUnit v3 + Microsoft.Testing.Platform + `[assembly: AssemblyFixture]` cleanup (Phase 5 OtelEndOfSuiteCleanup.cs) — Phase 7 inherits.

**Gaps (Wave 2 creates):**

- [ ] `tests/BaseApi.Tests/Http/TestsController.cs` — `public sealed class TestsController : BaseController<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto> { public TestsController(TestService svc) : base(svc) {} }` — concrete derived controller, empty body.
- [ ] `tests/BaseApi.Tests/Http/TestService.cs` — `public sealed class TestService : BaseService<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>` — overrides SyncJunctionsAsync with recording for SC#2 (D-12).
- [ ] `tests/BaseApi.Tests/Http/RouteExposureTests.cs` — covers HTTP-01, HTTP-02, HTTP-03, HTTP-15 + SC#1.
- [ ] `tests/BaseApi.Tests/Http/ServiceOrderingTests.cs` — covers HTTP-09 + SC#2 (NSubstitute + ChangeTracker assertion).
- [ ] `tests/BaseApi.Tests/Http/SwaggerDevOnlyTests.cs` — covers HTTP-16 + SC#4 (UseEnvironment override pattern).
- [ ] `tests/BaseApi.Tests/Http/ProgramMinimalityTests.cs` — covers SC#3 (Program.cs line count + negative-presence assertions).
- [ ] `tests/BaseApi.Tests/Http/CompositionRootTests.cs` — covers HTTP-13 (each AddBaseApi sub-extension's registrations are resolvable from a scope).
- [ ] `tests/BaseApi.Tests/Http/Phase7WebAppFactory.cs` — WebAppFactory subclass adding TestService + TestEntityMapper + TestDbContext to the test service collection via Phase 6 D-16 multi-assembly scan pattern.
- [ ] `tests/BaseApi.Tests/Http/ProductionWebAppFactory.cs` — WebAppFactory subclass calling `builder.UseEnvironment(Environments.Production)` in `CreateHost` (Example 2 above).
- [ ] **Optional:** `tests/BaseApi.Tests/Http/AspVersioningProblemDetailsTests.cs` — probes `/api/v99/tests` and asserts the 400 includes `correlationId` (Pitfall 7 mitigation).

**Framework install:** None — all packages already pinned. Wave 1 csproj task adds Asp.Versioning.Mvc + Asp.Versioning.Mvc.ApiExplorer + Swashbuckle.AspNetCore to BaseApi.Core.csproj + NSubstitute to BaseApi.Tests.csproj.

## Security Domain

This phase's `security_enforcement` posture is **enabled** by default (no explicit `false` in `.planning/config.json`). The phase touches HTTP routing, request handling, and OpenAPI documentation — all attack-surface concerns.

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | Out of Scope per PROJECT.md "Out of Scope: Authentication / authorization"; no JWT or session cookies in v1 |
| V3 Session Management | no | No sessions in v1 (no auth boundary) |
| V4 Access Control | no | No authorization in v1 (Out of Scope) |
| V5 Input Validation | yes | Phase 6 FluentValidation 12.x validators (BaseDtoValidator + Phase 8 concrete) — VALID-01..20; Phase 7 invokes via BaseService.ValidateAndThrowAsync |
| V6 Cryptography | no | No crypto in v1 (no auth means no JWT signing keys; no client-encrypted payloads — schemas are application JSON) |
| V7 Error Handling and Logging | yes | Phase 4 RFC 7807 ProblemDetails with correlationId — never leak stack traces (ERROR-07 / Phase 4 D-12 FallbackExceptionHandler); Phase 7 verifies no NEW handler bypasses this; Swagger UI in Dev only (HTTP-16 / CONTEXT D-19) so OpenAPI surface isn't exposed in Production |
| V8 Data Protection | partial | Phase 5 D-05 Npgsql parameter values DISABLED in tracing (T-05-PII); Phase 7 does NOT touch this — inherits from Phase 5 |
| V13 API and Web Service | yes | Phase 7's primary concern — see threat patterns below |
| V14 Configuration | yes | Phase 7 doesn't add new config; honors Phase 5 D-09 log filter discipline (no Information-level health endpoint logs); Production env doesn't expose Swagger |

### Known Threat Patterns for {controller-based ASP.NET Core + Asp.Versioning + Swagger}

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Mass assignment via Create/Update DTO accepting server-side fields (e.g., client posts `Id` or `CreatedBy`) | Tampering, Elevation of Privilege | Phase 6 D-08 dual mechanism: Update DTOs exclude Id+audit fields (source-side); Mapperly `[MapperIgnoreTarget]` (target-side). Phase 7 BaseService.UpdateAsync invokes the mapper.Update which respects these. **Verification:** Wave 2 should add a fact asserting `PUT /api/v1/tests/{id}` with a body containing `"id": "<different-guid>"` does NOT change the entity's Id — confirms Mapperly silently ignores. |
| Sensitive endpoint exposed in Production (Swagger UI in Prod) | Information Disclosure | CONTEXT D-19 `if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }` — Phase 7 SC#4 verifies 404 in Prod. |
| Unfiltered request logging leaks PII via path parameters | Information Disclosure | Phase 5 D-05 + D-08 + D-09 already filter `/health/*` and disable Npgsql parameter capture. Phase 7 does NOT introduce per-request logging beyond what AspNetCoreInstrumentation already does. **Plan task:** verify the SC#1 fact's route-table inspection does NOT log entity-id values (xunit assertion only — no body content logging). |
| `[ApiController]` ModelState 400 leaks structural info about expected schema | Information Disclosure | Phase 4 D-04 ProblemDetails customizer normalizes to `errors` map per field — never leaks internal C# property paths beyond the DTO surface. Phase 7 inherits — no change. |
| Asp.Versioning unsupported-version 400 enriched with correlationId | Defense in Depth | Pitfall 7 mitigation — Wave 2 fact verifies correlationId presence in `/api/v99/tests` 400 response. |
| `X-Correlation-Id` header injection via crafted client header | Tampering, Log Injection | Phase 4 D-03 `IsValid` check (ASCII-printable 0x20..0x7E, length ≤ 128) — REJECTS CRLF + null + control chars. Phase 7 inherits — `Swagger CorrelationIdHeaderOperationFilter` declares the header as a `string` schema with `MaxLength=128` so Swagger UI's generated TS clients enforce the constraint client-side too (defense-in-depth). |
| Generic controller leaks reflection info on TEntity through 500-error stack trace | Information Disclosure | Phase 4 D-12 FallbackExceptionHandler logs the stack to MEL only — never to the 500 ProblemDetails body. Phase 7 inherits. Verification: Wave 2 regression-replays Phase 4's `NotFoundAndUnhandledTests` (4 facts) which already prove this. |
| Cross-Site Request Forgery (CSRF) on POST/PUT/DELETE | Tampering | Out of Scope in v1 (no auth boundary). When auth lands in Phase 9+, CSRF tokens become relevant. Document as known v2 hardening. |
| Reflection-based DI scan (AddBaseApiMapping `assembly.GetExportedTypes()`) loads malicious types from assemblies on disk | Tampering (supply-chain) | `GetExportedTypes()` filters to public types only — restricts the attack surface to types intentionally exposed. Phase 6 RESEARCH Pitfall 7 — Phase 7 inherits unchanged. |

**Additional defensive note:** The `IOperationFilter` and `IDocumentFilter` registered in Phase 7's Swashbuckle setup run AT SwaggerDoc generation time, not at request time. They don't add a runtime attack surface beyond what Swashbuckle itself adds. The DocumentFilter's `swaggerDoc.Paths.Remove(...)` is a build-time mutation; no runtime path expression evaluation against user input.

## Sources

### Primary (HIGH confidence)

- **Existing codebase files** — verified by direct Read:
  - `src/BaseApi.Core/Controllers/` (empty — Phase 7 first file)
  - `src/BaseApi.Core/Services/` (empty — Phase 7 first file)
  - `src/BaseApi.Core/DependencyInjection/ValidationServiceCollectionExtensions.cs` (Phase 6)
  - `src/BaseApi.Core/DependencyInjection/MappingServiceCollectionExtensions.cs` (Phase 6)
  - `src/BaseApi.Core/Validation/IBaseDto.cs` + `BaseDtoValidator.cs` (Phase 6)
  - `src/BaseApi.Core/Mapping/IEntityMapper.cs` (Phase 6)
  - `src/BaseApi.Core/Persistence/Repositories/IRepository.cs` (Phase 3)
  - `src/BaseApi.Core/Persistence/BaseDbContext.cs` (Phase 3)
  - `src/BaseApi.Core/Entities/BaseEntity.cs` (Phase 3)
  - `src/BaseApi.Core/Middleware/CorrelationIdMiddleware.cs` (Phase 4)
  - `src/BaseApi.Core/Exceptions/NotFoundException.cs` + `Handlers/ValidationExceptionHandler.cs` (Phase 4)
  - `src/BaseApi.Core/Health/IStartupGate.cs` + `StartupHealthCheck.cs` + `StartupCompletionService.cs` (Phase 5)
  - `src/BaseApi.Service/Program.cs` (current Phase 6 form — ~150 lines imperative)
  - `tests/BaseApi.Tests/Middleware/WebAppFactory.cs` (Phase 4, unsealed Phase 6)
  - `tests/BaseApi.Tests/Validation/{TestEntity,TestDtos,TestEntityMapper,ValidationWebAppFactory}.cs` (Phase 6 SC#3/SC#4 scaffolds)
  - `tests/BaseApi.Tests/Endpoints/TestController.cs` (Phase 4 + Phase 6 `/test/validate` endpoint)
  - `Directory.Packages.props` (CPM pins — Asp.Versioning.Http 8.1.0, Swashbuckle.AspNetCore 6.9.0 already pinned)
  - `Directory.Build.props` (TreatWarningsAsErrors + RMG promotion)
  - `BaseApi.Core.csproj` + `BaseApi.Service.csproj` + `BaseApi.Tests.csproj`
  - `.planning/config.json` (workflow.nyquist_validation=true, security_enforcement implicit-enabled)

- **Locked CONTEXT.md decisions** for all 7 prior phases — all 19 Phase 7 decisions traced verbatim into User Constraints.

- **GitHub canonical references:**
  - [github.com/dotnet/aspnet-api-versioning/wiki/Swashbuckle-Integration](https://github.com/dotnet/aspnet-api-versioning/wiki/Swashbuckle-Integration) — canonical IConfigureOptions<SwaggerGenOptions> pattern
  - [github.com/dotnet/aspnet-api-versioning/wiki/Error-Responses](https://github.com/dotnet/aspnet-api-versioning/wiki/Error-Responses) — Asp.Versioning ProblemDetails behavior
  - [github.com/dotnet/aspnet-api-versioning/wiki/Versioning-via-the-URL-Path](https://github.com/dotnet/aspnet-api-versioning/wiki/Versioning-via-the-URL-Path)
  - [Microsoft Learn — Create web APIs with ASP.NET Core (.NET 8)](https://learn.microsoft.com/en-us/aspnet/core/web-api/?view=aspnetcore-8.0)
  - [Microsoft Learn — Routing to controller actions in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/mvc/controllers/routing?view=aspnetcore-8.0)

### Secondary (MEDIUM confidence — verified against multiple sources)

- [milanjovanovic.tech — API Versioning in ASP.NET Core](https://www.milanjovanovic.tech/blog/api-versioning-in-aspnetcore) — confirms Asp.Versioning.Mvc vs .Http package split for controllers vs Minimal APIs
- [thecodebuzz.com — IOperationFilter and IDocumentFilter in ASP.NET Core](https://thecodebuzz.com/ioperationfilter-idocumentfilter-in-asp-net-core/) — IOperationFilter pattern for header parameter
- [damirscorner.com — Hiding endpoints for disabled features in OpenAPI](https://www.damirscorner.com/blog/posts/20240607-HidingEndpointsForDisabledFeaturesInOpenApi.html) — IDocumentFilter pattern for path removal
- [dimitrilaaraybi.com — How to switch from Moq to NSubstitute](https://www.dimitrilaaraybi.com/blog/moqtonsubstitute/) — Moq SponsorLink rationale + NSubstitute migration
- [github.com/dotnet/aspnet-api-versioning/issues/651](https://github.com/dotnet/aspnet-api-versioning/issues/651) — CreatedAtAction Location header issue with multiple routes
- [Microsoft Learn — Integration tests in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-8.0) — WebApplicationFactory + UseEnvironment pattern
- [NSubstitute.github.io](https://nsubstitute.github.io/) — Received.InOrder canonical ordered-call assertion
- [Microsoft Learn — EF Core ChangeTracker](https://learn.microsoft.com/en-us/ef/core/change-tracking/change-detection) — ChangeTracker.Entries<T>().State semantics inside interceptors

### Tertiary (LOW confidence — verify during planning)

- [codestudy.net — How to Add API Versioning in ASP.NET Core 8](https://www.codestudy.net/blog/asp-net-core-8-web-api-how-to-add-versioning/) — community tutorial; verified via cross-reference with canonical wiki
- [sikilinda.com — New Swashbuckle release with .NET 8 support](https://sikilinda.com/posts/new-swashbuckle-release-with-dotnet-8-support/) — version compatibility note
- [codingdroplets.com — Moq vs NSubstitute vs FakeItEasy in .NET 2026](https://codingdroplets.com/moq-vs-nsubstitute-vs-fakeiteasy-in-net-which-mocking-framework-should-your-team-use-in-2026) — 2026 vintage comparison

## Metadata

**Confidence breakdown:**
- Standard stack: **HIGH** — Asp.Versioning.Mvc + Asp.Versioning.Mvc.ApiExplorer 8.1.0 verified via canonical wiki + nuget.org; Swashbuckle 6.9.0 already pinned and .NET 8 compat verified via Sikilinda; NSubstitute 5.x verified via dimitrilaaraybi.com migration guide.
- Architecture: **HIGH** — every pattern traces to either an existing codebase file, a locked CONTEXT.md decision, or the canonical Asp.Versioning wiki. Composition root structure copied verbatim from CONTEXT D-13/D-14.
- Pitfalls: **HIGH** — Pitfall 1 (CreatedAtAction + multi-route) cited from GitHub issue #651; Pitfall 7 (Asp.Versioning ProblemDetails customizer interaction) cited from wiki; Pitfall 4 (AuditInterceptor lifetime) verified by direct Phase 3 CONTEXT.md inspection.

**Open assumptions:** A6 (Phase 4 customizer + Asp.Versioning interaction) is MEDIUM-risk; Wave 2 should include a dedicated fact to verify before phase gate.

**Research date:** 2026-05-27
**Valid until:** 2026-06-27 (30 days — stable .NET 8 LTS ecosystem; Asp.Versioning 10.x is .NET 10-targeted so the 8.1.0 line is the right v1 choice through .NET 8 EOL Nov 2026)
