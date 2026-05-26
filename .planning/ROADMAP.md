# Roadmap: Steps API

## Overview

This roadmap delivers the Steps API as a .NET 8 modular monolith in 8 dependency-ordered phases. Phases 1-2 establish the skeleton and Postgres runtime. Phases 3-7 build the reusable `BaseApi.Core` infrastructure layer by layer in strict code-dependency order: persistence base before cross-cutting concerns, cross-cutting concerns before observability, observability before the validation/mapping seam, then the generic HTTP base and composition root. Phase 8 plugs the five concrete entities (Schema -> Processor -> Step -> Assignment -> Workflow) into the finished base, generates the initial migration, builds the runtime image, and stands up the integration test harness. The build order is forced by the code-dependency graph (BaseEntity is a type constraint on Repository/Controller; AddBaseApi requires every base component) — it is not arbitrary milestone padding.

## Phases

**Phase Numbering:**
- Integer phases (1, 2, 3): Planned milestone work
- Decimal phases (2.1, 2.2): Urgent insertions (marked with INSERTED)

Decimal phases appear between their surrounding integers in numeric order.

- [x] **Phase 1: Repository Scaffold** - Solution structure, SDK pin, central package management, and config skeleton compile cleanly
- [x] **Phase 2: Postgres + Docker Compose** - Local Postgres 17 container is reachable from the host with persistent storage and a healthcheck
- [ ] **Phase 3: EF Core Persistence Base** - `BaseEntity`, `BaseDbContext`, `AuditInterceptor`, snake_case convention, and generic `Repository<T>` exist before any migration is generated
- [ ] **Phase 4: Cross-Cutting Middleware + Error Handling** - Correlation IDs flow end-to-end and every error returns RFC 7807 Problem Details with the correlation ID
- [ ] **Phase 5: Observability + Health Probes** - OTel logs, metrics, and traces reach the Collector; three distinct health probes (live/ready/startup) respond correctly
- [ ] **Phase 6: Validation + Mapping Base** - Base DTO validator + Mapperly + `IEntityMapper<,,,>` seam are wired so any future entity slots in without rewriting base rules
- [ ] **Phase 7: Generic HTTP Base + Composition Root** - `BaseController`/`BaseService` + `AddBaseApi`/`UseBaseApi` compose the runnable service with versioned routes and Swagger
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
  4. `appsettings.json` in `BaseApi.Service` contains `Logging`, `Service` (`Name="steps-api"`, `Version="3.2.0"`), `ConnectionStrings:Postgres`, and `OpenTelemetry` sections (values may be placeholders) and is valid JSON with no comments
