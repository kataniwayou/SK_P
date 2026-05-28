# Steps API — Milestones

A historical log of shipped milestones. Each entry is a frozen snapshot; full archives live in `milestones/`.

---

## v3.2.0 — Steps API MVP

**Shipped:** 2026-05-28
**Tag:** `v3.2.0`
**Archives:** [milestones/v3.2.0-ROADMAP.md](milestones/v3.2.0-ROADMAP.md) · [milestones/v3.2.0-REQUIREMENTS.md](milestones/v3.2.0-REQUIREMENTS.md)

### Delivered

A .NET 8 Web API platform exposing CRUD over the 5-entity workflow-engine data model (Schema, Processor, Step, Assignment, Workflow) on top of a reusable `BaseApi.Core` library — full Postgres persistence, OpenTelemetry observability (logs to Elasticsearch, metrics to Prometheus), RFC 7807 error responses, three K8s-style health probes, and a 142-fact integration test suite running 3-consecutive-GREEN against real backends.

### Stats

| Metric                    | Value                                          |
| ------------------------- | ---------------------------------------------- |
| Phases                    | 11                                             |
| Plans                     | 41                                             |
| C# files                  | 190 (105 src / 85 tests)                       |
| Lines of code             | 12,321 (5,924 src / 6,397 tests)               |
| Git commits               | 334 (76 feat / 59 fix / docs/test/refactor)    |
| Git range                 | `9a606ed` → `cbdc342`                          |
| Timeline                  | 2026-05-26 → 2026-05-28 (3 days)               |
| Integration facts (final) | 142/142 GREEN × 3 consecutive runs             |

### Key Accomplishments

1. **Reusable `BaseApi.Core` composition root** — `AddBaseApi`/`UseBaseApi` wires generic `BaseController` / `BaseService` / `Repository<T>` through abstract DTO / Validator / Mapper seams; `Program.cs` capped at ≤10 non-trivial body lines; observability split to `IHostApplicationBuilder.AddBaseApiObservability` for MEL bridge.
2. **5 v1 entity feature folders + 3 junction tables + InitialCreate migration + multistage Dockerfile** — Schema / Processor / Step / Assignment / Workflow CRUD over real Postgres 17 with audit-symmetric Mapperly mappings, FluentValidation per-entity rules, and explicit FK constraint names matching the Phase 4 `PostgresExceptionMapper` regex.
3. **Cross-cutting middleware + RFC 7807 Problem Details** — `X-Correlation-Id` middleware (read-or-generate, echoed on every response), 4-handler `IExceptionHandler` chain (NotFound → Validation → DbUpdate → Fallback), Postgres SQLSTATE → HTTP mapping (`23503`→422, `23505`→409, both with offending field name).
4. **OpenTelemetry backend cutover (Phase 11)** — logs ship to Elasticsearch (`logs-generic.otel-default`, OTLP-normalized field shape), metrics ship to Prometheus (`up{job="otel-collector"}=1`, `service_name="sk-api"` label preserved via `resource_to_telemetry_conversion`), traces dropped; Collector v0.152.0 wired in compose stack alongside ES 8.15.5 + Prom v3.11.3; Phase 5 file-exporter test infrastructure fully retired.
5. **Three K8s-style health probes with strict tag discipline** — `/health/live` (process only, never DB per Pitfall 15), `/health/ready` (Postgres + StartupCompletionService), `/health/startup` (migration-gated via `MigrateAsync` in `StartupCompletionService.ExecuteAsync` try/catch/LogCritical/no-rethrow contract).
6. **Test suite discipline** — 142/142 integration facts × 3 consecutive GREEN runs (163s / 161s / 162s on final Phase 11 close), zero flakes; byte-identical `psql \l` SHA-256 snapshot `0d98b0de…0aac127` BEFORE = AFTER (4 baseline DBs, zero leaked `stepsdb_test_*` databases — Phase 3 D-15 invariant preserved across all 11 phases).

### Decisions Set in Stone (v3.2.0)

