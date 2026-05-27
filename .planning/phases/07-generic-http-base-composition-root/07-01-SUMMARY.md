---
phase: 07-generic-http-base-composition-root
plan: 01
subsystem: api

tags: [aspnetcore, controllers, di, composition-root, asp-versioning, swashbuckle, openapi, mvc, generic-base, dotnet8]

# Dependency graph
requires:
  - phase: 03-ef-core-persistence-base
    provides: BaseEntity, BaseDbContext (abstract + xmin), IRepository<TEntity>, Repository<TEntity> sealed, AuditInterceptor
  - phase: 04-cross-cutting-middleware-error-handling
    provides: CorrelationIdMiddleware, 4 IExceptionHandler chain, NotFoundException, AddProblemDetails customizer
  - phase: 05-observability-health-probes
    provides: OTel logs via MEL bridge / metrics / traces, IStartupGate, StartupHealthCheck, StartupCompletionService, UIResponseWriter
  - phase: 06-validation-mapping-base
    provides: IBaseDto, BaseDtoValidator<T>, IEntityMapper<,,,>, AddBaseApiValidation, AddBaseApiMapping
provides:
  - IHasId marker interface (TRead generic constraint)
  - Abstract generic BaseController<TEntity,TCreate,TUpdate,TRead> exposing 5 CRUD verbs at /api/v{version:apiVersion}/[controller]
  - Abstract generic BaseService<TEntity,TCreate,TUpdate,TRead> owning locked 6-step CreateAsync order + virtual SyncJunctionsAsync hook
  - 3 Swagger helpers (CorrelationIdHeaderOperationFilter, HideHealthEndpointsDocumentFilter, ConfigureSwaggerOptions)
  - 5 internal/public sub-extensions (AddBaseApiPersistence, AddBaseApiObservability, AddBaseApiHealth, AddBaseApiErrorHandling, AddBaseApiHttp)
  - 2 public top-level entries (AddBaseApi<TDbContext>, UseBaseApi)
  - Empty placeholder AppDbContext satisfying AddBaseApi<TDbContext> type argument
  - Program.cs collapsed from ~150 lines to ~7-line declarative composition root
affects: [08-entities, plan-07-02]

# Tech tracking
tech-stack:
  added:
    - Asp.Versioning.Mvc 8.1.0
    - Asp.Versioning.Mvc.ApiExplorer 8.1.0
    - Swashbuckle.AspNetCore 6.9.0 (PackageReference; pin existed)
    - NSubstitute 5.3.0 (test mocking; Plan 07-02 consumer)
  patterns:
    - "Open-generic abstract base controller decorated [ApiController]+[ApiVersion]+[Route] inherited by empty-body concretes"
    - "BaseService 6-step CreateAsync verb order with virtual SyncJunctionsAsync hook between repo.Add and SaveChanges"
    - "Sub-extension composition root: top-level AddBaseApi<TDbContext> chains internal AddBaseApi* methods on IServiceCollection"
    - "OTel observability invoked separately on IHostApplicationBuilder (D-13 amendment — MEL bridge needs ILoggingBuilder)"
    - "URL-segment API versioning via Asp.Versioning.Mvc + SubstituteApiVersionInUrl + GroupNameFormat 'v'VVV"
    - "Swashbuckle one SwaggerDoc per ApiVersionDescription via IConfigureOptions<SwaggerGenOptions>"
    - "Dev-only Swagger UI gated by app.Environment.IsDevelopment() (Production returns 404)"

key-files:
  created:
    - src/BaseApi.Core/Contracts/IHasId.cs
    - src/BaseApi.Core/Controllers/BaseController.cs
    - src/BaseApi.Core/Services/BaseService.cs
    - src/BaseApi.Core/Swagger/CorrelationIdHeaderOperationFilter.cs
    - src/BaseApi.Core/Swagger/HideHealthEndpointsDocumentFilter.cs
    - src/BaseApi.Core/Swagger/ConfigureSwaggerOptions.cs
    - src/BaseApi.Core/DependencyInjection/PersistenceServiceCollectionExtensions.cs
    - src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs
    - src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs
    - src/BaseApi.Core/DependencyInjection/ErrorHandlingServiceCollectionExtensions.cs
    - src/BaseApi.Core/DependencyInjection/HttpServiceCollectionExtensions.cs
    - src/BaseApi.Core/DependencyInjection/BaseApiServiceCollectionExtensions.cs
    - src/BaseApi.Core/DependencyInjection/BaseApiApplicationBuilderExtensions.cs
    - src/BaseApi.Service/AppDbContext.cs
  modified:
    - Directory.Packages.props
    - src/BaseApi.Core/BaseApi.Core.csproj
    - tests/BaseApi.Tests/BaseApi.Tests.csproj
    - src/BaseApi.Service/Program.cs

