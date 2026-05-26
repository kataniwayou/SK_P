# Project Research Summary

**Project:** Steps API (BaseApi.Core + BaseApi.Service)
**Domain:** .NET 8 Web API modular monolith CRUD service over PostgreSQL with shared base library, OTel observability, RFC 7807 errors
**Researched:** 2026-05-26
**Confidence:** HIGH

## Executive Summary

This project is a greenfield .NET 8 controller-based Web API built as a modular monolith: a reusable infrastructure class library (BaseApi.Core) and a single runnable service (BaseApi.Service). The library provides abstract generic CRUD infrastructure (base entity, base controller, generic repository, base service, audit interceptor, correlation middleware, OTel wiring, health probes, RFC 7807 exception handler); the service plugs in five concrete entities (Schema, Processor, Step, Assignment, Workflow). The entire stack is locked by PROJECT.md -- .NET 8 LTS, EF Core 8 + Npgsql 8, Mapperly source-gen, FluentValidation 12, OpenTelemetry 1.15.x with OTLP to an external Collector, and PostgreSQL 17, all verified against current NuGet releases as of 2026-05-26.

The recommended build order is strictly dependency-driven: solution skeleton, then persistence base (BaseEntity, DbContext, Repository), then cross-cutting concerns (correlation middleware, exception handler, health checks, OTel), then the generic HTTP base (BaseController, BaseService, base validator), then the composition root, then the five concrete entity feature slices, then migrations and Docker Compose, then the test layer. Each phase delivers a compilable increment. All five entity feature slices are parallelizable once the base is complete.

The dominant risks are infrastructure-wiring mistakes that silently degrade correctness: using the deprecated FluentValidation.AspNetCore package (removed in v12); miswiring the OTel logging provider (WithLogging() vs builder.Logging.AddOpenTelemetry()) so logs never reach the Collector; storing non-UTC DateTime in Npgsql timestamptz columns causing InvalidCastException; and adding the EFCore.NamingConventions snake_case plugin after the first migration has run. All four change what must be built: they are requirements, not afterthoughts.

---

## Key Findings

### Locked Stack -- Authoritative Version Table

All versions verified against NuGet.org as of 2026-05-26. Copy this table into requirements as the definitive pin list.

| Package | Version | Notes |
|---------|---------|-------|
| .NET SDK | **8.0.421** | Pin via global.json. Runtime 8.0.27. LTS through Nov 2026. |
| ASP.NET Core / EF Core | **8.0.27** | All Microsoft.EntityFrameworkCore.* packages. Match runtime patch monthly. |
| Npgsql.EntityFrameworkCore.PostgreSQL | **8.0.10** | Targets EF Core 8.x. Do NOT mix with EF Core 9/10. |
| PostgreSQL Docker image | **postgres:17-alpine** | Npgsql 8.x validated against PG 14-17. PG 18 is GA but not yet validated against Npgsql 8.x. |
| Riok.Mapperly | **4.3.1** | Source generator. PrivateAssets=all + ExcludeAssets=runtime in csproj. |
| FluentValidation | **12.1.1** | + FluentValidation.DependencyInjectionExtensions 12.1.1. Do NOT reference FluentValidation.AspNetCore. |
| OpenTelemetry | **1.15.3** | Core + Extensions.Hosting + Exporter.OpenTelemetryProtocol all at 1.15.3. |
| OTel Instrumentation | **1.15.0** | OpenTelemetry.Instrumentation.AspNetCore + .Http. Separate version cadence from core. |
| JsonSchema.Net | **9.2.1** | For SchemaEntity.Definition validation. System.Text.Json native, supports draft 2020-12. |
| Cronos | **0.13.0** | For WorkflowEntity.CronExpression validation. DST-aware, 5/6-field cron. |
| AspNetCore.HealthChecks.NpgSql | **9.0.0** | Xabaril. Runs fine on .NET 8.0.27 despite the version number. |
| Microsoft.AspNetCore.Mvc.Testing | **8.0.27** | Integration test host. Match runtime patch. |
| xunit.v3 | **3.2.2** | Modern .NET 8+ compatible. |
| Testcontainers.PostgreSql | **4.11.0** | Real Postgres for integration tests. |
| EFCore.NamingConventions | latest 8.x | Snake_case for all DB identifiers. MUST be wired before the first migration. |

