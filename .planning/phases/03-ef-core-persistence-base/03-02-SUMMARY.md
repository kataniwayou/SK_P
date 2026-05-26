---
phase: 03-ef-core-persistence-base
plan: 02
subsystem: persistence
tags: [ef-core-8, npgsql-8, snake_case-convention, audit-interceptor, xmin-concurrency, generic-repository, phase-3-acceptance, xunit-v3-mtp, fake-time-provider, throwaway-db-fixture]

# Dependency graph
requires:
  - phase: 03-01
    provides: "BaseEntity + BaseDbContext + AuditInterceptor + IRepository<>/Repository<> + Microsoft.Extensions.TimeProvider.Testing 8.10.0 pin + FrameworkReference Microsoft.AspNetCore.App on both Core/Tests projects"
provides:
  - "Phase 3 SC#1 GREEN: snake_case schema via EnsureCreatedAsync (information_schema.columns proof)"
  - "Phase 3 SC#2 GREEN: AuditInterceptor stamps Kind=Utc timestamps via FakeTimeProvider pinned to 2026-01-15T12:30:00Z"
  - "Phase 3 SC#3 GREEN: CreatedBy stamped from HttpContext + null fallback (two facts in AuditInterceptorTests)"
  - "Phase 3 SC#4 GREEN: DbContext registered Scoped via AddDbContext<TestDbContext> (Assert.Same within scope; Assert.NotSame across scopes)"
  - "Dim 6 GREEN: xmin shadow property non-null, IsConcurrencyToken=true, GetColumnType='xid' (PERSIST-16)"
  - "Dim 7 GREEN: IRepository<> exposes exactly 5 methods {AddAsync, DeleteAsync, GetAsync, ListAsync, Update}; no IQueryable<> (PERSIST-11)"
  - "D-15 cleanup discipline verified: BEFORE/AFTER psql \\l snapshots identical (4 baseline DBs each); zero leaked stepsdb_test_* databases"
  - "9 new test files in tests/BaseApi.Tests/Persistence/ (4 scaffold + 5 fact)"
  - "11 requirements closed: ENTITY-01, ENTITY-02, PERSIST-02..07, PERSIST-11, PERSIST-15, PERSIST-16"
affects: [04-middleware, 05-observability, 06-validation-mapping, 07-http-base, 08-entities]

# Tech tracking
tech-stack:
  added: []  # No new NuGet pins in 03-02; all surface inherited from 03-01
  patterns:
    - "Per-test-class throwaway database fixture via IAsyncLifetime + Guid-suffix naming (D-15: stepsdb_test_{Guid:N})"
    - "ClearAllPools + DROP DATABASE WITH (FORCE) discipline so xUnit v3 class-parallelism leaves no orphan DBs"
    - "Handwritten test doubles (StubHttpContextAccessor) over Moq/NSubstitute per REQUIREMENTS.md Out of Scope (auth/mocking libs)"
    - "FakeTimeProvider for deterministic UTC stamping in audit-interceptor tests (Microsoft.Extensions.Time.Testing namespace — DIFFERS from package name)"
    - "Reflection-based surface lock for generic interface (RepositorySurfaceTests: BindingFlags.DeclaredOnly + IQueryable<> negative check)"
    - "Model-introspection regression guard for shadow properties (XminConcurrencyTokenTests: db.Model.FindEntityType().FindProperty('xmin'))"
    - "xUnit v3 3.2.2 TestContext.Current.CancellationToken threading through async test calls (xUnit1051 analyzer compliance under TreatWarningsAsErrors=true)"

key-files:
  created:
    - "tests/BaseApi.Tests/Persistence/TestEntity.cs - Trivial BaseEntity subclass, empty body, public sealed"
    - "tests/BaseApi.Tests/Persistence/TestDbContext.cs - Concrete BaseDbContext with DbSet<TestEntity>"
    - "tests/BaseApi.Tests/Persistence/PostgresFixture.cs - IAsyncLifetime throwaway DB (CREATE on init; ClearAllPools + DROP WITH FORCE on dispose)"
    - "tests/BaseApi.Tests/Persistence/StubHttpContextAccessor.cs - Handwritten IHttpContextAccessor double with SetUser(string?)"
    - "tests/BaseApi.Tests/Persistence/SchemaTests.cs - SC#1 fact: information_schema.columns snake_case + PascalCase negative assertion"
    - "tests/BaseApi.Tests/Persistence/AuditInterceptorTests.cs - SC#2 + SC#3 facts: FakeTimeProvider UTC stamping + alice user + null fallback"
    - "tests/BaseApi.Tests/Persistence/DiLifetimeTests.cs - SC#4 fact: AddDbContext Scoped lifetime (Assert.Same + Assert.NotSame)"
    - "tests/BaseApi.Tests/Persistence/XminConcurrencyTokenTests.cs - Dim 6 fact: PERSIST-16 model-introspection guard"
    - "tests/BaseApi.Tests/Persistence/RepositorySurfaceTests.cs - Dim 7 fact: PERSIST-11 5-method surface (reflection)"
  modified: []  # No source/csproj changes in 03-02 per D-18 evidence-only

