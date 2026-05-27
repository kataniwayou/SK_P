# Phase 8: Entity Build-Out + Migrations + Docker Runtime + Tests - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-05-27
**Phase:** 08-entity-build-out-migrations-docker-runtime-tests
**Areas discussed:** Plan shape & wave structure, Test harness reconciliation, Service-side layout conventions, Runtime image + startup migration

---

## Plan shape & wave structure

### Q: How should Phase 8 be sliced into plans for the executor?

| Option | Description | Selected |
|--------|-------------|----------|
| 5 entity + 3 infra plans | Wave A foundation (test harness + Dockerfile + compose) → Wave B 5 parallel entity plans → Wave C migration + integration tests | ✓ |
| FK-ordered sequential (5 plans) | Schema → Processor → Step → Assignment → Workflow as 5 dependent plans; each ships its own migration | |
| Monolithic single plan | One ~40-task plan covering all entities + migration + Dockerfile + tests | |
| Hybrid: Schema+Processor pair, then 3 sequential | Plan 1 foundation; Plan 2 Schema+Processor; Plan 3 Step; Plan 4 Assignment; Plan 5 Workflow; Plan 6 migration+tests | |

**User's choice:** 5 entity + 3 infra plans (recommended)
**Notes:** Maps directly to ROADMAP's "parallelizable: yes" guidance and Wave B is the maximum parallel win for 5 identical-shape entities.

### Q: Where do the smoke + error-mapping tests live?

| Option | Description | Selected |
|--------|-------------|----------|
| Per-entity smoke in each entity plan + 1 cross-entity test plan | Each entity plan ships its 5 happy-path facts; final plan ships 4 error-mapping facts | ✓ |
| All tests in one dedicated test-harness plan after entities | Entity plans produce only production code; one big test plan in Wave C | |
| Tests deferred to first verification gate | Tests generated retroactively via /gsd-add-tests | |

**User's choice:** Per-entity smoke in each entity plan + 1 cross-entity test plan (recommended)
**Notes:** Each entity plan can self-verify its own HTTP surface during Wave B execution.

### Q: When does the InitialCreate migration land?

| Option | Description | Selected |
|--------|-------------|----------|
| Dedicated migration plan in Wave C, AFTER all 5 entities exist on AppDbContext | Single InitialCreate; matches ROADMAP SC#1 wording | ✓ |
| Migration as last task of last entity plan (Workflow plan) | Saves one plan boundary; couples migration to entity timing | |
| 5 incremental migrations (one per entity plan) | Finer-grained rollback; deviates from SC#1 singular wording | |

**User's choice:** Dedicated migration plan in Wave C (recommended)

### Q: Should the Dockerfile + compose update land before, alongside, or after entity work?

| Option | Description | Selected |
|--------|-------------|----------|
| Before entities (Wave A foundation) | Dockerfile lands alongside test harness; entity plans can validate `docker compose up` between waves | ✓ |
| After entities (Wave C verification) | First `docker compose up` exercises complete service; image errors surface late | |
| Alongside Workflow plan | Bundle into the topologically-last entity plan | |

**User's choice:** Before entities (Wave A foundation) (recommended)

---

## Test harness reconciliation

### Q: Container strategy — TEST-03 (Testcontainers.PostgreSQL) vs existing PostgresFixture pattern

| Option | Description | Selected |
|--------|-------------|----------|
| Keep existing PostgresFixture pattern; relax TEST-03 | Existing pattern proven across 98 facts; move TEST-03 to v2 | ✓ |
| Adopt Testcontainers per TEST-03; migrate existing tests | REQ-compliant; ~3-5s cold-start per fixture; 5 fixture rewrites required | |
| Hybrid: Testcontainers per assembly + throwaway DBs per class | One container session-wide; per-class throwaway DBs on it | |

**User's choice:** Keep existing PostgresFixture pattern; relax TEST-03 (recommended)
**Notes:** REQUIREMENTS.md amendment required in Wave A foundation plan with deferral reasoning.

### Q: DB reset strategy — TEST-04 (Respawn) vs Phase 3 D-15 throwaway DBs