**Anti-stack (never use):**
- FluentValidation.AspNetCore -- deprecated, removed in FV 12, blocks async validation
- AutoMapper -- runtime reflection, commercial license, explicitly locked out by PROJECT.md
- EF Core InMemory or SQLite for tests -- no FK enforcement, no SQLSTATE, invalid for this project
- Newtonsoft.Json.Schema -- commercial license
- MediatR -- went commercial 2024, adds dispatcher with no benefit to 3-tier CRUD
- Serilog as primary sink -- duplicates MEL pipeline; use ILogger<T> + builder.Logging.AddOpenTelemetry()

**Tooling:** global.json pins SDK 8.0.421; Directory.Packages.props at repo root for central package management.
### Must-Have v1 Feature Set

**Infrastructure:** AddBaseApi<TDbContext> + UseBaseApi composition root extensions; Options pattern + ValidateOnStart(); EF Core migrations on startup (single-instance, document multi-replica limitation); three distinct health probes (/health/live process-alive-only, /health/ready DB reachable, /health/startup migrations complete); AppDbContext scoped with AuditInterceptor using DateTime.UtcNow.

**HTTP/CRUD:** BaseController<TEntity,TCreate,TUpdate,TRead> with 5 virtual verbs (GET list, GET /{id:guid}, POST->201+Location, PUT->200, DELETE->204); Repository<T> with no SaveChangesAsync inside ops (service owns commit); per-entity concrete service class; IEntityMapper<TEntity,TCreate,TUpdate,TRead> interface implemented by each Mapperly partial -- the key abstraction enabling the generic base controller to delegate mapping without knowing concrete types; id:guid route constraint; LowercaseUrls=true; hard row cap on ListAsync.

**Validation:** FluentValidation via AddValidatorsFromAssembly only (never AddFluentValidation or auto-validation); BaseEntityValidator<T> with Name/Version/Description rules; per-entity validators adding SHA-256 hex regex + lowercase normalization (Processor), JSON syntactic validity (Assignment.Payload + Schema.Definition), JSON Schema 2020-12 structural validity (Schema.Definition), Cronos cron validity when non-null (Workflow.CronExpression); InvalidModelStateResponseFactory aligned with RFC 7807 + correlationId shape.

**Observability:** OTel wired via builder.Logging.AddOpenTelemetry(o => { o.IncludeFormattedMessage=true; o.IncludeScopes=true; o.AddOtlpExporter(); }) -- NOT AddOpenTelemetry().WithLogging(); ResourceBuilder.AddService from cfg[Service:Name]/cfg[Service:Version]; HTTP server metrics + traces via AddAspNetCoreInstrumentation() with /health* path filter; X-Correlation-Id middleware.

**Error Handling:** services.AddProblemDetails() + services.AddExceptionHandler<GlobalExceptionHandler>() -- BOTH required; SQLSTATE mapping 23503->422, 23505->409, 23502->400, 23514->400; unhandled->500 generic message; every error response includes correlationId; no stack traces in response bodies; middleware order: UseExceptionHandler first, UseCorrelationId second.

**Mapping:** Mapperly [Mapper] public static partial class per entity; CreateDto/UpdateDto must not contain server-controlled fields (Id, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy); Mapperly warnings MP0001/MP0011/MP0020/MP0021 promoted to errors in CI.

**Testing:** WebApplicationFactory<Program> with ConfigureAppConfiguration override (not ConfigureTestServices); shared PostgresFixture collection fixture (one container per collection, not per test class); Respawn between tests; golden-path + error-mapping integration tests per entity.

