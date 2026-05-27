# Steps API

## What This Is

A .NET 8 Web API platform that exposes CRUD over a workflow-engine data model — Schema, Processor, Step, Assignment, Workflow — built as a modular monolith on top of a reusable base library (`BaseApi.Core`) that future entities can plug into without rewriting infrastructure. The service ships logs and HTTP metrics to an OpenTelemetry Collector, runs against PostgreSQL via Docker Compose, and exposes RFC 7807 Problem Details on every error. Consumers (external Orchestrator + Scheduler) read Workflow entities to drive scheduled data-processing pipelines.

## Core Value

**A solid, observable, validated CRUD foundation that future workflow-platform features build on without rework.** If everything else fails, the base must reliably persist entities, validate inputs, emit telemetry, surface clean errors, and answer health probes.

## Requirements

### Validated

**Phase 1 (Repository Scaffold) — 2026-05-26**
- [x] Service runs on .NET 8 Web API (scaffold boots; `dotnet --version` returns pinned 8.0.421; `GET /` returns HTTP 404 with no controllers registered)
- [x] Service configuration is in `appsettings.json` with environment overrides (`appsettings.json` + `appsettings.Development.json` with localhost dev defaults; 4 sections per INFRA-04)

**Phase 2 (Postgres + Docker Compose) — 2026-05-26**
- [x] PostgreSQL (official Docker image) is the only data store (`postgres:17-alpine` pinned in `compose.yaml`)
- [x] `compose.yaml` orchestrates Postgres + the service for local dev (`baseapi-service` block deferred behind `phase-8` profile per D-08 until Phase 8 lands the Dockerfile)

**Phase 3 (EF Core Persistence Base) — 2026-05-27**
- [x] `BaseEntity` (abstract, no table) provides the 8 shared fields `Id`/`Name`/`Version`/`CreatedAt`/`UpdatedAt`/`CreatedBy?`/`UpdatedBy?`/`Description?` (ENTITY-01, ENTITY-02)
- [x] `AuditInterceptor` auto-stamps `CreatedAt`/`UpdatedAt`/`CreatedBy`/`UpdatedBy` on `SavingChangesAsync` using `TimeProvider.GetUtcNow().UtcDateTime` (Pitfall 1 — `Kind == Utc`) and `HttpContext?.User?.Identity?.Name` (null-safe per D-08) (PERSIST-03, PERSIST-04, PERSIST-07)
- [x] `BaseDbContext` (abstract, no DbSets) wires `UseSnakeCaseNamingConvention()` and the Postgres `xmin` shadow concurrency token via model-builder iteration over every `BaseEntity` subclass (PERSIST-02, PERSIST-05, PERSIST-06, PERSIST-16 — new requirement appended this phase per D-03b)
- [x] Generic `IRepository<TEntity : BaseEntity>` + sealed `Repository<TEntity>` expose EXACTLY 5 methods (`GetAsync`, `ListAsync`, `AddAsync`, `Update`, `DeleteAsync`) — no `IQueryable<>` surface, no `Where(predicate)` overload (PERSIST-11, D-04)
- [x] Phase 3 SC#1-4 + Dim 6 + Dim 7 GREEN against real Postgres 17 at `localhost:5433` (per-class throwaway `stepsdb_test_{Guid:N}` DBs; ClearAllPools + DROP WITH FORCE on dispose; byte-identical `psql \l` BEFORE/AFTER snapshots prove zero leaks per D-15)
- [x] `DbContext` Scoped DI lifetime verified end-to-end via `AddDbContext<TestDbContext>` + two-scope round-trip (PERSIST-15)
- [x] CPM contract preserved (zero `Version=` attributes on any `<PackageReference>` across Core + Tests csproj) — Microsoft.Extensions.TimeProvider.Testing 8.10.0 pin added (D-13), 4 EF Core PackageReferences + FrameworkReference `Microsoft.AspNetCore.App` added to Core (D-12), 5 PackageReferences + FrameworkReference added to Tests (D-13)

### Active

Grouped for readability; final REQ-IDs assigned in REQUIREMENTS.md.

