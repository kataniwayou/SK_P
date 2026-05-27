# Requirements: Steps API

**Defined:** 2026-05-26
**Core Value:** A solid, observable, validated CRUD foundation that future workflow-platform features build on without rework.

## v1 Requirements

Requirements for initial release. Each maps to roadmap phases.

### Infrastructure

- [x] **INFRA-01
**: Solution structure is `src/BaseApi.Core/` (class library) + `src/BaseApi.Service/` (runnable webapi) + `tests/BaseApi.Tests/`
- [x] **INFRA-02
**: `.NET 8.0` SDK pinned via `global.json` (current 8.0.x LTS patch)
- [x] **INFRA-03
**: `Directory.Packages.props` centralizes NuGet versions across all projects
- [x] **INFRA-04
**: `appsettings.json` contains `Logging`, `Service` (`Name="sk-api"`, `Version="3.2.0"`), `ConnectionStrings:Postgres`, `OpenTelemetry` sections
- [ ] **INFRA-05**: Multistage `Dockerfile` using `mcr.microsoft.com/dotnet/sdk:8.0` for build and `mcr.microsoft.com/dotnet/aspnet:8.0` for runtime
- [x] **INFRA-06
**: `docker-compose.yml` defines `postgres:17-alpine` with `pg_isready` healthcheck plus `BaseApi.Service` with `depends_on: postgres: condition: service_healthy`
- [x] **INFRA-07
**: Postgres data persisted in a named volume across `docker-compose down/up`

### Persistence

- [ ] **PERSIST-01**: Single `AppDbContext` in `BaseApi.Service/Persistence/` exposing `DbSet<T>` for all 5 concrete entities and 3 junction entities
- [x] **PERSIST-02**: `BaseDbContext` (abstract) in `BaseApi.Core/Persistence/` registers `AuditInterceptor`
- [x] **PERSIST-03**: `ISaveChangesInterceptor` implementation (`AuditInterceptor`) auto-stamps `CreatedAt`/`UpdatedAt` with `DateTime.UtcNow` (required by Npgsql `timestamptz`)
- [x] **PERSIST-04**: `AuditInterceptor` sets `CreatedBy`/`UpdatedBy` from `IHttpContextAccessor.HttpContext?.User?.Identity?.Name` when available, null otherwise
- [x] **PERSIST-05**: Snake_case naming via `EFCore.NamingConventions` (`UseSnakeCaseNamingConvention()`) applied BEFORE first migration (cannot be retrofitted)
- [x] **PERSIST-06**: All primary keys are `Guid` mapped to Postgres `uuid`
- [x] **PERSIST-07**: `BaseEntity.CreatedAt`/`UpdatedAt` map to `timestamptz` columns
- [ ] **PERSIST-08**: `Schema.Definition` and `Assignment.Payload` map to `jsonb` columns
- [ ] **PERSIST-09**: Migrations applied on startup by `BaseApi.Service` via `db.Database.MigrateAsync()` before readiness probe transitions to Healthy
- [ ] **PERSIST-10**: Migration failure surfaces as failing readiness probe (process does not crash)
- [x] **PERSIST-11**: Generic `Repository<TEntity>` in `BaseApi.Core` (Get, List, Add, Update, Delete with `CancellationToken`)
- [ ] **PERSIST-12**: Junction tables `StepNextSteps`, `WorkflowEntrySteps`, `WorkflowAssignments` configured as explicit join entities with composite PKs and FKs to both sides
- [ ] **PERSIST-13**: All FK columns have DB-level FK constraints (enforced by Postgres)
- [ ] **PERSIST-14**: Unique index on `Processor.SourceHash`
- [x] **PERSIST-15**: `DbContext` registered with Scoped lifetime in DI
- [x] **PERSIST-16**: `BaseEntity` rows carry a Postgres `xmin` shadow concurrency token mapped via `IsConcurrencyToken()` on every `BaseEntity` subclass (model-builder iteration in `BaseDbContext.OnModelCreating`). Phase 3 wires the shadow property; Phase 4 maps `DbUpdateConcurrencyException` -> HTTP 409 (D-03a / cross-phase impact from Phase 3 CONTEXT.md D-03)