**Deferred to v1.x / v2+:** Pagination, PATCH, ETags, soft delete, auth, custom Meter metrics, bulk ops, MediatR/CQRS, NuGet packaging of BaseApi.Core.
### Architecture Approach

The service is a modular monolith in two projects. BaseApi.Core is organized by layer (Entities, Persistence, Services, Controllers, Validation, Middleware, ErrorHandling, Health, Telemetry, DependencyInjection). BaseApi.Service is organized by entity feature folder (Features/Schemas/, etc.) with a shared Persistence/Configurations/ folder. Core never references Service; Service concrete types flow into Core generic infrastructure via type parameters.

**Load-bearing seams -- must be correct from the start:**

1. **IEntityMapper<TEntity,TCreate,TUpdate,TRead> interface** -- implemented by each Mapperly static partial class. Key abstraction letting BaseController and BaseService call mapper methods without knowing concrete entity types. Without it every concrete controller must override all mapping methods, defeating the base class.
2. **IExceptionHandler + services.AddProblemDetails() pairing** -- AddExceptionHandler<T>() alone is insufficient; AddProblemDetails() must also be called or the framework falls back to HTML/empty bodies on some error paths.
3. **builder.Logging.AddOpenTelemetry(...) not .WithLogging(...)** -- the MEL bridge for ILogger. WithLogging() sets up OTels own logging API, not the MEL bridge. Wrong choice: logs appear in console but never reach the Collector.
4. **UseSnakeCaseNamingConvention() before the first migration** -- must be in BaseDbContext and the first migration generated in the same commit. Adding it after any migration requires destructive ALTER scripts.
5. **UseExceptionHandler before UseCorrelationId in pipeline** -- both share HttpContext.Items so the handler reads the correlation id set by the correlation middleware.

| Component | Lives in | Responsibility |
|-----------|----------|----------------|
| BaseEntity (abstract, no table) | BaseApi.Core/Entities/ | Id, Name, Version, audit fields, Description? |
| Concrete entities + junction classes | BaseApi.Core/Entities/ | Entities in Core so generic type constraints on Repository/Controller resolve |
| BaseDbContext (abstract) | BaseApi.Core/Persistence/ | Conventions, interceptor wiring; no DbSets |
| AppDbContext | BaseApi.Service/Persistence/ | All DbSets + junctions; single-line ApplyConfigurationsFromAssembly |
| AuditInterceptor : ISaveChangesInterceptor | BaseApi.Core/Persistence/Interceptors/ | Stamps audit fields on Added/Modified; singleton |
| IRepository<T> + Repository<T> | BaseApi.Core/Persistence/Repositories/ | Generic CRUD; caller owns SaveChanges |
| IService<T,C,U,R> + BaseService<T,C,U,R> | BaseApi.Core/Services/ | Validate->map->repo->SyncJunctionsAsync (virtual)->SaveChanges |
| BaseController<T,C,U,R> (abstract) | BaseApi.Core/Controllers/ | 5 virtual CRUD verbs; route from [controller] token |
| GlobalExceptionHandler + PostgresErrorMapper | BaseApi.Core/ErrorHandling/ | SQLSTATE->HTTP; domain exceptions->ProblemDetails |
| CorrelationIdMiddleware | BaseApi.Core/Middleware/ | Read/generate id; log scope; Activity tag; response header |
| AddBaseApi<TDbContext> + UseBaseApi | BaseApi.Core/DependencyInjection/ | Single composition root for all cross-cutting concerns |
| Per-entity feature folder | BaseApi.Service/Features/<Entity>/ | Controller, Service, Validator, Mapper, DTOs |
| IEntityTypeConfiguration<T> per entity | BaseApi.Service/Persistence/Configurations/ | jsonb types, unique indexes, FK/cascade rules |

### Critical Pitfalls

**Pitfalls that change what must be built (requirements-altering):**