key-decisions:
  - "CONTEXT D-13 amendment encoded: AddBaseApi<TDbContext> chains 6 sub-extensions on IServiceCollection (Persistence -> Health -> ErrorHandling -> Http -> Validation -> Mapping); Observability is invoked separately on IHostApplicationBuilder from Program.cs because OTel MEL bridge requires ILoggingBuilder which IServiceCollection does not expose"
  - "AuditInterceptor lifetime reconciled: Singleton (RESEARCH Pitfall 4 / Phase 3 D-06 canonical) — overrides CONTEXT D-14's Scoped wording snippet"
  - "BaseDbContext alias lifetime reconciled: Scoped via sp.GetRequiredService<TDbContext>() (RESEARCH Pitfall 5) so BaseService can resolve abstract type without captive-lifetime risk"
  - "IHasId marker interface chosen (RESEARCH Open Q2 option b) over dynamic dispatch / reflection in BaseController.Create for accessing TRead.Id"
  - "BaseService validator null-guards throw InvalidOperationException with descriptive message (CONTEXT Discretion option a) — Phase 8 always ships concrete validators so missing-validator scenario is a misconfiguration not a runtime branch"
  - "BaseService exposes _dbContext as protected BaseDbContext DbContext { get; } property (RESEARCH Pitfall 3) so derived RecordingTestService (Plan 07-02) can read ChangeTracker state inside SyncJunctionsAsync override"
  - "BaseController is abstract but NOT sealed; BaseService is abstract but NOT sealed (Phase 8 concretes inherit with empty bodies)"
  - "ProducesResponseType attributes use status-code-only or non-generic ProblemDetails variants (Rule 1 — C# CS0416 forbids typeof(TRead) attribute arguments on open generics); ActionResult<TRead> return type still surfaces schema on Swagger"
  - "AddBaseApiObservability promoted from internal to public static (Rule 3 — required for cross-assembly invocation from BaseApi.Service/Program.cs per D-13 amendment)"
  - "Existing Asp.Versioning.Http 8.1.0 pin RETAINED alongside new Asp.Versioning.Mvc 8.1.0 pin (RESEARCH Open Q4 — CPM explicit-pin auditability)"

patterns-established:
  - "Open-generic abstract base controller + base service with closed-generic concretes via empty inheritance (HTTP-02 / HTTP-08)"
  - "Composition root sub-extension chain (Phase 7 / D-13)"
  - "OTel observability extension on IHostApplicationBuilder (D-13 amendment)"
  - "BaseService 6-step CreateAsync verb order with virtual SyncJunctionsAsync hook (D-11)"

requirements-completed: [HTTP-01, HTTP-02, HTTP-03, HTTP-08, HTTP-09, HTTP-13, HTTP-14, HTTP-15, HTTP-16]

# Metrics
duration: 11m
completed: 2026-05-27
---

# Phase 7 Plan 01: Generic HTTP Base + Composition Root Summary

**14 new BaseApi.Core files (IHasId marker + BaseController + BaseService + 3 Swagger helpers + 7 DI extensions) + 1 new AppDbContext + Program.cs collapsed from ~150-line imperative wiring to ~7-line declarative composition root via AddBaseApi<TDbContext> + UseBaseApi + builder.AddBaseApiObservability (D-13 amendment).**

## Performance

- **Duration:** 11m 13s
- **Started:** 2026-05-27T14:05:24Z
- **Completed:** 2026-05-27T14:16:37Z
- **Tasks:** 4 of 4 complete (all autonomous, no checkpoints)
- **Files created:** 14
- **Files modified:** 4

## Accomplishments