key-decisions:
  - "D-18 honored: Plan 03-02 is verification-only; all commits use docs(03-02) or fix(03-02) prefix (no feat(03-02))"
  - "fix(03-02) classification chosen for xUnit1051 escalation: the defect was test-side (async test methods missing TestContext.Current.CancellationToken thread-through), production code in Plan 03-01 was unchanged — so the fix belongs to 03-02, not 03-01"
  - "Per-class throwaway DBs over per-fact: xUnit v3 parallelizes test CLASSES by default, so the IAsyncLifetime fixture is keyed by class (4 fixtures total: SchemaTests, AuditInterceptorTests, DiLifetimeTests, XminConcurrencyTokenTests; RepositorySurfaceTests is pure reflection, no fixture)"
  - "Verbatim-skeleton execution: all 9 new test files copied directly from 03-PATTERNS.md / 03-RESEARCH.md without semantic deviation — only mechanical change was wiring CancellationToken through await calls in the two DB-touching test classes"

patterns-established:
  - "Verification plans (autonomous:false) commit evidence only with docs(YY-XX): ... messages; fix-forwards to test code commit as fix(YY-XX): ... and fix-forwards to production code from the consuming plan commit as fix(prior-YY-XX): ..."
  - "When xUnit v3 analyzers (xUnit1051 in particular) demand TestContext.Current.CancellationToken under TreatWarningsAsErrors=true, route the token through ALL async DbContext/EnsureCreated/SaveChanges/AddAsync/DROP/CREATE calls inside the test body, not just the assertion's top-level await"
  - "psql \\l snapshots before AND after the test run are the D-15 cleanup proof — byte-identical output is the GREEN signal; any new row matching stepsdb_test_[a-f0-9]+ is a leak that surfaces as a WARNING (per RESEARCH.md A4 acknowledged limitation)"

requirements-completed: [ENTITY-01, ENTITY-02, PERSIST-02, PERSIST-03, PERSIST-04, PERSIST-05, PERSIST-06, PERSIST-07, PERSIST-11, PERSIST-15, PERSIST-16]

# Metrics
duration: ~3min (autonomous wave Tasks 2-6) + human review
completed: 2026-05-27
---

# Phase 3 Plan 02: Acceptance Verification Summary

**Phase 3 acceptance battery executed against the Phase 2 Postgres container at localhost:5433 — all 4 ROADMAP success criteria (snake_case schema, UTC timestamp stamping, HttpContext-based CreatedBy + null fallback, Scoped DbContext lifetime) plus both dimension regression guards (xmin shadow concurrency token wiring, IRepository 5-method surface lock) reported GREEN against 7 xUnit v3 facts, with zero leaked throwaway databases (D-15 cleanup discipline proven by byte-identical BEFORE/AFTER `psql \l` snapshots).**

## Performance

- **Duration:** ~3 min 17 sec (autonomous Task 2 commit `5080896` at 2026-05-27T00:38:23+03:00 to Task 5a fix commit `7636429` at 2026-05-27T00:41:40+03:00) + human-review pause + metadata commit
- **Started:** 2026-05-27T00:35Z (Task 1 pre-flight Postgres health check)
- **Completed:** 2026-05-27 (Task 7 checkpoint approval + this SUMMARY)
- **Tasks:** 7 (Task 1 pre-flight + Tasks 2-5 scaffold/fact files + Task 6 build/test verification + Task 7 checkpoint)
- **Files modified:** 10 (9 new test files + 1 SUMMARY)
- **Commits:** 6 wave-2 commits (5 atomic per task + 1 fix-forward for xUnit1051) + final metadata commit

## Accomplishments

