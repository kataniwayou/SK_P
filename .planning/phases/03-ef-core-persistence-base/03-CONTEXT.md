# Phase 3: EF Core Persistence Base - Context

**Gathered:** 2026-05-26
**Status:** Ready for planning

<domain>
## Phase Boundary

Build the persistence foundation in `BaseApi.Core` — `BaseEntity` (abstract, no table), `BaseDbContext` (abstract, no DbSets), `AuditInterceptor` (`ISaveChangesInterceptor`), snake_case convention via `EFCore.NamingConventions`, and the generic `Repository<TEntity>` — before any migration is generated, so the convention applies to the very first schema. This phase produces compilable, unit-testable infrastructure inside `BaseApi.Core` plus four xUnit v3 fact tests in the existing `BaseApi.Tests` project that pin SC#1-4 against the running Phase 2 Postgres container.

Out of this phase: the concrete `AppDbContext` (Phase 8 — owns the 5 entity DbSets + 3 junction DbSets), `IEntityTypeConfiguration<T>` files per entity (Phase 8), the `InitialCreate` migration (Phase 8), wiring `AddDbContext<AppDbContext>` in `Program.cs` (Phase 8), the startup migration runner + readiness probe gating (Phase 5/8), `DbUpdateException` → 422/409 mapping (Phase 4), `DbUpdateConcurrencyException` → 409 mapping (Phase 4 — new, cross-phase impact of D-03 below), FluentValidation / Mapperly wiring (Phase 6), composition-root `AddBaseApi`/`UseBaseApi` extensions (Phase 7).

</domain>

<decisions>
## Implementation Decisions

### Guid Identity

- **D-01:** Guid values for `BaseEntity.Id` are stamped **client-side by `AuditInterceptor`** on `EntityState.Added` if `Id == Guid.Empty`. No `gen_random_uuid()` Postgres default; no `HasDefaultValueSql`; no `ValueGeneratedOnAdd` annotation. Rationale: the locked design (PROJECT.md, ARCHITECTURE.md) uses scalar `Guid` FK columns + explicit junction entities with **no** navigation properties between entities. A `POST /workflows` in Phase 8 must add the `WorkflowEntity` AND junction rows (`WorkflowEntrySteps`, `WorkflowAssignments`) in a single `SaveChanges` transaction — the junction `WorkflowId` FK must be a real value at `Add` time, not `Guid.Empty` resolved post-flush. Client-side generation makes the Id available immediately, keeps single-transaction semantics, and centralises all server-controlled fields (Id, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy) in one interceptor.
- **D-02:** `AuditInterceptor` **honors a caller-set non-empty `Id`** — it only generates when `Id == Guid.Empty`. This lets tests pin predictable Guid constants (`TestGuids.Workflow1`) and lets internal seeders use known values. CreateDtos exclude `Id` per HTTP-05, so HTTP requests never reach the interceptor with a populated Id — production-path generation is always interceptor-driven; only test/scratch code populates Id pre-Add.

### Concurrency Control

- **D-03:** `BaseDbContext.OnModelCreating` wires a Postgres `xmin` shadow concurrency token on **every** `BaseEntity` subclass via a model-builder iteration. Shape:
  ```csharp
  foreach (var entityType in modelBuilder.Model.GetEntityTypes()
      .Where(t => typeof(BaseEntity).IsAssignableFrom(t.ClrType)))
  {
      modelBuilder.Entity(entityType.ClrType)
          .Property<uint>("xmin")
          .HasColumnName("xmin")
          .HasColumnType("xid")
          .ValueGeneratedOnAddOrUpdate()
          .IsConcurrencyToken();
  }
  ```
  Junction entities (`StepNextSteps`, `WorkflowEntrySteps`, `WorkflowAssignments` — Phase 8) are excluded because they are DELETE+INSERT'd as a set, never UPDATE'd, so a concurrency token would be noise.
- **D-03a (cross-phase impact, surface in CONTEXT.md but actioned in Phase 4):** A new error mapping rule must land in Phase 4: `DbUpdateConcurrencyException` → HTTP 409 (`Conflict`) with detail "The resource was modified by another request; reload and retry." Current ERROR-04/05 cover only Postgres SQLSTATE 23503/23505 — `DbUpdateConcurrencyException` is an EF-layer exception, not a SQLSTATE. The Phase 4 verification suite must add a lost-update test (two PUTs racing on the same row → second returns 409 with `correlationId`).
- **D-03b (cross-phase impact, surface in CONTEXT.md):** A new requirement `PERSIST-16: BaseEntity rows carry a Postgres xmin shadow concurrency token mapped via IsConcurrencyToken()` should be appended to REQUIREMENTS.md during Phase 3 planning. Coverage rolls into Phase 3's 10-requirement count (becomes 11) and adds one Phase 4 error-mapping rule (Phase 4 count becomes 15).