**Runtime & deployment**

**Persistence**
- [ ] EF Core 8 + Npgsql for all data access
- [ ] Single shared database, single shared `AppDbContext`
- [ ] Migrations owned by the service, applied on startup
- [ ] All entity primary keys are `Guid`
- [ ] `BaseEntity` (abstract, no table) provides shared fields: `Id`, `Name`, `Version`, `CreatedAt`, `UpdatedAt`, `CreatedBy?`, `UpdatedBy?`, `Description?`
- [ ] `AuditInterceptor` auto-stamps `CreatedAt`/`UpdatedAt`/`CreatedBy`/`UpdatedBy` on `SaveChanges`
- [ ] Cross-entity references use scalar `Guid` FK columns; multi-references use junction tables (no navigation properties between bounded contexts)
- [ ] DB-level foreign-key constraints enforced

**Entity model (5 concrete entities)**
- [ ] `SchemaEntity` — `Definition` (jsonb; validated JSON + valid JSON Schema)
- [ ] `ProcessorEntity` — `SourceHash` (SHA-256 hex, unique), `InputSchemaId` (nullable, FK→Schema), `OutputSchemaId` (nullable, FK→Schema)
- [ ] `StepEntity` — `ProcessorId`, `NextStepIds` (optional M2M self-ref), `EntryCondition` (enum, defaults to `PreviousCompleted`)
- [ ] `AssignmentEntity` — `StepId`, `SchemaId`, `Payload` (jsonb; valid JSON syntactically; no dynamic schema conformance check)
- [ ] `WorkflowEntity` — `EntryStepIds` (required M2M to Step), `AssignmentIds` (optional M2M to Assignment), `CronExpression` (optional; nullable → not scheduled)
- [ ] Junction tables: `StepNextSteps`, `WorkflowEntrySteps`, `WorkflowAssignments`

**HTTP surface**
- [ ] One controller per entity, each derived from abstract generic `BaseController<TEntity, TCreateDto, TUpdateDto, TReadDto>`
- [ ] Standard CRUD verbs: `GET` (list), `GET /{id}`, `POST`, `PUT /{id}`, `DELETE /{id}`
- [ ] DTO shape: 3 DTOs per entity (Create, Update, Read)
- [ ] Layering: Controller → Service → Repository (generic repository in base; service holds entity-specific logic + M2M sync)
- [ ] Mapper: Mapperly source-generators; one `[Mapper] partial` class per entity

**Validation**
- [ ] FluentValidation
- [ ] `BaseEntityValidator<T>` rules: `Name` not empty / max length 200; `Version` matches SemVer `^\d+\.\d+\.\d+$`; `Description` max length
- [ ] Per-entity validators inherit base rules and add entity-specific rules (e.g., SHA-256 regex for `SourceHash`, JSON Schema validation for `Schema.Definition`, valid JSON syntax for `Assignment.Payload`)
- [ ] `(Name, Version)` is *not* unique

**Observability**
- [ ] OpenTelemetry SDK wired for logs + HTTP server metrics
- [ ] OTLP exporter to an external OTel Collector (`OTEL_EXPORTER_OTLP_ENDPOINT` honored)
- [ ] `Logging:LogLevel` from `appsettings.json` filters both console and OTel sinks identically (single source of truth)
- [ ] `Service:Name` (`sk-api`) and `Service:Version` (`3.2.0`) populate the OTel `service.name` / `service.version` resource attributes
- [ ] `X-Correlation-Id` middleware: read header if present, generate UUID if missing; attach to log scope and to every response (incl. error responses)

**Health probes**
- [ ] Startup probe — service has finished initialization (DI built, migrations applied)
- [ ] Liveness probe — process is alive
- [ ] Readiness probe — service can reach Postgres (and any other required dependency)

