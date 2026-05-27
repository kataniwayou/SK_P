# Roadmap: Steps API

## Overview

This roadmap delivers the Steps API as a .NET 8 modular monolith in 8 dependency-ordered phases. Phases 1-2 establish the skeleton and Postgres runtime. Phases 3-7 build the reusable `BaseApi.Core` infrastructure layer by layer in strict code-dependency order: persistence base before cross-cutting concerns, cross-cutting concerns before observability, observability before the validation/mapping seam, then the generic HTTP base and composition root. Phase 8 plugs the five concrete entities (Schema -> Processor -> Step -> Assignment -> Workflow) into the finished base, generates the initial migration, builds the runtime image, and stands up the integration test harness. The build order is forced by the code-dependency graph (BaseEntity is a type constraint on Repository/Controller; AddBaseApi requires every base component) â€” it is not arbitrary milestone padding.

## Phases

**Phase Numbering:**
- Integer phases (1, 2, 3): Planned milestone work
- Decimal phases (2.1, 2.2): Urgent insertions (marked with INSERTED)

Decimal phases appear between their surrounding integers in numeric order.

- [x] **Phase 1: Repository Scaffold** - Solution structure, SDK pin, central package management, and config skeleton compile cleanly
- [x] **Phase 2: Postgres + Docker Compose** - Local Postgres 17 container is reachable from the host with persistent storage and a healthcheck
- [x] **Phase 3: EF Core Persistence Base** - `BaseEntity`, `BaseDbContext`, `AuditInterceptor`, snake_case convention, and generic `Repository<T>` exist before any migration is generated
- [x] **Phase 4: Cross-Cutting Middleware + Error Handling** - Correlation IDs flow end-to-end and every error returns RFC 7807 Problem Details with the correlation ID
- [x] **Phase 5: Observability + Health Probes** - OTel logs, metrics, and traces reach the Collector; three distinct health probes (live/ready/startup) respond correctly
- [ ] **Phase 6: Validation + Mapping Base** - Base DTO validator + Mapperly + `IEntityMapper<,,,>` seam are wired so any future entity slots in without rewriting base rules
- [x] **Phase 7: Generic HTTP Base + Composition Root** - `BaseController`/`BaseService` + `AddBaseApi`/`UseBaseApi` compose the runnable service with versioned routes and Swagger
- [ ] **Phase 8: Entity Build-Out + Migrations + Docker Runtime + Tests** - All 5 entities CRUD over real Postgres with smoke + error-mapping integration tests passing

## Phase Details

### Phase 1: Repository Scaffold
**Goal**: Establish the solution layout, SDK pin, and central package management so every subsequent phase compiles against a known toolchain.
**Depends on**: Nothing (first phase)
**Requirements**: INFRA-01, INFRA-02, INFRA-03, INFRA-04
**Success Criteria** (what must be TRUE):
  1. `dotnet build` from the repo root succeeds with zero warnings against the empty `BaseApi.Core` class library, `BaseApi.Service` webapi, and `BaseApi.Tests` test project
  2. `dotnet --version` invoked at the repo root returns the SDK version pinned in `global.json` (8.0.421), not whatever is globally installed
  3. Adding a NuGet reference in any project resolves its version from `Directory.Packages.props` (no per-project `<Version>` attributes)
  4. `appsettings.json` in `BaseApi.Service` contains `Logging`, `Service` (`Name="sk-api"`, `Version="3.2.0"`), `ConnectionStrings:Postgres`, and `OpenTelemetry` sections (values may be placeholders) and is valid JSON with no comments
**Plans**: 3 plans
- [x] 01-01-PLAN.md â€” Repo-root foundation files: global.json (SDK pin), Directory.Build.props (warnings-as-errors + strictness), Directory.Packages.props (22 NuGet pins via CPM), .editorconfig (Microsoft .NET style), .gitignore (dotnet flavor), .gitattributes (LF endings), README.md (prereqs + quickstart)
- [x] 01-02-PLAN.md â€” Solution + 3 projects: SK_P.sln, BaseApi.Core.csproj (class library), BaseApi.Service.csproj (webapi with Program.cs D-10 scaffold + appsettings REQ INFRA-04), BaseApi.Tests.csproj (xUnit v3 + MetaTest sanity), Core folder skeleton (11 .gitkeep folders), Service folder skeleton (3 .gitkeep folders)
- [x] 01-03-PLAN.md â€” Verification + smoke: dotnet --version=8.0.421, dotnet restore, dotnet build (Release+Debug, 0 warnings), dotnet test (Sanity passes), dotnet run smoke (host boots, GET / returns 404), SUMMARY documenting Phase 1 SC#1-4 met
**UI hint**: no
**Parallelizable**: no (single foundation step)