### Entity Model

- [x] **ENTITY-01**: `BaseEntity` (abstract) in `BaseApi.Core/Entities/BaseEntity.cs` with: `Id` Guid, `Name` string, `Version` string, `CreatedAt` DateTime, `UpdatedAt` DateTime, `CreatedBy` string?, `UpdatedBy` string?, `Description` string?
- [x] **ENTITY-02**: `BaseEntity` is abstract — no table; 5 concrete tables, no inheritance discriminator
- [ ] **ENTITY-03**: `SchemaEntity : BaseEntity` adds `Definition` string (jsonb)
- [ ] **ENTITY-04**: `ProcessorEntity : BaseEntity` adds `SourceHash` string (SHA-256 hex, unique), `InputSchemaId` Guid? (nullable FK→Schema), `OutputSchemaId` Guid? (nullable FK→Schema). Null permitted on both — supports source processors (no input) and sink processors (no output). DB columns are nullable; Postgres FK constraint still enforced when value is non-null.
- [ ] **ENTITY-05**: `StepEntity : BaseEntity` adds `ProcessorId` Guid (FK→Processor), `NextStepIds` List<Guid>? (M2M self-ref via `StepNextSteps`), `EntryCondition` `StepEntryCondition` (default `PreviousCompleted`)
- [ ] **ENTITY-06**: `StepEntryCondition` enum: `PreviousProcessing=0`, `PreviousCompleted=1`, `PreviousFailed=2`, `PreviousCancelled=3`, `Always=4`, `Never=5`
- [ ] **ENTITY-07**: `AssignmentEntity : BaseEntity` adds `StepId` Guid (FK→Step), `SchemaId` Guid (FK→Schema), `Payload` string (jsonb)
- [ ] **ENTITY-08**: `WorkflowEntity : BaseEntity` adds `EntryStepIds` List<Guid> (M2M to Step via `WorkflowEntrySteps`, required non-empty), `AssignmentIds` List<Guid>? (M2M to Assignment via `WorkflowAssignments`), `CronExpression` string? (nullable)
- [ ] **ENTITY-09**: No navigation properties between entities — only `Guid` FK columns + explicit junction entities
- [ ] **ENTITY-10**: `(Name, Version)` is NOT unique on any entity

### HTTP Surface

- [ ] **HTTP-01**: Controller-based ASP.NET Core (not Minimal APIs) — required for `BaseController` inheritance
- [ ] **HTTP-02**: `BaseController<TEntity, TCreate, TUpdate, TRead>` abstract generic in `BaseApi.Core/Controllers/`, decorated with `[ApiController]` and `[Route("api/v1/[controller]")]`
- [ ] **HTTP-03**: Standard CRUD verbs on every concrete controller: `GET /api/v1/{entity}` (list), `GET /api/v1/{entity}/{id}`, `POST /api/v1/{entity}`, `PUT /api/v1/{entity}/{id}`, `DELETE /api/v1/{entity}/{id}`
- [ ] **HTTP-04**: Each entity has 3 DTOs (Create, Update, Read) under `BaseApi.Service/{Entity}/Dtos/`
- [ ] **HTTP-05**: `CreateDto` excludes server-controlled fields (`Id`, `CreatedAt`, `UpdatedAt`, `CreatedBy`, `UpdatedBy`)
- [ ] **HTTP-06**: `UpdateDto` excludes `Id`, `CreatedAt`, `CreatedBy` (everything else mutable)
- [ ] **HTTP-07**: `ReadDto` includes every entity field
- [ ] **HTTP-08**: Layering enforced: Controller → Service → Repository (no Controller-to-Repository shortcut)
- [ ] **HTTP-09**: `BaseService<TEntity, TCreate, TUpdate, TRead>` in `BaseApi.Core` provides generic CRUD plus virtual `SyncJunctionsAsync` hook for M2M sync
- [x] **HTTP-10
**: `IEntityMapper<TEntity, TCreate, TUpdate, TRead>` interface in `BaseApi.Core` defines mapping signatures consumed by `BaseService`
- [ ] **HTTP-11**: Each entity has a Mapperly `[Mapper] partial class` in `BaseApi.Service/{Entity}/Mapping/` implementing `IEntityMapper`
- [ ] **HTTP-12**: Concrete controllers are empty derived classes (e.g., `public class SchemasController : BaseController<SchemaEntity, SchemaCreateDto, SchemaUpdateDto, SchemaReadDto>`)
- [ ] **HTTP-13**: `AddBaseApi<TDbContext>(IConfiguration)` extension in `BaseApi.Core/Extensions/` registers DI for: DbContext + naming convention + interceptors, generic repositories, generic services, mappers, validators, OTel, correlation, error middleware, health checks
- [ ] **HTTP-14**: `UseBaseApi()` extension in `BaseApi.Core/Extensions/` registers middleware in correct order (exception → correlation → routing → CORS → endpoints)
- [ ] **HTTP-15**: API versioning via `Asp.Versioning.Http` with URL prefix `/api/v1/` from v1 release (prevents breaking URL change later)
- [ ] **HTTP-16**: OpenAPI/Swagger UI via `Swashbuckle.AspNetCore`; exposed in Development environment