- **Phase 3 ROADMAP success criteria battery GREEN.** All four ROADMAP-listed SCs (snake_case schema via EnsureCreatedAsync, UTC `Kind` audit stamping, HttpContext-based CreatedBy + null fallback, Scoped DbContext lifetime in DI) verified against real Postgres 17 with discrete `[Fact]` tests. No skip, no warning, no flaky-test downgrade — exit 0 deterministic.
- **Dim 6 (PERSIST-16) and Dim 7 (PERSIST-11) regression guards landed.** The xmin shadow-property model-introspection check fires immediately if a future plan removes the `BaseDbContext.OnModelCreating` iteration. The IRepository reflection check catches any `IQueryable<>` / `ExistsAsync` / Where-predicate leakage in the D-04 surface contract.
- **9 new test files landed in `tests/BaseApi.Tests/Persistence/`** — 4 scaffold (TestEntity, TestDbContext, PostgresFixture, StubHttpContextAccessor) + 5 fact (SchemaTests, AuditInterceptorTests with 2 facts, DiLifetimeTests, XminConcurrencyTokenTests, RepositorySurfaceTests). Total fact count: 6 Phase 3 + 1 baseline (MetaTest.Sanity) = 7 facts; `Passed: 7, Failed: 0, Skipped: 0`.
- **D-15 cleanup discipline proven.** Pre-test and post-test `psql \l` snapshots are byte-identical (same 4 baseline DBs: postgres, stepsdb, template0, template1). ClearAllPools + DROP DATABASE WITH (FORCE) on `PostgresFixture.DisposeAsync` ran cleanly for every class fixture. Zero orphan `stepsdb_test_*` databases.
- **xUnit1051 analyzer escalation fixed inline** (Rule 3 deviation). xUnit v3 3.2.2 + TreatWarningsAsErrors=true demanded `TestContext.Current.CancellationToken` threading through all async test calls. Fixed forward as `fix(03-02): wire TestContext.Current.CancellationToken to satisfy xUnit1051` (commit `7636429`) — 10 call sites across 2 test classes touched, behavior preserved. Classified as `fix(03-02)` not `fix(03-01)` because the defect was test-side; Plan 03-01 production code unchanged.
- **No `src/` source changes.** Plan 03-02 honored D-18 evidence-only: zero edits under `src/BaseApi.Core/` or `src/BaseApi.Service/`. The fact battery exercised Plan 03-01's surface as built.

## Task Commits

Each autonomous task was committed atomically (Task 1 pre-flight is verification-only; Task 6 is verification-only; Task 7 is the checkpoint that produced this SUMMARY):

1. **Task 1: Pre-flight — Phase 2 Postgres healthy + BEFORE `\l` snapshot** — (verification only; no commit; snapshot persisted to `%TEMP%\phase3-psql-before.txt`)
2. **Task 2: TestEntity.cs + TestDbContext.cs scaffold** — `5080896` (docs)
3. **Task 3: PostgresFixture.cs (IAsyncLifetime throwaway DB)** — `c4b4196` (docs)
4. **Task 4: StubHttpContextAccessor.cs (handwritten test double)** — `376b29e` (docs)
5. **Task 5: 5 fact-test files (Schema/AuditInterceptor/DiLifetime/XminConcurrencyToken/RepositorySurface)** — `9ad6d42` (docs)
6. **Task 5a: Deviation fix — TestContext.Current.CancellationToken threading (xUnit1051)** — `7636429` (fix(03-02))
7. **Task 6: dotnet build Release + dotnet test + AFTER `\l` snapshot** — (verification only; no commit)
8. **Task 7: Human-verify checkpoint + 03-02-SUMMARY.md** — final metadata commit (this SUMMARY + STATE.md + ROADMAP.md update)

**Plan metadata commit:** landed alongside this SUMMARY, includes STATE.md and ROADMAP.md updates.

## Phase 3 Success Criteria — GREEN/RED Grid

| Criterion | Source | Fact | Result |
|-----------|--------|------|--------|
| **SC#1** snake_case schema via EnsureCreatedAsync | ROADMAP Phase 3 SC#1 | `SchemaTests.Test_TestDbContext_EnsureCreated_ProducesSnakeCaseSchema` | **GREEN** |
| **SC#2** AuditInterceptor stamps `Kind=Utc` timestamps | ROADMAP Phase 3 SC#2 | `AuditInterceptorTests.Test_AuditInterceptor_StampsUtcTimestamps_OnInsert` | **GREEN** |
| **SC#3** CreatedBy from HttpContext + null fallback | ROADMAP Phase 3 SC#3 | `AuditInterceptorTests.Test_AuditInterceptor_StampsUtcTimestamps_OnInsert` (alice case) + `AuditInterceptorTests.Test_AuditInterceptor_StampsCreatedBy_FromHttpContext_NullFallback` (null case) | **GREEN** |
| **SC#4** DbContext registered Scoped in DI | ROADMAP Phase 3 SC#4 | `DiLifetimeTests.Test_DbContext_IsRegisteredScoped_InDI` | **GREEN** |
| **Dim 6** xmin shadow concurrency token wiring | PERSIST-16 (D-03b) | `XminConcurrencyTokenTests.Test_BaseEntity_HasXminShadowProperty` | **GREEN** |
| **Dim 7** IRepository 5-method surface lock | PERSIST-11 (D-04) | `RepositorySurfaceTests.Test_IRepository_ExposesExactlyFiveMethods` | **GREEN** |

