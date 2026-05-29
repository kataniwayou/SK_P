# Steps API

## Current State

**Shipped:** v3.2.0 (Steps API MVP) — 2026-05-28 · [milestone archive](milestones/v3.2.0-ROADMAP.md)

v1 delivered a reusable `BaseApi.Core` library plus a runnable `BaseApi.Service` exposing CRUD over 5 entities (Schema, Processor, Step, Assignment, Workflow) + 3 junctions, against Postgres 17 in Docker Compose. OpenTelemetry logs ship to Elasticsearch; metrics to Prometheus; no traces (per Phase 11 D-03). RFC 7807 Problem Details on every error path with `X-Correlation-Id` end-to-end. Three K8s-style health probes (live/ready/startup) with migration-gated readiness. 142/142 integration facts GREEN × 3 consecutive runs against real Postgres + ES + Prom. 5,924 LOC src + 6,397 LOC tests across 190 C# files.

## Current Milestone: v3.3.0 Orchestration L3 → L1 → L2 Build Pipeline

**Progress:** Phase 16 complete (5/5, 100% — milestone v3.3.0 phase work done) — idempotency + concurrency + L1-cleanup + end-to-end happy-path coverage shipped 2026-05-29 plus the v3.3.0 phase-close gate run GREEN (3×235 deterministic, `psql \l` + `redis-cli --scan` SHA-256 BEFORE=AFTER held). New Orchestration fact classes `HappyPathE2EFacts` (TEST-REDIS-06), `GateNoWriteFacts` (TEST-REDIS-07, gate 422 + SCAN-zero no-write), `IdempotencyFacts` (TEST-REDIS-08, last-write-wins re-Start + concurrent-Start), `StopScanFacts` (TEST-REDIS-09, inverted post-Stop key state); `scripts/phase-16-close.{ps1,sh}` clone the audited Phase-12 dual-SHA + 3-GREEN gate. Ready for `/gsd-complete-milestone`.

Phase 15 complete (2026-05-29) — L2 Redis projection write + Redis-based Stop shipped (227/227 facts GREEN × multiple runs against the live compose stack). `RedisProjectionWriter.UpsertAsync` projects each workflow's L1 snapshot into the 3 flat Redis keyspaces in one `CreateBatch()` pipeline (camelCase `[JsonPropertyName]`-pinned records, processor-only TTL via `ProcessorKeyTtlDays`, `TimeProvider` liveness, partial-failure = one MEL warning + rethrow); `RedisL2Cleanup` is a tolerant cycle-safe GET-and-follow BFS that deletes root + per-step keys (never processor keys, no key enumeration). `OrchestrationService` is now the live per-workflow Start loop (pre-clean → L1 build → locked validators → L2 write) and the Redis-EXISTS Stop gate (collect ALL missing → 422 with the full set, else per-workflow cleanup → 204). Redis-fault 500s carry the offending op name via `exception.Data["redisOp"]`. Validated in Phase 15: L2-PROJECT-01..07, ORCH-START-01..08, ORCH-STOP-01..07, OBSERV-REDIS-01..03 (OBSERV-REDIS-04 Redis metrics explicitly deferred). Next: Phase 16 (idempotency + concurrency + L1 cleanup + 3-green closeout).

Earlier phases this milestone: L1 validation gates (Phase 14, L1-VALIDATE-01..10) — `CycleDetector` iterative DFS, `SchemaEdgeValidator`, SSRF-locked `PayloadConfigSchemaValidator`, all in locked order with a single `OrchestrationValidationException` → 422.

**Goal:** On `POST /api/v1/orchestration/start`, build a transient in-memory (L1) representation of each requested workflow (validating existence, cycles, schema-edge compatibility, and Payload↔ConfigSchema conformance) and project it into a Redis (L2) materialized view that external consumers read. Stop is an L2-existence check only in v3.3.0 (no eviction — full Stop-side semantics deferred to a later milestone).