### Validation

- [x] **VALID-01
**: `FluentValidation` 12.x + `FluentValidation.DependencyInjectionExtensions` only (no `FluentValidation.AspNetCore` — deprecated)
- [x] **VALID-02
**: Validators discovered via `services.AddValidatorsFromAssembly(...)`
- [x] **VALID-03
**: `IValidator<TDto>` invoked explicitly in the Service layer via `ValidateAsync`; no MVC auto-validation
- [x] **VALID-04
**: `BaseDtoValidator<T>` in `BaseApi.Core/Validators/` provides shared rules for `Name`, `Version`, `Description`; concrete validators inherit via `Include(...)`
- [x] **VALID-05
**: `Name`: NotEmpty, MaxLength(200)
- [x] **VALID-06
**: `Version`: NotEmpty, regex `^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$` (strict SemVer numeric triple, no leading zeros)
- [x] **VALID-07
**: `Description`: MaxLength(2000)
- [ ] **VALID-08**: `SchemaCreate/UpdateDto.Definition`: valid JSON syntax AND valid JSON Schema (draft 2020-12) via `JsonSchema.Net`
- [ ] **VALID-09**: `JsonSchema.Net` remote `$ref` network access disabled (SSRF prevention)
- [ ] **VALID-10**: `ProcessorCreate/UpdateDto.SourceHash`: regex `^[a-f0-9]{64}$` (lowercase SHA-256 hex)
- [x] **VALID-11**: `ProcessorCreate/UpdateDto.InputSchemaId`/`OutputSchemaId`: nullable `Guid?` — null is valid (source/sink processor). When present, must not equal `Guid.Empty`. FluentValidation pattern: `When(x => x.InputSchemaId.HasValue, () => RuleFor(x => x.InputSchemaId!.Value).NotEqual(Guid.Empty));` (same for `OutputSchemaId`). `Guid.Empty` (`00000000-0000-0000-0000-000000000000`) is rejected at HTTP 400 by the validator, NOT at the DB layer. FK existence for non-empty Guids is still enforced by Postgres at persist time (SQLSTATE 23503 → HTTP 422 per ERROR-04
).
- [ ] **VALID-12**: `StepCreate/UpdateDto.ProcessorId`: `NotEmpty` Guid
- [ ] **VALID-13**: `StepCreate/UpdateDto.NextStepIds`: each unique; on Update, none equal to the Step's own Id
- [ ] **VALID-14**: `StepCreate/UpdateDto.EntryCondition`: `IsInEnum()`
- [ ] **VALID-15**: `AssignmentCreate/UpdateDto.StepId`/`SchemaId`: `NotEmpty` Guid
- [ ] **VALID-16**: `AssignmentCreate/UpdateDto.Payload`: valid JSON syntax (parsed by `System.Text.Json`), MaxLength 1,048,576 chars (~1 MB)
- [ ] **VALID-17**: `WorkflowCreate/UpdateDto.EntryStepIds`: `NotEmpty`, each unique
- [ ] **VALID-18**: `WorkflowCreate/UpdateDto.AssignmentIds`: each unique when present
- [ ] **VALID-19**: `WorkflowCreate/UpdateDto.CronExpression`: when present, parses as valid 5-field expression via `Cronos`
- [ ] **VALID-20**: Concrete entity validators inherit base rules via `Include(new BaseDtoValidator<...>())`