| Decision                                                                  | Outcome  |
| ------------------------------------------------------------------------- | -------- |
| Single-API (Option B) instead of 6 separate webapis                       | ✓ Good   |
| `BaseApi.Core` shared class library + abstract generic controllers        | ✓ Good   |
| `BaseEntity` abstract, no table; 5 concrete entities → 5 tables           | ✓ Good   |
| Single Postgres DB + single `AppDbContext`                                | ✓ Good   |
| Mapperly source-generators (RMG007/RMG012/RMG020/RMG089 → solution error) | ✓ Good   |
| FluentValidation with inheritable `BaseDtoValidator<T>`                   | ✓ Good   |
| OTel Collector via OTLP — logs to ES, metrics to Prom, no traces          | ✓ Good   |
| `MigrateAsync` at startup inside `StartupCompletionService` (PERSIST-10)  | ✓ Good   |
| `Processor.ConfigSchemaId` (Phase 10) — symmetric Input/Output/Config FKs | ✓ Good   |
| `Assignment` surface collapsed to `(StepId, Payload)` only (Phase 10)     | ✓ Good   |

### Closed Requirements (per phase)

- **Phase 1 (Repository Scaffold)** — INFRA-01, INFRA-02, INFRA-03, INFRA-04
- **Phase 2 (Postgres + Docker Compose)** — INFRA-06, INFRA-07
- **Phase 3 (EF Core Persistence Base)** — ENTITY-01, ENTITY-02, PERSIST-02..07, PERSIST-11, PERSIST-15, PERSIST-16
- **Phase 4 (Cross-Cutting Middleware + Error Handling)** — OBSERV-09..11, ERROR-01..11
- **Phase 5 (Observability + Health Probes)** — OBSERV-01..08, OBSERV-12 (later superseded by Phase 11 D-03), HEALTH-01..05
- **Phase 6 (Validation + Mapping Base)** — VALID-01..07, HTTP-10
- **Phase 7 (Generic HTTP Base + Composition Root)** — HTTP-01..03, HTTP-08, HTTP-09, HTTP-13..16
- **Phase 8 (Entity Build-Out + Migrations + Docker + Tests)** — 41 REQ-IDs: PERSIST-01/08/09/10/12/13/14, ENTITY-03..10, HTTP-04..07/11/12, VALID-08..20, INFRA-05, TEST-01/02/05/06
- **Phase 9 (Processor.GetBySourceHash + Orchestration Start/Stop)** — 6 SPEC-local REQ-IDs (REQ-1..REQ-6)
- **Phase 10 (Remove SchemaId on Assignment; add ConfigSchemaId on Processor)** — ENTITY-04, ENTITY-07, VALID-11, VALID-15 amended
- **Phase 11 (Migrate Prometheus + Elasticsearch into compose stack)** — OBSERV-13, OBSERV-14, INFRA-08, TEST-07; OBSERV-12 supersession finalized; INFRA-06 amendment locked

### Notes

- **TEST-03 / TEST-04 deferred to v2** (Phase 8 D-05/D-06) — concurrency & xmin token integration tests deferred; current unit-level coverage (Phase 3 `XminConcurrencyTokenTests`) judged sufficient for v1 ship.
- **`WorkflowScheduleEntity` dropped** — replaced by `Workflow.CronExpression` nullability for gating.
- **Authentication/Authorization out of scope for v1** — service is open; `CreatedBy`/`UpdatedBy` populate only when `HttpContext.User.Identity.Name` is available.
- **`(Name, Version)` is not unique** — user-specified; `Version` is metadata.

### Code Review Posture at Close

Across Phase 11 close: 0 critical / 5 warning / 8 info — all warnings test-hygiene only (cancellation token threading, env var restoration, TOCTOU windows). None blocked phase completion. Aggregated across all 11 phases: 0 critical findings reached production paths; all warnings advisory or test-hygiene.

---

*Next milestone planning begins with `/gsd-new-milestone`.*