**Target features:**
- **L3 fetch + L1 build (transient)** — flat `Dictionary<Guid, EntityDto>` for Workflows, Assignments, Steps, Processors, Schemas; populated inside `OrchestrationService.StartAsync`; discarded at end of request.
- **Workflow-graph traversal with cycle detection** — DFS from each `Workflow.EntryStepIds[*]` via `StepEntity.NextStepIds`; per-traversal `visited` temp list rejects cycles (422), missing next-step ids (422), accepts `null` as terminal.
- **Schema-edge compatibility gate** — for every `(parent step → child step)` edge in the traversal, `parent.Processor.OutputSchemaId` must equal `child.Processor.InputSchemaId` (strict Schema.Id equality in v3.3.0; structural/canonical compatibility is a future-milestone candidate). If either side is `null` (source/sink/unconfigured processor per Phase 10) the edge passes. Mismatch returns 422 with the offending edge's `(parentStepId, childStepId)` pair.
- **Payload↔ConfigSchema validation gate (closes deferred VALID-21)** — for every Assignment, resolve `Step.ProcessorId → Processor.ConfigSchemaId → Schema.Definition`, validate `Assignment.Payload` against that JSON Schema (draft 2020-12, SSRF-disabled per Phase 8). Failure returns 422 with offending assignment id. Orchestration-start only; Assignment-PUT remains "valid JSON only" (v3.2.0 behavior preserved).
- **L2 (Redis) materialized projection** — three key spaces:
  - `{workflowId}` → `{ entryStepIds[], cron, jobId(Guid), liveness{timestamp,interval,status}, correlationId }` — `correlationId` is the `X-Correlation-Id` value from the originating Start POST request; lets external consumers trace each projection back to its build request.
  - `{workflowId:stepId}` → `{ entryCondition, processorId, payload, nextStepIds[] }` — `nextStepIds[]` is a list because `StepNextSteps` is many-to-many; a step can branch to multiple children. Empty/null list = terminal step.
  - `{processorId}` → `{ inputDefinition, outputDefinition, liveness{timestamp,interval,status} }`
- **Start is idempotent (PUT-like)** — second Start for the same WorkflowIds re-runs the full pipeline and replaces L2 keys; returns 204. Concurrent Starts: last-write-wins, no Redis lock.
- **Stop is a Redis-EXISTS gate + root/step cleanup (revised in Phase 15)** — batch `KeyExistsAsync` over every requested `{workflowId}` root key, collecting ALL missing ids (not fail-fast); any missing → 422 listing the full missing set with NO deletion. When all roots exist, run the per-workflow tolerant cleanup (delete root + reachable per-step keys; processor keys are NEVER deleted). Returns 204. Non-idempotent by design: a repeated Stop of an already-cleaned workflow re-fails the gate (422). **(Scope evolved across two revisions: 2026-05-28 /gsd-new-milestone reduced the original mirror-of-Start eviction to existence-check-only; Phase 15 CONTEXT amendments D-04/D-06 then re-added root+step deletion — but still never processor keys — and made the gate collect-all-missing. REQUIREMENTS.md/ROADMAP.md SC3/SC5 reconciled in 15-05.)**
- **L1 cleanup contract** — explicit teardown of the in-memory dictionary + temp traversal lists at the end of `StartAsync` (success or failure path).

**Key context / locked constraints:**
- Reuse existing `StepEntity.EntryCondition` enum (PreviousProcessing/Completed/Failed/Cancelled/Always/Never) — no new entity columns.
- `JobId` (Guid) and `Liveness` (`{ DateTime timestamp, int interval, string status }`) are L2-DTO field shapes only — no Hangfire/Quartz integration this milestone; write semantics deferred.
- No new SQL-side entities or columns — L3 schema is the v3.2.0 `InitialCreate` unchanged.
- Validation order is mandatory: existence → cycles → schema-edge compatibility → Payload↔ConfigSchema → L1 build → L2 write → cleanup.
- Schema-edge compatibility in v3.3.0 is **strict Schema.Id equality** (parent.Output == child.Input). Null on either side passes (preserves Phase 10 source/sink/unconfigured processor semantics).
- Redis is a new infrastructure dependency (added to `compose.yaml` next to Postgres/Elasticsearch/Prometheus/OTel Collector).
- L2 DTO field names: `inputDefinition` / `outputDefinition` (NOT `definitionIn` / `definitionOut` or `definitionInput` / `definitionOutput`).

**Deferred (not in this milestone):**
- **Full Stop-side L2 eviction** — v3.3.0 Stop is existence-check only. A future milestone will add DELETE semantics, processor-key reference-counting / orphan cleanup, and (if needed) Stop-side Redis transactions.
- Scheduler integration (who writes `JobId` / emits `Liveness`) — TBD, possibly external Scheduler service.
- OTel Redis instrumentation — Phase 11 D-03 stripped `.WithTracing()`; Redis trace spans deferred until a traces backend exists in a future milestone.
- `generationId` on the L2 root DTO — additive forward-compat candidate; revisit when Scheduler integration introduces multi-writer races.
- v2 hardening items: TEST-03 (Testcontainers), TEST-04 (Respawn), INFRA-09 (advisory-lock startup migration), INFRA-10 (least-privilege Postgres roles).
- v2 querying items: HTTP-17/18/19 (pagination, filtering, sorting).
- Authentication boundary — still out of scope.

## What This Is