### SC#1: snake_case schema via EnsureCreatedAsync
- **Fact:** `BaseApi.Tests.Persistence.SchemaTests.Test_TestDbContext_EnsureCreated_ProducesSnakeCaseSchema`
- **Backend:** throwaway DB `stepsdb_test_{Guid:N}` inside Phase 2 container
- **Assertion (positive):** `information_schema.columns` returns `created_at`, `updated_at`, `created_by`, `updated_by`, `id`, `name`, `version`, `description`
- **Assertion (negative):** `CreatedAt` and `UpdatedAt` (PascalCase) NOT present in the column list
- **Result:** **GREEN**

### SC#2: AuditInterceptor stamps Kind=Utc timestamps
- **Fact:** `BaseApi.Tests.Persistence.AuditInterceptorTests.Test_AuditInterceptor_StampsUtcTimestamps_OnInsert`
- **Time source:** `FakeTimeProvider` (Microsoft.Extensions.TimeProvider.Testing 8.10.0) pinned to `new DateTimeOffset(2026, 1, 15, 12, 30, 0, TimeSpan.Zero)`
- **Assertion:** `entity.CreatedAt == new DateTime(2026, 1, 15, 12, 30, 0, DateTimeKind.Utc)` AND `entity.CreatedAt.Kind == DateTimeKind.Utc` AND `entity.CreatedBy == "alice"`
- **Result:** **GREEN** — no Npgsql `InvalidCastException`; UTC roundtrips cleanly into `timestamptz`

### SC#3: CreatedBy from HttpContext + null fallback (TWO facts)
- **Fact A (set):** `BaseApi.Tests.Persistence.AuditInterceptorTests.Test_AuditInterceptor_StampsUtcTimestamps_OnInsert` — `stub.SetUser("alice")` → inserted row's `CreatedBy == "alice"`
- **Fact B (null):** `BaseApi.Tests.Persistence.AuditInterceptorTests.Test_AuditInterceptor_StampsCreatedBy_FromHttpContext_NullFallback` — `stub.HttpContext = null` → `SaveChangesAsync` does NOT throw → `entity.CreatedBy IS NULL`
- **Result:** **GREEN** — null-conditional chain `HttpContext?.User?.Identity?.Name` returns null safely (PERSIST-04 / D-08)

### SC#4: DbContext registered Scoped lifetime in DI
- **Fact:** `BaseApi.Tests.Persistence.DiLifetimeTests.Test_DbContext_IsRegisteredScoped_InDI`
- **Registration:** `services.AddDbContext<TestDbContext>(opts => opts.UseNpgsql(_fixture.ConnectionString))`
- **Assertion (intra-scope):** `Assert.Same(a, b)` where `a` and `b` resolved from same `scope1.ServiceProvider`
- **Assertion (cross-scope):** `Assert.NotSame(a, c)` where `c` resolved from `scope2.ServiceProvider`
- **Result:** **GREEN** — Scoped is the EF Core default for `AddDbContext` (Pitfall 2 anchor); PERSIST-15 satisfied

### Dim 6: xmin Shadow Concurrency Token Wiring (PERSIST-16, D-03b new requirement)
- **Fact:** `BaseApi.Tests.Persistence.XminConcurrencyTokenTests.Test_BaseEntity_HasXminShadowProperty`
- **Assertion:** `db.Model.FindEntityType(typeof(TestEntity)).FindProperty("xmin")` is non-null AND `xmin.IsConcurrencyToken == true` AND `xmin.GetColumnType() == "xid"`
- **Backing implementation:** `BaseDbContext.OnModelCreating` iterates `modelBuilder.Model.GetEntityTypes().Where(t => typeof(BaseEntity).IsAssignableFrom(t.ClrType))` and configures the Pitfall 6 verbatim shadow property `Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken()` (Plan 03-01 Task 5)
- **Result:** **GREEN** — model-metadata regression guard armed; junction entities (non-BaseEntity) excluded naturally by the assignable-from filter

### Dim 7: IRepository Surface Lock (PERSIST-11, D-04)
- **Fact:** `BaseApi.Tests.Persistence.RepositorySurfaceTests.Test_IRepository_ExposesExactlyFiveMethods`
- **Assertion:** `typeof(IRepository<>).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Count == 5`; sorted method names exactly `{AddAsync, DeleteAsync, GetAsync, ListAsync, Update}`; no method's return type is generic `IQueryable<>`
- **Result:** **GREEN** — surface contract locked; any future `ExistsAsync` / `Where(predicate)` / `IQueryable<>` leakage will fail this fact immediately

## Build Verification (Task 6 verbatim output)

### `dotnet build --configuration Release --no-restore`

