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

### Active

Grouped for readability; final REQ-IDs assigned in REQUIREMENTS.md.

**Runtime & deployment**
- [ ] PostgreSQL (official Docker image) is the only data store
- [ ] `docker-compose.yml` orchestrates Postgres + the service for local dev

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
- [ ] `Service:Name` (`steps-api`) and `Service:Version` (`3.2.0`) populate the OTel `service.name` / `service.version` resource attributes
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
*Last updated: 2026-05-26 — Phase 1 (Repository Scaffold) complete: solution structure + SDK pin + CPM + appsettings validated.*