A .NET 8 Web API platform that exposes CRUD over a workflow-engine data model — Schema, Processor, Step, Assignment, Workflow — built as a modular monolith on top of a reusable base library (`BaseApi.Core`) that future entities can plug into without rewriting infrastructure. The service ships logs to Elasticsearch and HTTP/runtime metrics to Prometheus via an OpenTelemetry Collector, runs against PostgreSQL via Docker Compose, and exposes RFC 7807 Problem Details on every error. Consumers (external Orchestrator + Scheduler) read Workflow entities to drive scheduled data-processing pipelines.

## Core Value

**A solid, observable, validated CRUD foundation that future workflow-platform features build on without rework.** If everything else fails, the base must reliably persist entities, validate inputs, emit telemetry, surface clean errors, and answer health probes. **Validated at v3.2.0 ship — base components were re-used 5× without rewrite for the 5 v1 entities; Phase 10 demonstrated that field-shape revisions land as bisect-friendly 5-commit sequences without infrastructure churn.**

## Requirements

### Validated

**Phase 1 (Repository Scaffold) — 2026-05-26**
- ✓ Service runs on .NET 8 Web API (scaffold boots; pinned 8.0.421) — v3.2.0
- ✓ Service configuration in `appsettings.json` with environment overrides — v3.2.0

**Phase 2 (Postgres + Docker Compose) — 2026-05-26**
- ✓ PostgreSQL (`postgres:17-alpine`) is the only data store — v3.2.0
- ✓ `compose.yaml` orchestrates Postgres + service for local dev — v3.2.0

**Phase 3 (EF Core Persistence Base) — 2026-05-27**
- ✓ `BaseEntity` (abstract, no table) — 8 shared fields — v3.2.0
- ✓ `AuditInterceptor` (TimeProvider-based, null-safe HttpContext) — v3.2.0
- ✓ `BaseDbContext` (abstract) with snake_case + xmin shadow concurrency token — v3.2.0
- ✓ Generic `IRepository<TEntity>` + sealed `Repository<TEntity>` — exactly 5 methods (no `IQueryable<>` surface) — v3.2.0
- ✓ `DbContext` Scoped DI lifetime verified end-to-end — v3.2.0

**Phase 4 (Cross-Cutting Middleware + Error Handling) — 2026-05-27**
- ✓ `CorrelationIdMiddleware` (Guid `"N"` format, ASCII-printable guard, log scope, response echo) — v3.2.0
- ✓ 4-handler `IExceptionHandler` chain (NotFound → Validation → DbUpdate → Fallback) — v3.2.0
- ✓ `PostgresExceptionMapper` (Option A regex, SQLSTATE 23503→422 / 23505→409 with field name) — v3.2.0
- ✓ RFC 7807 Problem Details with `correlationId` + `instance` on every error — v3.2.0

**Phase 5 (Observability + Health Probes) — 2026-05-27**
- ✓ OpenTelemetry wired via MEL bridge (`builder.Logging.AddOpenTelemetry`) — v3.2.0
- ✓ Metrics + Npgsql instrumentation via services chain — v3.2.0
- ✓ Three K8s-style health probes with strict tag discipline (live never touches DB) — v3.2.0
- ✓ `IStartupGate` + `StartupCompletionService` (Phase 8 swap-target for migration-gated readiness) — v3.2.0

**Phase 6 (Validation + Mapping Base) — 2026-05-27**
- ✓ `BaseDtoValidator<T>` with shared FluentValidation rules (Name/Version/Description) — v3.2.0
- ✓ `IEntityMapper<TEntity,TCreate,TUpdate,TRead>` 3-method contract — v3.2.0
- ✓ Mapperly RMG007/RMG012/RMG020/RMG089 promoted to solution-wide build errors — v3.2.0
- ✓ FluentValidation `AddBaseApiValidation` + closed-generic mapper scan `AddBaseApiMapping` — v3.2.0

**Phase 7 (Generic HTTP Base + Composition Root) — 2026-05-27**
- ✓ Abstract `BaseController<TEntity, TCreate, TUpdate, TRead>` with 5 CRUD verbs — v3.2.0
- ✓ Abstract `BaseService<...>` enforcing locked 6-step `CreateAsync` verb order — v3.2.0
- ✓ `AddBaseApi<TDbContext>` + `AddBaseApiObservability` + `UseBaseApi()` composition root — v3.2.0
- ✓ `Program.cs` capped at 7 non-trivial body lines (cap: 10) — v3.2.0
- ✓ API versioning (`Asp.Versioning.Http` 8.1.0) + Swagger (Dev only) — v3.2.0