**Error responses**
- [ ] All failures return RFC 7807 Problem Details JSON in the body in addition to HTTP status code
- [ ] FluentValidation failures → 400 with field-level `errors` map
- [ ] Postgres FK violation (SQLSTATE `23503`) → 422 with offending field
- [ ] Postgres unique violation (SQLSTATE `23505`) → 409 with offending field
- [ ] Resource not found → 404 with id detail
- [ ] Unhandled exception → 500 with generic message + correlationId; full stack to logs only
- [ ] Every error response includes the request's `correlationId`

### Out of Scope

- **Authentication / authorization** — not in v1; service is open. `CreatedBy`/`UpdatedBy` populated only when `HttpContext.User.Identity.Name` is available; otherwise null. *Why: unspecified in milestone scope; will be added when an auth boundary is defined.*
- **Orchestrator / scheduler logic** — external systems consume `WorkflowEntity.CronExpression` and the workflow graph. The webapi is CRUD only. *Why: explicitly out of scope per user.*
- **Workflow execution engine** — running a workflow, tracking runs, retries, parallelism. *Why: external responsibility.*
- **`WorkflowScheduleEntity`** (originally listed) — removed; control is per-workflow via `CronExpression` nullability. *Why: redundant given per-workflow control was sufficient.*
- **Dynamic Payload-vs-Schema conformance** — `Assignment.Payload` is only validated as syntactically valid JSON. *Why: explicitly chosen "no" (N2).*
- **Pre-validation of FK existence over HTTP** — relies on Postgres FK constraint + clean error mapping; no upfront `EntityBApi` GET. *Why: chosen Option 1.*
- **`IsActive` / `Environment` gating flag on `WorkflowEntity`** — `CronExpression` nullability serves as implicit gating. *Why: explicitly declined.*
- **Soft delete** — not in v1. CRUD `DELETE` is a hard delete. *Why: unspecified; can be added later as a base concern if needed.*
- **Pagination / filtering / sorting on list endpoints** — basic `GET` returns all; complex query is out of v1. *Why: defer until proven needed.*
- **Multiple deployable services / NuGet packaging of `BaseApi.Core`** — Option B (single API) chosen. *Why: bounded-context entities are tightly related via FKs, single team, milestone speed prioritized.*
- **Workflow execution result tracking / run history** — would have been `WorkflowRunEntity`; not in this model. *Why: external responsibility.*

## Context

- **Project type:** Greenfield .NET 8 Web API on Windows host, targeting Linux containers for deployment.
- **Repository:** Currently empty (git initialized this session); no prior code or schema.
- **Project structure plan:**
  - `src/BaseApi.Core/` — reusable class library (abstract `BaseEntity`, `BaseDbContext`, `AuditInterceptor`, `Repository<T>`, `BaseController<TEntity,...>`, base validators, correlation + error middleware, OTel wiring, health checks, `AddBaseApi(...)` extension)
  - `src/BaseApi.Service/` — the one runnable webapi: `AppDbContext` (knows all entities and junctions), per-entity files (entity class, DTOs, validator, Mapperly mapper, controller, service), migrations, `Program.cs`, `appsettings.json`, `Dockerfile`
  - `docker-compose.yml` — Postgres + `BaseApi.Service`
  - `tests/` — TBD when test stack chosen
- **Domain framing:** workflow / data-pipeline platform. `Schema` defines data shapes; `Processor` transforms input→output schemas; `Step` couples a Processor to next steps; `Assignment` binds a Step to a Schema with a Payload; `Workflow` is the DAG of entry steps + assignments with an optional cron. External Orchestrator + Scheduler consume `Workflow` records to drive execution.
- **Future milestones:** entity build-out is well-defined and parallelizable. Each additional milestone adds one entity (controller, DTOs, validator, mapper, service, plus migration) on top of the locked-in base library.
- **Inheritance model:** abstract generic controllers + base entity + base validators + service extensions. No `dotnet new` templates; no NuGet packaging (Option B chose monorepo + single service).
- **FK relationship topology:** Schema (root) ← Processor; Processor ← Step (+self-ref via `NextStepIds`); Step ← Assignment; Schema ← Assignment; Step + Assignment ← Workflow. Migration order is fixed by this graph.

## Constraints

