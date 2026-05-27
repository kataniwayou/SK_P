# Phase 8: Entity Build-Out + Migrations + Docker Runtime + Tests - Context

**Gathered:** 2026-05-27
**Status:** Ready for planning

<domain>
## Phase Boundary

Plug all 5 concrete entities (Schema → Processor → Step → Assignment → Workflow) into the finished `BaseApi.Core` base, generate the single `InitialCreate` migration, build the runtime Docker image, and prove the stack end-to-end with smoke + error-mapping integration tests against real Postgres.

In scope (41 REQ-IDs across 9 categories):
- **Entities (8):** ENTITY-03..10 — 5 concrete `BaseEntity` subclasses + 3 junction entities (`StepNextSteps`, `WorkflowEntrySteps`, `WorkflowAssignments`); no navigation properties between entities (FK-by-Guid only).
- **Persistence (7):** PERSIST-01/08/09/10/12/13/14 — `AppDbContext` populated; `jsonb` columns; startup-applied `InitialCreate` migration; migration-failure ⇒ readiness unhealthy (no process crash); junction PKs + FKs; FK constraints enforced; unique index on `Processor.SourceHash`.
- **HTTP (6):** HTTP-04..07, HTTP-11, HTTP-12 — 3 DTOs per entity; concrete `[Mapper]` partials; concrete controllers (empty bodies inheriting `BaseController`).
- **Validation (13):** VALID-08..20 — entity-specific validators; JsonSchema.Net (draft 2020-12, remote `$ref` disabled); Cronos 5-field; SHA-256 hex regex; junction id uniqueness.
- **Infrastructure (1):** INFRA-05 — multistage Dockerfile.
- **Tests (6):** TEST-01..06 — xUnit v3 + WebApplicationFactory + real Postgres + minimum 25 smoke (5×5) + 4 error-mapping facts.

Out of scope (deferred to v2 or out of v1 entirely):
- Pagination / filtering / sorting on list endpoints (HTTP-17/18/19 → v2).
- Dynamic `Payload`-vs-Schema conformance check (VALID-21 → v2).
- Multi-instance migration coordination via advisory lock (INFRA-08 → v2).
- Separate Postgres migration vs runtime roles (INFRA-09 → v2).
- Authentication / authorization (out of v1 entirely).
- Soft delete (out of v1 entirely).
- ETag / If-Match optimistic concurrency over HTTP (Phase 7 deferred — `xmin` runs at SaveChanges, HTTP-layer is the missing half).

</domain>

<decisions>
## Implementation Decisions

### Plan shape & wave structure (Area 1)