1. **FluentValidation.AspNetCore deprecated and removed in v12.** Wire via AddValidatorsFromAssembly + ValidateAndThrowAsync explicitly in BaseService. The auto-validation pipeline no longer exists.
2. **OTel logging provider miswiring.** Use builder.Logging.AddOpenTelemetry(o => { o.IncludeFormattedMessage=true; o.IncludeScopes=true; o.AddOtlpExporter(); }). NOT AddOpenTelemetry().WithLogging(). Wrong choice: logs appear in console but never reach the Collector.
3. **Non-UTC DateTime rejected by Npgsql timestamptz.** AuditInterceptor must always use DateTime.UtcNow. Do not flip the legacy switch -- it will be removed.
4. **EFCore.NamingConventions must be wired before the first migration.** Adding UseSnakeCaseNamingConvention() after migrations exist mangles __EFMigrationsHistory column names. Convention must be in BaseDbContext and initial migration generated in the same commit.
5. **Explicit junction entities required -- not skip-navigation.** Use StepNextStep, WorkflowEntryStep, WorkflowAssignment classes with composite PKs and explicit DeleteBehavior. StepNextStep needs DeleteBehavior.Restrict on the second FK to avoid multiple cascade path errors.
6. **Liveness probe must not check the database.** /health/live returns 200 always; /health/ready checks DB; /health/startup flips once migrations have run.
7. **services.AddProblemDetails() is required alongside AddExceptionHandler<T>().** Without it IExceptionHandler falls back to HTML or empty bodies on some error paths.
8. **SHA-256 hex: accept both cases, normalize to lowercase before persist.** Regex: ^[0-9A-Fa-f]{64}$. Normalize via ToLowerInvariant() before save.
9. **WebApplicationFactory config override must use ConfigureAppConfiguration.** Add in-memory config source so AddDbContext itself uses the Testcontainers connection string.
10. **Hard-delete cascade decisions must be explicit per FK.** WorkflowEntryStep.StepId and WorkflowAssignment.AssignmentId must use DeleteBehavior.Restrict.

---

## Implications for Roadmap

### Suggested Phase Decomposition

8 phases based on the forced code-dependency graph from ARCHITECTURE.md.

### Phase 0: Repository Scaffold
**Rationale:** Every subsequent phase depends on the solution compiling. Establish project layout, tooling pins, and dev-environment hygiene before writing any domain code.
**Delivers:** SK_P.sln, project files for BaseApi.Core / BaseApi.Service / tests, global.json (SDK 8.0.421), Directory.Packages.props (all version pins), .editorconfig (nullable enable), User Secrets setup, no secrets in appsettings.json.
**Avoids:** Pitfall 30 (JSON comments cause startup failures), Pitfall 39 (secrets in git), SDK float to .NET 9/10.
**Research flag:** Standard patterns -- skip phase research.

### Phase 1: Postgres + Docker Compose
**Rationale:** Establish the Postgres container with correct healthcheck before any EF work begins.
**Delivers:** docker-compose.yml with postgres:17-alpine, named pgdata volume, pg_isready healthcheck, depends_on: service_healthy, host port 5433:5432, POSTGRES_INITDB_ARGS with UTF8 + C.UTF-8 locale, connection string shape with password via env var.
**Avoids:** Pitfalls 24 (depends_on without condition), 25 (port 5432 conflict), 26 (volume loss on down -v), 27 (locale mismatch).
**Research flag:** Standard patterns -- skip phase research.

### Phase 2: EF Core Persistence Base
**Rationale:** All entity and migration work depends on this. Snake_case convention and audit interceptor must exist before the first migration. Highest-density pitfall phase.
**Delivers:** BaseEntity (abstract, no table), BaseDbContext (abstract, no DbSets) with UseSnakeCaseNamingConvention() wired, AuditInterceptor using DateTime.UtcNow, IRepository<T> + Repository<T> (no SaveChangesAsync inside ops), DbContext registered as scoped.
**Critical constraint:** UseSnakeCaseNamingConvention() must be present before dotnet ef migrations add is ever run.
**Avoids:** Pitfalls 1 (UTC DateTime), 2 (DbContext lifetime), 4 (NamingConventions before first migration), 5 (explicit junctions vs skip-nav).
**Research flag:** Standard patterns -- skip phase research.