- Abstract generic `BaseController<TEntity,TCreate,TUpdate,TRead>` with `[ApiController]+[ApiVersion("1.0")]+[Route("api/v{version:apiVersion}/[controller]")]` exposing exactly 5 CRUD verbs (List/GetById/Create/Update/Delete) with locked status codes per CONTEXT D-01..D-04
- Abstract generic `BaseService<TEntity,TCreate,TUpdate,TRead>` owning the locked 6-step CreateAsync verb order (validate -> ToEntity -> repo.Add -> SyncJunctionsAsync -> SaveChanges -> ToRead) with protected DbContext property + virtual no-op SyncJunctionsAsync hook + InvalidOperationException null-guards on validators
- Composition root: public `AddBaseApi<TDbContext>(IConfiguration)` chains 6 internal/public sub-extensions on IServiceCollection (Persistence -> Health -> ErrorHandling -> Http -> Validation -> Mapping); separate public `AddBaseApiObservability(builder, cfg)` on IHostApplicationBuilder for OTel MEL bridge (D-13 amendment)
- Middleware pipeline: public `UseBaseApi()` in locked D-19 order (UseExceptionHandler -> CorrelationIdMiddleware -> UseRouting -> Dev-only UseSwagger + UseSwaggerUI -> MapHealthChecks x3)
- Swagger surface: per-version `SwaggerDoc` via `IConfigureOptions<SwaggerGenOptions>` + `CorrelationIdHeaderOperationFilter` (X-Correlation-Id documented on every op) + `HideHealthEndpointsDocumentFilter` (defense-in-depth)
- Program.cs collapsed from ~150 lines to 7 non-trivial body lines (cap: 10) — `WebApplication.CreateBuilder` + `builder.AddBaseApiObservability` + `builder.Services.AddBaseApi<AppDbContext>` + `Build` + `app.UseBaseApi` + `MapControllers` + `Run` + load-bearing `public partial class Program { }` marker
- Empty placeholder `AppDbContext : BaseDbContext` enables the declarative form without Phase 8 entity model (Phase 3 BaseDbContext.OnModelCreating safely iterates an empty entity collection)
- Zero-warning Release + Debug build across SK_P.sln (Phase 1 D-02 inherited)

## Task Commits

Each task was committed atomically:

1. **Task 1: Pin packages and add PackageReferences** — `c86cf08` (chore)
2. **Task 2: Create IHasId + BaseController + BaseService** — `099b5e4` (feat)
3. **Task 3: 3 Swagger helpers + 5 internal sub-extensions + 2 public DI extensions** — `ff6d866` (feat)
4. **Task 4: AppDbContext placeholder + Program.cs declarative rewrite** — `89dbf55` (feat)

**Plan metadata:** _to be assigned on final docs commit_

## Files Created/Modified

### Created (14)