- **Tech stack — runtime:** .NET 8 (LTS). *Reason: user-specified.*
- **Tech stack — database:** PostgreSQL via the official Docker image. *Reason: user-specified.*
- **Tech stack — ORM:** EF Core 8 + Npgsql (`Npgsql.EntityFrameworkCore.PostgreSQL`). *Reason: native Guid↔uuid mapping, audit interceptor, generic repository, idiomatic for the stack.*
- **Tech stack — mapping:** Mapperly. *Reason: source-generated, zero runtime reflection, AOT-safe, modern .NET 8 standard.*
- **Tech stack — validation:** FluentValidation. *Reason: inheritable validators match the base→concrete extension pattern; SemVer regex and per-entity rules compose cleanly.*
- **Tech stack — observability:** OpenTelemetry .NET SDK with OTLP exporter. *Reason: user-specified Collector destination, vendor-neutral.*
- **Tech stack — error format:** RFC 7807 Problem Details. *Reason: built-in to ASP.NET Core, standard, machine-readable.*
- **Architecture:** Single deployable service (modular monolith). *Reason: cross-entity FKs + single-team + milestone speed; bounded-context separation by code organization, not by deployment.*
- **Persistence:** Single Postgres database, single `AppDbContext`, DB-level FK constraints. *Reason: forced by cross-entity FKs.*
- **Identity strategy:** all primary keys are `Guid`. *Reason: user-specified.*
- **Version semantics:** `Version` field is metadata (mutable in place via PUT, no `(Name, Version)` uniqueness). *Reason: user-specified.*
- **CRUD only at base:** no execution, scheduling, or business behaviors at the API surface. *Reason: scope boundary; orchestration is external.*

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| Single-API (Option B) instead of 6 separate webapis | Entities have cross-entity FKs → can't isolate DBs; one team; faster milestones; can split later if needed | — Pending |
| Shared class library `BaseApi.Core` + abstract generic controllers | Single source of infrastructure; concrete entities are pure HTTP shells | — Pending |
| `BaseEntity` is abstract, no table; 5 concrete entities, 5 tables | Avoids inheritance-mapping complexity; clean per-entity schema | — Pending |
| Single Postgres DB, single `AppDbContext` | Forced by cross-entity FK requirement | — Pending |
| FK columns scalar; M2M via junction tables (no nav properties between entities) | DB-enforced integrity; minimal code coupling between entity definitions | — Pending |
| 3 DTOs per entity (Create / Update / Read) | Explicit separation of server-controlled fields; future-proof if Create/Update diverge | — Pending |
| 3-tier layering: Controller → Service → Repository | Service is the home for entity-specific logic + M2M sync; Repository generic in base | — Pending |
| Mapperly (source-gen) over AutoMapper | Zero runtime reflection, faster, AOT-safe, modern .NET 8 idiom | — Pending |
| FluentValidation over DataAnnotations | Inheritable validators; supports SemVer + per-entity composition cleanly | — Pending |
| OTel exporter target = external OTel Collector via OTLP | User-specified; vendor-neutral; Collector handles backend fan-out | — Pending |
| `Logging:LogLevel` single source of truth for console and OTel sinks | Both sinks bind to MEL pipeline; `Logging:LogLevel` filters before either sink runs | — Pending |
| `X-Correlation-Id` middleware: read or generate; attach to log scope and all responses | User-specified for log correlation; also propagates into error bodies | — Pending |
| RFC 7807 Problem Details with `correlationId` field on every error | Machine-readable, standard, ties errors to logs | — Pending |
| FK pre-validation: rely on Postgres constraint + clean error mapping (Option 1) | No upfront HTTP hop to verify FK existence | — Pending |
| No dynamic Payload-vs-Schema conformance (N2 = No) | Explicit user decision; keeps validator scope bounded | — Pending |
| `Processor.SourceHash` SHA-256, unique | User-specified algorithm + uniqueness constraint | — Pending |
| `Processor.InputSchemaId` and `OutputSchemaId` are nullable (`Guid?`) | Supports source processors (no input) and sink processors (no output); FK still enforced by Postgres when non-null. Decided 2026-05-26 during Phase 1 discuss-phase. | — Pending |
| `(Name, Version)` not unique | User-specified | — Pending |
| Drop `WorkflowScheduleEntity`; rely on `Workflow.CronExpression` nullability for gating | Redundant given per-workflow control was sufficient | — Pending |
| Migrations applied on startup by `BaseApi.Service` | Single owner of schema; no separate migration tool needed | — Pending |
| `Schema.Definition` validated as valid JSON + valid JSON Schema | User-specified | — Pending |
| `Assignment.Payload` validated as valid JSON only (not against referenced Schema) | User-specified (N2) | — Pending |