### Phase 3: Cross-Cutting Middleware and Error Handling
**Rationale:** Error handling and correlation must be present before any HTTP endpoint is testable. The middleware order is load-bearing.
**Delivers:** CorrelationIdMiddleware; GlobalExceptionHandler : IExceptionHandler; PostgresErrorMapper (23503->422, 23505->409, 23502->400, 23514->400); NotFoundException + ConflictException; services.AddProblemDetails + services.AddExceptionHandler paired; InvalidModelStateResponseFactory aligned with RFC 7807 + correlationId shape. Middleware order: UseExceptionHandler first, UseCorrelationId second.
**Avoids:** Pitfalls 11 (correlation scope lost), 12 (correlation too late), 13 (stack trace leak), 14 (SQLSTATE mapping), 38 (rethrow loses stack).
**Research flag:** Standard patterns -- skip phase research.

### Phase 4: Observability (OTel + Health Probes)
**Rationale:** Wiring must be correct before integration tests run. The MEL-vs-WithLogging pitfall is the most commonly misapplied wiring in this stack.
**Delivers:** OTel SDK wired via builder.Logging.AddOpenTelemetry (NOT WithLogging); AddOpenTelemetry().WithMetrics with /health* path filter; ResourceBuilder.AddService from config; three health probe endpoints (live=no-DB, ready=DB check, startup=migration-complete flag); OTLP self-diagnostics enabled; explicit batch options.
**Critical constraint:** Startup probe must not report healthy until migrations complete; flip the flag from the migration runner.
**Avoids:** Pitfalls 8 (OTel logger wiring), 9 (LogLevel filter), 10 (health probes spam metrics), 15 (liveness checks DB), 31 (OTLP silent drops), 32 (metric cardinality).
**Research flag:** Include a smoke-test assertion that a real ILogger<T>.LogInformation call appears in the OTLP export.

### Phase 5: Validation and Mapping Base
**Rationale:** Base validator and Mapperly infrastructure must exist before any entity validator or mapper. FluentValidation wiring and Mapperly project setup made once here.
**Delivers:** BaseEntityValidator<T> with Name/Version/Description rules (SemVer regex -- lock variant here); FluentValidation via AddValidatorsFromAssembly only; Mapperly project setup verified (MP warnings as errors in CI); IEntityMapper<TEntity,TCreate,TUpdate,TRead> interface defined; DTO convention enforced; Description null round-trip rule; [ApiController] auto-400 aligned with custom error shape.
**Open decisions to lock here:** SemVer regex variant (strict triple vs full SemVer 2.0); JSON Schema draft version (recommend 2020-12).
**Avoids:** Pitfalls 7, 16, 17, 18, 33, 34, 36.
**Research flag:** No additional research needed -- open decisions above are the only items to lock.

### Phase 6: Generic HTTP Base and Composition Root
**Rationale:** Abstract base controller and service are the last base components before concrete entities. Composition root brings everything from phases 2-5 together.
**Delivers:** IService<T,C,U,R>; BaseService<T,C,U,R> (abstract map hooks + virtual SyncJunctionsAsync); BaseController<T,C,U,R> (5 virtual verbs, id:guid, lowercase routing); AddBaseApi<TDbContext> + UseBaseApi; Swagger/OpenAPI; open-generic IRepository<> DI registration.
**Avoids:** Pitfall 2 (DbContext lifetime in service), Pitfall 23 (connection pool via transaction shape), anti-patterns (validation in controller, SaveChanges in repository).
**Research flag:** Standard patterns -- skip phase research.