| Option | Description | Selected |
|--------|-------------|----------|
| Keep throwaway DBs per class; relax TEST-04 | Phase 3 D-15 byte-identical `psql \l` proof preserved | ✓ |
| Add Respawn within-class between facts; throwaway DB per class stays | Best of both; closes TEST-04 honestly | |
| Full Respawn per REQ; abandon throwaway DB pattern | REQ-compliant; loses Phase 3 D-15 no-leak proof | |

**User's choice:** Keep throwaway DBs per class; relax TEST-04 (recommended)

### Q: Test density for the 25 smoke tests

| Option | Description | Selected |
|--------|-------------|----------|
| 1 test class per entity, 5 [Fact]s each = 25 explicit facts | SchemasIntegrationTests / ProcessorsIntegrationTests / etc.; explicit failure messages | ✓ |
| 5 [Theory]s per entity (one per verb, parameterized data) | Less LOC; harder failure triage | |
| Mix: smoke as [Fact]s; validator/error tests as [Theory]s | Likely organic outcome; lock convention up front | |

**User's choice:** 1 test class per entity, 5 [Fact]s each = 25 explicit facts (recommended)

### Q: Test factory shape

| Option | Description | Selected |
|--------|-------------|----------|
| Single Phase8WebAppFactory + per-test ConfigureTestServices overrides | Mirrors Phase 4/6 WebAppFactory pattern; one factory, one PostgresFixture wiring | ✓ |
| Phase8WebAppFactory + 5 per-entity subclasses | Each entity gets explicit per-test wiring; 5 new factory classes | |
| Inline factory construction per test class | Maximum isolation; no inheritance; duplicated boilerplate | |

**User's choice:** Single Phase8WebAppFactory + per-test ConfigureTestServices overrides (recommended)

---

## Service-side layout conventions

### Q: Per-entity folder structure inside src/BaseApi.Service/

| Option | Description | Selected |
|--------|-------------|----------|
| Feature folders | `Features/{Entity}/` contains Entity, Dtos, Mapper, Service (if any), Controller, Validator, EntityConfiguration | ✓ |
| Type-bucket folders | `Entities/`, `Controllers/`, `Services/`, etc. — 5 entities mix in each | |
| Hybrid: Features/ for orchestration types, Persistence/ for EF | Features/{Entity}/ + Persistence/Configurations/{Entity}EntityConfiguration.cs | |

**User's choice:** Feature folders (recommended), with EF configs resolved to `Persistence/Configurations/` per Q3 selection (effectively the hybrid path).
**Notes:** Resolution noted in CONTEXT.md D-09 — production types in `Features/{Entity}/`; EF configurations in `Persistence/Configurations/`.

### Q: DTO file pattern

| Option | Description | Selected |
|--------|-------------|----------|
| One file, 3 records | `{Entity}Dtos.cs` contains CreateDto/UpdateDto/ReadDto records (Phase 6 TestDtos pattern) | ✓ |
| 3 files per entity | Separate `{Entity}CreateDto.cs` / `UpdateDto.cs` / `ReadDto.cs` | |

**User's choice:** One file, 3 records (recommended)

### Q: AppDbContext / EF configuration style

| Option | Description | Selected |
|--------|-------------|----------|
| IEntityTypeConfiguration<T> classes in Persistence/Configurations/ | 8 config classes; AppDbContext calls ApplyConfigurationsFromAssembly | ✓ |
| Inline OnModelCreating in AppDbContext | All entities + junctions + indexes + jsonb in one method | |
| Inline configuration via Entity static methods | Each entity exposes `Configure(EntityTypeBuilder<TEntity>)` static method | |

**User's choice:** IEntityTypeConfiguration<T> classes in Persistence/Configurations/ (recommended)

### Q: Concrete service class shape per entity

| Option | Description | Selected |
|--------|-------------|----------|
| Marker class only for entities that override SyncJunctionsAsync | StepService + WorkflowService only; Schema/Processor/Assignment use closed-generic via DI | ✓ |
| Marker class for every entity (5 concrete services) | Symmetry; 3 empty classes | |
| Open-generic registration only — no concrete service classes | Cleanest in theory; abandons Phase 7 D-09 contract | |

**User's choice:** Marker class only for overriders (recommended)
**Notes:** CONTEXT.md D-12 documents a wrinkle: BaseService<,,,> is abstract, so DI cannot construct it directly. Resolution still being decided by planner — likely needs 5 marker classes (3 empty passthrough, 2 overriding). Logged as Claude's Discretion item.