### Generic Repository

- **D-04:** `IRepository<TEntity> where TEntity : BaseEntity` exposes exactly **5 async methods** — no `IQueryable` leakage, no `ExistsAsync` helper, no `Where(predicate)` overload. Signatures the planner will derive (subject to small adjustments at plan time):
  ```csharp
  Task<TEntity?> GetAsync(Guid id, CancellationToken ct);        // null if missing; Service throws NotFoundException (ERROR-06)
  Task<IReadOnlyList<TEntity>> ListAsync(CancellationToken ct);  // all rows; no paging in v1 (HTTP-17..19 are v2)
  Task AddAsync(TEntity entity, CancellationToken ct);           // tracker only; does NOT call SaveChanges
  void Update(TEntity entity);                                   // tracker only; sync shape (no I/O); does NOT call SaveChanges
  Task DeleteAsync(Guid id, CancellationToken ct);               // load-then-remove (preserves D-03 xmin check); does NOT call SaveChanges
  ```
  Generic constraint is `where TEntity : BaseEntity` — narrows the repository to audit-stamped entities. Junction entities are non-`BaseEntity` and accessed via raw `DbContext.Set<TJunction>()` from the entity-specific Service in Phase 8 (junction sync logic lives there per ARCHITECTURE.md, not in a generic repository).
- **D-05:** **Service owns `SaveChangesAsync`**, not Repository. Repositories mutate the change tracker; Services compose multi-entity transactions (Workflow + 2 junction tables = 1 SaveChanges = 1 Postgres transaction). This matches the unit-of-work pattern, keeps `AuditInterceptor` firing once per transaction boundary, and lets Phase 4's `DbUpdateException` mapper catch the failure at a single point. Implementation note: `BaseService<...>` (Phase 7) holds both `IRepository<TEntity>` and `DbContext` (or `BaseDbContext`-derived) — the DbContext injection is what enables raw `Set<TJunction>()` access for junction sync.

### Audit Interceptor

- **D-06:** `AuditInterceptor : ISaveChangesInterceptor` is registered as **Singleton** in DI (matches ARCHITECTURE.md L225 — `services.AddSingleton<AuditInterceptor>()`) and depends on `IHttpContextAccessor` (Singleton-safe wrapper for the scoped `HttpContext`) and `TimeProvider` (Singleton).
- **D-07:** `AuditInterceptor` depends on **`TimeProvider`** (.NET 8 BCL abstraction) — not `DateTime.UtcNow` direct, not a custom `IClock`. Production registers `builder.Services.AddSingleton(TimeProvider.System)`. Tests can inject `FakeTimeProvider` (from `Microsoft.Extensions.TimeProvider.Testing`) to pin time for deterministic audit-timestamp assertions. The UTC-only rule (Pitfall 1) is still enforced — the interceptor calls `_clock.GetUtcNow().UtcDateTime` (always returns `Kind=Utc`).
- **D-08:** Audit-stamping logic (in `SavingChangesAsync` override):
  - On `EntityState.Added`: stamp `CreatedAt = _clock.GetUtcNow().UtcDateTime`, `UpdatedAt = same`, `CreatedBy = _httpContextAccessor.HttpContext?.User?.Identity?.Name`, `UpdatedBy = same`, and `Id = Guid.NewGuid()` IF `Id == Guid.Empty` (D-01/D-02).
  - On `EntityState.Modified`: stamp `UpdatedAt = _clock.GetUtcNow().UtcDateTime`, `UpdatedBy = _httpContextAccessor.HttpContext?.User?.Identity?.Name`. Do NOT touch `CreatedAt`/`CreatedBy`/`Id`.
  - Non-HTTP execution paths (background work, migrations, scratch console, unit tests without `HttpContext`) get `CreatedBy = null` / `UpdatedBy = null` — PERSIST-04 explicitly locks this; no crash, no warning log.

### BaseDbContext

- **D-09:** `BaseDbContext` is **abstract** and lives in `src/BaseApi.Core/Persistence/BaseDbContext.cs`. It has **no `DbSet<T>` properties** (those belong on the concrete `AppDbContext` in Phase 8 per ARCHITECTURE.md). It contains:
  - `protected BaseDbContext(DbContextOptions options) : base(options) { }` constructor.
  - `protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)` — calls `UseSnakeCaseNamingConvention()` (PERSIST-05) AND adds `AuditInterceptor` via `optionsBuilder.AddInterceptors(...)`. **Defense-in-depth**: the snake_case + interceptor wiring is duplicated in the `AddBaseApi<TDbContext>()` extension (Phase 7) so a consumer that bypasses `OnConfiguring` still gets the conventions; but defining them here means the test-only `TestDbContext : BaseDbContext` in Phase 3's verification suite is correctly configured without an `AddBaseApi` composition root (which Phase 7 doesn't deliver until later).
  - `protected override void OnModelCreating(ModelBuilder modelBuilder)` — the D-03 `xmin` iteration block (no entity-specific config; that's Phase 8's `ApplyConfigurationsFromAssembly(...)`).