**Phase 8 (Entity Build-Out + Migrations + Docker Runtime + Tests) — 2026-05-28**
- ✓ 5 entity feature folders (Schema, Processor, Step, Assignment, Workflow) — uniform 6-file layout — v3.2.0
- ✓ 3 junction tables (StepNextSteps, WorkflowEntrySteps, WorkflowAssignments) — explicit join entities — v3.2.0
- ✓ Multistage `Dockerfile` + `InitialCreate` migration with 11 explicit FK constraint names — v3.2.0
- ✓ `StartupCompletionService` swap to `MigrateAsync` at startup (PERSIST-10) — v3.2.0
- ✓ JSON Schema validation (draft 2020-12, SSRF-disabled) on Schema.Definition — v3.2.0
- ✓ Cronos 5-field CronExpression validation on Workflow.CronExpression — v3.2.0

**Phase 9 (Processor.GetBySourceHash + Orchestration Start/Stop) — 2026-05-28**
- ✓ `ProcessorsController.GetBySourceHash` + `ProcessorService.GetBySourceHashAsync` — v3.2.0
- ✓ `Features/Orchestration/` folder with `OrchestrationController` (Start/Stop) — v3.2.0
- ✓ `WorkflowIdsValidator` (primitive-collection validator auto-discovered) — v3.2.0

**Phase 10 (Remove SchemaId on AssignmentEntity; add ConfigSchemaId on ProcessorEntity) — 2026-05-28**
- ✓ `AssignmentEntity` surface collapsed to `(StepId, Payload)` — v3.2.0
- ✓ `ProcessorEntity` now carries 3 symmetric nullable Schema FKs (Input/Output/Config) — v3.2.0
- ✓ `InitialCreate` migration regenerated in place (no delta migration) — v3.2.0
- ✓ Bisect-friendly 5-commit sequence demonstrated as the canonical revision pattern — v3.2.0

**Phase 11 (Migrate Prometheus + Elasticsearch from compose stack) — 2026-05-28**
- ✓ Logs ship to Elasticsearch via OTel Collector `elasticsearch` exporter (`logs-generic.otel-default` index) — v3.2.0
- ✓ Metrics ship to Prometheus via OTel Collector `prometheus` exporter on `:8889` (`service_name` Prom label preserved) — v3.2.0
- ✓ `.WithTracing(...)` stripped from `AddBaseApiObservability` (no traces backend in v1) — v3.2.0
- ✓ Phase 5 file-exporter test infrastructure fully retired (OtelCollectorFixture deleted) — v3.2.0
- ✓ E2E round-trip test pair (`SchemasLogsE2ETests`, `SchemasMetricsE2ETests`) drives real HTTP and polls both backends — v3.2.0

### Active

Defined in `.planning/REQUIREMENTS.md` for milestone v3.3.0 (Orchestration L3 → L1 → L2 Build Pipeline). Created during `/gsd-new-milestone` on 2026-05-28.

### Out of Scope