## Evolution

This document evolves at phase transitions and milestone boundaries.

**After each phase transition** (via `/gsd-transition`):
1. Requirements invalidated? → Move to Out of Scope with reason
2. Requirements validated? → Move to Validated with phase reference
3. New requirements emerged? → Add to Active
4. Decisions to log? → Add to Key Decisions
5. "What This Is" still accurate? → Update if drifted

**After each milestone** (via `/gsd-complete-milestone`):
1. Full review of all sections
2. Core Value check — still the right priority?
3. Audit Out of Scope — reasons still valid?
4. Update Context with current state

---
*Last updated: 2026-05-27 — Phase 7 (Generic HTTP Base + Composition Root) complete — see most recent footer entry below.*

*2026-05-27 — Phase 7 (Generic HTTP Base + Composition Root) complete: 18 new BaseApi.Core source files + Program.cs cutover land the abstract generic composition root the rest of the milestone hangs on. (1) `IHasId` marker exposes `Guid Id { get; }` so `BaseController.Create` can read `read.Id` without dynamic dispatch (RESEARCH Open Q2 option b). (2) `BaseController<TEntity, TCreate, TUpdate, TRead>` (abstract, NOT sealed, `[ApiController] + [ApiVersion("1.0")] + [Route("api/v{version:apiVersion}/[controller]")]`) exposes exactly 5 CRUD verbs (GET list, GET by-id, POST, PUT, DELETE) — Phase 8 concretes inherit with empty bodies. `[ProducesResponseType]` uses status-code-only / non-generic ProblemDetails variants (Rule 1 — CS0416 forbids `typeof(TRead)` on open generics; `ActionResult<TRead>` still surfaces schema). (3) `BaseService<TEntity, TCreate, TUpdate, TRead>` (abstract, NOT sealed) enforces the locked 6-step `CreateAsync` verb order: `validate → ToEntity → repo.Add → SyncJunctionsAsync → SaveChangesAsync → ToRead` (D-11). Exposes `protected BaseDbContext DbContext { get; }` so derived test services can read `ChangeTracker.Entries<TEntity>().Single().State` (RESEARCH Pitfall 3). `SyncJunctionsAsync` is `protected virtual ... => Task.CompletedTask;` (D-09/10). Validator null-guards throw `InvalidOperationException` (CONTEXT Discretion option a). (4) 3 Swagger helpers: `ConfigureSwaggerOptions` (per-`ApiVersionDescription` SwaggerDoc registration), `CorrelationIdHeaderOperationFilter` (X-Correlation-Id header parameter on every operation, CONTEXT D-18), `HideHealthEndpointsDocumentFilter` (excludes /health/* from spec). (5) Five internal sub-extensions on `IServiceCollection` (Persistence, Health, ErrorHandling, Http, plus existing Validation + Mapping from Phase 6) chained from `AddBaseApi<TDbContext>`. (6) **CONTEXT D-13 amendment encoded**: Observability split to `IHostApplicationBuilder` via `builder.AddBaseApiObservability(cfg)` (Rule 3 deviation — promoted from internal to public) because OTel MEL bridge (`builder.Logging.AddOpenTelemetry`) requires `ILoggingBuilder` which `IServiceCollection` alone does not expose. (7) `AuditInterceptor` lifetime reconciled: **Singleton** per Phase 3 D-06 / RESEARCH Pitfall 4 (overrides CONTEXT D-14's Scoped wording snippet). (8) `BaseDbContext` alias registered Scoped via `sp.GetRequiredService<TDbContext>()` (RESEARCH Pitfall 5) — no captive-lifetime risk. (9) `AddBaseApiHttp` calls `AddApiVersioning().AddMvc().AddApiExplorer()` BEFORE `AddSwaggerGen()` (RESEARCH Pitfall 2). (10) `UseBaseApi()` enforces the locked D-19 pipeline order: `UseExceptionHandler → UseMiddleware<CorrelationIdMiddleware> → UseRouting → (Dev only: UseSwagger + UseSwaggerUI) → MapHealthChecks ×3`. (11) `Program.cs` is now 7 non-trivial body lines (cap: 10) — contains all 4 positive literals (`AddBaseApi<`, `UseBaseApi(`, `MapControllers`, `AddBaseApiObservability`) and none of the 8 forbidden per-concern strings (SC#3). (12) `AppDbContext` placeholder is empty `public sealed class AppDbContext(DbContextOptions<AppDbContext> opts) : BaseDbContext(opts) { }` (RESEARCH Path B / A5). Wave 2 lands 15 new test files: Phase7TestDbContext (sibling DbContext tracking BaseApi.Tests.Validation.TestEntity, scoped to fight cross-namespace ambiguity), TestsController + RecordingTestService (abstract-base injection per Warning 7 option b — makes Phase7WebAppFactory's alias registration load-bearing), Phase7WebAppFactory (IAsyncLifetime per-class throwaway Postgres DB; Rule 3 fix-forward addressed plan gap that AppDbContext placeholder has no DbSets), ProductionWebAppFactory (`builder.UseEnvironment(Environments.Production)` — canonical SC#4 Prod-404 pattern), TestCreateDtoValidator (Blocker 2 fix — BaseService ctor null-guards `IValidator<TestCreateDto>`), and 8 fact classes (AddBaseApiFacts, UseBaseApiPipelineFacts, ProgramMinimalityFacts, BaseControllerRoutesFacts, BaseServiceOrderingFacts, NotFoundFacts, VersioningFacts, SwaggerEnvironmentFacts) covering all 9 HTTP-* REQ-IDs and SC#1-4. **98/98 facts GREEN × 3 consecutive regression replays** (76 prior Phase 1-6 + 22 new Phase 7) + 4th confirmation run during regression gate; psql `\l` BEFORE/AFTER byte-identical; `tests/.otel-out/telemetry.jsonl` absent post-suite (Phase 5 D-11 honored). Both Release and Debug builds clean with zero warnings. 9 REQ-IDs closed (HTTP-01, HTTP-02, HTTP-03, HTTP-08, HTTP-09, HTTP-13, HTTP-14, HTTP-15, HTTP-16). 6 fix-forward auto-corrections during Wave 2 (Rule 1 type-aliases for ambiguous `TestEntity`/`PostgresFixture`, NSubstitute targeting base `IValidator.ValidateAsync(IValidationContext)` overload, `VersioningFacts` broadened to accept 404 — RESEARCH A6 falsified: Asp.Versioning URL-segment returns 404 route-no-match for unsupported versions, not 400). Manual Swagger UI smoke checkpoint resolved via user-approved automated-coverage rationale: `SwaggerEnvironmentFacts` (Dev 200 / Prod 404 + per-version SwaggerDoc + CorrelationIdHeaderOperationFilter) IS the canonical SC#4 proof because v1 `BaseApi.Service.dll` has zero concrete controllers (Phase 8 adds them — live `dotnet run` empty Swagger UI in v1 is structurally expected, not a regression). New package pins: Asp.Versioning.Mvc 8.1.0, Asp.Versioning.Mvc.ApiExplorer 8.1.0, NSubstitute 5.3.0 (existing Asp.Versioning.Http 8.1.0 + Swashbuckle.AspNetCore 6.9.0 retained per RESEARCH Open Q4 CPM explicit-pin auditability). Code review: 0 critical / 5 warning / 9 info — WR-01 BaseService._logger unused (no per-step breadcrumbs in 6-step CreateAsync), WR-02 BaseServiceOrderingFacts runs real SaveChangesAsync without AuditInterceptor (latent test-only `Kind=Unspecified` → `timestamptz` risk per project PITFALLS.md), WR-03 inconsistent required-config pattern (`!` NRE vs `?? fallback`), WR-04 ProgramMinimalityFacts brittle `..×5` path walk, WR-05 Phase7WebAppFactory.DisposeAsync triggers process-wide `NpgsqlConnection.ClearAllPools()` (parallel-class pool clobber). All advisory — none block phase verification. Phase 8 is now one inheritance step away from working: subclass BaseController/BaseService with empty bodies for the 5 concrete entities, register DbSets on AppDbContext, ship migrations and Dockerfile.*

*2026-05-27 — Phase 3 (EF Core Persistence Base) complete: BaseEntity + BaseDbContext (abstract, snake_case + xmin shadow concurrency token) + AuditInterceptor (TimeProvider-based, null-safe HttpContext) + IRepository/Repository (sealed generic, 5-method surface, load-then-remove DeleteAsync); 7/7 facts GREEN against real Postgres 17; zero leaked stepsdb_test_* databases; D-11 (no Program.cs touch) and D-16 (no migrations) both honored — Phase 8 still owns first migration so snake_case + xmin apply to the very first schema.*

*2026-05-27 — Phase 4 (Cross-Cutting Middleware + Error Handling) complete: CorrelationIdMiddleware (Guid `"N"` format, ASCII-printable IsValid guard, Response.OnStarting echo, ILogger.BeginScope) + .NET 8 IExceptionHandler chain (NotFound→Validation→DbUpdate→Fallback per D-06; DbUpdateExceptionHandler concurrency-FIRST per Pitfall 7; FallbackExceptionHandler T-04-LEAK guard — stack logged via MEL, never in body) + PostgresExceptionMapper (Option A regex preserving `_id` suffix, SQLSTATE 23503→422 / 23505→409) + Program.cs composition root (AddHttpContextAccessor + AddProblemDetails customizer injecting correlationId+instance into ALL ProblemDetails including framework 400/404/500 + [ApiController] model-binding 400). 31/31 facts GREEN over real Postgres 17 (WebApplicationFactory<Program> + per-class throwaway DBs); Phase 3 D-15 byte-identical psql `\l` snapshots preserved. 14 REQ-IDs closed (OBSERV-09/10/11 + ERROR-01..11). Threat model verified: T-04-LEAK / T-04-XMIN / T-04-INJECT all PASS via behavioral assertions. Npgsql pinned at 8.0.9 (binary-compat with EFCore.PostgreSQL 8.0.10 — fix-forward from initial 9.0.0 pin). 3 advisory code-review warnings tracked for Phase 7/8 (WR-01 FallbackExceptionHandler return-value defensive edge, WR-02 EnsureCreatedAsync race risk, WR-03 FK regex underscore-in-table-name caveat). Phase 7 will refactor Program.cs wiring into AddBaseApi()/UseBaseApi() extensions without behavior change.*

*2026-05-27 — Phase 6 (Validation + Mapping Base) complete: 5 BaseApi.Core seam files — IBaseDto (3 read-only getters: Name/Version/Description), BaseDtoValidator<T> (3 shared FluentValidation rules: NotEmpty+MaxLength(200) on Name, strict-SemVer NotEmpty+regex on Version, MaxLength(2000) on Description), IEntityMapper<TEntity,TCreate,TUpdate,TRead> (3-method contract: ToEntity / void Update / ToRead), AddBaseApiValidation (FluentValidation.DependencyInjectionExtensions wrapper, ServiceLifetime.Scoped, includeInternalTypes:false, params Assembly[]), AddBaseApiMapping (closed-generic reflection scan via typeof(IEntityMapper<,,,>) filter, Singleton registration). Program.cs wired between AddProblemDetails and AddExceptionHandler chain (D-18). Mapperly RMG007/RMG012/RMG020/RMG089 promoted to solution-wide build errors via Directory.Build.props (MP-codes corrected to RMG-codes per RESEARCH A-01). Riok.Mapperly package added to BaseApi.Tests (PrivateAssets=all, ExcludeAssets=runtime per D-19). WebAppFactory unsealed (sealed→public class) so ValidationWebAppFactory can subclass it. 13 new test files + extended TestController validate endpoint verify SC#1-4 across 29 new facts; full suite 76/76 GREEN × 3 consecutive runs; byte-identical psql \l snapshots; tests/.otel-out/ clean (Phase 5 D-11 inherited). Drift-detection proven LIVE on all 3 mapper methods: temporarily adding a `Drift` property to TestEntity fires RMG012 (ToEntity + Update) and RMG020 (ToRead). 8 REQ-IDs closed (VALID-01..07 + HTTP-10). 1 plan-gap fix-forward documented: Phase 8 must replicate the 3-method [MapperIgnoreTarget]/[MapperIgnoreSource] attribute pattern across all 5 entity mappers (not just on Update as the plan originally specified). Code review: 0 critical / 2 warning / 4 info — both warnings are symmetric DI idempotency latencies (WR-01 AddBaseApiMapping Singleton re-registration on repeated assembly scans; WR-02 AddValidatorsFromAssembly non-TryAdd semantics) tracked for Phase 7 AddBaseApi composition root.*

*2026-05-27 — Phase 5 (Observability + Health Probes) complete: OpenTelemetry wired via the MEL bridge (`builder.Logging.AddOpenTelemetry` — never `WithLogging()` per Pitfall 8) + services-chain metrics+traces (`AddOpenTelemetry().WithMetrics().WithTracing()` with ASP.NET Core + HttpClient + Npgsql + Runtime instrumentation) + bare `.AddNpgsql()` (no callback — RESEARCH-corrected against CONTEXT D-05; `NpgsqlTracingOptions` has no parameter-capture knob, default already secure-by-default for T-05-PII) + dual resource builders (`service.name=sk-api`, `service.version=3.2.0` on both logger and meter/tracer providers). Three K8s-style health probes (`/health/live`, `/health/ready`, `/health/startup`) with strict tag discipline (live → only "self", never DB per Pitfall 15; ready → NpgSql + StartupHealthCheck; startup → StartupHealthCheck) and `UIResponseWriter.WriteHealthCheckUIResponse` JSON body (T-05-READY-DB-EXPOSE sanitization). `IStartupGate` (public sealed, Volatile.Read / Interlocked.Exchange one-shot latch) + `StartupCompletionService` (IHostedService — Phase 8 swap-target for migration-gated readiness, clean 1-line substitution). `otel-collector` Docker Compose service + config wired with OTLP gRPC :4317 + HTTP :4318 receivers, file exporter to bind-mounted `tests/.otel-out/`, `health_check` extension :13133, and a `filter/health_metrics` processor (OTTL `IsMatch(attributes["http.route"], "^/health/.*")`) that closes the SC#4 metrics-half gap via Collector-side filtering (worked around OTel 1.15.0 `MeterProviderBuilder.AddAspNetCoreInstrumentation` being parameterless). 16 net new fact tests across 5 classes covering ROADMAP SC#1-5 + D-16 + 4 STRIDE threats; 47/47 GREEN × 3 consecutive runs (~18s each); BEFORE/AFTER psql `\l` snapshots byte-identical (Phase 3 D-15 honored). xUnit v3 [assembly: AssemblyFixture(typeof(OtelEndOfSuiteCleanup))] shells out to `docker compose stop|delete|start otel-collector` once at end-of-suite — D-11 cleanup discipline automated despite the Collector holding an exclusive write handle on telemetry.jsonl during runtime. 14 REQ-IDs closed (OBSERV-01..08, OBSERV-12, HEALTH-01..05). 3 fix-forward commits to Plan 05-01 (compose healthcheck without wget; `user: "0:0"` for Windows bind-mount writes; Collector-side metrics filter). Code review: 0 critical / 3 warning / 6 info — all fixture-lifecycle robustness items, none in production paths.*