### Phase 7: Concrete Entity Build-Out (5 entities -- parallelizable after Phase 6)
**FK dependency order for migrations:** Schema (root) -> Processor (references Schema twice) -> Step (references Processor + self-ref M2M) -> Assignment (references Step + Schema) -> Workflow (references Step + Assignment via junctions).
**Delivers per entity:** CreateDto, UpdateDto, ReadDto; [Mapper] public static partial class implementing IEntityMapper; entity-specific FluentValidation validator inheriting BaseEntityValidator<T>; concrete Service (overrides SyncJunctionsAsync for Step and Workflow); concrete Controller; IEntityTypeConfiguration<T>. AppDbContext with all DbSets. dotnet ef migrations add InitialCreate after all entities are in place.
**Entity-specific rules:** Schema.Definition: JSON syntactic + JSON Schema 2020-12 validity; Processor.SourceHash: accept mixed case, normalize to lowercase, unique index; Step.NextStepIds: self-ref junction with DeleteBehavior.Restrict on second FK; Assignment.Payload: JSON syntactic only (PROJECT.md N2); Workflow.CronExpression: nullable, Cronos 5-field when non-null.
**Avoids:** Pitfalls 5, 19, 20, 21, 22, 35.
**Research flag:** Standard patterns -- skip phase research for all 5 entities.

### Phase 8: Migrations, Docker Runtime, and Test Stack
**Rationale:** Once all entities are in place, generate the single initial migration, wire the startup migration runner, build the Docker image, and establish the integration test harness.
**Delivers:** InitialCreate migration; Program.cs migration runner with migrations.start/complete log lines + StopApplication() on failure; startup probe flag flipped after migrations; Dockerfile (multistage, aspnet:8.0, non-root user); docker-compose.yml with service_healthy gate; WebApplicationFactory<Program> with ConfigureAppConfiguration override; PostgresFixture collection fixture; Respawn between tests; golden-path + error-mapping integration tests per entity.
**Avoids:** Pitfalls 3 (migration race), 24 (compose healthcheck), 28 (container per test class), 29 (WebAppFactory config override too late).
**Research flag:** Standard patterns -- skip phase research.

---

### Phase Ordering Rationale

- Phases 0-1 are pre-code setup; without them nothing compiles or runs.
- Phase 2 (EF base) must precede Phase 7 (entities): BaseEntity is the type constraint on Repository and Controller.
- Phase 3 (error handling) must precede any testable HTTP endpoint.
- Phase 4 (OTel) is cheapest to wire correctly once, before log assertions.
- Phase 5 (validation/mapping) must precede Phase 6: BaseService injects IValidator<T> and the Mapperly interface.
- Phase 6 (generic base) must precede Phase 7: concrete controllers and services derive from it.
- Phase 7 entities must precede Phase 8 (migrations): EF needs the complete model to emit DDL.
- Phase 8 test stack follows everything: integration tests require the full running application.

### Open Decisions That Must Be Locked Before Phase 5

| Decision | Options | Recommendation |
|----------|---------|----------------|
| SemVer regex for BaseEntity.Version | Option A: strict triple (no prerelease); Option B: full SemVer 2.0 regex | Pick A and document as SemVer 2.0 core version only unless consumers have pre-release versions today |
| JSON Schema draft for Schema.Definition | Draft 2019-09 or 2020-12 | Lock to 2020-12 (current spec); reject schemas whose dollar-schema does not match; document |
| Cron expression format for Workflow.CronExpression | 5-field POSIX, 6-field with seconds, Quartz-style question-mark | Cronos 5-field standard; document no seconds field, no Quartz extensions; surface in OpenAPI description |
| Assignment.Payload max size | Unbounded vs explicit cap | Add Kestrel MaxRequestBodySize + FluentValidation MaxLength on Payload (e.g., 1 MB) to prevent DoS |

### Research Flags

**Needs phase research during planning:** None -- all phases use well-documented patterns. Research is complete.
**Skip per-phase research:** All 8 phases. Proceed directly to requirements definition.