- **Authentication / authorization** — not in v1; service is open. `CreatedBy`/`UpdatedBy` populated only when `HttpContext.User.Identity.Name` is available; otherwise null. *Why: unspecified in milestone scope; will be added when an auth boundary is defined.*
- **Orchestrator / scheduler logic** — external systems consume `WorkflowEntity.CronExpression` and the workflow graph. The webapi is CRUD only. *Why: explicitly out of scope per user.*
- **Workflow execution engine** — running a workflow, tracking runs, retries, parallelism. *Why: external responsibility.*
- **`WorkflowScheduleEntity`** (originally listed) — removed; control is per-workflow via `CronExpression` nullability. *Why: redundant given per-workflow control was sufficient.*
- **Dynamic Payload-vs-Schema conformance** — `Assignment.Payload` is only validated as syntactically valid JSON. *Why: explicitly chosen "no" (N2).*
- **Pre-validation of FK existence over HTTP** — relies on Postgres FK constraint + clean error mapping; no upfront `EntityBApi` GET. *Why: chosen Option 1.*
- **`IsActive` / `Environment` gating flag on `WorkflowEntity`** — `CronExpression` nullability serves as implicit gating. *Why: explicitly declined.*
- **Soft delete** — not in v1. CRUD `DELETE` is a hard delete. *Why: unspecified; can be added later as a base concern if needed.*
- **Pagination / filtering / sorting on list endpoints** — basic `GET` returns all; complex query is out of v1. *Why: defer until proven needed.* [Tracked as v2 HTTP-17/18/19.]
- **Multiple deployable services / NuGet packaging of `BaseApi.Core`** — Option B (single API) chosen. *Why: bounded-context entities are tightly related via FKs, single team, milestone speed prioritized.* **Validated at v3.2.0 — the locked decision held; no NuGet-packaging gap emerged across 11 phases.**
- **Workflow execution result tracking / run history** — would have been `WorkflowRunEntity`; not in this model. *Why: external responsibility.*
- **OTel tracing pipeline (traces backend)** — Phase 11 D-03 supersedes OBSERV-12; no traces backend in v1 (mirrors sk2_1 CLAUDE.md non-negotiable #2). `.WithTracing()` stripped from `AddBaseApiObservability`; collector traces pipeline deleted; `TraceExportTests` deleted. *Why: no traces backend deployed in v1; revival is a future-milestone candidate when request-flow debugging becomes painful.*

## Context

- **Shipped:** v3.2.0 — 11 phases / 41 plans / 142 integration facts / 12,321 LOC C# (5,924 src + 6,397 tests) / 190 .cs files / 334 git commits over 3 days (2026-05-26 → 2026-05-28).
- **Tech stack (locked):** .NET 8.0.421 (pinned via `global.json`), EF Core 8 + Npgsql 8.0.9, Postgres 17-alpine, FluentValidation 12.x, Mapperly (source-generators), OpenTelemetry .NET SDK 1.15.x with OTLP, Asp.Versioning.Http 8.1.0, Swashbuckle.AspNetCore 6.9.0, JsonSchema.Net (draft 2020-12), Cronos (5-field), Elasticsearch 8.15.5, Prometheus v3.11.3, OTel Collector contrib 0.152.0.
- **Repository structure:** `src/BaseApi.Core/` (reusable class library — 5,924 LOC including tests/, actually src/BaseApi.Core + src/BaseApi.Service split), `src/BaseApi.Service/` (the runnable webapi + Features/* per-entity folders), `tests/BaseApi.Tests/` (xUnit v3 + WebApplicationFactory + per-class throwaway Postgres DBs).
- **Test discipline:** 3-consecutive-GREEN cadence locked as the phase-close gate (Phase 3 D-18) — proven 11×. Byte-identical `psql \l` SHA-256 BEFORE/AFTER snapshot proves zero leaked `stepsdb_test_*` databases (Phase 3 D-15) — locked invariant honored across all 11 phases (most recent SHA-256: `0d98b0de57125b164489958eef5fc3da26969d18a7ef8bba845da02f20aac127`, the 4th consecutive phase to record this baseline).
- **Domain framing:** workflow / data-pipeline platform. `Schema` defines data shapes; `Processor` transforms input→output schemas (and optionally references a Config schema, per Phase 10); `Step` couples a Processor to next steps; `Assignment` binds a Step to a Payload; `Workflow` is the DAG of entry steps + assignments with an optional cron. External Orchestrator + Scheduler consume `Workflow` records to drive execution.
- **Inheritance model:** abstract generic controllers + base entity + base validators + service extensions. No `dotnet new` templates; no NuGet packaging (Option B).
- **FK relationship topology:** Schema (root) ← Processor (Input/Output/Config); Processor ← Step (+self-ref via `NextStepIds`); Step ← Assignment; Step + Assignment ← Workflow. Migration order is fixed by this graph; `InitialCreate` migration generates all 11 explicit FK constraint names matching the Phase 4 `PostgresExceptionMapper` Option A regex.

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
| Single-API (Option B) instead of 6 separate webapis | Entities have cross-entity FKs → can't isolate DBs; one team; faster milestones; can split later if needed | ✓ Good (v3.2.0) — 11 phases / 41 plans / no NuGet-packaging gap |
| Shared class library `BaseApi.Core` + abstract generic controllers | Single source of infrastructure; concrete entities are pure HTTP shells | ✓ Good (v3.2.0) — 5 entity feature folders are uniform 6-file shells inheriting all behavior |
| `BaseEntity` is abstract, no table; 5 concrete entities, 5 tables | Avoids inheritance-mapping complexity; clean per-entity schema | ✓ Good (v3.2.0) |
| Single Postgres DB, single `AppDbContext` | Forced by cross-entity FK requirement | ✓ Good (v3.2.0) |
| FK columns scalar; M2M via junction tables (no nav properties between entities) | DB-enforced integrity; minimal code coupling between entity definitions | ✓ Good (v3.2.0) — 3 junction tables + 11 explicit FK constraint names |
| 3 DTOs per entity (Create / Update / Read) | Explicit separation of server-controlled fields; future-proof if Create/Update diverge | ✓ Good (v3.2.0) |
| 3-tier layering: Controller → Service → Repository | Service is the home for entity-specific logic + M2M sync; Repository generic in base | ✓ Good (v3.2.0) — `Step.SyncJunctionsAsync` + `Workflow.SyncJunctionsAsync` overrides demonstrate the seam |
| Mapperly (source-gen) over AutoMapper | Zero runtime reflection, faster, AOT-safe, modern .NET 8 idiom | ✓ Good (v3.2.0) — RMG012/RMG020 drift detection proven live |
| FluentValidation over DataAnnotations | Inheritable validators; supports SemVer + per-entity composition cleanly | ✓ Good (v3.2.0) |
| OTel exporter target = external OTel Collector via OTLP | User-specified; vendor-neutral; Collector handles backend fan-out | ✓ Good (v3.2.0) — backend cutover (Phase 11) swapped logs/metrics destinations without touching the SDK side |
| `Logging:LogLevel` single source of truth for console and OTel sinks | Both sinks bind to MEL pipeline; `Logging:LogLevel` filters before either sink runs | ✓ Good (v3.2.0) |
| `X-Correlation-Id` middleware: read or generate; attach to log scope and all responses | User-specified for log correlation; also propagates into error bodies | ✓ Good (v3.2.0) — Phase 11 E2E test proves corrId round-trips through OTel to ES |
| RFC 7807 Problem Details with `correlationId` field on every error | Machine-readable, standard, ties errors to logs | ✓ Good (v3.2.0) |
| FK pre-validation: rely on Postgres constraint + clean error mapping (Option 1) | No upfront HTTP hop to verify FK existence | ✓ Good (v3.2.0) — Phase 4 PostgresExceptionMapper Option A regex held across 5 entities + 11 FK constraints |
| No dynamic Payload-vs-Schema conformance (N2 = No) | Explicit user decision; keeps validator scope bounded | ✓ Good (v3.2.0) — deferred to v2 as VALID-21 |
| `Processor.SourceHash` SHA-256, unique | User-specified algorithm + uniqueness constraint | ✓ Good (v3.2.0) — `uq_processor_source_hash` index in InitialCreate |
| `Processor.InputSchemaId`/`OutputSchemaId` nullable; `ConfigSchemaId` added Phase 10 | Supports source/sink/unconfigured processors; FK enforced by Postgres when non-null | ✓ Good (v3.2.0) — Phase 10 symmetric addition of Config FK demonstrated the locked pattern |
| `(Name, Version)` not unique | User-specified | ✓ Good (v3.2.0) |
| Drop `WorkflowScheduleEntity`; rely on `Workflow.CronExpression` nullability for gating | Redundant given per-workflow control was sufficient | ✓ Good (v3.2.0) |
| Migrations applied on startup by `BaseApi.Service` | Single owner of schema; no separate migration tool needed | ✓ Good (v3.2.0) — Phase 8 wired `MigrateAsync` into `StartupCompletionService` with try/catch/no-rethrow contract; PERSIST-10 verified via `MigrationFailureWebAppFactory` |
| `Schema.Definition` validated as valid JSON + valid JSON Schema | User-specified | ✓ Good (v3.2.0) — JsonSchema.Net draft 2020-12, SSRF-disabled (`<500ms` regression assertion) |
| `Assignment.Payload` validated as valid JSON only (not against referenced Schema) | User-specified (N2) | ✓ Good (v3.2.0) — Phase 10 simplified further by dropping SchemaId from Assignment |
| Bisect-friendly N-commit sequence for v1-surface revisions (Phase 10 precedent) | Each commit isolates one concern; final commit flips test project GREEN | ✓ Good (v3.2.0) — 5-commit Phase 10 sequence intact in git log; pattern adopted by Phase 11 (10 commits) |
| 3-consecutive-GREEN test cadence + byte-identical psql `\l` snapshot as phase-close gate | Proves no flakes + no leaked test DBs | ✓ Good (v3.2.0) — locked in Phase 3 D-15/D-18; honored 11× across the milestone |

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
*Last updated: 2026-05-29 — Phase 16 complete (idempotency + concurrency + L1-cleanup + happy-path coverage facts; v3.3.0 close gate GREEN at 3×235, dual-SHA BEFORE=AFTER). milestone v3.3.0 phase work at 5/5 (100%) — ready for /gsd-complete-milestone. v3.2.0 (Steps API MVP) shipped 2026-05-28; see `milestones/v3.2.0-ROADMAP.md` for the full phase narrative; per-phase footers preserved below for git-blame continuity.*

<details>
<summary>Historical phase footers (Phases 1-11, v3.2.0)</summary>

*2026-05-28 — Phase 11 (Migrate Prometheus and Elasticsearch from Compose Stack) complete: 10 plans / 6 waves shipped the canonical Phase-5→Phase-11 observability backend transition. Wave 1 (11-01) doc-first amendment: REQUIREMENTS.md mutates OBSERV-12 SUPERSEDED, extends INFRA-06, adds OBSERV-13/14 + INFRA-08 + TEST-07. Wave 2 (11-02 + 11-03) compose.yaml extended with elasticsearch + prometheus services; collector-config rewired (logs→ES exporter only, metrics→prometheus exporter only, no traces, no debug/file/logging). Wave 3 (11-04 + 11-05) prometheus.yml created; ObservabilityServiceCollectionExtensions stripped .WithTracing chain + 2 orphan usings; TraceExportTests + OtelEndOfSuiteCleanup + tests/.otel-out/ deleted. Wave 4 (11-06) 4 test-helper files + Wave 0 ES index probe (live index = `logs-generic.otel-default`, OTLP-normalized field shape despite mapping.mode:none deprecation). Wave 5 (11-07) SchemasLogsE2ETests + SchemasMetricsE2ETests (route-template literal http_route label per Rule 1 fix-forward). Wave 6 (11-08a/b/c) HealthEndpointsTests decoupled from OtelCollectorFixture; LogExport/LogLevelFilter/MetricsExport rewritten ES/Prom-polling; OtelCollectorFixture deleted; closing 3-GREEN cadence 163s/161s/162s at 142/142, byte-identical psql \l SHA-256 0d98b0de…0aac127. **Phase 11 COMPLETE — sk_p's observability backend mirrors sk2_1's runtime posture (logs ES, metrics Prom, no traces).***

*2026-05-28 — Phase 10 (Remove SchemaId on AssignmentEntity and add ConfigSchemaId on ProcessorEntity) complete: 5 plans / 4 waves coordinated symmetric v1-surface field-shape revision as canonical bisect-friendly 5-commit sequence (1de7e71 → 79b07d1 → 12577ac → 146d482 → 6d043e4). Wave 1 (10-01) doc-first REQUIREMENTS.md amendment. Wave 2 (10-02 + 10-03) refactor(asm) removes SchemaId from AssignmentEntity + 3 satellite files; feat(proc) adds Guid? ConfigSchemaId to ProcessorEntity + 3 DTOs + 2 validator When-blocks + lambda-less HasOne FK block (SetNull cascade, fk_processor_config_schema_id matching PostgresExceptionMapper Option A regex). Wave 3 (10-04) migration: regenerated InitialCreate in place per D-07 teardown sequence. Wave 4 (10-05) test: 10 ProcessorCreateDto/UpdateDto sites updated (SPEC said 8; runtime audit found 10), AssignmentsIntegrationTests.CreatePrereqAsync simplified Task<(Guid,Guid)> → Task<Guid>, 2 new ConfigSchemaId round-trip facts. 3-consecutive GREEN at 142/142 (29.281s/28.979s/28.869s); psql \l SHA-256 0d98b0de…0aac127 BEFORE=AFTER. **Phase 10 COMPLETE — Processor now carries 3 symmetric nullable Schema FKs (Input/Output/Config); Assignment is `(StepId, Payload)`-only.***

*2026-05-28 — Phase 9 (Processor.GetBySourceHash + Orchestration Start/Stop) complete: 3 plans / 2 waves shipped 10 source files closing Phase 9 v1 surface. (Wave 1 / 09-01) ProcessorService.GetBySourceHashAsync extends concrete service with DbContext.Set lookup + NotFoundException-on-miss; ProcessorsController.GetBySourceHash mapped to `[HttpGet("by-source-hash/{sourceHash}")]`. (Wave 1 / 09-02) New Features/Orchestration/ folder lands WorkflowIdsValidator (primitive-collection validator, auto-discovered), OrchestrationService (NOT BaseService-derived; injects BaseDbContext + IValidator + 5 mappers up-front), OrchestrationController (SINGULAR class name — first in codebase), OrchestrationServiceCollectionExtensions; both Start/Stop endpoints delegate to same ValidateWorkflowIdsAsync method returning 204 No Content. AppFeatures wired as 6th call. (Wave 2 / 09-03) 3 new test classes (GetBySourceHashFacts + StartOrchestrationFacts + StopOrchestrationFacts) covering all 6 REQ-IDs. 3-consecutive GREEN 138/138 (~29.5s); SHA-256 1C611C60…09FE5E BEFORE=AFTER. **Phase 9 COMPLETE.***

*2026-05-28 — Phase 8 (Entity Build-Out + Migrations + Docker + Runtime Tests) complete: 8 plans / 4 waves shipped 53 source files spanning 5 v1 entities + 3 junctions + InitialCreate migration + multistage Dockerfile + AppDbContext composition + 30 new integration facts. (Wave 1 / 08-01) Foundation: multistage Dockerfile + .dockerignore, compose.yaml build mutation, dotnet-ef 8.0.27 pinned, 4 packages added (JsonSchema.Net, Cronos, Riok.Mapperly, EFCore.Design), Phase8WebAppFactory, TEST-03/TEST-04 deferred to v2. (Wave 2 / 08-02..08-06) 5 entity feature folders following uniform Schema-feature-folder layout. Step + Workflow override BaseService.SyncJunctionsAsync. (Wave 3 / 08-07) AppDbContext populated with 8 DbSets, AppFeatures aggregator keeps Program.cs at 8 non-trivial body lines. StartupCompletionService body-swapped to MigrateAsync-at-startup with try/catch/LogCritical/no-rethrow/no-MarkReady contract (PERSIST-10). InitialCreate migration: 8 CreateTable, 11 explicit FK constraint names. (Wave 4 / 08-08) ErrorMappingFacts (4 cross-entity error-mapping facts) + MigrationFailureWebAppFactory + MigrationFailureFacts (PERSIST-10 — /health/live=200 while /health/startup=503 & /health/ready=503). 3-consecutive GREEN 128/128. **v1 milestone shippable: 39 active REQ-IDs satisfied; 8/8 phases complete.***

*2026-05-27 — Phase 7 (Generic HTTP Base + Composition Root) complete: 18 new BaseApi.Core source files + Program.cs cutover land the abstract generic composition root. IHasId marker; abstract BaseController + BaseService; CONTEXT D-13 amendment — observability split to `IHostApplicationBuilder.AddBaseApiObservability` (needs ILoggingBuilder). AuditInterceptor lifetime reconciled as Singleton (Phase 3 D-06). UseBaseApi() enforces D-19 pipeline order. Program.cs is 7 non-trivial body lines (cap: 10). 98/98 facts GREEN × 3 consecutive runs (76 prior + 22 new). 9 REQ-IDs closed. Manual Swagger UI smoke resolved via SwaggerEnvironmentFacts (Dev 200 / Prod 404) — v1 BaseApi.Service has zero concrete controllers, so live-boot empty UI is structurally expected.*

*2026-05-27 — Phase 6 (Validation + Mapping Base) complete: 5 BaseApi.Core seam files. Mapperly RMG007/RMG012/RMG020/RMG089 promoted to solution-wide build errors (MP-codes corrected to RMG-codes per RESEARCH A-01). WebAppFactory unsealed. 76/76 GREEN × 3 consecutive runs. Drift-detection proven LIVE on all 3 mapper methods. 8 REQ-IDs closed. Plan-gap fix-forward: Phase 8 must replicate 3-method [MapperIgnoreTarget]/[MapperIgnoreSource] attribute pattern across all 5 entity mappers.*

*2026-05-27 — Phase 5 (Observability + Health Probes) complete: OTel wired via MEL bridge (never WithLogging per Pitfall 8) + services-chain metrics+traces + bare AddNpgsql() (no callback per RESEARCH-corrected D-05 / Reconciliation 1) + dual resource builders (service.name=sk-api, service.version=3.2.0). Three K8s-style health probes with strict tag discipline (live → only "self", never DB per Pitfall 15). IStartupGate + StartupCompletionService. otel-collector Docker Compose service with OTLP gRPC :4317 + HTTP :4318 + file exporter to bind-mounted tests/.otel-out/ + filter/health_metrics processor (OTTL IsMatch on http.route). 47/47 GREEN × 3 consecutive runs. 14 REQ-IDs closed. [Note: Phase 5 file-exporter infrastructure retired by Phase 11; OBSERV-12 superseded.]*

*2026-05-27 — Phase 4 (Cross-Cutting Middleware + Error Handling) complete: CorrelationIdMiddleware + .NET 8 IExceptionHandler chain (NotFound→Validation→DbUpdate→Fallback per D-06; DbUpdateExceptionHandler concurrency-FIRST per Pitfall 7) + PostgresExceptionMapper (Option A regex preserving `_id` suffix, SQLSTATE 23503→422 / 23505→409) + Program.cs composition root with AddProblemDetails customizer. 31/31 facts GREEN over real Postgres 17. 14 REQ-IDs closed. Threat model verified: T-04-LEAK / T-04-XMIN / T-04-INJECT all PASS. Npgsql pinned at 8.0.9 (binary-compat with EFCore.PostgreSQL 8.0.10 — fix-forward from initial 9.0.0 pin).*

*2026-05-27 — Phase 3 (EF Core Persistence Base) complete: BaseEntity + BaseDbContext (abstract, snake_case + xmin shadow concurrency token) + AuditInterceptor (TimeProvider-based, null-safe HttpContext) + IRepository/Repository (sealed generic, 5-method surface, load-then-remove DeleteAsync); 7/7 facts GREEN against real Postgres 17; zero leaked stepsdb_test_* databases; D-11 (no Program.cs touch) and D-16 (no migrations) both honored — Phase 8 owns first migration so snake_case + xmin apply to the very first schema.*

</details>