- **D-10:** `BaseDbContext.OnConfiguring` resolves `AuditInterceptor` from the DI container, **NOT** by `new AuditInterceptor(...)`. Pattern: pass `IServiceProvider` into `DbContextOptionsBuilder` via the `AddDbContext` overload (Phase 7's `AddBaseApi` does this). For Phase 3 test purposes, the test fixture builds a small `ServiceCollection` that registers `IHttpContextAccessor`, `TimeProvider.System`, and `AuditInterceptor`, then constructs the `TestDbContext` with options bound to that provider.
- **D-11:** Phase 3 does **NOT** call `AddDbContext<AppDbContext>` in `Program.cs`. `AppDbContext` doesn't exist until Phase 8. Phase 3's `Program.cs` is unchanged from Phase 1 D-10 — the scaffolded `WebApplication.CreateBuilder` + `AddControllers` + `MapControllers` + `Run` that 404s on every path. SC#4 (scoped lifetime verification) is satisfied inside the test fixture via a standalone `ServiceCollection` (or a derived `WebApplicationFactory<Program>` that overrides `ConfigureServices` to add a `TestDbContext`).

### Project Wiring (BaseApi.Core.csproj package references)

- **D-12:** `src/BaseApi.Core/BaseApi.Core.csproj` adds the following `<PackageReference>` entries (versions already pinned in `Directory.Packages.props` — no `Version=` attributes per CPM):
  - `Microsoft.EntityFrameworkCore` (8.0.27) — for `DbContext`, `ISaveChangesInterceptor`, `DbContextOptions`
  - `Microsoft.EntityFrameworkCore.Relational` (8.0.27) — for `ModelBuilder` extensions used by D-03
  - `Npgsql.EntityFrameworkCore.PostgreSQL` (8.0.10) — `UseNpgsql` lives here (used by Phase 7's `AddBaseApi`; consumed by Phase 3's test fixture)
  - `EFCore.NamingConventions` (8.0.3) — `UseSnakeCaseNamingConvention` extension method
  - `Microsoft.AspNetCore.Http.Abstractions` is in-box (transitively via `Microsoft.AspNetCore.App` framework reference). `BaseApi.Core` is `Microsoft.NET.Sdk` (class library), NOT `Microsoft.NET.Sdk.Web`, so `IHttpContextAccessor` requires either (a) explicit package ref to `Microsoft.AspNetCore.Http.Features` / `Microsoft.AspNetCore.Http.Abstractions`, or (b) adding `<FrameworkReference Include="Microsoft.AspNetCore.App" />` to `BaseApi.Core.csproj`. Researcher should determine the cleanest path; recommended is (b) `FrameworkReference` since `BaseApi.Core` is fundamentally ASP.NET-coupled (it owns middleware, controllers, health checks per ARCHITECTURE.md). Either path keeps zero `<Version>` attributes in the csproj.
- **D-13:** `tests/BaseApi.Tests/BaseApi.Tests.csproj` adds `<PackageReference>` entries for `Microsoft.EntityFrameworkCore`, `Microsoft.EntityFrameworkCore.Relational`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `EFCore.NamingConventions`, and `Microsoft.Extensions.TimeProvider.Testing` (the `FakeTimeProvider` package). The last package version is NOT yet in `Directory.Packages.props` — researcher resolves the current stable version and Phase 3's first build plan adds a `PackageVersion` entry alongside the existing 22 pins.

### Verification

- **D-14:** Verification target is the existing `tests/BaseApi.Tests/` xUnit v3 project (INFRA-01 says one test project). Four `[Fact]` tests map to ROADMAP SC#1-4:
  - **SC#1:** `Test_TestDbContext_EnsureCreated_ProducesSnakeCaseSchema` — constructs `TestDbContext : BaseDbContext` with a single `DbSet<TestEntity>` where `TestEntity : BaseEntity`; calls `await db.Database.EnsureCreatedAsync()`; queries Postgres `information_schema.columns` to assert `created_at`, `updated_at`, `created_by`, `updated_by` (snake_case), NOT `CreatedAt` etc.
  - **SC#2:** `Test_AuditInterceptor_StampsUtcTimestamps_OnInsert` — inserts a `TestEntity`, calls `SaveChangesAsync`, asserts `entity.CreatedAt.Kind == DateTimeKind.Utc` AND reads the row back via raw SQL and confirms the column type is `timestamp with time zone` (no `InvalidCastException`).
  - **SC#3:** `Test_AuditInterceptor_StampsCreatedBy_FromHttpContext` — TWO assertions in one test (or two facts):
    - With `IHttpContextAccessor.HttpContext.User.Identity.Name = "alice"` set via a stub: inserted row's `created_by == "alice"`.
    - With `IHttpContextAccessor.HttpContext = null`: inserted row's `created_by IS NULL` (no exception).
  - **SC#4:** `Test_DbContext_IsRegisteredScoped_InDI` — builds a `ServiceCollection`, calls `AddDbContext<TestDbContext>(...)`, builds the provider; resolves twice from the same `IServiceScope` (assert ReferenceEqual), resolves once each from two separate scopes (assert NOT ReferenceEqual).
- **D-15:** Tests connect to the running `docker compose up postgres` container at **`localhost:5433`** (Phase 2 D-01 host port). Test fixture's connection string is read from `appsettings.Development.json` (`Host=localhost;Port=5433;Database=stepsdb;Username=postgres;Password=postgres`) — same shape Phase 2 verified. Tests create a per-test throwaway database via a randomized name (e.g., `stepsdb_test_{Guid:N}`) using a `IAsyncLifetime` fixture that CREATEs the DB in `InitializeAsync` and DROPs in `DisposeAsync` — keeps Phase 3 tests from polluting the Phase 2 `stepsdb` namespace, but stays within REQ TEST-03's restriction (Testcontainers is Phase 8; we're using the **already-running** Phase 2 container, just creating throwaway logical databases inside it). NOT `EnsureDeletedAsync()` alone, because that races against other test runs sharing the container.
- **D-16:** Tests do **not** run migrations (`Database.MigrateAsync()`) — Phase 3 has no migrations to run; the schema is created via `EnsureCreatedAsync()` purely for SC#1 verification of the snake_case mapping. Phase 8 introduces the first migration; Phase 3 must not generate one (Pitfall 4: migration generated before snake_case is wired is destructive to retrofit).

### Plan Structure

- **D-17:** Phase 3 follows the Phase 1/2 split: **2 plans** are likely sufficient.
  - `03-01-PLAN.md` — Build plan: add csproj package refs (D-12, D-13); add `Directory.Packages.props` pin for `Microsoft.Extensions.TimeProvider.Testing`; create `BaseEntity.cs`, `BaseDbContext.cs`, `AuditInterceptor.cs`, `IRepository.cs`, `Repository.cs`; commit. Builds with 0 warnings (Phase 1 D-02 enforces this).
  - `03-02-PLAN.md` — Verification + smoke plan: create `TestDbContext.cs` + `TestEntity.cs` test scaffold; create the 4 fact tests (D-14); ensure the docker compose postgres is running before tests; run `dotnet test`; commit a SUMMARY documenting SC#1-4 GREEN.
  - Planner has discretion to split further (e.g., 03-01 just `BaseEntity` + csproj refs, 03-02 `BaseDbContext` + `AuditInterceptor`, 03-03 `Repository<T>`, 03-04 verification) IF the build plan grows unwieldy, but the ROADMAP "Parallelizable: no" call means a single executor processes them sequentially regardless.
- **D-18:** Plan 03-02 is a **verification plan** — same shape as 01-03 and 02-02 in prior phases: `autonomous: false`, evidence-only commits (`docs(03)` not `feat(03)`), SUMMARY documents command output verbatim, Deviations section captures any in-flight fixes against 03-01.

### Claude's Discretion

- Exact `Repository<T>.Update` signature: `void Update(TEntity entity)` (sync) vs `Task UpdateAsync(TEntity entity, CancellationToken ct)` (async for symmetry). EF Core's `DbSet<T>.Update` is sync (no I/O), so the sync shape is honest; planner picks whichever reads better.
- Whether `Repository<T>` exposes a constructor that takes `DbContext` directly OR a typed `TDbContext where TDbContext : BaseDbContext` generic parameter. Latter gives stronger type safety for Phase 7's `AddBaseApi<TDbContext>()` registration; former is simpler. Planner decides.
- Test database naming scheme — `stepsdb_test_{Guid:N}` vs `stepsdb_phase3_test_{timestamp}` etc. Behavior is identical; pick the one that reads better in `docker compose exec postgres psql -l` output during debugging.
- Whether the `xmin` shadow-property iteration uses `Where(t => typeof(BaseEntity).IsAssignableFrom(t.ClrType))` (clean) or `Where(t => !t.IsOwned() && t.ClrType.IsAssignableTo(typeof(BaseEntity)))` (defensive against owned types). Phase 3 has no owned types; either works.
- Whether `TestEntity` lives in the test project or a `BaseApi.Core` internal test-only namespace exposed via `InternalsVisibleTo`. Planner picks; latter is overkill for one type.
- Whether to add an XML doc comment on `BaseEntity` explicitly documenting Pitfall 1 ("`CreatedAt`/`UpdatedAt` must be UTC — see `AuditInterceptor`"). Strongly recommended but not load-bearing.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Phase Boundary + Acceptance Criteria
- `.planning/ROADMAP.md` § Phase 3 — Goal, Depends on (Phase 1), Requirements list (ENTITY-01, ENTITY-02, PERSIST-02, PERSIST-03, PERSIST-04, PERSIST-05, PERSIST-06, PERSIST-07, PERSIST-11, PERSIST-15), 4 Success Criteria (the testable definition of done).
- `.planning/REQUIREMENTS.md` § Persistence (PERSIST-02..PERSIST-07, PERSIST-11, PERSIST-15) and § Entity Model (ENTITY-01, ENTITY-02).
- `.planning/REQUIREMENTS.md` § Out of Scope — soft delete, pagination, multi-instance migration (these stay out for v1 — confirms the tight Repository surface D-04 and the v1 boundaries).

### Prior Phase Locks
- `.planning/PROJECT.md` — Key Decisions table (rows on Guid PKs, single AppDbContext, no nav properties, Mapperly, FluentValidation 12, RFC 7807 with `correlationId`). Confirms D-01..D-11 architectural choices.
- `.planning/phases/01-repository-scaffold/01-CONTEXT.md` — D-02 (TreatWarningsAsErrors enforces Phase 3 SC #1 zero-warning build), D-05 (NuGet pins for EF Core 8.0.27 / Npgsql 8.0.10 / EFCore.NamingConventions 8.0.3 already in `Directory.Packages.props`), D-10 (`Program.cs` scaffold shape Phase 3 does NOT modify), D-11 (xUnit v3 test project shape Phase 3 extends).
- `.planning/phases/01-repository-scaffold/01-CONTEXT.md` § Scaffold Depth D-08 — pre-created folders `Persistence/`, `Persistence/Interceptors/`, `Persistence/Repositories/`, `Entities/` are where Phase 3's new files land.
- `.planning/phases/02-postgres-docker-compose/02-CONTEXT.md` — D-01 (host port 5433 — Phase 3 tests connect there), D-02 (`appsettings.Development.json` is Port=5433 — Phase 3 reads it), D-04..D-07 (`.env` values — `stepsdb`/`postgres`/`postgres`), § Deferred Ideas "To Phase 3 (EF Core Persistence Base) discussion — Guid generator side" — explicitly resolved as D-01 above.

### Stack + Pitfalls
- `.planning/research/STACK.md` § EF Core + Npgsql rows — locked versions Microsoft.EntityFrameworkCore 8.0.27, Npgsql.EntityFrameworkCore.PostgreSQL 8.0.10, EFCore.NamingConventions 8.0.3. § "FluentValidation 12 Wiring Pattern" is Phase 6, NOT Phase 3 (don't conflate). § ".NET 8 LTS support policy" — confirms .NET 8.0.27 runtime + EF Core 8.0.27 patch lockstep.
- `.planning/research/PITFALLS.md` § Pitfall 1 — UTC-only DateTime for `timestamptz` (the rationale for D-07 + D-08). Verbatim source for `Kind == Utc` assertion in SC#2 test.
- `.planning/research/PITFALLS.md` § Pitfall 2 — DbContext lifetime (the rationale for D-11 + SC#4 verification).
- `.planning/research/PITFALLS.md` § Pitfall 3 — migrations on startup with restart loop (informational only — Phase 3 has no migrations; relevant when Phase 8/5 wire the runner).
- `.planning/research/PITFALLS.md` § Pitfall 4 — `EFCore.NamingConventions` mangles `__EFMigrationsHistory` IF retrofitted. **CRITICAL** for Phase 3: snake_case MUST be applied before the first migration is generated (Phase 8 generates it). The rationale for PERSIST-05 + D-09.
- `.planning/research/PITFALLS.md` § Pitfall 5 — M2M skip-navigation misconfiguration (informational — Phase 8 wires junctions; Phase 3's D-09 model-builder iteration excludes them correctly).
- `.planning/research/PITFALLS.md` § Pitfall 6 — `xmin` concurrency token wired wrong. Verbatim source for D-03 shape (shadow property, `xid` column type, `IsRowVersion()` / `IsConcurrencyToken()` semantics). Cross-references the Phase 4 mapping requirement (D-03a).

### Architecture + Features
- `.planning/research/ARCHITECTURE.md` — Persistence layer diagram (Repository → AppDbContext + AuditInterceptor); component table rows for `BaseEntity`, `BaseDbContext`, `AuditInterceptor`, `Repository<TEntity>`; folder structure under `BaseApi.Core/Persistence/`; § Composition root sketch lines 217-235 (`AddBaseApi` will eventually wire `AddSingleton<AuditInterceptor>` + `AddDbContext<TDbContext>` with `UseNpgsql + UseSnakeCaseNamingConvention + AddInterceptors` — Phase 7 work, but Phase 3 must produce shapes compatible with this composition).
- `.planning/research/FEATURES.md` — Row 70 "Generic `Repository<TEntity>`" — the verbatim source for D-04's "tight surface, no IQueryable leakage" guidance.

### Authoritative External Docs (for researcher reference)
- https://learn.microsoft.com/en-us/ef/core/saving/interceptors — `ISaveChangesInterceptor` contract used by D-06..D-08.
- https://learn.microsoft.com/en-us/ef/core/modeling/value-conversions — shadow properties used by D-03.
- https://www.npgsql.org/efcore/mapping/general.html#date-and-time-types — `timestamptz` Kind=Utc rule (Pitfall 1 source).
- https://www.npgsql.org/efcore/modeling/concurrency.html — `xmin` concurrency token pattern.
- https://learn.microsoft.com/en-us/dotnet/api/system.timeprovider — D-07 .NET 8 abstraction.
- https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.time.testing.faketimeprovider — D-07 test counterpart.
- https://github.com/efcore/EFCore.NamingConventions — D-09 snake_case convention.

### State
- `.planning/STATE.md` § Blockers/Concerns — Windows + Docker Desktop WSL2 backend already confirmed during Phase 2 (Phase 3 tests rely on the same compose stack). REQUIREMENTS.md header off-by-one (102 vs 103) is still open but doesn't affect Phase 3. SemVer regex / JSON Schema draft / Cron format are Phase 6 concerns.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `src/BaseApi.Core/Persistence/` (Phase 1 D-08) — `.gitkeep` markers in `Persistence/`, `Persistence/Interceptors/`, `Persistence/Repositories/`. Phase 3 lands `BaseDbContext.cs` in the root, `AuditInterceptor.cs` under `Interceptors/`, `IRepository.cs` + `Repository.cs` under `Repositories/`.
- `src/BaseApi.Core/Entities/` (Phase 1 D-08) — `.gitkeep`. Phase 3 lands `BaseEntity.cs` here.
- `tests/BaseApi.Tests/` (Phase 1 D-11) — xUnit v3 project with one `MetaTest.cs` (`[Fact] Sanity()`). Phase 3 adds new fact tests + a `TestDbContext.cs` + `TestEntity.cs` test scaffold; does NOT touch `MetaTest.cs`.
- `src/BaseApi.Service/appsettings.Development.json` (Phase 2 D-02) — `ConnectionStrings:Postgres` set to `Host=localhost;Port=5433;Database=stepsdb;Username=postgres;Password=postgres`. Phase 3 tests read this for fixture connection.
- `compose.yaml` (Phase 2 D-10) — `postgres:17-alpine` healthy at `localhost:5433`. Phase 3 tests assume it's running (`docker compose up postgres` before `dotnet test`).
- `Directory.Packages.props` (Phase 1 D-05) — EF Core 8.0.27, Npgsql 8.0.10, EFCore.NamingConventions 8.0.3 pinned. Phase 3 csproj files reference these (no `Version=` per CPM); Phase 3 adds ONE new pin (`Microsoft.Extensions.TimeProvider.Testing`).
- `Directory.Build.props` (Phase 1 D-01) — TreatWarningsAsErrors + nullable + analysis-mode strictness. Phase 3 inherits; zero warnings = zero build warnings + zero EF Core 8 model-validation warnings (e.g., shadow-property naming hints).

### Established Patterns
- **2-plan phase structure** (Phase 1: 3, Phase 2: 2) — Phase 3 follows D-17.
- **Verification plan pattern** (01-03, 02-02) — `autonomous: false`, evidence-only commits (`docs(03)` not `feat(03)`), SUMMARY documents command output verbatim. Phase 3's 03-02 mirrors.
- **Fix-forward deviation pattern** (Phase 1 had 4 deviations, Phase 2 had 1) — Phase 3 may surface its own (e.g., `AuditInterceptor` shadow-property naming nuance, `FrameworkReference` vs explicit package ref for `IHttpContextAccessor` in `BaseApi.Core`). When discovered during 03-02 verification, fix via `fix(03-01) <sha>` commit with user-approved checkpoint, NOT a new plan.
- **CPM Version=null contract** (Phase 1 D-05/D-06) — every `<PackageReference>` in Phase 3 csprojs has NO `Version=` attribute. The pin is in `Directory.Packages.props`.
- **Source-generator asset attributes** (Phase 1 D-07) — informational only; no source-gen package added in Phase 3 (`Riok.Mapperly` is Phase 6).

### Integration Points
- `BaseDbContext` is the seam for Phase 4's correlation-id middleware (the interceptor logs would benefit from the correlation-id scope when Phase 4 lands; Phase 3 just exposes a logger via `ILogger<AuditInterceptor>` ctor injection — Phase 4 retrofits the scope automatically once the middleware is in place).
- `BaseDbContext.OnConfiguring`'s `UseSnakeCaseNamingConvention()` call is consumed by Phase 7's `AddBaseApi<TDbContext>()` extension via the `where TDbContext : BaseDbContext` constraint — Phase 7 inherits the convention without duplicating the call (D-09 defense-in-depth ALSO duplicates it in `AddBaseApi`, so test paths and production paths agree).
- `AuditInterceptor` injects `IHttpContextAccessor` (Singleton) — Phase 4's `CorrelationIdMiddleware` ALSO uses it. Phase 3 must NOT register `AddHttpContextAccessor()` in `Program.cs` (that's Phase 7's composition-root job); the Phase 3 test fixture registers it inside the test `ServiceCollection`.
- The `xmin` shadow column from D-03 is read by Phase 4's `DbUpdateConcurrencyException` → 409 mapper (the EF exception carries the entity entries that failed — Phase 4 extracts the entity type for the response `detail` field).

</code_context>

<specifics>
## Specific Ideas

- **Pitfall 1 verbatim** — `AuditInterceptor` line that stamps `CreatedAt`: `entry.Property(nameof(BaseEntity.CreatedAt)).CurrentValue = _clock.GetUtcNow().UtcDateTime;` — never `DateTime.Now`, never `DateTime.UtcNow` (latter works but is non-testable per D-07), never `new DateTime(...)` without `DateTimeKind`. The XML doc on `BaseEntity.CreatedAt`/`UpdatedAt` should reference this in one line: `/// <remarks>UTC by convention — set by AuditInterceptor; never assign manually.</remarks>`.
- **xmin shadow-property naming nuance** — EF Core 8 emits a warning `RelationalEventId.AmbientTransactionWarning` or a model-validation warning if a shadow property's column name conflicts with a CLR property. Postgres `xmin` is a system column; EF must NOT try to create it (only read/track it). The D-03 shape (`HasColumnType("xid")` + `ValueGeneratedOnAddOrUpdate()` + `IsConcurrencyToken()`) is the canonical Npgsql pattern that avoids the warning — researcher should confirm against Npgsql 8.0.10 release notes if any signature shifted.
- **FrameworkReference vs PackageReference for `IHttpContextAccessor`** — `BaseApi.Core` is currently `<Project Sdk="Microsoft.NET.Sdk">` (class library, no implicit `Microsoft.AspNetCore.App` framework reference). The cleanest fix is `<FrameworkReference Include="Microsoft.AspNetCore.App" />` in `BaseApi.Core.csproj` — gives access to `IHttpContextAccessor`, `HealthChecks`, `Authentication`, etc., without per-API package refs. This converts `BaseApi.Core` into an "ASP.NET Core class library" pattern. Researcher: confirm this doesn't break NuGet packaging for `BaseApi.Core` (Out of Scope per Option B, but worth knowing).
- **`appsettings.Development.json` connection-string reuse** — Phase 3 tests load configuration from this file via `Microsoft.Extensions.Configuration.AddJsonFile("appsettings.Development.json")` in the fixture. Avoids duplicating connection-string values; ensures the test fixture and the eventual Phase 8 Service use identical Postgres connection settings.
- **Throwaway database per test run** — `stepsdb_test_{Guid.NewGuid():N}` ensures parallel xUnit test classes (xUnit v3 parallelizes by default) don't collide. The fixture's `InitializeAsync` runs `CREATE DATABASE` via raw `Npgsql.NpgsqlConnection` against the `postgres` admin database; `DisposeAsync` runs `DROP DATABASE`. Wrap in try/catch on dispose so a failed test doesn't leak DBs (or accept that prefix-based cleanup is a v2 nice-to-have).
- **`FakeTimeProvider` package** — `Microsoft.Extensions.TimeProvider.Testing` is the current stable name (Microsoft.Extensions.Diagnostics namespace, released alongside .NET 8). Researcher confirms current version; planner adds the pin to `Directory.Packages.props` alongside the other 22 in 03-01.

</specifics>

<deferred>
## Deferred Ideas

### To Phase 4 (Cross-Cutting Middleware + Error Handling)
- **`DbUpdateConcurrencyException` → HTTP 409 mapping** (D-03a) — the EF exception is NOT a Postgres SQLSTATE, so the existing ERROR-04/05 (SQLSTATE 23503/23505 → 422/409) rules don't cover it. Phase 4 adds the new mapping rule + a lost-update integration test. The response shape mirrors ERROR-05: 409 Conflict, RFC 7807 with `correlationId`, `detail` = "The resource was modified by another request; reload and retry."
- **Correlation-id scope flowing into `AuditInterceptor` logs** — Phase 3's `AuditInterceptor` accepts `ILogger<AuditInterceptor>` via ctor but doesn't establish the scope itself. Phase 4's `CorrelationIdMiddleware` pushes the scope via `_logger.BeginScope(...)` — once that lands, the interceptor's logs (if it logs) automatically inherit. No retrofit needed.

### To Phase 5 (Observability + Health Probes)
- **Startup probe flip from migrations** — Phase 5/8 owns the startup probe that turns Healthy after `await db.Database.MigrateAsync()`. Phase 3 does not run migrations and does not wire the probe.
- **Npgsql tracing instrumentation** (`OpenTelemetry.Instrumentation.Npgsql` or `Npgsql.OpenTelemetry`) — DB spans under request spans. STACK.md mentions `Npgsql.OpenTelemetry 8.0.4` as the optional pair; ROADMAP Phase 5 SC#5 calls for it; not Phase 3 work.

### To Phase 6 (Validation + Mapping Base)
- **`BaseDtoValidator<T>` with `Name`/`Version`/`Description` rules** — REQUIREMENTS.md VALID-01..07; the SemVer regex variant (Strict numeric triple per Pitfall 18) is already locked in REQUIREMENTS.md VALID-06.
- **Mapperly `[Mapper]` partial-class template** — Pitfall 16/17 cover the source-gen pitfalls; Phase 6 establishes the pattern.

### To Phase 7 (Generic HTTP Base + Composition Root)
- **`AddBaseApi<TDbContext>(IConfiguration)` extension** — wires `AddSingleton<AuditInterceptor>` + `AddDbContext<TDbContext>` with `UseNpgsql + UseSnakeCaseNamingConvention + AddInterceptors(sp.GetRequiredService<AuditInterceptor>())` + `AddScoped(typeof(IRepository<>), typeof(Repository<>))` + `AddHttpContextAccessor()` + `AddSingleton(TimeProvider.System)`. Phase 3's D-09 + D-10 + D-11 ensure the shapes Phase 7 wires are exactly compatible.
- **API versioning + Swagger** (Asp.Versioning.Http, Swashbuckle.AspNetCore) — pinned in `Directory.Packages.props`; not Phase 3.

### To Phase 8 (Entity Build-Out + Migrations + Docker Runtime + Tests)
- **`AppDbContext` concrete with 5 entity DbSets + 3 junction DbSets** — derives from `BaseDbContext`. `Program.cs` adds `AddBaseApi<AppDbContext>(builder.Configuration)`.
- **`InitialCreate` migration** — generated via `dotnet ef migrations add InitialCreate` AFTER all 5 entities + 3 junctions are committed. Includes the snake_case naming, the `xmin` shadow columns on every `BaseEntity` subclass, and the explicit junction tables.
- **Testcontainers integration suite** (REQ TEST-03) — Phase 8's tests, NOT Phase 3's; Phase 3 uses the already-running `docker compose up postgres`.
- **Migration runner with startup-probe gating** (PERSIST-09 + PERSIST-10) — `await db.Database.MigrateAsync()` before readiness flips Healthy.

### To v2 (next milestone)
- **`ExistsAsync` helper on `Repository<T>`** — if Service-layer existence pre-checks become a hot path. v1 calls `GetAsync` + null check (one extra column read; trivial cost).
- **`IQueryable` exposure on `Repository<T>`** — if v2 query endpoints (HTTP-17..19: pagination/filtering/sorting) need LINQ composition. Likely better via a dedicated `IQuerySpec<TEntity>` abstraction than raw IQueryable leakage.
- **Multi-instance migration with `pg_advisory_lock`** (INFRA-08 v2 list) — v1 ships single-replica; Phase 3's migration generation in Phase 8 doesn't need this hardening yet.
- **Soft delete** — currently in Out of Scope; if reintroduced, becomes a `BaseEntity.DeletedAt?` field + global query filter on `BaseDbContext`. The xmin token from D-03 covers concurrency for soft-delete too.

</deferred>

---

*Phase: 03-ef-core-persistence-base*
*Context gathered: 2026-05-26*