| File | Role |
|------|------|
| `src/BaseApi.Core/Contracts/IHasId.cs` | Marker interface for TRead generic constraint exposing Guid Id |
| `src/BaseApi.Core/Controllers/BaseController.cs` | Abstract generic controller exposing 5 CRUD verbs against versioned URL |
| `src/BaseApi.Core/Services/BaseService.cs` | Abstract generic orchestrator owning the locked 6-step verb order + SyncJunctionsAsync hook |
| `src/BaseApi.Core/Swagger/CorrelationIdHeaderOperationFilter.cs` | IOperationFilter adding X-Correlation-Id header parameter to every operation |
| `src/BaseApi.Core/Swagger/HideHealthEndpointsDocumentFilter.cs` | IDocumentFilter stripping /health/* paths from OpenAPI doc |
| `src/BaseApi.Core/Swagger/ConfigureSwaggerOptions.cs` | IConfigureOptions<SwaggerGenOptions> emitting one SwaggerDoc per ApiVersionDescription + registering the two filters |
| `src/BaseApi.Core/DependencyInjection/PersistenceServiceCollectionExtensions.cs` | Internal AddBaseApiPersistence<TDbContext>(cfg) wiring DbContext + Npgsql + snake_case + AuditInterceptor (Singleton) + IRepository<> open-generic + BaseDbContext alias=Scoped |
| `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs` | Public AddBaseApiObservability(builder, cfg) on IHostApplicationBuilder wiring OTel logs (MEL bridge) + metrics + traces + AddNpgsql |
| `src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs` | Internal AddBaseApiHealth(cfg) wiring IStartupGate + 3 probes + StartupCompletionService hosted service |
| `src/BaseApi.Core/DependencyInjection/ErrorHandlingServiceCollectionExtensions.cs` | Internal AddBaseApiErrorHandling() wiring AddProblemDetails customizer + 4 IExceptionHandler in D-06 order |
| `src/BaseApi.Core/DependencyInjection/HttpServiceCollectionExtensions.cs` | Internal AddBaseApiHttp(cfg) wiring AddControllers + Asp.Versioning.Mvc + ApiExplorer + Swashbuckle + 2 filters + ConfigureSwaggerOptions |
| `src/BaseApi.Core/DependencyInjection/BaseApiServiceCollectionExtensions.cs` | Public AddBaseApi<TDbContext>(cfg) top-level composition root chaining 6 sub-extensions on IServiceCollection |
| `src/BaseApi.Core/DependencyInjection/BaseApiApplicationBuilderExtensions.cs` | Public UseBaseApi() middleware pipeline extension |
| `src/BaseApi.Service/AppDbContext.cs` | Empty placeholder DbContext satisfying Program.cs AddBaseApi<AppDbContext> type argument (Phase 8 adds DbSets) |

### Modified (4)

| File | Change |
|------|--------|
| `Directory.Packages.props` | Added `Asp.Versioning.Mvc 8.1.0`, `Asp.Versioning.Mvc.ApiExplorer 8.1.0`, `NSubstitute 5.3.0` pins; existing `Asp.Versioning.Http 8.1.0` + `Swashbuckle.AspNetCore 6.9.0` pins retained |
| `src/BaseApi.Core/BaseApi.Core.csproj` | Added `PackageReference` entries for `Asp.Versioning.Mvc`, `Asp.Versioning.Mvc.ApiExplorer`, `Swashbuckle.AspNetCore` (no Version= per CPM) |
| `tests/BaseApi.Tests/BaseApi.Tests.csproj` | Added `PackageReference` for `NSubstitute` (no Version=, no PrivateAssets — runtime test library; for Plan 07-02 SC#2 ordering proof) |
| `src/BaseApi.Service/Program.cs` | Rewritten from ~150-line imperative wiring to ~7-line declarative composition root delegating to AddBaseApi + UseBaseApi + AddBaseApiObservability |

## Decisions Made

| Decision | Rationale |
|----------|-----------|
| CONTEXT D-13 amendment encoded: 6-call chain on IServiceCollection + separate AddBaseApiObservability invocation on IHostApplicationBuilder | OpenTelemetry MEL bridge (`builder.Logging.AddOpenTelemetry`) requires `ILoggingBuilder` surface, which `IServiceCollection` does not expose. Alternatives rejected: (a) overload AddBaseApi to take IHostApplicationBuilder — bloats public API, (b) defer MEL wiring to post-Build — OTel docs mandate pre-build registration, (c) cast IServiceCollection -> IHostApplicationBuilder — architecturally invalid |
| AuditInterceptor lifetime = Singleton (overrides CONTEXT D-14's Scoped snippet) | Phase 3 D-06 canonical; AuditInterceptor's IHttpContextAccessor + TimeProvider deps are both Singleton-safe (RESEARCH Pitfall 4 reconciliation) |
| BaseDbContext alias = Scoped via `sp.GetRequiredService<TDbContext>()` | Matches AddDbContext default lifetime + PERSIST-15 locked Scoped pattern; BaseService can resolve the abstract type without captive-lifetime risk (RESEARCH Pitfall 5) |
| IHasId marker over dynamic dispatch / reflection for TRead.Id access in BaseController.Create | Static-typed approach; no `dynamic` keyword (project does not use); HTTP-07 already requires Id on every Read DTO so the constraint is free (RESEARCH Open Q2 option b) |
| InvalidOperationException null-guards on IValidator<TCreate>/<TUpdate> with descriptive message | Phase 8 always ships concrete validators; missing-validator is a misconfiguration that should fail loudly at startup, not be silently skipped (CONTEXT Discretion option a) |
| Protected BaseDbContext DbContext { get; } property exposure | Plan 07-02 RecordingTestService overrides SyncJunctionsAsync and must read `DbContext.ChangeTracker.Entries<TEntity>().Single().State` to verify EntityState.Added (RESEARCH Pitfall 3) |
| BaseController + BaseService abstract but NOT sealed | Phase 8's 5 concrete controllers + 5 concrete services inherit with empty bodies (HTTP-02 / HTTP-12) |
| Asp.Versioning.Http 8.1.0 pin RETAINED alongside new Asp.Versioning.Mvc 8.1.0 pin | CPM explicit-pin auditability (RESEARCH Open Q4) — Mvc transitively pulls Http but keeping the explicit pin is project convention |

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Added `using Microsoft.AspNetCore.Http;` to BaseController.cs**
- **Found during:** Task 2 (BaseController compile)
- **Issue:** `StatusCodes` constants used in `[ProducesResponseType(...)]` attributes resolved to `error CS0103: The name 'StatusCodes' does not exist in the current context`. BaseApi.Core uses `Microsoft.NET.Sdk` (not `.Web`) and `FrameworkReference Microsoft.AspNetCore.App` doesn't auto-import the Http namespace via ImplicitUsings
- **Fix:** Added `using Microsoft.AspNetCore.Http;` after `using Microsoft.AspNetCore.Mvc;`
- **Files modified:** `src/BaseApi.Core/Controllers/BaseController.cs`
- **Verification:** `dotnet build src/BaseApi.Core -c Release` exit 0
- **Committed in:** `099b5e4` (Task 2 commit)

**2. [Rule 1 - Bug] Removed `typeof(TRead)` and `typeof(IReadOnlyList<TRead>)` from ProducesResponseType attributes**
- **Found during:** Task 2 (BaseController compile)
- **Issue:** C# CS0416 `'TRead': an attribute argument cannot use type parameters`. C# attributes are evaluated at compile time and cannot reference open generic type parameters. The plan body specified `[ProducesResponseType(typeof(IReadOnlyList<TRead>), StatusCodes.Status200OK)]` and `[ProducesResponseType(typeof(TRead), StatusCodes.Status201Created)]` which is a C# language restriction not anticipated in the plan template
- **Fix:** Replaced typed `ProducesResponseType` with status-code-only variants (e.g., `[ProducesResponseType(StatusCodes.Status200OK)]`) and retained non-generic-type variants (`typeof(Microsoft.AspNetCore.Mvc.ProblemDetails)`, `typeof(Microsoft.AspNetCore.Mvc.ValidationProblemDetails)`). The action return type `ActionResult<TRead>` still surfaces the generic schema in the Swagger document via ApiExplorer. Added inline `<remarks>` documenting the deviation. Phase 8 concrete controllers MAY add typed `[ProducesResponseType(typeof(ConcreteReadDto), 200)]` in their bodies if they want explicit per-status schemas.
- **Files modified:** `src/BaseApi.Core/Controllers/BaseController.cs`
- **Verification:** `dotnet build src/BaseApi.Core -c Release` exit 0; all 5 `[Http*]` attributes still present (per Task 2 acceptance criteria); `CreatedAtAction(nameof(GetById), new { id = read.Id }, read)` preserved verbatim
- **Committed in:** `099b5e4` (Task 2 commit)

**3. [Rule 1 - Bug] Added `using Microsoft.Extensions.DependencyInjection;` to ConfigureSwaggerOptions.cs**
- **Found during:** Task 3 (ConfigureSwaggerOptions compile)
- **Issue:** `SwaggerGenOptions` extension methods (`SwaggerDoc`, `OperationFilter<T>`, `DocumentFilter<T>`) are defined in `Swashbuckle.AspNetCore.SwaggerGen.dll` but their declaring class `SwaggerGenOptionsExtensions` lives in namespace `Microsoft.Extensions.DependencyInjection` (verified via the Swashbuckle 6.9.0 net8.0 XML doc). The plan body listed only `using Swashbuckle.AspNetCore.SwaggerGen;` and `using Microsoft.OpenApi.Models;` — neither imports the extension methods
- **Fix:** Added `using Microsoft.Extensions.DependencyInjection;` with inline comment naming `SwaggerGenOptionsExtensions`
- **Files modified:** `src/BaseApi.Core/Swagger/ConfigureSwaggerOptions.cs`
- **Verification:** `dotnet build src/BaseApi.Core -c Release` exit 0
- **Committed in:** `ff6d866` (Task 3 commit)

**4. [Rule 1 - Bug] Added `using Microsoft.Extensions.Hosting;` to BaseApiApplicationBuilderExtensions.cs**
- **Found during:** Task 3 (BaseApiApplicationBuilderExtensions compile)
- **Issue:** `app.Environment.IsDevelopment()` — `IsDevelopment()` extension is in `Microsoft.Extensions.Hosting` namespace (on `IHostEnvironment` base interface). The plan body listed only `Microsoft.AspNetCore.Hosting` which contains the older `HostingEnvironmentExtensions.IsDevelopment(IHostingEnvironment)` overload that doesn't match `IWebHostEnvironment`'s contemporary surface
- **Fix:** Added `using Microsoft.Extensions.Hosting;` with inline comment naming `HostEnvironmentEnvExtensions.IsDevelopment(IHostEnvironment)`
- **Files modified:** `src/BaseApi.Core/DependencyInjection/BaseApiApplicationBuilderExtensions.cs`
- **Verification:** `dotnet build src/BaseApi.Core -c Release` exit 0
- **Committed in:** `ff6d866` (Task 3 commit)

**5. [Rule 3 - Blocking] Promoted AddBaseApiObservability from internal to public**
- **Found during:** Task 4 (full solution build after Program.cs rewrite)
- **Issue:** The plan body declared `AddBaseApiObservability` as `internal static`, but the D-13 amendment (encoded in the plan's `<context_deviation>` block) requires it to be invoked from `BaseApi.Service/Program.cs` across the assembly boundary (`builder.AddBaseApiObservability(builder.Configuration)`). `internal` extension methods are only callable from the same assembly — compile error CS1061 fired in Program.cs. This is a plan-internal inconsistency between the visibility specifier and the D-13 amendment's call-site requirement
- **Fix:** Promoted `internal static class ObservabilityServiceCollectionExtensions` -> `public static class` and `internal static IHostApplicationBuilder AddBaseApiObservability(...)` -> `public static IHostApplicationBuilder AddBaseApiObservability(...)`. Added explicit `<para>` doc block naming the deviation rationale and citing the alternatives considered (InternalsVisibleTo was rejected — adds indirection without value). Public visibility matches the other top-level entries: `AddBaseApi`, `UseBaseApi` (Phase 7), `AddBaseApiValidation`, `AddBaseApiMapping` (Phase 6)
- **Files modified:** `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs`
- **Verification:** `dotnet build SK_P.sln -c Release` exit 0 (all 3 projects); `dotnet build SK_P.sln -c Debug` exit 0
- **Committed in:** `89dbf55` (Task 4 commit)

---

**Total deviations:** 5 auto-fixed (4 Rule 1 bugs from C#/library API mismatches in the plan body, 1 Rule 3 visibility plan-gap surfacing the D-13 amendment vs. internal-visibility tension)
**Impact on plan:** All auto-fixes were required for the plan to compile and link correctly. None changed the encoded semantics (verb shapes, status codes, 6-step CreateAsync order, pipeline order, lifetime reconciliations, threat mitigations are all preserved verbatim). The Task 2 ProducesResponseType simplification is the only deviation visible in the produced API surface — Phase 8 concrete controllers may re-add typed variants without touching BaseController.

## Authentication Gates

None encountered during execution.

## Issues Encountered

None during planned work — all 5 deviations were auto-fixed under deviation rules 1 and 3 and committed inline.

## User Setup Required

None - no external service configuration required for Wave 1 (build-clean verification only).

## Open Items Handed Forward to Plan 07-02

1. **Regression replay:** All 76 Phase 1-6 facts must pass × 3 consecutive `dotnet test SK_P.sln` runs post-AddBaseApi composition (Phase 3 D-18 cadence; CONTEXT D-15). Encoded as PowerShell loop in Plan 07-02 Task 3 `<automated>` block per validation matrix.
2. **New fact files:** Plan 07-02 builds `tests/BaseApi.Tests/Composition/` (TestsController, RecordingTestService, AddBaseApiFacts, UseBaseApiPipelineFacts), `Controllers/BaseControllerRoutesFacts`, `Services/BaseServiceOrderingFacts + NotFoundFacts`, `Versioning/VersioningFacts`, `Swagger/SwaggerEnvironmentFacts`, `Composition/ProgramMinimalityFacts`. The NSubstitute pin landed in Plan 07-01 Task 1 enables the SC#2 ordering proof.
3. **Wave 0 test scaffolds (Plan 07-02 Task 1):** Phase7TestDbContext, TestCreateDtoValidator, Phase7WebAppFactory, ProductionWebAppFactory, TestDtos IBaseDto+IHasId combination. The plan 07-01 BaseController + BaseService surface is sized to accept all of these without further base modification.
4. **CONTEXT.md D-13 carry-through:** At Phase 7 close, update `.planning/phases/07-generic-http-base-composition-root/07-CONTEXT.md` D-13 with the amendment note (6-call IServiceCollection chain + separate AddBaseApiObservability on IHostApplicationBuilder + engineering rationale around OTel MEL bridge needing ILoggingBuilder). This summary acts as the authoritative source until then.
5. **Manual UI smoke (HTTP-16 / SC#4):** Plan 07-02 Task 4 manual checkpoint — browse `/swagger` in Development to confirm UI lists 5 verbs under "Tests" group; `curl /swagger` in Production returns 404. Live-process verification supplementing the automated SwaggerEnvironmentFacts (WebApplicationFactory<Program> Dev/Prod env override).

## Next Phase Readiness

Plan 07-01 ships the entire BaseApi.Core composition root surface required by Plan 07-02. The Program.cs migration is complete and verified build-clean × Release + Debug. The next executor (Plan 07-02) lands the test seam and regression replay; no further BaseApi.Core changes anticipated for Phase 7.

## Self-Check: PASSED

**File existence** (14 / 14):
- FOUND: src/BaseApi.Core/Contracts/IHasId.cs
- FOUND: src/BaseApi.Core/Controllers/BaseController.cs
- FOUND: src/BaseApi.Core/Services/BaseService.cs
- FOUND: src/BaseApi.Core/Swagger/CorrelationIdHeaderOperationFilter.cs
- FOUND: src/BaseApi.Core/Swagger/HideHealthEndpointsDocumentFilter.cs
- FOUND: src/BaseApi.Core/Swagger/ConfigureSwaggerOptions.cs
- FOUND: src/BaseApi.Core/DependencyInjection/PersistenceServiceCollectionExtensions.cs
- FOUND: src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs
- FOUND: src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs
- FOUND: src/BaseApi.Core/DependencyInjection/ErrorHandlingServiceCollectionExtensions.cs
- FOUND: src/BaseApi.Core/DependencyInjection/HttpServiceCollectionExtensions.cs
- FOUND: src/BaseApi.Core/DependencyInjection/BaseApiServiceCollectionExtensions.cs
- FOUND: src/BaseApi.Core/DependencyInjection/BaseApiApplicationBuilderExtensions.cs
- FOUND: src/BaseApi.Service/AppDbContext.cs

**Commit existence** (4 / 4):
- FOUND: c86cf08 chore(07-01) — pin Asp.Versioning.Mvc + ApiExplorer + NSubstitute and add PackageReferences
- FOUND: 099b5e4 feat(07-01) — add IHasId marker + abstract generic BaseController + BaseService
- FOUND: ff6d866 feat(07-01) — add 3 Swagger helpers + 5 internal sub-extensions + 2 public DI extensions
- FOUND: 89dbf55 feat(07-01) — land AppDbContext placeholder and rewrite Program.cs to declarative form

**Build verification:**
- `dotnet restore SK_P.sln` exits 0
- `dotnet build SK_P.sln -c Release` exits 0 with zero warnings
- `dotnet build SK_P.sln -c Debug` exits 0 with zero warnings

**must_haves.truths verification:** all 18 truths from the plan frontmatter pass via grep + line-ordering inspection + build-clean status (verified inline during task execution).

---
*Phase: 07-generic-http-base-composition-root*
*Completed: 2026-05-27*