```
  BaseApi.Core -> C:\Users\UserL\source\repos\SK_P\src\BaseApi.Core\bin\Release\net8.0\BaseApi.Core.dll
  BaseApi.Tests -> C:\Users\UserL\source\repos\SK_P\tests\BaseApi.Tests\bin\Release\net8.0\BaseApi.Tests.dll
  BaseApi.Service -> C:\Users\UserL\source\repos\SK_P\src\BaseApi.Service\bin\Release\net8.0\BaseApi.Service.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:01.52
```

**Exit code:** 0. Aggregate: `0 Warning(s)` AND `0 Error(s)` — zero-warning regime intact (Phase 1 D-02 still load-bearing under EF Core 8 + xUnit v3 + xUnit1051 analyzer escalation patch).

### `dotnet test --no-build --configuration Release --logger "console;verbosity=normal"`

```
C:\Users\UserL\.nuget\packages\microsoft.testing.platform.msbuild\1.9.1\buildMultiTargeting\Microsoft.Testing.Platform.MSBuild.targets(376,5): warning MTP0001: VSTest-specific properties are set but will be ignored when using Microsoft.Testing.Platform. The following properties are set: VSTestLogger; . [C:\Users\UserL\source\repos\SK_P\tests\BaseApi.Tests\BaseApi.Tests.csproj]
  Run tests: 'C:\Users\UserL\source\repos\SK_P\tests\BaseApi.Tests\bin\Release\net8.0\BaseApi.Tests.dll' [net8.0|x64]
  Passed! - Failed: 0, Passed: 7, Skipped: 0, Total: 7, Duration: 2s 602ms - BaseApi.Tests.dll (net8.0|x64)
  Tests succeeded: 'C:\Users\UserL\source\repos\SK_P\tests\BaseApi.Tests\bin\Release\net8.0\BaseApi.Tests.dll' [net8.0|x64]
```

**Exit code:** 0.

| Metric | Value |
|--------|-------|
| Total facts | 7 (1 baseline `MetaTest.Sanity` + 6 Phase 3 facts) |
| Passed | 7 |
| Failed | 0 |
| Skipped | 0 |
| Wall-clock duration | 2.602 s (4 throwaway-DB CREATE+DROP lifecycles + reflection-only fact + MetaTest.Sanity) |
| Runner | Microsoft.Testing.Platform (MTP) — xUnit v3 3.2.2 |

**Note on `MTP0001` warning:** This is an MSBuild build-time advisory about a legacy VSTest property (`VSTestLogger`) being passed through unused. The MTP runner itself emits no warnings; the test build (`Build succeeded. 0 Warning(s).`) remains zero-warning. Non-blocking; non-fatal; documented for future cleanup if/when the test SDK drops VSTest compat shims.

## D-15 Cleanup Verification — `psql \l` Snapshots

### BEFORE `dotnet test` (captured during Task 1 pre-flight to `%TEMP%\phase3-psql-before.txt`)

```
                                                    List of databases
   Name    |  Owner   | Encoding | Locale Provider |  Collate   |   Ctype    | Locale | ICU Rules |   Access privileges
-----------+----------+----------+-----------------+------------+------------+--------+-----------+-----------------------
 postgres  | postgres | UTF8     | libc            | en_US.utf8 | en_US.utf8 |        |           |
 stepsdb   | postgres | UTF8     | libc            | en_US.utf8 | en_US.utf8 |        |           |
 template0 | postgres | UTF8     | libc            | en_US.utf8 | en_US.utf8 |        |           | =c/postgres          +
           |          |          |                 |            |            |        |           | postgres=CTc/postgres
 template1 | postgres | UTF8     | libc            | en_US.utf8 | en_US.utf8 |        |           | =c/postgres          +
           |          |          |                 |            |            |        |           | postgres=CTc/postgres
(4 rows)
```

### AFTER `dotnet test` (captured during Task 6 to `%TEMP%\phase3-psql-after.txt`)

```
                                                    List of databases
   Name    |  Owner   | Encoding | Locale Provider |  Collate   |   Ctype    | Locale | ICU Rules |   Access privileges
-----------+----------+----------+-----------------+------------+------------+--------+-----------+-----------------------
 postgres  | postgres | UTF8     | libc            | en_US.utf8 | en_US.utf8 |        |           |
 stepsdb   | postgres | UTF8     | libc            | en_US.utf8 | en_US.utf8 |        |           |
 template0 | postgres | UTF8     | libc            | en_US.utf8 | en_US.utf8 |        |           | =c/postgres          +
           |          |          |                 |            |            |        |           | postgres=CTc/postgres
 template1 | postgres | UTF8     | libc            | en_US.utf8 | en_US.utf8 |        |           | =c/postgres          +
           |          |          |                 |            |            |        |           | postgres=CTc/postgres
(4 rows)
```