### Observability

- [x] **OBSERV-01
**: `OpenTelemetry` 1.15.x with `Exporter.OpenTelemetryProtocol`, `Instrumentation.AspNetCore`, `Instrumentation.Http`, `Extensions.Hosting`
- [x] **OBSERV-02
**: Logs wired via `builder.Logging.AddOpenTelemetry(...)` (MEL integration — NOT `services.AddOpenTelemetry().WithLogging()` which bypasses MEL)
- [x] **OBSERV-03
**: HTTP server + client metrics via `services.AddOpenTelemetry().WithMetrics(...).AddAspNetCoreInstrumentation().AddHttpClientInstrumentation()`
- [x] **OBSERV-04
**: OTLP exporter targets external OTel Collector; endpoint from `OTEL_EXPORTER_OTLP_ENDPOINT` env var (default fallback to `OpenTelemetry:Endpoint` appsettings); protocol gRPC by default
- [x] **OBSERV-05
**: OTel resource attributes `service.name` and `service.version` populated from `Service:Name` and `Service:Version` in appsettings
- [x] **OBSERV-06
**: `Logging:LogLevel` from appsettings filters BOTH console and OTel sinks identically (single source of truth — MEL pipeline filters before either sink runs)
- [x] **OBSERV-07
**: OTel logger options: `IncludeFormattedMessage=true`, `IncludeScopes=true`, `ParseStateValues=true`
- [x] **OBSERV-08
**: Health endpoints excluded from metrics (filter via `AspNetCoreInstrumentationOptions.Filter`)
- [x] **OBSERV-09
**: `CorrelationIdMiddleware` in `BaseApi.Core/Middleware/`: reads `X-Correlation-Id` header if present, generates a new UUID if missing
- [x] **OBSERV-10
**: Correlation ID written to `HttpContext.Items["CorrelationId"]` and attached to log scope via `ILogger.BeginScope`
- [x] **OBSERV-11
**: Correlation ID echoed in `X-Correlation-Id` response header on every response (including error responses)
- [x] **OBSERV-12
**: OTel tracing enabled (logs + metrics + traces) with `AddAspNetCoreInstrumentation`, `AddHttpClientInstrumentation`, `AddNpgsql` for DB spans

### Health Probes

- [x] **HEALTH-01
**: `/health/startup` returns Healthy after DI is built AND migrations have applied
- [x] **HEALTH-02
**: `/health/live` returns Healthy as long as the process is responsive (no external dependencies checked)
- [x] **HEALTH-03
**: `/health/ready` returns Healthy when Postgres is reachable AND startup probe is Healthy
- [x] **HEALTH-04
**: `AspNetCore.HealthChecks.NpgSql` registered for the Postgres reachability check
- [x] **HEALTH-05
**: Health endpoints (`/health/*`) excluded from logging and metrics emission

### Error Responses

- [x] **ERROR-01
**: RFC 7807 `ProblemDetails` JSON body returned on every non-2xx response
- [x] **ERROR-02
**: `IExceptionHandler` implementation in `BaseApi.Core` registered via `services.AddExceptionHandler<...>()` + `services.AddProblemDetails()` (.NET 8 modern pattern)
- [x] **ERROR-03
**: `FluentValidation.ValidationException` → HTTP 400 with field-level `errors` map (`{ "Version": ["Version must be SemVer..."], "SourceHash": ["..."] }`)
- [x] **ERROR-04
**: Postgres SQLSTATE `23503` (FK violation) → HTTP 422 with offending FK field name in detail
- [x] **ERROR-05
**: Postgres SQLSTATE `23505` (unique violation) → HTTP 409 with offending field name in detail
- [x] **ERROR-06
**: `NotFoundException` (custom exception thrown by Service when entity by id is missing) → HTTP 404 with resource type + id
- [x] **ERROR-07
**: Any other unhandled exception → HTTP 500 with generic message; full stack trace logged only (never leaked to client)
- [x] **ERROR-08
**: Every Problem Details body includes a `correlationId` field
- [x] **ERROR-09
**: Every Problem Details body includes an `instance` field (request path)
- [x] **ERROR-10
**: `[ApiController]`'s default `InvalidModelStateResponseFactory` aligned to emit the same Problem Details shape used elsewhere (no divergent error formats)
- [x] **ERROR-11
**: Postgres constraint names follow convention (e.g., `fk_processor_input_schema_id`, `uq_processor_source_hash`) so middleware can extract friendly field names