### Phase 2: Postgres + Docker Compose
**Goal**: Stand up a local Postgres 17 container with persistent storage and a healthcheck that the service will later depend on.
**Depends on**: Phase 1
**Requirements**: INFRA-06, INFRA-07
**Success Criteria** (what must be TRUE):
  1. `docker compose up postgres` brings up `postgres:17-alpine` and `pg_isready` reports healthy within the container's healthcheck interval
  2. Connecting from the host with `psql` using the configured connection string succeeds and lists the default `postgres` database
  3. Running `docker compose down` (without `-v`) and then `docker compose up postgres` again preserves any rows previously inserted in the named volume
  4. The compose file declares `BaseApi.Service` with `depends_on: postgres: condition: service_healthy` so service startup will block until Postgres is reachable
**Plans**: 2 plans
- [x] 02-01-PLAN.md â€” compose.yaml + .env + .gitignore + README + appsettings.Development.json port reconcile (CONTEXT.md D-01..D-13, closes Phase 1 D-14 carry-forward)
- [x] 02-02-PLAN.md â€” verification + smoke battery (SC#1 health, SC#2 psql connect, SC#3 named-volume persistence, SC#4 depends_on graph) + D-15 cleanup
**UI hint**: no
**Parallelizable**: no (linear setup)

### Phase 3: EF Core Persistence Base
**Goal**: Build the persistence foundation in `BaseApi.Core` â€” `BaseEntity`, `BaseDbContext`, `AuditInterceptor`, snake_case convention, and the generic `Repository<T>` â€” before any migration is generated so the convention applies to the very first schema.
**Depends on**: Phase 1
**Requirements**: ENTITY-01, ENTITY-02, PERSIST-02, PERSIST-03, PERSIST-04, PERSIST-05, PERSIST-06, PERSIST-07, PERSIST-11, PERSIST-15, PERSIST-16
**Success Criteria** (what must be TRUE):
  1. A unit test or scratch console can construct a `BaseDbContext` subclass with a trivial `DbSet<T>` and `EnsureCreated()` against the Phase 2 Postgres, producing tables with snake_case identifiers (e.g., `created_at`, not `CreatedAt`)
  2. Inserting a `BaseEntity` subclass through `Repository<T>` and calling `SaveChangesAsync()` on the context stamps `CreatedAt`/`UpdatedAt` with `DateTime.UtcNow` (`Kind == Utc`) and writes them to `timestamptz` columns without Npgsql throwing `InvalidCastException`
  3. With an `IHttpContextAccessor.HttpContext?.User?.Identity?.Name` of `"alice"`, the inserted row's `created_by` equals `"alice"`; with no HTTP context, `created_by` is null (no crash)
  4. Resolving `DbContext` twice from the same DI scope returns the same instance; resolving from a different scope returns a different instance (Scoped lifetime verified)
**Plans**: 2 plans
- [x] 03-01-PLAN.md â€” Build plan: append PERSIST-16 to REQUIREMENTS.md (D-03b); pin Microsoft.Extensions.TimeProvider.Testing 8.10.0 in Directory.Packages.props (D-13); add EF Core + Npgsql + EFCore.NamingConventions PackageReferences + FrameworkReference Microsoft.AspNetCore.App to BaseApi.Core.csproj (D-12) and BaseApi.Tests.csproj (D-13); create BaseEntity.cs, BaseDbContext.cs, AuditInterceptor.cs, IRepository.cs, Repository.cs (verbatim skeletons from RESEARCH.md); aggregate `dotnet build` Release+Debug zero warnings per W-02 (D-17)
- [x] 03-02-PLAN.md â€” Verification + smoke (autonomous:false, D-18) â€” SHIPPED 2026-05-27: created 9 test files (TestEntity/TestDbContext/PostgresFixture/StubHttpContextAccessor scaffold + 5 facts: SchemaTests SC#1, AuditInterceptorTests SC#2+SC#3 as 2 facts, DiLifetimeTests SC#4, XminConcurrencyTokenTests Dim 6 PERSIST-16, RepositorySurfaceTests Dim 7 PERSIST-11); `dotnet test` exit 0 with Passed: 7 Failed: 0 against Phase 2 Postgres at localhost:5433; byte-identical `psql \l` BEFORE/AFTER snapshots (4 baseline DBs each, zero leaks) confirm D-15 cleanup; 03-02-SUMMARY documents SC#1-4 + Dim 6 + Dim 7 ALL GREEN (D-14, D-15, D-18 honored)
**UI hint**: no
**Parallelizable**: no (snake_case + AuditInterceptor + BaseEntity are tightly coupled and must land in a single commit before the first migration)

### Phase 4: Cross-Cutting Middleware + Error Handling
**Goal**: Wire correlation-ID middleware, the global `IExceptionHandler`, and Postgres SQLSTATE -> HTTP mapping so any HTTP error path produced by later phases already returns RFC 7807 with a correlation ID.
**Depends on**: Phase 3 (needs Postgres + EF concepts for SQLSTATE tests)
**Requirements**: OBSERV-09, OBSERV-10, OBSERV-11, ERROR-01, ERROR-02, ERROR-03, ERROR-04, ERROR-05, ERROR-06, ERROR-07, ERROR-08, ERROR-09, ERROR-10, ERROR-11
**Success Criteria** (what must be TRUE):
  1. A request without an `X-Correlation-Id` header receives a generated UUID echoed back in the `X-Correlation-Id` response header on a 2xx, 4xx, and 5xx response path; a request that supplies the header sees the same value echoed back
  2. A handler that throws `FluentValidation.ValidationException` produces HTTP 400 with `Content-Type: application/problem+json`, a field-level `errors` map, and a `correlationId` field matching the response header
  3. An EF `SaveChanges` that violates a FK constraint surfaces as HTTP 422 with the offending FK field name in `detail` (SQLSTATE 23503 mapped); a unique-constraint violation surfaces as HTTP 409 (SQLSTATE 23505) with the offending field name; both responses include `correlationId` and `instance` (request path)
  4. A custom `NotFoundException` thrown from a service produces HTTP 404 with the resource type + id in `detail`; an unhandled exception produces HTTP 500 with a generic message and no stack trace in the body (stack trace is logged only)
  5. `[ApiController]` model-binding failures (e.g., malformed JSON body) produce the same Problem Details shape as FluentValidation failures â€” no divergent error format
**Plans**: 2 plans
- [x] 04-01-PLAN.md â€” Build + wire: Npgsql + FluentValidation csproj refs (CPM); CorrelationIdMiddleware (D-03 + Pitfall 3 ASCII-printable input); NotFoundException (D-07); PostgresExceptionMapper (D-08, Option A regex preserving `_id` suffix); 4 IExceptionHandler classes in D-06 order (NotFound â†’ Validation â†’ DbUpdate with Pitfall 7 concurrency-FIRST â†’ Fallback with D-12 LogError-only); Program.cs edits (AddHttpContextAccessor + AddProblemDetails customizer + AddExceptionHandlerÃ—4 + UseExceptionHandler â†’ UseMiddleware<CorrelationIdMiddleware> â†’ UseRouting); zero-warning Release + Debug builds (Phase 1 D-02 + Phase 3 W-02)
- [x] 04-02-PLAN.md â€” Verification battery (autonomous:false, Phase 3 D-18) â€” SHIPPED 2026-05-27: created 13 test files (5 fixtures Wave-0 [WebAppFactory + Middleware/PostgresFixture + TestParentEntity + TestChildEntity + TestErrorDbContext] + 1 TestController with 8 deliberately-throwing endpoints + 7 verification facts [CorrelationIdTests 9 facts, ValidationErrorTests 2 facts, SqlStateMappingTests 2 facts, NotFoundAndUnhandledTests 2 facts, ConcurrencyTokenTests 1 fact, ProblemDetailsExtensionsTests 4 facts, PostgresExceptionMapperTests 4 facts]) + 1 csproj edit (Mvc.Testing PackageRef + ProjectRef to BaseApi.Service); `dotnet test` exit 0 Passed: 31 Failed: 0 (24 new + 7 Phase 3 carry-over) across 3 consecutive runs (~1.5-2.9s); BEFORE/AFTER `psql \l` snapshots byte-identical (D-15 cleanup proven); 1 fix-forward to Plan 04-01 (`fix(04-01) ad3f1a1` corrects Npgsql pin 9.0.0 â†’ 8.0.9 for runtime binary compat with EFCore.PostgreSQL 8.0.10 â€” TypeLoadException on Npgsql.Internal.HackyEnumTypeMapping surfaced under WebApplicationFactory boot); 04-02-SUMMARY documents SC#1-5 + D-03a + ERROR-08/09 + T-04-LEAK/XMIN/INJECT all GREEN; 14 phase REQ-IDs closed (OBSERV-09/10/11 + ERROR-01..11)
**UI hint**: no
**Parallelizable**: no (middleware order â€” `UseExceptionHandler` then `UseCorrelationId` â€” is load-bearing; one team must own the pipeline)

### Phase 5: Observability + Health Probes
**Goal**: Wire OpenTelemetry logs/metrics/traces via the MEL-bridge path and stand up three distinct health probes so the service is observable end-to-end and Kubernetes-style probes can target each endpoint.
**Depends on**: Phase 4 (correlation IDs must already be in the log scope so they propagate into OTel exports)
**Requirements**: OBSERV-01, OBSERV-02, OBSERV-03, OBSERV-04, OBSERV-05, OBSERV-06, OBSERV-07, OBSERV-08, OBSERV-12, HEALTH-01, HEALTH-02, HEALTH-03, HEALTH-04, HEALTH-05
**Success Criteria** (what must be TRUE):
  1. With the OTLP endpoint pointed at a local OTel Collector (or `otel-tui`), an `ILogger<T>.LogInformation` call emitted during a request appears in the Collector's log export with `service.name=sk-api`, `service.version=3.2.0`, and the request's correlation ID as a log attribute
  2. Setting `Logging:LogLevel:Default` to `Warning` in appsettings suppresses `Information` logs from BOTH the console sink AND the OTLP export â€” confirming the single MEL filter path (not `WithLogging()`)
  3. `GET /health/live` returns 200 even when Postgres is stopped; `GET /health/ready` returns 503 when Postgres is unreachable and 200 when reachable; `GET /health/startup` returns 503 until the migration runner flips the flag and 200 thereafter
  4. HTTP server metrics for application endpoints appear at the Collector (e.g., `http.server.request.duration`) but requests to `/health/*` do not produce metrics or appear in OTLP logs (filtered out)
  5. A Postgres query issued during a request produces a child span under the ASP.NET Core request span in the trace export (Npgsql instrumentation active)
**Plans**: 2 plans
- [x] 05-01-PLAN.md â€” Wave 1 build: 3 new CPM pins (OpenTelemetry.Instrumentation.Runtime 1.15.0, Npgsql.OpenTelemetry 8.0.4, AspNetCore.HealthChecks.UI.Client 9.0.0) + BaseApi.Core/Health/ types (IStartupGate + StartupHealthCheck + StartupCompletionService, all public sealed per Reconciliation 3) + Program.cs additive OTel logs via MEL bridge + metrics + traces (bare .AddNpgsql() per RESEARCH D-05 correction / Reconciliation 1) + AddHostedService<StartupCompletionService> (Reconciliation 2) + 3 MapHealthChecks + compose.yaml otel-collector service + compose/otel-collector-config.yaml + .gitignore tests/.otel-out/ + 0/0 Release+Debug builds — SHIPPED 2026-05-27
- [x] 05-02-PLAN.md â€” Wave 2 verification (autonomous:false, Phase 3 D-18 cadence) — SHIPPED 2026-05-27: created 9 test files (OtelCollectorFixture + CollectionDefinitions + TestObservabilityController + 5 fact-test classes [LogExportTests 2 + LogLevelFilterTests 2 + HealthEndpointsTests 7 + MetricsExportTests 3 + TraceExportTests 2 = 16 facts] + OtelEndOfSuiteCleanup assembly fixture); `dotnet test SK_P.sln --no-restore` exit 0 with Passed: 47 Failed: 0 across 3 consecutive runs (17.67/17.71/18.09s); BEFORE/AFTER `psql \l` byte-identical (D-15 cleanup proven); tests/.otel-out/telemetry.jsonl absent post-test (D-11 cleanup automated via xUnit v3 [assembly: AssemblyFixture]); 14 phase REQ-IDs runtime-verified (OBSERV-01..08 + OBSERV-12 + HEALTH-01..05); 2 prior-plan gaps closed in continuation block — SC#4 metrics-half via Collector-side filterprocessor (Plan 05-01 fix-forward `2f3ae45`), telemetry.jsonl deferred-cleanup via xUnit v3 [assembly: AssemblyFixture] (`598c016`)
**UI hint**: no
**Parallelizable**: yes (OTel wiring and health probes can be split into separate plans executed concurrently â€” they share `Program.cs` registration but no runtime state)

### Phase 6: Validation + Mapping Base
**Goal**: Establish the validation and mapping seam â€” `BaseDtoValidator<T>`, FluentValidation DI registration, Mapperly project setup, and the `IEntityMapper<,,,>` interface â€” so concrete entities in Phase 8 plug in without rewriting base rules or coupling controllers to Mapperly directly.
**Depends on**: Phase 3 (validators reference `BaseEntity` field names) and Phase 4 (ValidationException -> 400 mapping must already exist)
**Requirements**: VALID-01, VALID-02, VALID-03, VALID-04, VALID-05, VALID-06, VALID-07, HTTP-10
**Success Criteria** (what must be TRUE):
  1. A concrete DTO validator that calls `Include(new BaseDtoValidator<MyDto>())` automatically gets the `Name` (NotEmpty + MaxLength 200), `Version` (strict SemVer regex `^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$`), and `Description` (MaxLength 2000) rules without restating them
  2. Validators are discovered automatically via `AddValidatorsFromAssembly` â€” no manual `services.AddScoped<IValidator<T>>` calls; no `FluentValidation.AspNetCore` package referenced anywhere in the solution
  3. A test DTO that calls `IValidator<TestDto>.ValidateAsync(badDto)` from a service-layer call (not from MVC auto-validation) returns a `ValidationResult` with the expected field-level errors; the same call inside a controller action surfaces as the Phase 4 HTTP 400 Problem Details response
  4. A trivial Mapperly `[Mapper] partial class` that implements `IEntityMapper<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>` compiles with Mapperly diagnostics RMG007/RMG012/RMG020/RMG089 promoted to errors in the project file (codes corrected from original MP0001/MP0011/MP0020/MP0021 per Phase 6 RESEARCH A-01 — Mapperly uses RMG-prefix exclusively; MP codes do not exist)
**Plans**: 2 plans
- [x] 06-01-PLAN.md — Wave 1 build (autonomous) — SHIPPED 2026-05-27: 5 new BaseApi.Core C# files (IBaseDto + BaseDtoValidator<T> + IEntityMapper<,,,> + AddBaseApiValidation + AddBaseApiMapping = 195 LOC); FluentValidation.DependencyInjectionExtensions PackageReference added to BaseApi.Core.csproj; Riok.Mapperly PackageReference (PrivateAssets=all ExcludeAssets=runtime per D-19) added to BaseApi.Tests.csproj; Directory.Build.props promotes RMG007/RMG012/RMG020/RMG089 (D-11/D-12 amended MP→RMG per RESEARCH A-01) and supersedes stale Phase 1 D-04 comment; Program.cs D-18 wiring (AddBaseApiValidation + AddBaseApiMapping between AddProblemDetails and AddExceptionHandler chain); WebAppFactory unsealed (sealed→public class) per PATTERNS.md SUBCLASS BLOCKER path A. `dotnet build SK_P.sln -c Release` and `-c Debug` both exit 0 with zero warnings; Phase 1-5 regression-free (47/47 passed). 7 REQ-IDs closed (VALID-01, VALID-02, VALID-04, VALID-05, VALID-06, VALID-07, HTTP-10). 1 deviation (Rule 1 — plan internal inconsistency: removed MP-code strings from Directory.Build.props comment to satisfy plan's own verification grep-empty assertion). Commits: 416d40c (Task 1: 3 seam files), 2b702b1 (Task 2: DI extensions + DI Extensions PackageReference), b631ce8 (Task 3: Program.cs D-18 + Directory.Build.props + Tests.csproj Mapperly + WAF unseal).
- [x] 06-02-PLAN.md — Wave 2 verification (autonomous:false, Phase 3 D-18 cadence) — SHIPPED 2026-05-27 (approved at human-verify gate): all 4 automated checks PASSED — 3 consecutive GREEN runs (76/76 = 47 carryover + 29 new), byte-identical psql \l snapshots, tests/.otel-out/ clean, drift-detection LIVE on all 3 mapper methods (RMG012+RMG020 fire on ToEntity/Update/ToRead when a Drift property is added). 8 REQ-IDs closed (VALID-01..07 + HTTP-10). 1 deviation (Rule 3 plan-gap: TestEntityMapper required [MapperIgnoreTarget] on ToEntity + [MapperIgnoreSource] on ToRead, not just Update — Phase 8 must replicate the 3-method pattern across all 5 entity mappers). Commits: d4a389d (6 scaffolds), 57f4aa0 (TestController endpoint), ced3c55 (7 fact-test classes), f928cab (SUMMARY+STATE+REQUIREMENTS). Original scope: land 6 test scaffolds in tests/BaseApi.Tests/Validation/ (TestEntity, TestDtos one-file-3-records, TestDtoValidator with empty body + Include only, TestValidationService, TestEntityMapper [Mapper] partial with 5 [MapperIgnoreTarget] attrs per D-08 amended dual-mechanism, ValidationWebAppFactory subclass adding Tests-assembly validator scan per Pitfall 5); extend TestController with [HttpPost("validate")] endpoint (Pitfall 4 [FromBody] mandatory); land 7 fact-test classes (BaseDtoValidatorRuleTests covering VALID-05/06/07, BaseDtoValidatorIncludeTests SC#1, ValidatorAutoDiscoveryTests SC#2/VALID-02, ValidationEndpointTests SC#3/VALID-03, MapperRegistrationTests SC#4 DI half, MapperlyCompileTests SC#4 roundtrip half, PackageAuditTests VALID-01); end-of-phase gate: 3 consecutive GREEN dotnet test runs + byte-identical psql \l BEFORE/AFTER (Phase 3 D-15) + tests/.otel-out/telemetry.jsonl absent (Phase 5 D-11 inherited)
**UI hint**: no
**Parallelizable**: yes (BaseDtoValidator + Mapperly setup + IEntityMapper interface are independent and can be split across plans)

### Phase 7: Generic HTTP Base + Composition Root
**Goal**: Build the abstract generic `BaseController` and `BaseService`, the `AddBaseApi`/`UseBaseApi` composition root extensions, and wire API versioning + Swagger so the runnable service is one inheritance step away from working.
**Depends on**: Phase 3 (Repository), Phase 4 (error middleware), Phase 5 (OTel + health), Phase 6 (validator + mapper seam) — composition root brings them all together
**Requirements**: HTTP-01, HTTP-02, HTTP-03, HTTP-08, HTTP-09, HTTP-13, HTTP-14, HTTP-15, HTTP-16
**Success Criteria** (what must be TRUE):
  1. A scratch derived class `public class TestsController : BaseController<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>` with no body exposes `GET /api/v1/tests`, `GET /api/v1/tests/{id:guid}`, `POST /api/v1/tests`, `PUT /api/v1/tests/{id:guid}`, `DELETE /api/v1/tests/{id:guid}` — the verbs come from the base, the URL prefix comes from versioning
  2. `BaseService<...>.CreateAsync` runs in this order: validator -> map create-DTO to entity -> repository.Add -> virtual `SyncJunctionsAsync` -> `SaveChangesAsync` -> map entity to read-DTO; controllers never call repository directly (Controller -> Service -> Repository layering enforced by visibility)
  3. `BaseApi.Service`'s `Program.cs` is a thin file: it calls `builder.Services.AddBaseApi<AppDbContext>(builder.Configuration)`, `app.UseBaseApi()`, and `app.Run()` — no per-concern wiring duplicated outside `BaseApi.Core`
  4. Browsing `/swagger` in Development environment renders the OpenAPI UI listing the test controller's 5 verbs; the same endpoint returns 404 in Production
**Plans**: 2 plans
- [x] 07-01-PLAN.md — Wave 1 build (autonomous) — SHIPPED 2026-05-27: 14 new BaseApi.Core files (IHasId + BaseController + BaseService + 3 Swagger helpers + 7 DI extensions) + AppDbContext placeholder + Program.cs collapsed from ~150 lines to ~7 non-trivial body lines (cap: 10). 9 REQ-IDs closed (HTTP-01/02/03/08/09/13/14/15/16). dotnet build SK_P.sln -c Release & -c Debug both exit 0 with zero warnings. CONTEXT D-13 amendment encoded: AddBaseApi<TDbContext> chains 6 sub-extensions on IServiceCollection + separate builder.AddBaseApiObservability(cfg) on IHostApplicationBuilder for OTel MEL bridge (needs ILoggingBuilder). 5 deviations auto-fixed: 4× Rule 1 bug (missing Microsoft.AspNetCore.Http using; CS0416 typeof(TRead) in ProducesResponseType attributes; missing Microsoft.Extensions.DependencyInjection using for SwaggerGenOptions extensions; missing Microsoft.Extensions.Hosting using for IsDevelopment) + 1× Rule 3 visibility plan-gap (AddBaseApiObservability promoted internal→public for cross-assembly call from Program.cs). Commits: c86cf08 (Task 1 pins), 099b5e4 (Task 2 BaseController+BaseService), ff6d866 (Task 3 7+3 extensions), 89dbf55 (Task 4 AppDbContext+Program.cs). Regression replay deferred to Plan 07-02 per CONTEXT D-15.
- [x] 07-02-PLAN.md — Wave 2 verification (autonomous:false, Phase 3 D-18 cadence) — SHIPPED 2026-05-27: 14 new test files (TestCreateDtoValidator Blocker 2 + Phase7TestDbContext Blocker 1 + 4 scaffolds + 8 fact classes) covering all 9 HTTP-* REQ-IDs + SC#1-4. Run 1/2/3 = 98/98 GREEN (76 prior + 22 new) at 20.7s/19.0s/18.4s; identical Passed count across all 3 runs. psql \l BEFORE/AFTER byte-identical (Phase 3 D-15). tests/.otel-out/telemetry.jsonl absent post-suite (Phase 5 D-11). Task 4 manual Swagger UI smoke checkpoint resolved 2026-05-27 — user approved based on automated coverage rationale: SwaggerEnvironmentFacts (Dev 200 / Prod 404 via Phase7WebAppFactory AddApplicationPart + ProductionWebAppFactory UseEnvironment override) provides canonical SC#4 proof since v1 BaseApi.Service has zero concrete controllers (Phase 8 adds them — live-boot empty UI is structurally expected). 6 deviations auto-fixed inline during Task 2 (4× Rule 1 bug + 1× Rule 1 RESEARCH A6 falsification + 1× Rule 3 plan-gap for throwaway-DB wiring). Commits: 9d16e92 (Task 1 scaffolds), 89a3dcb (Task 2 facts), 1254b9b (Task 3 regression replay + VALIDATION update).
**UI hint**: no
**Parallelizable**: no (BaseController + BaseService + composition root are tightly coupled — split risks divergent DI graphs)

### Phase 8: Entity Build-Out + Migrations + Docker Runtime + Tests
**Goal**: Plug all 5 concrete entities into the finished base in FK order, generate the single `InitialCreate` migration, build the runtime Docker image, and prove the stack with smoke + error-mapping integration tests against real Postgres.
**Depends on**: Phase 7 (composition root must exist before entities slot in)
**Requirements**: PERSIST-01, PERSIST-08, PERSIST-09, PERSIST-10, PERSIST-12, PERSIST-13, PERSIST-14, ENTITY-03, ENTITY-04, ENTITY-05, ENTITY-06, ENTITY-07, ENTITY-08, ENTITY-09, ENTITY-10, HTTP-04, HTTP-05, HTTP-06, HTTP-07, HTTP-11, HTTP-12, VALID-08, VALID-09, VALID-10, VALID-11, VALID-12, VALID-13, VALID-14, VALID-15, VALID-16, VALID-17, VALID-18, VALID-19, VALID-20, INFRA-05, TEST-01, TEST-02, TEST-03, TEST-04, TEST-05, TEST-06
**Success Criteria** (what must be TRUE):
  1. `docker compose up` brings up Postgres + `BaseApi.Service`; the service applies the `InitialCreate` migration on startup, flips the startup probe to healthy, and `GET /api/v1/schemas` returns `200 OK` with an empty array; if migration fails, the readiness probe stays unhealthy but the process does NOT crash
  2. A `POST /api/v1/processors` with a SHA-256 `sourceHash` that already exists in the database returns HTTP 409 with the offending field name in `detail` (Postgres unique index `uq_processor_source_hash` -> SQLSTATE 23505 -> Phase 4 mapper) â€” proving DB-level FK/unique constraints + clean error mapping work end-to-end
  3. A `POST /api/v1/schemas` with `definition` that is syntactically valid JSON but is NOT a valid JSON Schema (draft 2020-12) returns HTTP 400 with the field-level error; `JsonSchema.Net` remote `$ref` network access is disabled (SSRF probe test passes)
  4. A `POST /api/v1/workflows` with `cronExpression="0 0 * * *"` succeeds (parses as 5-field Cronos); `cronExpression="0 0 * * * *"` (6-field) returns HTTP 400; `cronExpression=null` succeeds (nullable -> not scheduled)
  5. Creating a Workflow whose `entryStepIds` reference a non-existent Step returns HTTP 422 (Postgres FK violation -> Phase 4 mapper); deleting a Step that a Workflow references returns HTTP 422 (FK Restrict on `WorkflowEntryStep.StepId`)
  6. The integration test suite (`xUnit v3` + `WebApplicationFactory<Program>` + `Testcontainers.PostgreSql` + `Respawn`) runs at minimum: 25 happy-path tests (5 entities Ã— 5 CRUD verbs) and 4 error-mapping tests (400 validation, 404 not found, 409 unique, 422 FK), all passing against a real ephemeral Postgres container
**Plans**: TBD
**UI hint**: no
**Parallelizable**: yes â€” per-entity work (DTOs, Mapperly mapper, validator, entity-specific service overriding `SyncJunctionsAsync`, controller, `IEntityTypeConfiguration<T>`) is identical shape across all 5 entities and can be split into 5 parallel plans. Migration generation must happen AFTER all 5 entities are committed (the migration includes the whole graph). Dockerfile + test harness are independent plans that can run in parallel with entity work.

## Progress

**Execution Order:**
Phases execute in numeric order: 1 -> 2 -> 3 -> 4 -> 5 -> 6 -> 7 -> 8

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 1. Repository Scaffold | 3/3 | Complete | 2026-05-26 |
| 2. Postgres + Docker Compose | 2/2 | Complete | 2026-05-26 |
| 3. EF Core Persistence Base | 2/2 | Complete | 2026-05-27 |
| 4. Cross-Cutting Middleware + Error Handling | 2/2 | Complete | 2026-05-27 |
| 5. Observability + Health Probes | 2/2 | Complete    | 2026-05-27 |
| 6. Validation + Mapping Base | 0/2 | Planned | - |
| 7. Generic HTTP Base + Composition Root | 1/2 | In Progress | - |
| 8. Entity Build-Out + Migrations + Docker Runtime + Tests | 3/8 | In Progress | - |

## Coverage Summary

**Total v1 requirements:** 102 (note: REQUIREMENTS.md header says 103 but actual count of REQ-IDs is 102 â€” header arithmetic was off by one; flagged for user confirmation)
**Mapped to phases:** 102
**Unmapped:** 0
**Duplicated:** 0

Per-category coverage:
- INFRA (7): Phase 1 (01-04) + Phase 2 (06-07) + Phase 8 (05)
- PERSIST (15): Phase 3 (02,03,04,05,06,07,11,15) + Phase 8 (01,08,09,10,12,13,14)
- ENTITY (10): Phase 3 (01,02) + Phase 8 (03-10)
- HTTP (16): Phase 6 (10) + Phase 7 (01,02,03,08,09,13,14,15,16) + Phase 8 (04,05,06,07,11,12)
- VALID (20): Phase 6 (01-07) + Phase 8 (08-20)
- OBSERV (12): Phase 4 (09,10,11) + Phase 5 (01-08,12)
- HEALTH (5): Phase 5 (all)
- ERROR (11): Phase 4 (all)
- TEST (6): Phase 8 (all)

---
*Roadmap created: 2026-05-26*