**Plans**: 3 plans
- [x] 01-01-PLAN.md — Repo-root foundation files: global.json (SDK pin), Directory.Build.props (warnings-as-errors + strictness), Directory.Packages.props (22 NuGet pins via CPM), .editorconfig (Microsoft .NET style), .gitignore (dotnet flavor), .gitattributes (LF endings), README.md (prereqs + quickstart)
- [x] 01-02-PLAN.md — Solution + 3 projects: SK_P.sln, BaseApi.Core.csproj (class library), BaseApi.Service.csproj (webapi with Program.cs D-10 scaffold + appsettings REQ INFRA-04), BaseApi.Tests.csproj (xUnit v3 + MetaTest sanity), Core folder skeleton (11 .gitkeep folders), Service folder skeleton (3 .gitkeep folders)
- [x] 01-03-PLAN.md — Verification + smoke: dotnet --version=8.0.421, dotnet restore, dotnet build (Release+Debug, 0 warnings), dotnet test (Sanity passes), dotnet run smoke (host boots, GET / returns 404), SUMMARY documenting Phase 1 SC#1-4 met
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
- [x] 02-01-PLAN.md — compose.yaml + .env + .gitignore + README + appsettings.Development.json port reconcile (CONTEXT.md D-01..D-13, closes Phase 1 D-14 carry-forward)
- [x] 02-02-PLAN.md — verification + smoke battery (SC#1 health, SC#2 psql connect, SC#3 named-volume persistence, SC#4 depends_on graph) + D-15 cleanup
**UI hint**: no
**Parallelizable**: no (linear setup)

### Phase 3: EF Core Persistence Base
**Goal**: Build the persistence foundation in `BaseApi.Core` — `BaseEntity`, `BaseDbContext`, `AuditInterceptor`, snake_case convention, and the generic `Repository<T>` — before any migration is generated so the convention applies to the very first schema.
**Depends on**: Phase 1
**Requirements**: ENTITY-01, ENTITY-02, PERSIST-02, PERSIST-03, PERSIST-04, PERSIST-05, PERSIST-06, PERSIST-07, PERSIST-11, PERSIST-15, PERSIST-16
**Success Criteria** (what must be TRUE):
  1. A unit test or scratch console can construct a `BaseDbContext` subclass with a trivial `DbSet<T>` and `EnsureCreated()` against the Phase 2 Postgres, producing tables with snake_case identifiers (e.g., `created_at`, not `CreatedAt`)
  2. Inserting a `BaseEntity` subclass through `Repository<T>` and calling `SaveChangesAsync()` on the context stamps `CreatedAt`/`UpdatedAt` with `DateTime.UtcNow` (`Kind == Utc`) and writes them to `timestamptz` columns without Npgsql throwing `InvalidCastException`
  3. With an `IHttpContextAccessor.HttpContext?.User?.Identity?.Name` of `"alice"`, the inserted row's `created_by` equals `"alice"`; with no HTTP context, `created_by` is null (no crash)
  4. Resolving `DbContext` twice from the same DI scope returns the same instance; resolving from a different scope returns a different instance (Scoped lifetime verified)
**Plans**: 2 plans
- [ ] 03-01-PLAN.md — Build plan: append PERSIST-16 to REQUIREMENTS.md (D-03b); pin Microsoft.Extensions.TimeProvider.Testing 8.10.0 in Directory.Packages.props (D-13); add EF Core + Npgsql + EFCore.NamingConventions PackageReferences + FrameworkReference Microsoft.AspNetCore.App to BaseApi.Core.csproj (D-12) and BaseApi.Tests.csproj (D-13); create BaseEntity.cs, BaseDbContext.cs, AuditInterceptor.cs, IRepository.cs, Repository.cs (verbatim skeletons from RESEARCH.md); aggregate `dotnet build` Release+Debug zero warnings per W-02 (D-17)
- [ ] 03-02-PLAN.md — Verification + smoke (autonomous:false, D-18): create TestEntity/TestDbContext/PostgresFixture/StubHttpContextAccessor scaffold + 5 fact files (SchemaTests SC#1, AuditInterceptorTests SC#2+SC#3 as 2 facts, DiLifetimeTests SC#4, XminConcurrencyTokenTests Dim 6 PERSIST-16, RepositorySurfaceTests Dim 7 PERSIST-11); `dotnet test` against Phase 2 Postgres at localhost:5433 with per-class throwaway DBs; `psql \l` before/after for D-15 cleanup verification; 03-02-SUMMARY documenting SC#1-4 + Dim 6 + Dim 7 GREEN (D-14, D-15, D-18)
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
  5. `[ApiController]` model-binding failures (e.g., malformed JSON body) produce the same Problem Details shape as FluentValidation failures — no divergent error format
**Plans**: TBD
**UI hint**: no
**Parallelizable**: no (middleware order — `UseExceptionHandler` then `UseCorrelationId` — is load-bearing; one team must own the pipeline)

### Phase 5: Observability + Health Probes
**Goal**: Wire OpenTelemetry logs/metrics/traces via the MEL-bridge path and stand up three distinct health probes so the service is observable end-to-end and Kubernetes-style probes can target each endpoint.
**Depends on**: Phase 4 (correlation IDs must already be in the log scope so they propagate into OTel exports)
**Requirements**: OBSERV-01, OBSERV-02, OBSERV-03, OBSERV-04, OBSERV-05, OBSERV-06, OBSERV-07, OBSERV-08, OBSERV-12, HEALTH-01, HEALTH-02, HEALTH-03, HEALTH-04, HEALTH-05
**Success Criteria** (what must be TRUE):
  1. With the OTLP endpoint pointed at a local OTel Collector (or `otel-tui`), an `ILogger<T>.LogInformation` call emitted during a request appears in the Collector's log export with `service.name=steps-api`, `service.version=3.2.0`, and the request's correlation ID as a log attribute
  2. Setting `Logging:LogLevel:Default` to `Warning` in appsettings suppresses `Information` logs from BOTH the console sink AND the OTLP export — confirming the single MEL filter path (not `WithLogging()`)
  3. `GET /health/live` returns 200 even when Postgres is stopped; `GET /health/ready` returns 503 when Postgres is unreachable and 200 when reachable; `GET /health/startup` returns 503 until the migration runner flips the flag and 200 thereafter
  4. HTTP server metrics for application endpoints appear at the Collector (e.g., `http.server.request.duration`) but requests to `/health/*` do not produce metrics or appear in OTLP logs (filtered out)
  5. A Postgres query issued during a request produces a child span under the ASP.NET Core request span in the trace export (Npgsql instrumentation active)
**Plans**: TBD
**UI hint**: no
**Parallelizable**: yes (OTel wiring and health probes can be split into separate plans executed concurrently — they share `Program.cs` registration but no runtime state)

### Phase 6: Validation + Mapping Base
**Goal**: Establish the validation and mapping seam — `BaseDtoValidator<T>`, FluentValidation DI registration, Mapperly project setup, and the `IEntityMapper<,,,>` interface — so concrete entities in Phase 8 plug in without rewriting base rules or coupling controllers to Mapperly directly.
**Depends on**: Phase 3 (validators reference `BaseEntity` field names) and Phase 4 (ValidationException -> 400 mapping must already exist)
**Requirements**: VALID-01, VALID-02, VALID-03, VALID-04, VALID-05, VALID-06, VALID-07, HTTP-10
**Success Criteria** (what must be TRUE):
  1. A concrete DTO validator that calls `Include(new BaseDtoValidator<MyDto>())` automatically gets the `Name` (NotEmpty + MaxLength 200), `Version` (strict SemVer regex `^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$`), and `Description` (MaxLength 2000) rules without restating them
  2. Validators are discovered automatically via `AddValidatorsFromAssembly` — no manual `services.AddScoped<IValidator<T>>` calls; no `FluentValidation.AspNetCore` package referenced anywhere in the solution
  3. A test DTO that calls `IValidator<TestDto>.ValidateAsync(badDto)` from a service-layer call (not from MVC auto-validation) returns a `ValidationResult` with the expected field-level errors; the same call inside a controller action surfaces as the Phase 4 HTTP 400 Problem Details response
  4. A trivial Mapperly `[Mapper] partial class` that implements `IEntityMapper<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>` compiles with Mapperly diagnostics MP0001/MP0011/MP0020/MP0021 promoted to errors in the project file
**Plans**: TBD
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
**Plans**: TBD
**UI hint**: no
**Parallelizable**: no (BaseController + BaseService + composition root are tightly coupled — split risks divergent DI graphs)

### Phase 8: Entity Build-Out + Migrations + Docker Runtime + Tests
**Goal**: Plug all 5 concrete entities into the finished base in FK order, generate the single `InitialCreate` migration, build the runtime Docker image, and prove the stack with smoke + error-mapping integration tests against real Postgres.
**Depends on**: Phase 7 (composition root must exist before entities slot in)
**Requirements**: PERSIST-01, PERSIST-08, PERSIST-09, PERSIST-10, PERSIST-12, PERSIST-13, PERSIST-14, ENTITY-03, ENTITY-04, ENTITY-05, ENTITY-06, ENTITY-07, ENTITY-08, ENTITY-09, ENTITY-10, HTTP-04, HTTP-05, HTTP-06, HTTP-07, HTTP-11, HTTP-12, VALID-08, VALID-09, VALID-10, VALID-11, VALID-12, VALID-13, VALID-14, VALID-15, VALID-16, VALID-17, VALID-18, VALID-19, VALID-20, INFRA-05, TEST-01, TEST-02, TEST-03, TEST-04, TEST-05, TEST-06
**Success Criteria** (what must be TRUE):
  1. `docker compose up` brings up Postgres + `BaseApi.Service`; the service applies the `InitialCreate` migration on startup, flips the startup probe to healthy, and `GET /api/v1/schemas` returns `200 OK` with an empty array; if migration fails, the readiness probe stays unhealthy but the process does NOT crash
  2. A `POST /api/v1/processors` with a SHA-256 `sourceHash` that already exists in the database returns HTTP 409 with the offending field name in `detail` (Postgres unique index `uq_processor_source_hash` -> SQLSTATE 23505 -> Phase 4 mapper) — proving DB-level FK/unique constraints + clean error mapping work end-to-end
  3. A `POST /api/v1/schemas` with `definition` that is syntactically valid JSON but is NOT a valid JSON Schema (draft 2020-12) returns HTTP 400 with the field-level error; `JsonSchema.Net` remote `$ref` network access is disabled (SSRF probe test passes)
  4. A `POST /api/v1/workflows` with `cronExpression="0 0 * * *"` succeeds (parses as 5-field Cronos); `cronExpression="0 0 * * * *"` (6-field) returns HTTP 400; `cronExpression=null` succeeds (nullable -> not scheduled)
  5. Creating a Workflow whose `entryStepIds` reference a non-existent Step returns HTTP 422 (Postgres FK violation -> Phase 4 mapper); deleting a Step that a Workflow references returns HTTP 422 (FK Restrict on `WorkflowEntryStep.StepId`)
  6. The integration test suite (`xUnit v3` + `WebApplicationFactory<Program>` + `Testcontainers.PostgreSql` + `Respawn`) runs at minimum: 25 happy-path tests (5 entities × 5 CRUD verbs) and 4 error-mapping tests (400 validation, 404 not found, 409 unique, 422 FK), all passing against a real ephemeral Postgres container
**Plans**: TBD
**UI hint**: no
**Parallelizable**: yes — per-entity work (DTOs, Mapperly mapper, validator, entity-specific service overriding `SyncJunctionsAsync`, controller, `IEntityTypeConfiguration<T>`) is identical shape across all 5 entities and can be split into 5 parallel plans. Migration generation must happen AFTER all 5 entities are committed (the migration includes the whole graph). Dockerfile + test harness are independent plans that can run in parallel with entity work.

## Progress

**Execution Order:**
Phases execute in numeric order: 1 -> 2 -> 3 -> 4 -> 5 -> 6 -> 7 -> 8

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 1. Repository Scaffold | 3/3 | Complete | 2026-05-26 |
| 2. Postgres + Docker Compose | 2/2 | Complete | 2026-05-26 |
| 3. EF Core Persistence Base | 0/2 | Not started | - |
| 4. Cross-Cutting Middleware + Error Handling | 0/TBD | Not started | - |
| 5. Observability + Health Probes | 0/TBD | Not started | - |
| 6. Validation + Mapping Base | 0/TBD | Not started | - |
| 7. Generic HTTP Base + Composition Root | 0/TBD | Not started | - |
| 8. Entity Build-Out + Migrations + Docker Runtime + Tests | 0/TBD | Not started | - |

## Coverage Summary

**Total v1 requirements:** 102 (note: REQUIREMENTS.md header says 103 but actual count of REQ-IDs is 102 — header arithmetic was off by one; flagged for user confirmation)
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