**Diff:** byte-identical (4 baseline rows in both). **D-15 cleanup GREEN.** Zero leaked `stepsdb_test_*` databases — `PostgresFixture.DisposeAsync` (NpgsqlConnection.ClearAllPools + DROP DATABASE IF EXISTS "..." WITH (FORCE)) ran cleanly for every class fixture under xUnit v3's per-class parallelism. RESEARCH.md A4's acknowledged occasional-leak limitation did NOT surface this run.

## Files Created/Modified

### Created (9 test files + 1 SUMMARY = 10 total)

- `tests/BaseApi.Tests/Persistence/TestEntity.cs` — Trivial `public sealed class TestEntity : BaseEntity` with empty body. Inherits the 8 ENTITY-01 properties via `BaseEntity` without subclass-specific fields.
- `tests/BaseApi.Tests/Persistence/TestDbContext.cs` — `public sealed class TestDbContext : BaseDbContext` with `public DbSet<TestEntity> TestEntities => Set<TestEntity>();`. No `OnConfiguring` / `OnModelCreating` override (inherits Plan 03-01's snake_case + xmin wiring intact).
- `tests/BaseApi.Tests/Persistence/PostgresFixture.cs` — `public sealed class PostgresFixture : IAsyncLifetime`. `DatabaseName = $"stepsdb_test_{Guid.NewGuid():N}"`. `InitializeAsync`: open Npgsql admin connection at `Host=localhost;Port=5433;Database=postgres;Username=postgres;Password=postgres`, run `CREATE DATABASE "{name}"`. `DisposeAsync`: `NpgsqlConnection.ClearAllPools()` FIRST, then open admin connection and `DROP DATABASE IF EXISTS "{name}" WITH (FORCE)` (PG 13+ semantics; Phase 2 uses 17-alpine).
- `tests/BaseApi.Tests/Persistence/StubHttpContextAccessor.cs` — `public sealed class StubHttpContextAccessor : IHttpContextAccessor`. `HttpContext { get; set; }` mutable. `SetUser(string? name)` builds `ClaimsIdentity(authenticationType: "Test")` + `ClaimTypes.Name` claim + `ClaimsPrincipal` + `new DefaultHttpContext { User = principal }`. Passing `null` clears HttpContext (simulates non-HTTP execution).
- `tests/BaseApi.Tests/Persistence/SchemaTests.cs` — `IClassFixture<PostgresFixture>`; runs `db.Database.EnsureCreatedAsync()`; queries `SELECT column_name FROM information_schema.columns WHERE table_name = 'test_entities' ORDER BY column_name`; asserts presence of 8 snake_case columns and absence of `CreatedAt`/`UpdatedAt` PascalCase.
- `tests/BaseApi.Tests/Persistence/AuditInterceptorTests.cs` — `IClassFixture<PostgresFixture>`; TWO `[Fact]` methods. (1) UTC stamping with `FakeTimeProvider` pinned to 2026-01-15T12:30:00Z + `stub.SetUser("alice")` → asserts `CreatedAt`, `Kind==Utc`, `CreatedBy=="alice"`. (2) Null fallback with `stub.HttpContext == null` → `SaveChangesAsync` does NOT throw → asserts `CreatedBy IS NULL`. Uses `using Microsoft.Extensions.Time.Testing;` (the namespace DIFFERS from the package name `Microsoft.Extensions.TimeProvider.Testing`).
- `tests/BaseApi.Tests/Persistence/DiLifetimeTests.cs` — `IClassFixture<PostgresFixture>`; builds `ServiceCollection` with `AddDbContext<TestDbContext>`, two `CreateScope()` calls, `Assert.Same(a, b)` within scope, `Assert.NotSame(a, c)` across scopes.
- `tests/BaseApi.Tests/Persistence/XminConcurrencyTokenTests.cs` — `IClassFixture<PostgresFixture>`; pure model-introspection (no DB query, but fixture still needed to construct `DbContextOptions`). Asserts `FindProperty("xmin")` non-null, `IsConcurrencyToken == true`, `GetColumnType() == "xid"`.
- `tests/BaseApi.Tests/Persistence/RepositorySurfaceTests.cs` — NO fixture (pure reflection); `typeof(IRepository<>).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)`; exact-5 count + sorted-name array assertion + per-method `IQueryable<>` negative check.
- `.planning/phases/03-ef-core-persistence-base/03-02-SUMMARY.md` — this file.

### Modified (3 in metadata commit)

- `.planning/STATE.md` — advance Current Position to Phase 3 complete; append Plan 03-02 decisions; record session continuity; append performance metric row.
- `.planning/ROADMAP.md` — mark Phase 3 row `Complete` (Plans Complete: 2/2; Completed: 2026-05-27); flip Phase 3 checkbox to `[x]`.
- `.planning/REQUIREMENTS.md` — verified all 11 Phase 3 reqs already marked `Complete` from Plan 03-01's bulk mark-complete; no further edits required (see SDK-bug note in Deviations).

## Decisions Made

- **`fix(03-02)` classification chosen over `fix(03-01)` for the xUnit1051 patch.** The defect was test-side (test code lacked `TestContext.Current.CancellationToken` threading per xUnit v3 3.2.2 analyzer); Plan 03-01 production code was unchanged. Per the Phase 1/2 deviation convention, test-side fix-forwards live with the verification plan that surfaced them — Plan 03-01's commit graph remains untouched.
- **Verbatim PATTERNS.md skeletons used end-to-end.** All 9 new test files were copy-pasted from the Phase 3 PATTERNS.md / RESEARCH.md skeletons. The ONLY mechanical change was wiring `var ct = TestContext.Current.CancellationToken;` through 10 await call sites in SchemaTests (4 sites) and AuditInterceptorTests (6 sites — 3 per fact, two facts). Behavior preserved. Asserted facts unchanged.
- **Per-class throwaway DB strategy held under xUnit v3 parallelism.** No collisions, no leaks, no test interdependencies. The 4 fact classes that need a DB (SchemaTests, AuditInterceptorTests, DiLifetimeTests, XminConcurrencyTokenTests) each got their own `stepsdb_test_{Guid:N}` instance; RepositorySurfaceTests skipped the fixture entirely (pure reflection). D-15 cleanup proved on first run.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Wire `TestContext.Current.CancellationToken` to satisfy xUnit1051 analyzer**
- **Found during:** Task 6 (first attempt at `dotnet build --configuration Release --no-restore` against the new fact files)
- **Issue:** xUnit v3 3.2.2's xUnit1051 analyzer raised errors for every async test call (`EnsureCreatedAsync`, `SaveChangesAsync`, `AddAsync`, `OpenAsync`, `ExecuteReaderAsync`, `ReadAsync`) inside a `[Fact]` method that did NOT pass `TestContext.Current.CancellationToken`. Under Phase 1 D-02 (`TreatWarningsAsErrors=true` + `EnforceCodeStyleInBuild=true` globally) these analyzer warnings escalated to build errors and the test project failed to compile.
- **Fix:** Added `var ct = TestContext.Current.CancellationToken;` to every `[Fact]` body that performs async DB I/O, then threaded `ct` through all async calls (10 call sites across SchemaTests.cs (4 sites) and AuditInterceptorTests.cs (6 sites: 3 per fact × 2 facts)). The test logic and assertions were preserved verbatim — only the cancellation-token argument was added to existing await calls.
- **Files modified:** `tests/BaseApi.Tests/Persistence/SchemaTests.cs`, `tests/BaseApi.Tests/Persistence/AuditInterceptorTests.cs`
- **Verification:** Re-ran `dotnet build --configuration Release --no-restore` → exit 0, `Build succeeded. 0 Warning(s). 0 Error(s).` Re-ran `dotnet test` → exit 0, `Passed: 7`. xUnit1051 silent.
- **Committed in:** `7636429` (`fix(03-02): wire TestContext.Current.CancellationToken to satisfy xUnit1051`)
- **Classification rationale:** This is a TEST-CODE defect (PATTERNS.md skeletons predate xUnit v3 3.2.2 analyzer enforcement). Plan 03-01's production code (BaseEntity, BaseDbContext, AuditInterceptor, IRepository, Repository) was not modified. Per the Phase 1/2 convention (`fix(prior-plan)` for production-code fixes-forward triggered by the consuming plan), this fix belongs to `03-02` because the defect was IN 03-02's own test files.

---

**Total deviations:** 1 auto-fixed (Rule 3 - Blocking — test-side analyzer escalation)
**Impact on plan:** Zero impact on production code, zero impact on success criteria. The xUnit1051 patch is a test-side build-gate satisfaction, not a behavior change. All 6 Phase 3 facts assert the same things they asserted before the patch; the assertions just now run inside an async context that respects the analyzer's cancellation-propagation contract.

## Issues Encountered

- **xUnit v3 3.2.2 + analyzer escalation under `TreatWarningsAsErrors=true`.** PATTERNS.md / RESEARCH.md skeletons were authored before xUnit1051 was raised to default-on in xUnit v3 3.x. The fix is mechanical (one extra line per fact + one extra argument per await) and now documented in `key-decisions` / `patterns-established` for future verification plans. Recommended future action: update PATTERNS.md skeletons to embed `var ct = TestContext.Current.CancellationToken;` so the next phase doesn't re-incur this fix-forward.
- **`MTP0001` MSBuild warning during `dotnet test`.** The Microsoft.Testing.Platform.MSBuild target advises that legacy VSTest properties (`VSTestLogger`) are ignored under MTP. Non-blocking, non-fatal; build itself still reports `0 Warning(s)`. Investigate later if/when the test SDK drops VSTest compat — for now harmless.
- **Pre-existing gsd-sdk `requirements.mark-complete` UTF-8 / split-line corruption.** Documented in Plan 03-01 SUMMARY's deviation #4. Plan 03-02 deliberately did NOT call `requirements.mark-complete` again — the 11 Phase 3 reqs were already marked Complete in Plan 03-01's recovery edit. Verified by reading REQUIREMENTS.md: all 11 IDs show `[x] **ID**` (still split across two lines from the prior SDK damage) AND show `Complete` in the Traceability table. SCOPE BOUNDARY rule held: pre-existing markdown damage NOT in scope of 03-02.

## User Setup Required

None — no external service configuration required for this plan. All work is in-tree (9 new test files + 1 SUMMARY). The only external dependency is the Phase 2 Postgres container, which was already healthy at plan start and remains healthy at plan end (no new ports, no new env vars, no new dashboards).

## Next Phase Readiness

- **Phase 3 is COMPLETE.** Both plans shipped (03-01 = build, 03-02 = verification). All 4 ROADMAP success criteria + 2 dimension regression guards GREEN. 11 requirements closed (ENTITY-01, ENTITY-02, PERSIST-02..07, PERSIST-11, PERSIST-15, PERSIST-16). No blockers, no carry-forward concerns to Phase 4.
- **Phase 4 (Cross-Cutting Middleware + Error Handling) is ready to plan.** Phase 3 D-03a / PERSIST-16 cross-phase impact is now live: the xmin shadow column wires on every `BaseEntity` subclass via `BaseDbContext.OnModelCreating`, so Phase 4's `DbUpdateConcurrencyException → HTTP 409` mapper will have a runtime substrate to exercise. Phase 4's correlation-ID middleware + RFC 7807 ProblemDetails + SQLSTATE 23503/23505 mapping work is unblocked.
- **Phase 8 (entity build-out + migration) substrate confirmed.** The 5 concrete entities will inherit the xmin shadow property automatically via the `typeof(BaseEntity).IsAssignableFrom(t.ClrType)` filter; the 3 junction entities (non-BaseEntity) will be excluded automatically. The InitialCreate migration will be the FIRST migration ever generated against this DbContext — Pitfall 4 (snake_case applied before any migration) is satisfied because Phase 3 wired UseSnakeCaseNamingConvention in `BaseDbContext.OnConfiguring` and Plan 03-02 just proved snake_case columns materialize against real Postgres.
- **Zero-warning regime intact.** Build still `0 Warning(s) 0 Error(s)` under EF Core 8 + xUnit v3 3.2.2 + xUnit1051 analyzer + `TreatWarningsAsErrors=true`. CPM contract intact (23 pins, no `Version=` attributes on PackageReference). Future plans inherit the same regime.

## Self-Check: PASSED

**Files verified (10/10):**
- FOUND: tests/BaseApi.Tests/Persistence/TestEntity.cs
- FOUND: tests/BaseApi.Tests/Persistence/TestDbContext.cs
- FOUND: tests/BaseApi.Tests/Persistence/PostgresFixture.cs
- FOUND: tests/BaseApi.Tests/Persistence/StubHttpContextAccessor.cs
- FOUND: tests/BaseApi.Tests/Persistence/SchemaTests.cs
- FOUND: tests/BaseApi.Tests/Persistence/AuditInterceptorTests.cs
- FOUND: tests/BaseApi.Tests/Persistence/DiLifetimeTests.cs
- FOUND: tests/BaseApi.Tests/Persistence/XminConcurrencyTokenTests.cs
- FOUND: tests/BaseApi.Tests/Persistence/RepositorySurfaceTests.cs
- FOUND: .planning/phases/03-ef-core-persistence-base/03-02-SUMMARY.md

**Commits verified (6/6 wave-2):**
- FOUND: 5080896 (Task 2 — TestEntity + TestDbContext)
- FOUND: c4b4196 (Task 3 — PostgresFixture)
- FOUND: 376b29e (Task 4 — StubHttpContextAccessor)
- FOUND: 9ad6d42 (Task 5 — 5 fact-test files)
- FOUND: 7636429 (Task 5a — fix(03-02) xUnit1051 patch)
- FOUND: (final metadata commit — created alongside this SUMMARY)

**Acceptance summary:** SC#1 GREEN, SC#2 GREEN, SC#3 GREEN, SC#4 GREEN, Dim 6 (PERSIST-16) GREEN, Dim 7 (PERSIST-11) GREEN. D-15 cleanup GREEN (no leaks). Build zero-warning. Test exit 0, 7/7 passed. **Phase 3 complete.**

---
*Phase: 03-ef-core-persistence-base*
*Plan: 02*
*Completed: 2026-05-27*