---

## Runtime image + startup migration

### Q: Dockerfile structure for INFRA-05 (multistage .NET 8)

| Option | Description | Selected |
|--------|-------------|----------|
| Standard multistage + non-root user + ASPNETCORE_URLS=8080 | sdk:8.0-bookworm-slim build; aspnet:8.0-bookworm-slim runtime; USER app; per-project COPY cache | ✓ |
| Alpine-based image (smaller, but musl libc differences) | ~50% smaller; Npgsql + Testcontainers + Alpine edge cases | |
| Standard multistage as root user | Skip USER app; easier debugging; flagged by scanners | |

**User's choice:** Standard multistage + non-root user (recommended)

### Q: compose.yaml update for baseapi-service block

| Option | Description | Selected |
|--------|-------------|----------|
| Drop profile gate; switch to build: . | Remove profiles: [phase-8]; build: { context: ., dockerfile: Dockerfile }; default `docker compose up` spins up the service | ✓ |
| Keep profile gate; bump to a tagged published image | Two-step dev loop; less alignment with INFRA-05 intent | |
| Drop profile gate; use prebuilt image (no build: directive) | Decouples compose from build context; extra `docker build` step locally | |

**User's choice:** Drop profile gate; switch to build: . (recommended)

### Q: Migration runner integration

| Option | Description | Selected |
|--------|-------------|----------|
| Swap StartupCompletionService body to MigrateAsync + MarkReady | Phase 5 D-06 1-line substitution; zero new types | ✓ |
| New MigrationHostedService runs first; StartupCompletionService stays gate-only | Cleaner separation; two hosted services | |
| Synchronous Migrate in Program.cs before app.Run() | Simple; violates PERSIST-10 (process must NOT crash on failure) | |

**User's choice:** Swap StartupCompletionService body (recommended)

### Q: Migration failure UX

| Option | Description | Selected |
|--------|-------------|----------|
| Catch in StartupCompletionService; log critical; do NOT MarkReady | Process alive; live=200, startup/ready=503; matches PERSIST-10 + HEALTH-01 + HEALTH-03 | ✓ |
| Retry-with-backoff before giving up | N × 5s retries before logging critical; complexity v2 | |
| Mark IStartupGate.MarkReady regardless; readiness check probes DB directly | Loosens HEALTH-01 semantics; rejected | |

**User's choice:** Catch + log critical + do NOT MarkReady (recommended)

---

## Claude's Discretion

- **StepEntryCondition enum DB mapping** — int default vs string column; planner picks (default int recommended).
- **Junction PK + FK column naming** — snake_case auto-resolves; planner picks composite-PK order + FK constraint names per Phase 4 ERROR-11 convention.
- **Junction DELETE cascade behavior** — Restrict on dependent side (SC#5 enforced); planner verifies principal side.
- **jsonb column convention** — `.HasColumnType("jsonb")` explicit (recommended) vs Npgsql auto.
- **Validator inheritance pattern** — `Include(new BaseDtoValidator<T>())` locked by Phase 6 VALID-20; planner picks rule organization.
- **Mapperly attribute pattern** — `[MapperIgnoreTarget]` still required on ToEntity + Update; `[MapperIgnoreSource]` on ToRead NOT needed since ReadDtos carry audit fields (HTTP-07).
- **Per-validator stress test density** — Theory vs Fact for validator negative cases; planner picks per-validator.
- **Per-entity service file existence for non-overriding entities** — D-12 wrinkle; likely 5 marker classes (3 passthrough, 2 overriding).

## Deferred Ideas

- TEST-03 (Testcontainers.PostgreSql) → v2
- TEST-04 (Respawn) → v2
- Theory-based validator stress test density convention → Phase 9+
- HEALTHCHECK directive in Dockerfile (vs compose-only) → backlog
- Retry-with-backoff on MigrateAsync failure → v2
- Per-API-version controller siblings → future milestone
- Bulk CRUD endpoints → backlog
- Paged<TRead> envelope for GET list → v2 (HTTP-17)
- Open-generic DI registration for non-overriding services → Phase 9+ refactor
- CORS configuration in UseBaseApi pipeline → future milestone
- StepEntryCondition `HasConversion<string>()` for postgres `\d` inspectability → backlog