### Testing

- [ ] **TEST-01**: Test project `tests/BaseApi.Tests/` using `xUnit` v3
- [ ] **TEST-02**: `Microsoft.AspNetCore.Mvc.Testing` + `WebApplicationFactory<Program>` for integration tests
- [ ] **TEST-03**: `Testcontainers.PostgreSql` for real Postgres in tests (no `InMemory`, no `SQLite` — they don't enforce FK or emit SQLSTATE that error-mapping depends on)
- [ ] **TEST-04**: `Respawn` (or equivalent) used to reset DB between tests (faster than tearing down container per test)
- [ ] **TEST-05**: At least one happy-path integration test per CRUD verb per entity (5 entities × 5 verbs = 25 smoke tests, minimum)
- [ ] **TEST-06**: At least one negative-path integration test per error mapping (400 validation, 404 not found, 409 unique violation, 422 FK violation)

## v2 Requirements

Deferred to future release. Tracked but not in current roadmap.

### Hardening

- **VALID-21**: Dynamic schema conformance — `Assignment.Payload` validated against `Schema.Definition` referenced by `Assignment.SchemaId` (cross-entity dynamic validation). *Deferred per N2 decision.*
- **INFRA-08**: Multi-instance startup migration with Postgres advisory lock to prevent concurrent migration corruption. *v1 ships single-replica.*
- **INFRA-09**: Separate Postgres roles for migration vs runtime (least-privilege). *v1 uses a single role.*

### Querying

- **HTTP-17**: Pagination on list endpoints (`?page=`, `?pageSize=`). *v1 returns all rows.*
- **HTTP-18**: Filtering on list endpoints (e.g., `?name=...`, `?version=...`). *v1 returns all rows.*
- **HTTP-19**: Sorting on list endpoints. *v1 returns rows in default DB order.*

## Out of Scope

Explicitly excluded. Documented to prevent scope creep.

| Feature | Reason |
|---------|--------|
| Authentication / authorization | Unspecified in milestone scope; `CreatedBy`/`UpdatedBy` populated only if HttpContext supplies user identity, null otherwise. Will be added when an auth boundary is defined. |
| Orchestrator / scheduler implementation | External systems consume `Workflow.CronExpression`; the webapi is CRUD only. |
| Workflow execution engine | Running workflows, retries, parallelism — external responsibility. |
| `WorkflowScheduleEntity` | User dropped; `Workflow.CronExpression` nullability provides implicit gating. |
| Dynamic Payload-vs-Schema conformance check | Explicit user decision (N2 = No). |
| FK pre-validation over HTTP call | Postgres FK constraint + clean error mapping is the gating mechanism (Option 1). |
| `IsActive` / `Environment` flag on `WorkflowEntity` | Explicitly declined; `CronExpression` nullability is the gating mechanism. |
| Soft delete | `DELETE` is hard-delete in v1. May be added as base concern later if needed. |
| Multiple deployable services / NuGet packaging of `BaseApi.Core` | Option B (single API) chosen; bounded-context entities are tightly related via FKs, single team, milestone speed prioritized. |
| Workflow run history / execution tracking entity | External responsibility (orchestrator/scheduler). |
| Bulk CRUD operations (`POST []`, `PATCH []`) | Not needed for v1 base; can be added per-entity if a concrete need emerges. |
| ETag / If-Match optimistic concurrency | Not needed for the current usage pattern; EF Core `xmin` token can be added later. |
| AutoMapper | Mapperly chosen; AutoMapper has commercial license shift + runtime reflection. |
| `FluentValidation.AspNetCore` auto-validation | Deprecated and removed in FluentValidation 12; explicit `ValidateAsync` is the modern pattern. |
| `MediatR` | Commercial license shift; redundant 4th hop given Controller → Service → Repository is sufficient. |
| Serilog as a primary sink | `Logging:LogLevel` + MEL + OTel sink is the locked single source of truth; Serilog would duplicate. |
| EF Core `InMemory` / `SQLite` for tests | No FK enforcement, no SQLSTATE 23503/23505 — locked error-mapping decisions depend on real Postgres. |

## Traceability

Which phases cover which requirements. Populated during roadmap creation.

| Requirement | Phase | Status |
|-------------|-------|--------|
| INFRA-01 | Phase 1 | Complete |
| INFRA-02 | Phase 1 | Complete |
| INFRA-03 | Phase 1 | Complete |
| INFRA-04 | Phase 1 | Complete |
| INFRA-05 | Phase 8 | Pending |
| INFRA-06 | Phase 2 | Complete |
| INFRA-07 | Phase 2 | Complete |
| PERSIST-01 | Phase 8 | Pending |
| PERSIST-02 | Phase 3 | Complete |
| PERSIST-03 | Phase 3 | Complete |
| PERSIST-04 | Phase 3 | Complete |
| PERSIST-05 | Phase 3 | Complete |
| PERSIST-06 | Phase 3 | Complete |
| PERSIST-07 | Phase 3 | Complete |
| PERSIST-08 | Phase 8 | Pending |
| PERSIST-09 | Phase 8 | Pending |
| PERSIST-10 | Phase 8 | Pending |
| PERSIST-11 | Phase 3 | Complete |
| PERSIST-12 | Phase 8 | Pending |
| PERSIST-13 | Phase 8 | Pending |
| PERSIST-14 | Phase 8 | Pending |
| PERSIST-15 | Phase 3 | Complete |
| PERSIST-16 | Phase 3 | Complete |
| ENTITY-01 | Phase 3 | Complete |
| ENTITY-02 | Phase 3 | Complete |
| ENTITY-03 | Phase 8 | Pending |
| ENTITY-04 | Phase 8 | Pending |
| ENTITY-05 | Phase 8 | Pending |
| ENTITY-06 | Phase 8 | Pending |
| ENTITY-07 | Phase 8 | Pending |
| ENTITY-08 | Phase 8 | Pending |
| ENTITY-09 | Phase 8 | Pending |
| ENTITY-10 | Phase 8 | Pending |
| HTTP-01 | Phase 7 | Pending |
| HTTP-02 | Phase 7 | Pending |
| HTTP-03 | Phase 7 | Pending |
| HTTP-04 | Phase 8 | Pending |
| HTTP-05 | Phase 8 | Pending |
| HTTP-06 | Phase 8 | Pending |
| HTTP-07 | Phase 8 | Pending |
| HTTP-08 | Phase 7 | Pending |
| HTTP-09 | Phase 7 | Pending |
| HTTP-10 | Phase 6 | Pending |
| HTTP-11 | Phase 8 | Pending |
| HTTP-12 | Phase 8 | Pending |
| HTTP-13 | Phase 7 | Pending |
| HTTP-14 | Phase 7 | Pending |
| HTTP-15 | Phase 7 | Pending |
| HTTP-16 | Phase 7 | Pending |
| VALID-01 | Phase 6 | Pending |
| VALID-02 | Phase 6 | Pending |
| VALID-03 | Phase 6 | Pending |
| VALID-04 | Phase 6 | Pending |
| VALID-05 | Phase 6 | Pending |
| VALID-06 | Phase 6 | Pending |
| VALID-07 | Phase 6 | Pending |
| VALID-08 | Phase 8 | Pending |
| VALID-09 | Phase 8 | Pending |
| VALID-10 | Phase 8 | Pending |
| VALID-11 | Phase 8 | Pending |
| VALID-12 | Phase 8 | Pending |
| VALID-13 | Phase 8 | Pending |
| VALID-14 | Phase 8 | Pending |
| VALID-15 | Phase 8 | Pending |
| VALID-16 | Phase 8 | Pending |
| VALID-17 | Phase 8 | Pending |
| VALID-18 | Phase 8 | Pending |
| VALID-19 | Phase 8 | Pending |
| VALID-20 | Phase 8 | Pending |
| OBSERV-01 | Phase 5 | Complete |
| OBSERV-02 | Phase 5 | Complete |
| OBSERV-03 | Phase 5 | Complete |
| OBSERV-04 | Phase 5 | Complete |
| OBSERV-05 | Phase 5 | Complete |
| OBSERV-06 | Phase 5 | Complete |
| OBSERV-07 | Phase 5 | Complete |
| OBSERV-08 | Phase 5 | Complete |
| OBSERV-09 | Phase 4 | Pending |
| OBSERV-10 | Phase 4 | Pending |
| OBSERV-11 | Phase 4 | Pending |
| OBSERV-12 | Phase 5 | Complete |
| HEALTH-01 | Phase 5 | Complete |
| HEALTH-02 | Phase 5 | Complete |
| HEALTH-03 | Phase 5 | Complete |
| HEALTH-04 | Phase 5 | Complete |
| HEALTH-05 | Phase 5 | Complete |
| ERROR-01 | Phase 4 | Pending |
| ERROR-02 | Phase 4 | Pending |
| ERROR-03 | Phase 4 | Pending |
| ERROR-04 | Phase 4 | Pending |
| ERROR-05 | Phase 4 | Pending |
| ERROR-06 | Phase 4 | Pending |
| ERROR-07 | Phase 4 | Pending |
| ERROR-08 | Phase 4 | Pending |
| ERROR-09 | Phase 4 | Pending |
| ERROR-10 | Phase 4 | Pending |
| ERROR-11 | Phase 4 | Pending |
| TEST-01 | Phase 8 | Pending |
| TEST-02 | Phase 8 | Pending |
| TEST-03 | Phase 8 | Pending |
| TEST-04 | Phase 8 | Pending |
| TEST-05 | Phase 8 | Pending |
| TEST-06 | Phase 8 | Pending |

**Coverage:**
- v1 requirements: 102 total (7 INFRA + 15 PERSIST + 10 ENTITY + 16 HTTP + 20 VALID + 12 OBSERV + 5 HEALTH + 11 ERROR + 6 TEST). Note: previous header line summed to 103 by counting ERROR-11 twice; corrected here.
- Mapped to phases: 102 (100%)
- Unmapped: 0

Per-phase coverage:
- Phase 1 (Repository Scaffold): 4 requirements (INFRA-01..04)
- Phase 2 (Postgres + Docker Compose): 2 requirements (INFRA-06, INFRA-07)
- Phase 3 (EF Core Persistence Base): 10 requirements (ENTITY-01, ENTITY-02, PERSIST-02..07, PERSIST-11, PERSIST-15)
- Phase 4 (Cross-Cutting Middleware + Error Handling): 14 requirements (OBSERV-09..11, ERROR-01..11)
- Phase 5 (Observability + Health Probes): 14 requirements (OBSERV-01..08, OBSERV-12, HEALTH-01..05)
- Phase 6 (Validation + Mapping Base): 8 requirements (VALID-01..07, HTTP-10)
- Phase 7 (Generic HTTP Base + Composition Root): 9 requirements (HTTP-01..03, HTTP-08..09, HTTP-13..16)
- Phase 8 (Entity Build-Out + Migrations + Docker Runtime + Tests): 41 requirements (PERSIST-01, PERSIST-08..10, PERSIST-12..14, ENTITY-03..10, HTTP-04..07, HTTP-11..12, VALID-08..20, INFRA-05, TEST-01..06)

---
*Requirements defined: 2026-05-26*
*Last updated: 2026-05-26 — ENTITY-04 and VALID-11 amended: ProcessorEntity InputSchemaId/OutputSchemaId are now nullable `Guid?`*