---

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| Stack | HIGH | All versions verified against NuGet.org as of 2026-05-26. EF Core + runtime patch alignment confirmed. Npgsql PG compatibility documented in release notes. |
| Features | HIGH | Cross-checked against PROJECT.md locked decisions. Feature table mirrors Active requirements with ecosystem best-practice additions. |
| Architecture | HIGH | Patterns verified against official EF Core / ASP.NET Core 8 docs. Build order derived from code dependency graph. |
| Pitfalls | HIGH (most), MEDIUM (2) | Pitfalls 1, 4, 7, 8, 14, 15 verified against official docs and GitHub issues. Pitfalls 3 (migration race behavior) and 21 (cron library edge cases) are MEDIUM. |

**Overall confidence:** HIGH

### Gaps to Address

- **Cron library match with external scheduler:** The external Orchestrator/Scheduler may use Quartz-style syntax or 6-field cron. Validate the external scheduler cron format before Phase 7 (WorkflowEntity). If it differs from Cronos 5-field, the validator will reject valid inputs.
- **Schema.Definition remote ref policy:** JsonSchema.Net may attempt to dereference remote  URIs when validating a JSON Schema. This is an SSRF risk. Verify JsonSchema.Net 9.x behavior and configure to disable network access in Phase 5.
- **Testcontainers + Windows Docker Desktop:** The project Windows 11 dev environment requires Docker Desktop with WSL2 backend for Testcontainers. Verify before Phase 8; no blocker expected, but confirm.

---

## Sources

### Primary (HIGH confidence)

- NuGet.org -- all package versions verified 2026-05-26 (see STACK.md for individual package links)
- https://github.com/dotnet/core/blob/main/release-notes/8.0/8.0.26/8.0.126.md -- .NET 8 SDK 8.0.421 / Runtime 8.0.27
- https://docs.fluentvalidation.net/en/latest/upgrading-to-12.html -- confirms FluentValidation.AspNetCore deprecation
- https://www.npgsql.org/doc/types/datetime.html -- confirms UTC enforcement since Npgsql 6.0
- https://github.com/efcore/EFCore.NamingConventions -- issues confirming MigrationHistory schema mangling
- https://opentelemetry.io/docs/languages/dotnet/logs/getting-started-aspnetcore/ -- confirms builder.Logging.AddOpenTelemetry as the MEL path
- https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling?view=aspnetcore-8.0 -- IExceptionHandler + AddProblemDetails pairing
- https://learn.microsoft.com/en-us/ef/core/logging-events-diagnostics/interceptors -- EF Core interceptors
- https://learn.microsoft.com/en-us/ef/core/modeling/relationships/many-to-many -- M2M explicit join entities
- https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying -- migration startup pattern
- https://mapperly.riok.app/docs/getting-started/installation/ -- Mapperly project setup
- https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks -- health probe patterns

### Secondary (MEDIUM confidence)

- https://www.thereformedprogrammer.net/how-to-safely-apply-an-ef-core-migrate-on-asp-net-core-startup/ -- single-replica migration pattern
- https://github.com/dotnet/efcore/issues/34439 -- EF Core migration advisory lock behavior
- https://github.com/open-telemetry/opentelemetry-dotnet/discussions/4653 -- confirms the two OTel logging API confusion
- https://testcontainers.com/guides/testing-an-aspnet-core-web-app/ -- Testcontainers + Respawn guide
- https://medium.com/@lateapexearlyspeed/performance-comparison-of-json-schema-implementations-for-net-ead3d092a473 -- JsonSchema.Net vs NJsonSchema
- https://dotnet.libhunt.com/compare-cronos-vs-ncrontab -- Cronos vs NCrontab

### Project-Authoritative

- .planning/PROJECT.md -- locked decisions, Out of Scope, entity model (authoritative for this project; all research aligns against it)

---
*Research completed: 2026-05-26*
*Ready for roadmap: yes*