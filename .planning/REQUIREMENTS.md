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
- [x] **INFRA-05
**: Multistage `Dockerfile` using `mcr.microsoft.com/dotnet/sdk:8.0` for build and `mcr.microsoft.com/dotnet/aspnet:8.0` for runtime
- [x] **INFRA-06
**: `docker-compose.yml` (filename `compose.yaml` per Compose v2 default per Phase 2 D-10) declares `postgres:17-alpine` (with `pg_isready` healthcheck), `elasticsearch:8.15.5` (with `curl -fs '/_cluster/health?wait_for_status=yellow&timeout=5s'` healthcheck and `start_period: 60s` per Phase 11 D-12; dev posture per sk2_1 — `discovery.type=single-node`, `xpack.security.enabled=false`, no volume), `otel-collector:0.152.0` (distroless — no in-container healthcheck per Phase 5 Plan 05-01 deviation #1), and `prom/prometheus:v3.11.3` (with `wget --spider /-/healthy` healthcheck per Phase 11 D-13). `baseapi-service.depends_on` extends to ALL four services: `postgres: service_healthy`, `otel-collector: service_started`, `elasticsearch: service_healthy`, `prometheus: service_healthy` (per Phase 11 D-15). Compose-up therefore blocks for ~60s on ES cold-start; acceptable for dev + CI.
- [x] **INFRA-07
**: Postgres data persisted in a named volume across `docker-compose down/up`
- [ ] **INFRA-08
**: Phase 11 compose-stack additions. `compose.yaml` declares `elasticsearch` service (image `docker.elastic.co/elasticsearch/elasticsearch:8.15.5` per Phase 11 D-10; env `discovery.type=single-node` + `xpack.security.enabled=false` + `xpack.security.enrollment.enabled=false` + `ES_JAVA_OPTS=-Xms512m -Xmx512m`; port `9200:9200`; healthcheck `curl -fs 'http://localhost:9200/_cluster/health?wait_for_status=yellow&timeout=5s' || exit 1` with `start_period: 60s`; no volume — ephemeral dev posture per D-12) AND `prometheus` service (image `prom/prometheus:v3.11.3` per Phase 11 D-11; command `--config.file=/etc/prometheus/prometheus.yml --web.enable-lifecycle`; bind-mount `./prometheus.yml:/etc/prometheus/prometheus.yml:ro`; port `9090:9090`; healthcheck `wget --no-verbose --tries=1 --spider http://localhost:9090/-/healthy`; depends on `otel-collector: service_started`; no volume — ephemeral dev posture per D-13). `otel-collector` image bumped to `otel/opentelemetry-collector-contrib:0.152.0` (per Phase 11 D-09; D-14 adds port `8889:8889` to the existing ports list and removes the `./tests/.otel-out:/var/otel-out` bind-mount + `user: "0:0"` override no longer needed without file exporter). `prometheus.yml` (NEW file at repo root) carries the single `job_name: 'otel-collector'` scrape config targeting `otel-collector:8889` per D-08. Container-name collision with sibling sk2_1 stack is acknowledged Out of Scope (RESEARCH Pitfall 4) — sk_p and sk2_1 stacks are mutually exclusive on a given Docker daemon.

### Persistence

- [x] **PERSIST-01
**: Single `AppDbContext` in `BaseApi.Service/Persistence/` exposing `DbSet<T>` for all 5 concrete entities and 3 junction entities
- [x] **PERSIST-02**: `BaseDbContext` (abstract) in `BaseApi.Core/Persistence/` registers `AuditInterceptor`
- [x] **PERSIST-03**: `ISaveChangesInterceptor` implementation (`AuditInterceptor`) auto-stamps `CreatedAt`/`UpdatedAt` with `DateTime.UtcNow` (required by Npgsql `timestamptz`)
- [x] **PERSIST-04**: `AuditInterceptor` sets `CreatedBy`/`UpdatedBy` from `IHttpContextAccessor.HttpContext?.User?.Identity?.Name` when available, null otherwise
- [x] **PERSIST-05**: Snake_case naming via `EFCore.NamingConventions` (`UseSnakeCaseNamingConvention()`) applied BEFORE first migration (cannot be retrofitted)
- [x] **PERSIST-06**: All primary keys are `Guid` mapped to Postgres `uuid`
- [x] **PERSIST-07**: `BaseEntity.CreatedAt`/`UpdatedAt` map to `timestamptz` columns
- [x] **PERSIST-08
**: `Schema.Definition` and `Assignment.Payload` map to `jsonb` columns
- [x] **PERSIST-09
**: Migrations applied on startup by `BaseApi.Service` via `db.Database.MigrateAsync()` before readiness probe transitions to Healthy
- [x] **PERSIST-10
**: Migration failure surfaces as failing readiness probe (process does not crash)
- [x] **PERSIST-11**: Generic `Repository<TEntity>` in `BaseApi.Core` (Get, List, Add, Update, Delete with `CancellationToken`)
- [x] **PERSIST-12
**: Junction tables `StepNextSteps`, `WorkflowEntrySteps`, `WorkflowAssignments` configured as explicit join entities with composite PKs and FKs to both sides
- [x] **PERSIST-13
**: All FK columns have DB-level FK constraints (enforced by Postgres)
- [x] **PERSIST-14
**: Unique index on `Processor.SourceHash`
- [x] **PERSIST-15**: `DbContext` registered with Scoped lifetime in DI
- [x] **PERSIST-16**: `BaseEntity` rows carry a Postgres `xmin` shadow concurrency token mapped via `IsConcurrencyToken()` on every `BaseEntity` subclass (model-builder iteration in `BaseDbContext.OnModelCreating`). Phase 3 wires the shadow property; Phase 4 maps `DbUpdateConcurrencyException` -> HTTP 409 (D-03a / cross-phase impact from Phase 3 CONTEXT.md D-03)

### Entity Model

- [x] **ENTITY-01**: `BaseEntity` (abstract) in `BaseApi.Core/Entities/BaseEntity.cs` with: `Id` Guid, `Name` string, `Version` string, `CreatedAt` DateTime, `UpdatedAt` DateTime, `CreatedBy` string?, `UpdatedBy` string?, `Description` string?
- [x] **ENTITY-02**: `BaseEntity` is abstract — no table; 5 concrete tables, no inheritance discriminator
- [x] **ENTITY-03
**: `SchemaEntity : BaseEntity` adds `Definition` string (jsonb)
- [x] **ENTITY-04
**: `ProcessorEntity : BaseEntity` adds `SourceHash` string (SHA-256 hex, unique), `InputSchemaId` Guid? (nullable FK→Schema), `OutputSchemaId` Guid? (nullable FK→Schema), `ConfigSchemaId` Guid? (nullable FK→Schema). Null permitted on all three — supports source processors (no input), sink processors (no output), and unconfigured processors (no config). DB columns are nullable; Postgres FK constraints (`fk_processor_input_schema_id`, `fk_processor_output_schema_id`, `fk_processor_config_schema_id`) still enforced when value is non-null.
- [x] **ENTITY-05
**: `StepEntity : BaseEntity` adds `ProcessorId` Guid (FK→Processor), `NextStepIds` List<Guid>? (M2M self-ref via `StepNextSteps`), `EntryCondition` `StepEntryCondition` (default `PreviousCompleted`)
- [x] **ENTITY-06
**: `StepEntryCondition` enum: `PreviousProcessing=0`, `PreviousCompleted=1`, `PreviousFailed=2`, `PreviousCancelled=3`, `Always=4`, `Never=5`
- [x] **ENTITY-07
**: `AssignmentEntity : BaseEntity` adds `StepId` Guid (FK→Step), `Payload` string (jsonb)
- [x] **ENTITY-08
**: `WorkflowEntity : BaseEntity` adds `EntryStepIds` List<Guid> (M2M to Step via `WorkflowEntrySteps`, required non-empty), `AssignmentIds` List<Guid>? (M2M to Assignment via `WorkflowAssignments`), `CronExpression` string? (nullable)
- [x] **ENTITY-09
**: No navigation properties between entities — only `Guid` FK columns + explicit junction entities
- [x] **ENTITY-10
**: `(Name, Version)` is NOT unique on any entity

### HTTP Surface

- [x] **HTTP-01
**: Controller-based ASP.NET Core (not Minimal APIs) — required for `BaseController` inheritance
- [x] **HTTP-02
**: `BaseController<TEntity, TCreate, TUpdate, TRead>` abstract generic in `BaseApi.Core/Controllers/`, decorated with `[ApiController]` and `[Route("api/v1/[controller]")]`
- [x] **HTTP-03
**: Standard CRUD verbs on every concrete controller: `GET /api/v1/{entity}` (list), `GET /api/v1/{entity}/{id}`, `POST /api/v1/{entity}`, `PUT /api/v1/{entity}/{id}`, `DELETE /api/v1/{entity}/{id}`
- [x] **HTTP-04
**: Each entity has 3 DTOs (Create, Update, Read) under `BaseApi.Service/{Entity}/Dtos/`
- [x] **HTTP-05
**: `CreateDto` excludes server-controlled fields (`Id`, `CreatedAt`, `UpdatedAt`, `CreatedBy`, `UpdatedBy`)
- [x] **HTTP-06
**: `UpdateDto` excludes `Id`, `CreatedAt`, `CreatedBy` (everything else mutable)
- [x] **HTTP-07
**: `ReadDto` includes every entity field
- [x] **HTTP-08
**: Layering enforced: Controller → Service → Repository (no Controller-to-Repository shortcut)
- [x] **HTTP-09
**: `BaseService<TEntity, TCreate, TUpdate, TRead>` in `BaseApi.Core` provides generic CRUD plus virtual `SyncJunctionsAsync` hook for M2M sync
- [x] **HTTP-10
**: `IEntityMapper<TEntity, TCreate, TUpdate, TRead>` interface in `BaseApi.Core` defines mapping signatures consumed by `BaseService`
- [x] **HTTP-11
**: Each entity has a Mapperly `[Mapper] partial class` in `BaseApi.Service/{Entity}/Mapping/` implementing `IEntityMapper`
- [x] **HTTP-12
**: Concrete controllers are empty derived classes (e.g., `public class SchemasController : BaseController<SchemaEntity, SchemaCreateDto, SchemaUpdateDto, SchemaReadDto>`)
- [x] **HTTP-13
**: `AddBaseApi<TDbContext>(IConfiguration)` extension in `BaseApi.Core/Extensions/` registers DI for: DbContext + naming convention + interceptors, generic repositories, generic services, mappers, validators, OTel, correlation, error middleware, health checks
- [x] **HTTP-14
**: `UseBaseApi()` extension in `BaseApi.Core/Extensions/` registers middleware in correct order (exception → correlation → routing → CORS → endpoints)
- [x] **HTTP-15
**: API versioning via `Asp.Versioning.Http` with URL prefix `/api/v1/` from v1 release (prevents breaking URL change later)
- [x] **HTTP-16
**: OpenAPI/Swagger UI via `Swashbuckle.AspNetCore`; exposed in Development environment

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
- [x] **VALID-08
**: `SchemaCreate/UpdateDto.Definition`: valid JSON syntax AND valid JSON Schema (draft 2020-12) via `JsonSchema.Net`
- [x] **VALID-09
**: `JsonSchema.Net` remote `$ref` network access disabled (SSRF prevention)
- [x] **VALID-10
**: `ProcessorCreate/UpdateDto.SourceHash`: regex `^[a-f0-9]{64}$` (lowercase SHA-256 hex)
- [x] **VALID-11**: `ProcessorCreate/UpdateDto.InputSchemaId`/`OutputSchemaId`/`ConfigSchemaId`: nullable `Guid?` — null is valid (source/sink/unconfigured processor). When present, must not equal `Guid.Empty`. FluentValidation pattern: `When(x => x.InputSchemaId.HasValue, () => RuleFor(x => x.InputSchemaId!.Value).NotEqual(Guid.Empty));` (same for `OutputSchemaId` and `ConfigSchemaId`). `Guid.Empty` (`00000000-0000-0000-0000-000000000000`) is rejected at HTTP 400 by the validator, NOT at the DB layer. FK existence for non-empty Guids is still enforced by Postgres at persist time (SQLSTATE 23503 → HTTP 422 per ERROR-04
).
- [x] **VALID-12
**: `StepCreate/UpdateDto.ProcessorId`: `NotEmpty` Guid
- [x] **VALID-13
**: `StepCreate/UpdateDto.NextStepIds`: each unique; on Update, none equal to the Step's own Id
- [x] **VALID-14
**: `StepCreate/UpdateDto.EntryCondition`: `IsInEnum()`
- [x] **VALID-15
**: `AssignmentCreate/UpdateDto.StepId`: `NotEmpty` Guid
- [x] **VALID-16
**: `AssignmentCreate/UpdateDto.Payload`: valid JSON syntax (parsed by `System.Text.Json`), MaxLength 1,048,576 chars (~1 MB)
- [x] **VALID-17
**: `WorkflowCreate/UpdateDto.EntryStepIds`: `NotEmpty`, each unique
- [x] **VALID-18
**: `WorkflowCreate/UpdateDto.AssignmentIds`: each unique when present
- [x] **VALID-19
**: `WorkflowCreate/UpdateDto.CronExpression`: when present, parses as valid 5-field expression via `Cronos`
- [x] **VALID-20
**: Concrete entity validators inherit base rules via `Include(new BaseDtoValidator<...>())`

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
- [ ] **OBSERV-12 [SUPERSEDED — Phase 11 D-03]
**: OTel tracing pipeline removed in Phase 11 (D-03). Moved to Out of Scope. Rationale: no traces backend in v1 (mirrors sk2_1 CLAUDE.md non-negotiable #2); `.WithTracing()` registration stripped from `AddBaseApiObservability`; collector traces pipeline + Npgsql tracing instrumentation no longer wired. See "OTel tracing pipeline (traces backend)" Out of Scope row.
- [ ] **OBSERV-13
**: OTel collector ships logs to Elasticsearch via the contrib `elasticsearch` exporter (`mapping.mode: none` per Phase 11 D-06). Logs land in the data stream `logs-generic-default` (backing index `.ds-logs-generic-default-NNNN`) with the OTLP raw field shape preserved — `Attributes.CorrelationId` matches the Phase 4 `X-Correlation-Id` middleware value; resource attributes `service.name=sk-api` and `service.version=3.2.0` appear under `Resource.attributes` on every log doc. ES exporter `endpoints: [http://elasticsearch:9200]`; no auth, no TLS (dev posture). Verification: Wave 0 manual `curl /_cat/indices` after first compose-up confirms actual index name and field shape (RESEARCH Open Q1).
- [ ] **OBSERV-14
**: OTel collector exposes a Prometheus exporter on `0.0.0.0:8889` with `resource_to_telemetry_conversion: { enabled: true }` (Phase 11 D-07; `service.name` becomes the `service_name` Prom label — load-bearing for test assertions) and `send_timestamps: true`. The standalone Prometheus container scrapes `otel-collector:8889` at 15s intervals (Phase 11 D-08, verbatim from sk2_1). Test code queries `http://localhost:9090/api/v1/query?query=...` and assertions reference Prom-form metric names (e.g., `http_server_request_duration_seconds_count` — dot-to-underscore + `_seconds_count` histogram suffix per OTel→Prom spec, RESEARCH Pitfall 1). `filter/health_metrics` processor still drops `/health/*` data points before they reach the Prom exporter (D-04; Phase 5 fix-forward preserved).

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

- [x] **TEST-01
**: Test project `tests/BaseApi.Tests/` using `xUnit` v3
- [x] **TEST-02
**: `Microsoft.AspNetCore.Mvc.Testing` + `WebApplicationFactory<Program>` for integration tests
- [x] **TEST-05
**: At least one happy-path integration test per CRUD verb per entity (5 entities × 5 verbs = 25 smoke tests, minimum)
- [x] **TEST-06
**: At least one negative-path integration test per error mapping (400 validation, 404 not found, 409 unique violation, 422 FK violation)
- [ ] **TEST-07
**: Round-trip E2E test class(es) under `tests/BaseApi.Tests/Observability/` (Phase 11 D-17) drive a real HTTP request against a sk_p business endpoint (Schema POST per D-17) via in-process `Phase11WebAppFactory : Phase8WebAppFactory` then poll BOTH backends within a bounded budget: (a) Elasticsearch `POST /logs-generic-default/_search` with `Attributes.CorrelationId` term query — must return a hit within 30s (`ElasticsearchTestClient` helper with exponential backoff 200ms→3.2s and HTTP 404 + empty-hits tolerance per RESEARCH Pattern 2); (b) Prometheus `GET /api/v1/query?query=http_server_request_duration_seconds_count{service_name="sk-api",http_route="api/v1/schemas"}` — must return a sample with cumulative count ≥ requests-issued within 60s (`PrometheusTestClient` helper with mandatory 15s initial sleep then 3s poll interval per RESEARCH Pattern 3 / Pitfall 7). Per-test unique correlation-id (`$"{Guid.NewGuid():N}"`) is the ES cleanup discipline (T-11-03 mitigation; analog to Phase 3 D-15 psql `\l` byte-identical proof). `Phase11WebAppFactory` overrides `PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 1_000` (RESEARCH Pattern 4 / Pitfall 7 — default 60s would exceed budget). Tests carry `[Trait("Phase","11")]` + `[Trait("Category","E2E")]` for `dotnet test --filter "Category!=E2E"` fast-path.

## v2 Requirements

Deferred to future release. Tracked but not in current roadmap.

### Hardening

- **VALID-21**: Dynamic schema conformance — `Assignment.Payload` validated against `Schema.Definition` referenced by `Assignment.SchemaId` (cross-entity dynamic validation). *Deferred per N2 decision.*
- **INFRA-08**: Multi-instance startup migration with Postgres advisory lock to prevent concurrent migration corruption. *v1 ships single-replica.*
- **INFRA-09**: Separate Postgres roles for migration vs runtime (least-privilege). *v1 uses a single role.*
- **TEST-03**: `Testcontainers.PostgreSql` for real Postgres in tests. *Deferred to v2 per Phase 8 D-05: PostgresFixture pattern (per-class throwaway DB on the Phase 2 compose-running Postgres at localhost:5433) is proven across 98 facts spanning Phases 3-7; Testcontainers cold-start adds ~3-5s/fixture on Windows Docker Desktop with no behavioral gain at v1 scale. Migration when CI requires self-contained test runs (no Docker compose prereq).*
- **TEST-04**: `Respawn` (or equivalent) used to reset DB between tests. *Deferred to v2 per Phase 8 D-06: per-class throwaway DBs preserve the Phase 3 D-15 byte-identical `psql \l` BEFORE/AFTER no-leak proof; Respawn would invalidate that proof. Revisit if per-fact reset cost becomes noticeable at higher fact counts.*

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
| OTel tracing pipeline (traces backend) | Phase 11 D-03 — no traces backend in v1; mirrors sk2_1 CLAUDE.md non-negotiable #2. Supersedes OBSERV-12. `.WithTracing()` stripped from `AddBaseApiObservability`; collector traces pipeline deleted; `TraceExportTests` deleted. Revival is a future-milestone candidate when request-flow debugging becomes painful. |

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
| PERSIST-09 | Phase 8 | Complete |
| PERSIST-10 | Phase 8 | Complete |
| PERSIST-11 | Phase 3 | Complete |
| PERSIST-12 | Phase 8 | Pending |
| PERSIST-13 | Phase 8 | Complete |
| PERSIST-14 | Phase 8 | Pending |
| PERSIST-15 | Phase 3 | Complete |
| PERSIST-16 | Phase 3 | Complete |
| ENTITY-01 | Phase 3 | Complete |
| ENTITY-02 | Phase 3 | Complete |
| ENTITY-03 | Phase 8 | Pending |
| ENTITY-04 | Phase 8 | Complete |
| ENTITY-05 | Phase 8 | Pending |
| ENTITY-06 | Phase 8 | Pending |
| ENTITY-07 | Phase 8 (amended Phase 10) | Complete |
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
| VALID-11 | Phase 8 | Complete |
| VALID-12 | Phase 8 | Pending |
| VALID-13 | Phase 8 | Pending |
| VALID-14 | Phase 8 | Pending |
| VALID-15 | Phase 8 (amended Phase 10) | Complete |
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
| OBSERV-12 | Phase 5 → Phase 11 (Superseded) | Out of Scope |
| HEALTH-01 | Phase 5 | Complete |
| HEALTH-02 | Phase 5 | Complete |
| HEALTH-03 | Phase 5 | Complete |
| HEALTH-04 | Phase 5 | Complete |
| HEALTH-05 | Phase 5 | Complete |
| ERROR-01 | Phase 4 | Pending |
| ERROR-02 | Phase 4 | Pending |
| ERROR-03 | Phase 4 | Pending |
| ERROR-04 | Phase 4 | Complete |
| ERROR-05 | Phase 4 | Pending |
| ERROR-06 | Phase 4 | Pending |
| ERROR-07 | Phase 4 | Pending |
| ERROR-08 | Phase 4 | Pending |
| ERROR-09 | Phase 4 | Pending |
| ERROR-10 | Phase 4 | Pending |
| ERROR-11 | Phase 4 | Complete |
| TEST-01 | Phase 8 | Pending |
| TEST-02 | Phase 8 | Pending |
| TEST-03 | v2 | Deferred |
| TEST-04 | v2 | Deferred |
| TEST-05 | Phase 8 | Complete |
| TEST-06 | Phase 8 | Complete |
| OBSERV-13 | Phase 11 | Pending |
| OBSERV-14 | Phase 11 | Pending |
| INFRA-08 | Phase 11 | Pending |
| TEST-07 | Phase 11 | Pending |

**Coverage:**
- v1 requirements: 105 total active v1 (one OBSERV superseded to Out of Scope; 4 new IDs added Phase 11). Breakdown: 7 INFRA (now 8 with INFRA-08) + 15 PERSIST + 10 ENTITY + 16 HTTP + 20 VALID + 12 OBSERV (now 13 active: OBSERV-12 superseded + OBSERV-13/14 added) + 5 HEALTH + 11 ERROR + 6 TEST (now 7 with TEST-07; TEST-03/04 still v2-deferred). Note: previous header line summed to 103 by counting ERROR-11 twice; corrected here.
- Mapped to phases: 103 v1 + 2 v2 (TEST-03, TEST-04) = 105 (100%)
- Unmapped: 0

Per-phase coverage:
- Phase 1 (Repository Scaffold): 4 requirements (INFRA-01..04)
- Phase 2 (Postgres + Docker Compose): 2 requirements (INFRA-06, INFRA-07)
- Phase 3 (EF Core Persistence Base): 10 requirements (ENTITY-01, ENTITY-02, PERSIST-02..07, PERSIST-11, PERSIST-15)
- Phase 4 (Cross-Cutting Middleware + Error Handling): 14 requirements (OBSERV-09..11, ERROR-01..11)
- Phase 5 (Observability + Health Probes): 13 requirements (OBSERV-01..08, HEALTH-01..05)
- Phase 6 (Validation + Mapping Base): 8 requirements (VALID-01..07, HTTP-10)
- Phase 7 (Generic HTTP Base + Composition Root): 9 requirements (HTTP-01..03, HTTP-08..09, HTTP-13..16)
- Phase 8 (Entity Build-Out + Migrations + Docker Runtime + Tests): 39 requirements (PERSIST-01, PERSIST-08..10, PERSIST-12..14, ENTITY-03..10, HTTP-04..07, HTTP-11..12, VALID-08..20, INFRA-05, TEST-01, TEST-02, TEST-05, TEST-06) — TEST-03 + TEST-04 deferred to v2 per Phase 8 CONTEXT.md D-05 / D-06
- Phase 11 (Migrate Prometheus and Elasticsearch from compose stack sk2_1): 4 requirements (OBSERV-13, OBSERV-14, INFRA-08, TEST-07) — plus amendments to OBSERV-12 (superseded → Out of Scope) and INFRA-06 (extended for ES + Prom + collector image bump)

---
*Requirements defined: 2026-05-26*
*Last updated: 2026-05-28 — Phase 11 amendments: OBSERV-12 superseded (traces removed); INFRA-06 extended (ES + Prom + collector image bump); new REQ-IDs OBSERV-13/14 + INFRA-08 + TEST-07*