- **D-01:** Phase 8 splits into **8 plans across 3 waves**:
  - **Wave A (1 plan, foundation):** `08-01-PLAN.md` — `Phase8WebAppFactory` + `tests/BaseApi.Tests/Composition/Phase8TestDbContext.cs` (if needed for parity with Phase 7); `Dockerfile` at repo root; `compose.yaml` updates (drop `phase-8` profile gate, switch placeholder image to `build: .`); REQUIREMENTS.md amendment moving TEST-03 + TEST-04 to v2.
  - **Wave B (5 plans, parallel):** `08-02-PLAN.md` through `08-06-PLAN.md` — one per entity in any order (Schema, Processor, Step, Assignment, Workflow). Each plan ships that entity's full feature folder (`Features/{Entity}/`) + EF config (`Persistence/Configurations/{Entity}EntityConfiguration.cs`) + 5 smoke `[Fact]` integration tests. The 3 junction `IEntityTypeConfiguration<T>` classes land in the plans of their **owning side** of the junction: `StepNextStepsConfiguration` in the Step plan, `WorkflowEntryStepsConfiguration` + `WorkflowAssignmentsConfiguration` in the Workflow plan.
  - **Wave C (2 plans, sequential):**
    1. `08-07-PLAN.md` — Migration generation: `dotnet ef migrations add InitialCreate -p src/BaseApi.Service`; verify the generated `*_InitialCreate.cs` + `*_InitialCreate.Designer.cs` capture all 5 entities + 3 junctions + jsonb columns + indexes + FK constraints + composite PKs; finalize AppDbContext.OnModelCreating (`modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly)` + `base.OnModelCreating(modelBuilder)` LAST so xmin iteration runs over the configured entities); swap `StartupCompletionService` body to apply migration on startup.
    2. `08-08-PLAN.md` — Cross-entity integration tests + final regression: 4 error-mapping facts (duplicate sourceHash → 409, non-existent entryStepIds → 422, DELETE-step-referenced-by-workflow → 422, invalid JSON Schema → 400 + SSRF $ref blocked); 3 consecutive GREEN runs of the full suite (Phase 3 D-18 cadence); byte-identical `psql \l` BEFORE/AFTER (Phase 3 D-15).

  **Why Wave A first:** The `Phase8WebAppFactory` is load-bearing for Wave B (entity smoke tests can't run without it). Dockerfile + compose update land in Wave A so that operators can `docker compose up` after Wave A and see Postgres + the service idling (no controllers yet — GET `/api/v1/schemas` returns 404 route-not-found until Wave B adds the route, then 200 + `[]`).

- **D-02:** **Per-entity smoke tests live in the entity's own plan** (Wave B). Each entity plan ships exactly 5 `[Fact]` integration tests covering its 5 CRUD verbs (POST/GET-list/GET-by-id/PUT/DELETE). 5 entities × 5 facts = 25 facts (TEST-05 floor exactly). Each plan self-verifies its HTTP surface end-to-end before exiting Wave B.

- **D-03:** **Single `InitialCreate` migration** generated in Wave C (`08-07-PLAN.md`), not 5 incremental migrations. Matches ROADMAP SC#1 wording ("the InitialCreate migration", singular). Wave B entity plans MUST NOT run `dotnet ef migrations add` — they only register their entity on `AppDbContext` (via the configuration class autoloaded by `ApplyConfigurationsFromAssembly`).

- **D-04:** **Dockerfile + compose update land in Wave A** as part of the foundation plan, NOT after entities. Image errors surface early (e.g., COPY path mistakes, base image pin issues, multistage cache regressions). Wave A `docker compose up` is functional (Postgres + idling service) even before any entity controllers exist.

### Test harness reconciliation (Area 2)

- **D-05:** **Keep existing PostgresFixture pattern**; **move TEST-03 (Testcontainers.PostgreSql) to v2**. Phase 8 reuses `tests/BaseApi.Tests/Persistence/PostgresFixture.cs` (per-class throwaway DB on the running Phase 2 Postgres container at `localhost:5433`). Existing pattern is proven across 98 facts spanning Phases 3-7; Testcontainers cold-start overhead (~3-5s/fixture on Windows Docker Desktop) and the open STATE.md concern ("Testcontainers + Windows Docker Desktop: confirm WSL2 backend") make migration high-risk for zero behavioral gain. REQUIREMENTS.md MUST be amended in Wave A foundation plan to mark TEST-03 deferred to v2 with this reasoning.

- **D-06:** **Keep per-class throwaway DBs**; **move TEST-04 (Respawn) to v2**. Phase 3 D-15 byte-identical `psql \l` BEFORE/AFTER snapshot is the canonical no-leak proof and has stayed GREEN across all subsequent phases. Respawn keeps DBs alive between runs and would invalidate that proof. REQUIREMENTS.md MUST be amended in Wave A to mark TEST-04 deferred to v2 with this reasoning. (If per-fact reset within a class becomes painful later, the hybrid "throwaway DB per class + Respawn within class" path stays open.)

- **D-07:** **Test density: 1 integration test class per entity, 5 `[Fact]`s each = 25 explicit smoke facts.** Class naming: `SchemasIntegrationTests`, `ProcessorsIntegrationTests`, `StepsIntegrationTests`, `AssignmentsIntegrationTests`, `WorkflowsIntegrationTests`. Each class uses `IClassFixture<PostgresFixture>` + `IClassFixture<Phase8WebAppFactory>`. Fact naming follows the existing Phase 4-7 convention (`Verb_Behavior_Condition`, e.g., `List_ReturnsEmptyArray_OnEmptyDb`, `Create_Returns201_AndLocationHeader_WhenValid`, `Delete_Returns204_WhenExisting`). Validator stress tests (e.g., SemVer/SHA-256/cron-shape negative cases) MAY use `[Theory]` where parameterized data genuinely reduces duplication; planner picks per-validator.

- **D-08:** **Single `Phase8WebAppFactory`** in `tests/BaseApi.Tests/Composition/Phase8WebAppFactory.cs`, with per-test customization via `factory.WithWebHostBuilder(b => b.ConfigureTestServices(...))`. Mirrors the Phase 4 / Phase 7 `WebAppFactory` pattern. The factory's constructor accepts `PostgresFixture` via xUnit `IClassFixture<>` injection — connection string flows through `Environment.SetEnvironmentVariable` BEFORE `WebApplicationFactory.Server` boots (Phase 5 D-08 / Plan 05-02 Pattern C — env-var-in-ctor pattern is the only path that works with `WebApplicationFactory<Program>`). Phase 8 must inherit Phase 5 D-11 cleanup discipline (the OTel Collector handle release) so `tests/.otel-out/telemetry.jsonl` is absent after the suite finishes.

### Service-side layout conventions (Area 3)

- **D-09:** **Per-entity feature folders** at `src/BaseApi.Service/Features/{Entity}/`. Each folder contains:
  - `{Entity}Entity.cs` — sealed class inheriting `BaseEntity`.
  - `{Entity}Dtos.cs` — single file with 3 records: `{Entity}CreateDto`, `{Entity}UpdateDto`, `{Entity}ReadDto` (D-11).
  - `{Entity}EntityMapper.cs` — `[Mapper] partial class` implementing `IEntityMapper<{Entity}Entity, {Entity}CreateDto, {Entity}UpdateDto, {Entity}ReadDto>`.
  - `{Entity}DtoValidator.cs` — concrete validators for `{Entity}CreateDto` + `{Entity}UpdateDto`, each inheriting `BaseDtoValidator<T>` via `Include(new BaseDtoValidator<...>())` per Phase 6 VALID-20.
  - `{Entity}Controller.cs` — empty body, inherits `BaseController<{Entity}Entity, {Entity}CreateDto, {Entity}UpdateDto, {Entity}ReadDto>`.
  - `{Entity}Service.cs` — **only for `StepService` and `WorkflowService`** (D-12). Schema/Processor/Assignment folders do NOT have a service file.

  REQUIREMENTS.md HTTP-04 + HTTP-11 use the wording `BaseApi.Service/{Entity}/Dtos/` and `BaseApi.Service/{Entity}/Mapping/` — the planner SHOULD treat that as illustrative; Phase 8 flattens to a single-file-per-concern feature folder (D-11), not nested Dtos/Mapping subfolders.

- **D-10:** **EF Core configurations in `src/BaseApi.Service/Persistence/Configurations/`** as `IEntityTypeConfiguration<T>` classes. 8 files total:
  - `SchemaEntityConfiguration.cs`, `ProcessorEntityConfiguration.cs`, `StepEntityConfiguration.cs`, `AssignmentEntityConfiguration.cs`, `WorkflowEntityConfiguration.cs` (5 main entities).
  - `StepNextStepsConfiguration.cs`, `WorkflowEntryStepsConfiguration.cs`, `WorkflowAssignmentsConfiguration.cs` (3 junctions).

  `AppDbContext.OnModelCreating` becomes:
  ```csharp
  protected override void OnModelCreating(ModelBuilder modelBuilder)
  {
      modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
      base.OnModelCreating(modelBuilder); // Phase 3 xmin shadow token iteration runs LAST — after configurations
  }
  ```
  Order matters: `ApplyConfigurationsFromAssembly` first so each entity is registered on the model; `base.OnModelCreating` last so Phase 3's `modelBuilder.Model.GetEntityTypes().Where(typeof(BaseEntity).IsAssignableFrom)` iteration sees all 5 newly-configured entities and stamps the `xmin` shadow concurrency token on each (PERSIST-16).

  Each configuration class is `internal sealed` and lives in `BaseApi.Service.Persistence.Configurations` namespace. Junction configurations set the composite PK + 2 FK columns + cascade behavior (planner picks Restrict-on-DELETE for the dependent side — see SC#5 "deleting a Step that a Workflow references returns 422 = FK Restrict").

- **D-11:** **DTO file pattern: one file with 3 records.** `{Entity}Dtos.cs` contains all three records:
  ```csharp
  public sealed record SchemaCreateDto(string Name, string Version, string? Description, string Definition) : IBaseDto;
  public sealed record SchemaUpdateDto(string Name, string Version, string? Description, string Definition) : IBaseDto;
  public sealed record SchemaReadDto(Guid Id, string Name, string Version, string? Description, string Definition, DateTime CreatedAt, DateTime UpdatedAt, string? CreatedBy, string? UpdatedBy) : IBaseDto, IHasId;
  ```
  Matches the Phase 6 `TestDtos.cs` pattern verbatim. ReadDto MUST implement `IHasId` (Phase 7 D-01 — required for `CreatedAtAction(nameof(GetById), new { id = read.Id }, read)`).

- **D-12:** **Concrete service marker class only for entities that override `SyncJunctionsAsync`.** Only `StepService` (NextStepIds → StepNextSteps) and `WorkflowService` (EntryStepIds → WorkflowEntrySteps; AssignmentIds → WorkflowAssignments) get a `{Entity}Service.cs`. Schema/Processor/Assignment have **no** service file — DI registers the open generic and controllers inject the closed generic:
  ```csharp
  // In AddBaseApi (already shipped) or per-entity registration:
  services.AddScoped<BaseService<SchemaEntity, SchemaCreateDto, SchemaUpdateDto, SchemaReadDto>>();
  services.AddScoped<BaseService<ProcessorEntity, ProcessorCreateDto, ProcessorUpdateDto, ProcessorReadDto>>();
  services.AddScoped<BaseService<AssignmentEntity, AssignmentCreateDto, AssignmentUpdateDto, AssignmentReadDto>>();
  services.AddScoped<BaseService<StepEntity, StepCreateDto, StepUpdateDto, StepReadDto>, StepService>();
  services.AddScoped<BaseService<WorkflowEntity, WorkflowCreateDto, WorkflowUpdateDto, WorkflowReadDto>, WorkflowService>();
  ```
  But `BaseService<,,,>` is `abstract` (Phase 7) so DI cannot construct it directly. **Resolution:** Schema/Processor/Assignment each get a thin sealed marker service class anyway (`SchemaService : BaseService<...>`) to satisfy `services.AddScoped<TConcrete>()`. The marker class has an empty body — just a passthrough constructor calling `base(...)`. So the operative rule is: **5 marker service classes; only `StepService` and `WorkflowService` override `SyncJunctionsAsync`.** Planner amends D-12 to this resolution if Phase 8 research surfaces a DI workaround (e.g., open-generic registration of the abstract base — unlikely without a factory).

  Phase 7 D-09/D-10 anticipated this: "4 of 5 v1 entities inherit the no-op and write zero override code" — meaning 4 of 5 override zero methods, NOT that 4 of 5 have zero service classes.

### Runtime image + startup migration (Area 4)

- **D-13:** **Multistage Dockerfile** at repo root `Dockerfile`:
  ```dockerfile
  # syntax=docker/dockerfile:1.7
  FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
  WORKDIR /src
  # Cache layer: restore depends only on csproj + props + lock files
  COPY ["Directory.Packages.props", "Directory.Build.props", "global.json", "./"]
  COPY ["src/BaseApi.Core/BaseApi.Core.csproj", "src/BaseApi.Core/"]
  COPY ["src/BaseApi.Service/BaseApi.Service.csproj", "src/BaseApi.Service/"]
  RUN dotnet restore "src/BaseApi.Service/BaseApi.Service.csproj"
  # Build layer: full source
  COPY src/ src/
  RUN dotnet publish "src/BaseApi.Service/BaseApi.Service.csproj" -c Release -o /publish --no-restore /p:UseAppHost=false

  FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
  WORKDIR /app
  COPY --from=build /publish .
  USER app
  ENV ASPNETCORE_URLS=http://+:8080
  EXPOSE 8080
  ENTRYPOINT ["dotnet", "BaseApi.Service.dll"]
  ```
  Notes:
  - `USER app` uses the pre-baked uid 1654 from the .NET 8 aspnet image (no need to `RUN adduser`).
  - `--no-restore` after the cached restore layer; `/p:UseAppHost=false` shrinks the published output (no per-OS native launcher; `dotnet BaseApi.Service.dll` is the entrypoint).
  - No `HEALTHCHECK` in the Dockerfile — Phase 2 compose handles healthchecks via the existing `pg_isready` for Postgres and (in Wave A) an HTTP healthcheck against `/health/ready` for the service.
  - `.dockerignore` at repo root MUST exclude `tests/`, `**/bin`, `**/obj`, `**/.vs`, `.git`, `.planning/`, `*.md` at root (preserves build-cache hits, omits non-runtime artifacts).

- **D-14:** **`compose.yaml` updates** in Wave A foundation plan:
  - **Drop `profiles: [phase-8]`** from the `baseapi-service` block so the service spins up under default `docker compose up`.
  - **Replace** `image: baseapi-service:phase-8-placeholder` with:
    ```yaml
    build:
      context: .
      dockerfile: Dockerfile
    ```
  - **Add environment block:**
    ```yaml
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ConnectionStrings__Postgres: "Host=postgres;Port=5432;Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"
    ```
    `Host=postgres;Port=5432` honors Phase 2 D-07 (Docker-internal connection path) and the existing base `appsettings.json`. The dollar-sign substitution reuses the `.env` file already present (Phase 2 D-04).
  - **Add port mapping:** `ports: ["8080:8080"]`.
  - **Add healthcheck (recommended):**
    ```yaml
    healthcheck:
      test: ["CMD", "wget", "--spider", "-q", "http://localhost:8080/health/ready"]
      interval: 10s
      timeout: 3s
      retries: 5
      start_period: 30s
    ```
    `start_period` covers migration apply time on first boot. `wget` is present in the .NET 8 aspnet image; if not, fall back to `curl --fail`.
  - **Preserve** `depends_on: postgres: { condition: service_healthy }` (Phase 2 D-08).
  - **Preserve** `otel-collector` dependency: add `depends_on: otel-collector: { condition: service_started }` (Phase 5).

- **D-15:** **Migration runner: swap `StartupCompletionService.StartAsync` body** to apply migrations before flipping the startup gate. Phase 5 D-06 explicitly designed for this 1-line substitution:
  ```csharp
  // Phase 5 (current):
  public async Task StartAsync(CancellationToken ct)
  {
      _gate.MarkReady();
      await Task.CompletedTask;
  }

  // Phase 8 (swap target):
  public async Task StartAsync(CancellationToken ct)
  {
      try
      {
          using var scope = _scopeFactory.CreateScope();
          var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
          await db.Database.MigrateAsync(ct);
          _gate.MarkReady();
      }
      catch (Exception ex)
      {
          _logger.LogCritical(ex, "Database migration failed on startup; readiness probe will remain unhealthy.");
          // DO NOT rethrow — IHostedService.StartAsync throwing crashes the process; PERSIST-10 forbids that.
          // DO NOT call _gate.MarkReady() — startup probe must remain Unhealthy per HEALTH-01.
      }
  }
  ```
  Constructor injection on `StartupCompletionService` must add `IServiceScopeFactory _scopeFactory` and `ILogger<StartupCompletionService> _logger` (the `AppDbContext` is Scoped — Phase 3 PERSIST-15 — so an explicit scope is required from a hosted service that runs at root).

- **D-16:** **Migration failure UX:** Process stays alive (no crash, PERSIST-10). `/health/live` returns 200 (process responsive, HEALTH-02). `/health/startup` returns 503 (gate never flipped, HEALTH-01 wording: "Healthy after DI is built AND migrations have applied" — the AND fails). `/health/ready` returns 503 (Phase 5 D-09 tag predicate `ready` includes both `NpgSql` and `StartupHealthCheck`; the startup half is unhealthy). Operators see `LogCritical` entry with stack + correlationId (NOT exposed in body — Phase 4 T-04-LEAK). Compose `depends_on: { condition: service_healthy }` on downstream services will block; explicit operator intervention required (fix migration, `docker compose restart baseapi-service`).

  **Integration test for D-16:** Plan 08-08 ships one fact that constructs a `Phase8WebAppFactory` configured with a bad connection string (e.g., wrong port `5434`), boots the service, and asserts: `(a)` process responsive (HTTP probe to `/health/live` returns 200), `(b)` startup unhealthy (`/health/startup` returns 503), `(c)` no unhandled exception thrown to the hosting platform.

### Claude's Discretion

- **StepEntryCondition enum DB mapping** — int column (EF Core default) vs string column (`HasConversion<string>()`). Planner picks; default `int` is fine for status enums where readability rarely matters. ENTITY-06 specifies the numeric values (`PreviousProcessing=0 ... Never=5`); preserving them via int storage avoids breakage if a future migration adds new variants.
- **Junction PK + FK column naming** — Phase 3 D-05 snake_case naming convention auto-resolves to `step_id` + `next_step_id` for `StepNextSteps`, etc. Planner picks composite-PK order (likely alphabetical) and FK constraint names (Phase 4 ERROR-11 convention: `fk_{table}_{column}` and `uq_{table}_{column}`).
- **Junction DELETE cascade behavior** — SC#5 says "deleting a Step that a Workflow references returns 422 (FK Restrict on WorkflowEntryStep.StepId)" — so the dependent side of every junction MUST be `OnDelete(DeleteBehavior.Restrict)`. Planner verifies the principal side default (typically Cascade for owned children).
- **jsonb column convention** — `Schema.Definition` + `Assignment.Payload` ENTITY-03 + ENTITY-07 require jsonb. Planner picks between `.HasColumnType("jsonb")` in the EntityTypeConfiguration vs Npgsql provider auto-conventions. The explicit `HasColumnType` is more visible in migrations and is the conventional choice.
- **Validator inheritance pattern** — Phase 6 VALID-20 + D-08 lock `Include(new BaseDtoValidator<T>())`. Planner picks per-validator whether to put entity-specific rules in the same constructor as the Include() or in a separate RuleSet (likely the former for readability).
- **Mapperly attribute pattern when ReadDto carries audit fields** — Phase 6 D-08 amended dual-mechanism + Plan 06-02 deviation: ToEntity + Update need `[MapperIgnoreTarget]` on `Id` + 4 audit fields (Mapperly's RequiredMappingStrategy=Both fires RMG012 otherwise). For Phase 8, ReadDto **carries** all audit fields (HTTP-07 requires "every entity field"), so `[MapperIgnoreSource]` on `ToRead` is **NOT** needed (the Phase 6 TestReadDto pattern of stripping audit fields does not apply here). Planner verifies; if RMG020 still fires on ToRead, re-add per-property `[MapperIgnoreSource]` selectively.
- **Per-validator stress test density** — D-07 locks 5 [Fact] smoke per entity. Validator stress tests (SemVer, SHA-256, cron-shape, JSON Schema invalid, Payload-too-large) MAY use `[Theory]` with `[InlineData]` where parameterized data genuinely reduces duplication. Planner picks per-validator class.
- **Per-entity service file existence for non-overriding entities** — Discretion item, see D-12. If the planner finds a clean open-generic DI registration that avoids the marker class boilerplate, prefer it; otherwise ship 5 marker classes (3 empty, 2 with SyncJunctionsAsync).

### Folded Todos

None — no pending todos matched Phase 8 scope.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase 8 boundary + success criteria + REQ-IDs
- `.planning/ROADMAP.md` §"Phase 8: Entity Build-Out + Migrations + Docker Runtime + Tests" — Goal, 41 REQ-IDs, 6 success criteria, parallelization guidance.
- `.planning/REQUIREMENTS.md` PERSIST-01/08/09/10/12/13/14, ENTITY-03..10, HTTP-04..07/11/12, VALID-08..20, INFRA-05, TEST-01..06 — locked acceptance criteria.
- `.planning/PROJECT.md` — single-API modular-monolith pattern; `Service:Name="sk-api"`, `Service:Version="3.2.0"`; entity domain framing (Schema→Processor→Step→Assignment→Workflow); FK relationship topology forces migration ordering.

### Phase 7 — generic HTTP base + composition root (load-bearing for D-09..D-12)
- `.planning/phases/07-generic-http-base-composition-root/07-CONTEXT.md`
  - **D-04** — GET list returns bare `IReadOnlyList<TRead>` (no paged envelope in v1).
  - **D-09 / D-10** — `SyncJunctionsAsync(TEntity, TCreate?, TUpdate?, ct)` signature; 4 of 5 v1 entities inherit no-op; Step + Workflow override (D-12).
  - **D-11** — Locked 6-step CreateAsync order (validate → ToEntity → repo.Add → SyncJunctionsAsync → SaveChangesAsync → ToRead); Phase 8 must not deviate.
  - **D-13** — `AddBaseApi<TDbContext>` already wires `AddBaseApiValidation(typeof(TDbContext).Assembly)` + `AddBaseApiMapping(typeof(TDbContext).Assembly)`; Phase 8's `AppDbContext.Assembly` is `BaseApi.Service` so all 5 new validators + 5 new mappers are auto-discovered with zero AddBaseApi changes.
  - **D-14** — `AddBaseApiPersistence` reads `cfg.GetConnectionString("Postgres")`; appsettings key is locked.
  - **`07-SUMMARY.md`** — 98/98 facts GREEN baseline; Phase 8 starts from this point.

### Phase 6 — validation + mapping seam (load-bearing for D-09, D-11, D-12, Claude's Discretion)
- `.planning/phases/06-validation-mapping-base/06-CONTEXT.md`
  - **D-08 amended** — 3-method Mapperly attribute coverage (ToEntity / Update / ToRead); Phase 8 mapper classes inherit this pattern but `[MapperIgnoreSource]` on ToRead becomes unnecessary because Phase 8 ReadDtos carry audit fields (see Claude's Discretion above).
  - **D-14 / D-15** — `AddBaseApiValidation` + `AddBaseApiMapping` public DI extensions already absorbed by `AddBaseApi`; assembly scan picks up Phase 8 types automatically.
  - **Plan 06-02 deviation note** — RMG012 fires on ToEntity + Update; RMG020 fires on ToRead. Promotion is LIVE across all 3 methods. Phase 8 must verify each mapper compiles cleanly under TreatWarningsAsErrors=true.

### Phase 5 — observability + health probes (load-bearing for D-15, D-16)
- `.planning/phases/05-observability-health-probes/05-CONTEXT.md`
  - **D-06** — `StartupCompletionService` swap-target ("clean 1-line substitution") — Phase 8 D-15 implements this.
  - **D-09** — Health probe tag predicates: `live` excludes DB; `ready` includes NpgSql + StartupHealthCheck; `startup` includes StartupHealthCheck. Phase 8 D-16 relies on this composition.
  - **Plan 05-02 Pattern C** — env-var-in-ctor for connection-string override in WebApplicationFactory tests; Phase 8 D-08 inherits this.
  - **D-11 cleanup discipline** — `tests/.otel-out/telemetry.jsonl` must be absent after suite finishes; Phase 8 inherits via `[assembly: AssemblyFixture(typeof(OtelEndOfSuiteCleanup))]`.

### Phase 4 — cross-cutting middleware + error handling (load-bearing for SC#2, SC#3, SC#5, D-16)
- `.planning/phases/04-cross-cutting-middleware-error-handling/04-CONTEXT.md`
  - **D-06** — IExceptionHandler chain order: NotFound → Validation → DbUpdate → Fallback. Phase 8 throws into this chain; never catches.
  - **D-04** — `AddProblemDetails` customizer injects `correlationId` + `instance` into ALL ProblemDetails responses. Phase 8 verifies via SC#5 422 + SC#2 409 + SC#3 400 facts.
  - **`PostgresExceptionMapper` (Option A regex)** — preserves `_id` suffix in field name; 23503 → 422 with offending FK field; 23505 → 409 with offending field.
  - **ERROR-11 constraint naming convention** — `fk_processor_input_schema_id`, `uq_processor_source_hash`. Phase 8 EntityTypeConfiguration must declare these names explicitly.

### Phase 3 — EF Core persistence base (load-bearing for D-10, D-05, D-07)
- `.planning/phases/03-ef-core-persistence-base/03-CONTEXT.md`
  - **D-05** — `UseSnakeCaseNamingConvention()` BEFORE first migration. Phase 8 IS the first migration — snake_case applies to every table, column, index, FK constraint name.
  - **D-15** — Per-class throwaway DB + byte-identical `psql \l` BEFORE/AFTER snapshot; Phase 8 D-05/D-06 explicitly preserve this.
  - **D-16** — Phase 3 deliberately deferred the first migration to Phase 8 — "Phase 8 still owns first migration so snake_case + xmin apply to the very first schema". Wave C Plan 08-07 honors this.
  - **D-18** — 3 consecutive GREEN runs over the full suite at phase-completion gate (regression cadence). Phase 8 Plan 08-08 inherits.
  - **PERSIST-16 (xmin shadow concurrency token)** — Phase 3 `BaseDbContext.OnModelCreating` iterates `BaseEntity` subclasses; Phase 8's 5 entities all get xmin automatically (D-10 ordering: `ApplyConfigurationsFromAssembly` → `base.OnModelCreating`).

### Phase 2 — Postgres + Docker Compose (load-bearing for D-14)
- `.planning/phases/02-postgres-docker-compose/02-CONTEXT.md`
  - **D-04** — `.env` file: `POSTGRES_DB=stepsdb`, `POSTGRES_USER=postgres`, `POSTGRES_PASSWORD=postgres`. Phase 8 D-14 reuses via `${...}` substitution.
  - **D-07** — Base `appsettings.json` connection string uses `Host=postgres;Port=5432` (Docker-internal). Phase 8 D-14 environment block matches.
  - **D-08** — `phase-8` profile gate on `baseapi-service` block, plus placeholder image `baseapi-service:phase-8-placeholder`. Phase 8 D-14 explicitly drops both.
  - **D-12** — `postgres:17-alpine` image pin; Phase 8 PostgresFixture connects to this (`localhost:5433` external port).
  - **Plan 02-02 fix-forward** — placeholder service block with profiles + image lines was designed for the explicit hand-off Wave A is now making.

### Phase 1 — repository scaffold (load-bearing for D-09, D-13)
- `.planning/phases/01-repository-scaffold/01-CONTEXT.md` + `01-RESEARCH.md`
  - **D-02 / Directory.Build.props** — `TreatWarningsAsErrors=true` globally; no `<GenerateDocumentationFile>` enabled (CS1591 would fatal); Phase 8 mappers/validators must compile clean.
  - **D-05** — CPM (Directory.Packages.props) is single source of truth; Phase 8 adds new package pins for JsonSchema.Net, Cronos (VALID-08 + VALID-19).
  - **D-08** — `.gitkeep` placeholder folders at scaffold time include `Features/` and `Persistence/Configurations/` — Phase 8 fills both per D-09 + D-10.

### Out-of-scope confirmations
- REQUIREMENTS.md "Out of Scope" — pagination, auth, soft delete, etc. are all excluded; downstream agents MUST NOT add capabilities not listed in Phase 8's 41 REQ-IDs.
- TEST-03 + TEST-04 ARE listed in Phase 8 scope (REQUIREMENTS.md), but D-05 + D-06 of this CONTEXT defer them to v2. The Wave A foundation plan MUST update REQUIREMENTS.md to reflect this deferral (move to "v2 Requirements" + add Out of Scope row with reason).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets

- **`src/BaseApi.Service/Features/.gitkeep`** — empty Features/ folder from Phase 1 scaffold; Phase 8 lands 5 entity subfolders here per D-09.
- **`src/BaseApi.Service/Persistence/Configurations/.gitkeep`** — empty folder from Phase 1 scaffold; Phase 8 lands 8 IEntityTypeConfiguration classes here per D-10.
- **`src/BaseApi.Service/AppDbContext.cs`** (Phase 7) — currently empty placeholder. Wave C Plan 08-07 adds 5 `DbSet<{Entity}Entity>` + 3 `DbSet<{Junction}>` properties + the `OnModelCreating` override per D-10.
- **`src/BaseApi.Service/Program.cs`** (Phase 7) — already minimal; Phase 8 does NOT touch it. All wiring goes through AddBaseApi composition + auto-discovered validators/mappers.
- **`src/BaseApi.Core/Controllers/BaseController.cs`** (Phase 7) — abstract generic; Phase 8 concrete controllers are empty-body inheritors.
- **`src/BaseApi.Core/Services/BaseService.cs`** (Phase 7) — abstract generic with virtual `SyncJunctionsAsync`; Phase 8 StepService + WorkflowService override the hook.
- **`src/BaseApi.Core/Validation/BaseDtoValidator.cs`** (Phase 6) — concrete Phase 8 validators inherit via `Include(new BaseDtoValidator<T>())` per VALID-20.
- **`src/BaseApi.Core/Mapping/IEntityMapper.cs`** (Phase 6) — concrete Phase 8 mappers implement this 3-method contract; Mapperly source-gen.
- **`src/BaseApi.Core/Persistence/BaseDbContext.cs`** (Phase 3) — `OnModelCreating` iterates `BaseEntity` subclasses for xmin; Phase 8's 5 entities get xmin automatically.
- **`src/BaseApi.Core/Health/StartupCompletionService.cs`** (Phase 5) — Phase 8 swaps the body per D-15.
- **`tests/BaseApi.Tests/Persistence/PostgresFixture.cs`** (Phase 3) — per-class throwaway DB; Phase 8 D-08 reuses verbatim.
- **`tests/BaseApi.Tests/Composition/Phase7WebAppFactory.cs`** (Phase 7) — Phase 8 D-08 lands `Phase8WebAppFactory.cs` modeled after this (DROP the Phase7TestDbContext rewiring — AppDbContext now has real DbSets so the abstract Repository<TEntity> binds naturally).
- **`tests/BaseApi.Tests/Composition/ProductionWebAppFactory.cs`** (Phase 7) — Phase 8 reuses for any Production-environment fact (e.g., Swagger 404 in Prod still verified).
- **`compose.yaml`** (Phase 2) — `baseapi-service` block exists with `profiles: [phase-8]` + placeholder image; Phase 8 Wave A amends per D-14.
- **`Directory.Packages.props`** (Phase 1) — Phase 8 Wave A adds pins for JsonSchema.Net (VALID-08) + Cronos (VALID-19); planner picks specific versions during research.
- **`.env`** (Phase 2) — env-var substitution targets for compose; reused by D-14 environment block.

### Established Patterns

- **DI extension naming:** `AddBaseApi*` for service-side, `UseBaseApi*` for builder-side (Phase 7 D-13). Phase 8 does not add new top-level extensions.
- **File-per-type with sealed classes:** Concrete `BaseEntity` subclasses, `IEntityTypeConfiguration<T>`, validators, mappers, controllers, services all `internal sealed` (or `public sealed` where cross-assembly visibility is required, e.g., DTOs that the controller's `[FromBody]` deserializes).
- **Snake_case DB naming** (Phase 3 D-05) auto-applies; no per-property override needed.
- **xUnit v3 + TestContext.Current.CancellationToken** flowed through every async call site (Phase 3 deviation #4; xUnit1051 analyzer escalation under TreatWarningsAsErrors=true).
- **Per-class throwaway PG DB** + byte-identical `psql \l` snapshot proof (Phase 3 D-15).
- **`[ApiController]` + `[ApiVersion("1.0")]` + `[Route("api/v{version:apiVersion}/[controller]")]`** inherited from `BaseController` — concrete controllers ARE the URL prefix via the `[controller]` token convention (e.g., `SchemasController` → `/api/v1/schemas`).
- **3 consecutive GREEN runs** as the regression gate (Phase 3 D-18) — Wave C Plan 08-08 inherits.
- **`PrivateAssets="all"` + `ExcludeAssets="runtime"`** on any csproj referencing Riok.Mapperly (Phase 6 D-13) — Phase 8 verifies this on `BaseApi.Service.csproj` and `BaseApi.Tests.csproj`.
- **Phase 4 `WebAppFactory` unsealed → subclass per test concern** (Phase 6) — Phase 8 inherits via `Phase8WebAppFactory : WebAppFactory`.

### Integration Points

- **`AppDbContext.cs`** — empty placeholder; Wave C Plan 08-07 adds 8 DbSet properties + OnModelCreating override (D-10).
- **`compose.yaml` `baseapi-service` block** — Wave A Plan 08-01 mutates (D-14): drop profile, switch image to build:., add environment + ports + healthcheck.
- **`StartupCompletionService.cs`** — Wave C Plan 08-07 swaps body to apply migrations (D-15). Constructor injection changes from `(IStartupGate gate)` to `(IStartupGate gate, IServiceScopeFactory scopeFactory, ILogger<StartupCompletionService> logger)`.
- **`Directory.Packages.props`** — Wave A or Wave B (planner picks) adds JsonSchema.Net + Cronos package pins. Phase 1 D-05 front-loading would have liked these in Phase 1, but they were intentionally deferred to whoever first needs them; Phase 8 is that whoever.
- **`REQUIREMENTS.md`** — Wave A Plan 08-01 amends to move TEST-03 + TEST-04 to v2 with reasoning (D-05, D-06).
- **`Dockerfile` + `.dockerignore` at repo root** — both new in Wave A Plan 08-01.

</code_context>

<specifics>
## Specific Ideas

- **Cross-entity error-mapping test set (Wave C Plan 08-08, exactly 4 facts per TEST-06 floor):**
  1. `Create_Duplicate_SourceHash_Returns409`: POST two Processors with the same `sourceHash` → first 201, second 409; assert `detail` contains `source_hash` or `uq_processor_source_hash`. Proves Phase 4 PostgresExceptionMapper 23505→409.
  2. `Create_Workflow_Non_Existent_EntryStepId_Returns422`: POST Workflow with `entryStepIds=[<random Guid>]` → 422; assert `detail` references the FK column. Proves Phase 4 PostgresExceptionMapper 23503→422 across junction tables.
  3. `Delete_Step_Referenced_By_Workflow_Returns422`: POST Step + POST Workflow referencing it; DELETE Step → 422 (FK Restrict on `WorkflowEntryStep.StepId`). Proves DELETE cascade behavior.
  4. `Create_Schema_Invalid_JsonSchema_Returns400` + sub-assertion: POST Schema with `definition='{"$ref":"https://attacker.example/schema.json"}'` → 400 with field-level error AND no outbound HTTP request observed (SSRF guard verified via test-time HttpClient interception or by JsonSchema.Net configuration assertion). Proves VALID-08 + VALID-09.

- **Migration-failure integration fact (Wave C Plan 08-08):** boot a `Phase8WebAppFactory` configured with `ConnectionStrings__Postgres="Host=localhost;Port=5434;..."` (wrong port → connection refused → MigrateAsync throws). Assert: `/health/live` 200; `/health/startup` 503; `/health/ready` 503; process responsive throughout. Proves D-16 + PERSIST-10.

- **Entity FK topology (planner uses this for IEntityTypeConfiguration FK declarations):**
  - `ProcessorEntity.InputSchemaId` (nullable Guid?) → Schema, `OnDelete(DeleteBehavior.SetNull)` likely (planner verifies).
  - `ProcessorEntity.OutputSchemaId` (nullable Guid?) → Schema, similar.
  - `StepEntity.ProcessorId` (Guid) → Processor, `OnDelete(DeleteBehavior.Restrict)`.
  - `StepNextSteps.StepId` + `StepNextSteps.NextStepId` (both Guid) → Step, `OnDelete(DeleteBehavior.Restrict)` on both.
  - `AssignmentEntity.StepId` → Step; `AssignmentEntity.SchemaId` → Schema; `Restrict`.
  - `WorkflowEntrySteps.WorkflowId` → Workflow (Cascade — junction lifecycle owned by Workflow); `WorkflowEntrySteps.StepId` → Step (Restrict — SC#5).
  - `WorkflowAssignments.WorkflowId` → Workflow (Cascade); `WorkflowAssignments.AssignmentId` → Assignment (Restrict).

- **Wave A REQUIREMENTS.md amendment template:**
  ```markdown
  Move from §"v1 Requirements" / Testing:
  - TEST-03: `Testcontainers.PostgreSql` for real Postgres in tests
  - TEST-04: `Respawn` (or equivalent) used to reset DB between tests

  Move to §"v2 Requirements" / Hardening:
  - TEST-03: `Testcontainers.PostgreSql` for real Postgres in tests. *Deferred to v2 per Phase 8 D-05: PostgresFixture pattern (per-class throwaway DB on the Phase 2 compose-running Postgres at localhost:5433) is proven across 98 facts spanning Phases 3-7; Testcontainers cold-start adds ~3-5s/fixture on Windows Docker Desktop with no behavioral gain at v1 scale. Migration when CI requires self-contained test runs.*
  - TEST-04: `Respawn` (or equivalent) used to reset DB between tests. *Deferred to v2 per Phase 8 D-06: per-class throwaway DBs preserve the Phase 3 D-15 byte-identical `psql \l` BEFORE/AFTER no-leak proof; Respawn would invalidate that proof. Revisit if per-fact reset cost becomes noticeable at higher fact counts.*

  Also amend the traceability table: TEST-03 phase → v2; TEST-04 phase → v2; Phase 8 row → 39 REQ-IDs (was 41).
  ```

- **The 4 v1 entities that inherit Phase 7 BaseService no-op SyncJunctionsAsync** (Phase 7 D-09/D-10 wording): SchemaEntity, ProcessorEntity, AssignmentEntity, TestEntity. Phase 8 contributes 4 of those (the TestEntity is Phase 6's). The two that override are StepEntity (NextStepIds → StepNextSteps) and WorkflowEntity (EntryStepIds → WorkflowEntrySteps; AssignmentIds → WorkflowAssignments).

</specifics>

<deferred>
## Deferred Ideas

- **TEST-03 (Testcontainers.PostgreSql) → v2.** Migrate when CI requires self-contained test runs (no Docker compose prereq). PostgresFixture pattern is the v1 baseline.
- **TEST-04 (Respawn) → v2.** Revisit if per-class throwaway-DB cost (~50ms CREATE/DROP) becomes painful at higher fact counts (e.g., >500 facts). Hybrid path (throwaway DB per class + Respawn within class) stays open.
- **Per-validator stress test density via xUnit Theory + InlineData.** Phase 8 will produce these organically inside each entity's validator test class. Locking the Theory convention is a refactor candidate for Phase 9+ when 50+ validator stress cases accumulate.
- **`HEALTHCHECK` directive baked into Dockerfile** (vs compose-only). Useful for Kubernetes / non-compose deployments. Backlog candidate.
- **Retry-with-backoff on MigrateAsync failure.** Useful when Postgres starts slower than `depends_on: { condition: service_healthy }` predicts. Current compose healthcheck already prevents the race for the v1 single-instance topology. v2 candidate for multi-instance deployments + advisory-lock coordination (INFRA-08).
- **Per-API-version controller pattern** (e.g., adding `[ApiVersion("2.0")]` siblings). Not in v1 scope but Phase 7 D-17 left the door open.
- **Bulk CRUD endpoints (POST [], PATCH []).** Listed in Out of Scope (PROJECT.md). Backlog candidate per concrete entity need.
- **`Paged<TRead>` envelope for GET list** (HTTP-17 v2). Phase 7 D-04 left the door open; Phase 8 inherits bare-list contract.
- **Open-generic DI registration for non-overriding entity services** (replaces D-12 marker classes). If a clean pattern emerges (e.g., custom IServiceCollection extension that constructs a closed-generic sealed type at runtime), revisit during Phase 9+.
- **`UseRouting`/`UseHttpsRedirection`/`UseCors` placement in UseBaseApi pipeline.** Phase 7 D-19 omitted CORS (no REQ-ID); browser-tooling devs may need a Dev-only permissive policy in a later phase.
- **`HasConversion<string>()` for `StepEntryCondition` enum DB mapping.** If postgres `\d` inspectability becomes important to operators, switch from int default. Discretion item per Phase 8 D-12 / Claude's Discretion.

</deferred>

---

*Phase: 08-entity-build-out-migrations-docker-runtime-tests*
*Context gathered: 2026-05-27*
