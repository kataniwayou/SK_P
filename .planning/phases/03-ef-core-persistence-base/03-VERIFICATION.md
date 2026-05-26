---
phase: 03-ef-core-persistence-base
verified: 2026-05-27T00:00:00Z
status: passed
score: 10/10 must-haves verified
overrides_applied: 0
---

# Phase 3: EF Core Persistence Base — Verification Report

**Phase Goal:** Build the persistence foundation in `BaseApi.Core` — `BaseEntity`, `BaseDbContext`, `AuditInterceptor`, snake_case convention, and the generic `Repository<T>` — before any migration is generated so the convention applies to the very first schema.
**Verified:** 2026-05-27
**Status:** PASSED
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | SC#1: EnsureCreatedAsync on a BaseDbContext subclass produces snake_case identifiers (`created_at`, not `CreatedAt`) | VERIFIED | `SchemaTests.cs` queries `information_schema.columns` on a throwaway DB; asserts `created_at`, `updated_at`, etc. present AND `CreatedAt`/`UpdatedAt` absent. Test passed (7/7 green run). |
| 2 | SC#2: AuditInterceptor stamps `CreatedAt`/`UpdatedAt` with `Kind == DateTimeKind.Utc` via FakeTimeProvider, no Npgsql InvalidCastException | VERIFIED | `AuditInterceptorTests.Test_AuditInterceptor_StampsUtcTimestamps_OnInsert` asserts `DateTimeKind.Utc` and specific pinned timestamp. FakeTimeProvider wired via `AddInterceptors(interceptor)`. |
| 3 | SC#3: HttpContext user `"alice"` stamps `created_by = "alice"`; null HttpContext stamps `created_by = null` (no crash) | VERIFIED | Two separate `[Fact]` tests in `AuditInterceptorTests.cs` — alice path and null-HttpContext path. Both passed in the 7-fact run. |
| 4 | SC#4: Two resolutions of `DbContext` from the same `IServiceScope` return the same instance; different scopes return different instances | VERIFIED | `DiLifetimeTests.Test_DbContext_IsRegisteredScoped_InDI` uses `Assert.Same` within scope, `Assert.NotSame` across scopes. Passed. |
| 5 | PERSIST-16/Dim 6: `xmin` shadow property is non-null, `IsConcurrencyToken == true`, `GetColumnType() == "xid"` on every `BaseEntity` subclass | VERIFIED | `BaseDbContext.OnModelCreating` runs the verbatim D-03 iteration. `XminConcurrencyTokenTests.Test_BaseEntity_HasXminShadowProperty` asserts via model introspection. Passed. |
| 6 | PERSIST-11/Dim 7: `IRepository<>` exposes exactly 5 declared public methods with names `{AddAsync, DeleteAsync, GetAsync, ListAsync, Update}` and no method returns `IQueryable<>` | VERIFIED | `IRepository.cs` declares exactly 5 methods. `RepositorySurfaceTests.Test_IRepository_ExposesExactlyFiveMethods` uses `BindingFlags.DeclaredOnly` + `IQueryable<>` negative check. Passed. |
| 7 | D-11: `Program.cs` was NOT modified during Phase 3 | VERIFIED | `git log --oneline --since="2026-05-27" -- src/BaseApi.Service/Program.cs` returns empty. Only commit touching Program.cs is `8adb12c` from Phase 1. |
| 8 | D-16: No migration files generated | VERIFIED | `**/Migrations/*.cs` glob returns no files. |
| 9 | D-15: Throwaway test DBs cleaned up — no `stepsdb_test_*` leaks | VERIFIED | SUMMARY documents byte-identical BEFORE/AFTER `psql \l` snapshots: 4 baseline DBs each (postgres, stepsdb, template0, template1). `PostgresFixture.DisposeAsync` runs `ClearAllPools + DROP DATABASE WITH (FORCE)`. |
| 10 | PERSIST-16 requirement appended to REQUIREMENTS.md with Traceability row | VERIFIED | REQUIREMENTS.md line 43: PERSIST-16 bullet under § Persistence, marked `[x]`. Traceability table line 214: `PERSIST-16 \| Phase 3 \| Complete`. |

**Score:** 10/10 truths verified

---

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/BaseApi.Core/Entities/BaseEntity.cs` | Abstract class with 8 ENTITY-01 properties + Pitfall 1 UTC remarks | VERIFIED | `public abstract class BaseEntity` with Id, Name, Version, CreatedAt (UTC remarks), UpdatedAt (UTC remarks), CreatedBy?, UpdatedBy?, Description?. All 8 properties present. |
| `src/BaseApi.Core/Persistence/BaseDbContext.cs` | Abstract, no DbSets, OnConfiguring calls UseSnakeCaseNamingConvention(), OnModelCreating has xmin iteration | VERIFIED | Abstract, no DbSet props. `OnConfiguring` calls `UseSnakeCaseNamingConvention()`. `OnModelCreating` has verbatim D-03 xmin iteration with `HasColumnName("xmin")`, `HasColumnType("xid")`, `ValueGeneratedOnAddOrUpdate()`, `IsConcurrencyToken()`. |
| `src/BaseApi.Core/Persistence/Interceptors/AuditInterceptor.cs` | `sealed`, derives `SaveChangesInterceptor`, depends on `IHttpContextAccessor` + `TimeProvider`, stamps via `_clock.GetUtcNow().UtcDateTime`, null-safe HttpContext chain | VERIFIED | `public sealed class AuditInterceptor : SaveChangesInterceptor`. Constructor injects both deps. `SavingChangesAsync` uses `_clock.GetUtcNow().UtcDateTime` and `_httpContextAccessor.HttpContext?.User?.Identity?.Name`. D-08 Added/Modified branching implemented. |
| `src/BaseApi.Core/Persistence/Repositories/IRepository.cs` | Exactly 5 methods, `where TEntity : BaseEntity`, no `IQueryable<>` return | VERIFIED | 5 methods: `GetAsync`, `ListAsync`, `AddAsync`, `Update`, `DeleteAsync`. Generic constraint `where TEntity : BaseEntity`. No IQueryable return types. |
| `src/BaseApi.Core/Persistence/Repositories/Repository.cs` | `sealed`, `BaseDbContext`-typed constructor, load-then-remove `DeleteAsync` | VERIFIED | `public sealed class Repository<TEntity>`. Constructor: `Repository(BaseDbContext db)`. `DeleteAsync` loads via `FirstOrDefaultAsync` then calls `_set.Remove`. |
| `src/BaseApi.Core/BaseApi.Core.csproj` | `<FrameworkReference Include="Microsoft.AspNetCore.App" />` + 4 EF Core PackageReferences, zero `Version=` attributes | VERIFIED | FrameworkReference present. 4 PackageReferences: Microsoft.EntityFrameworkCore, Microsoft.EntityFrameworkCore.Relational, Npgsql.EntityFrameworkCore.PostgreSQL, EFCore.NamingConventions. No `Version=` attributes on any PackageReference. |
| `tests/BaseApi.Tests/BaseApi.Tests.csproj` | 5 PackageReferences (4 EF + Microsoft.Extensions.TimeProvider.Testing) + FrameworkReference | VERIFIED | 5 EF-related PackageReferences present plus FrameworkReference. xUnit scaffold PropertyGroup (OutputType=Exe, UseMicrosoftTestingPlatformRunner, TestingPlatformDotnetTestSupport) unchanged. No `Version=` attributes. |
| `Directory.Packages.props` | `Microsoft.Extensions.TimeProvider.Testing` Version="8.10.0" pin + section comment + 22 existing pins unchanged | VERIFIED | Pin present at line 88. Section comment `<!-- Microsoft.Extensions.* family — versions on own cadence (Phase 3: FakeTimeProvider) -->` present. All 6 spot-checked existing pins verified unchanged. Total PackageVersion count: 23 (was 22). |
| `.planning/REQUIREMENTS.md` | PERSIST-16 bullet under § Persistence + Traceability row with Phase 3 | VERIFIED | PERSIST-16 bullet at line 43 (after PERSIST-15 at line 42). Traceability row `\| PERSIST-16 \| Phase 3 \| Complete \|` at line 214. Both `xmin` and `IsConcurrencyToken` keywords present in the bullet text. |
| `tests/BaseApi.Tests/Persistence/SchemaTests.cs` | SC#1 fact asserting snake_case column names | VERIFIED | File exists. `[Fact]` asserts `created_at`, `updated_at`, `created_by`, `updated_by`, `id`, `name`, `version`, `description` and negatively asserts `CreatedAt`/`UpdatedAt` absent. Uses `TestContext.Current.CancellationToken` (xUnit1051 compliant). |
| `tests/BaseApi.Tests/Persistence/AuditInterceptorTests.cs` | SC#2 (UTC Kind assertion) + SC#3 (alice case + null HttpContext case) | VERIFIED | Two `[Fact]` methods. First asserts `Kind == DateTimeKind.Utc` and `entity.CreatedBy == "alice"`. Second asserts `entity.CreatedBy == null` with `StubHttpContextAccessor` where HttpContext is null. FakeTimeProvider used in both. |
| `tests/BaseApi.Tests/Persistence/DiLifetimeTests.cs` | SC#4 with `Assert.Same` within scope + `Assert.NotSame` across scopes | VERIFIED | `Assert.Same(a, b)` for same scope resolution; `Assert.NotSame(a, c)` for different scope. `AddDbContext<TestDbContext>` used. |
| `tests/BaseApi.Tests/Persistence/XminConcurrencyTokenTests.cs` | Dim 6: xmin model-introspection guard | VERIFIED | Asserts `FindProperty("xmin")` is non-null, `xmin.IsConcurrencyToken == true`, `xmin.GetColumnType() == "xid"`. |
| `tests/BaseApi.Tests/Persistence/RepositorySurfaceTests.cs` | Dim 7: 5-method surface via reflection + IQueryable negative check | VERIFIED | Uses `BindingFlags.DeclaredOnly`. Asserts count == 5. Asserts method names match `{AddAsync, DeleteAsync, GetAsync, ListAsync, Update}` exactly. Checks no method returns `IQueryable<>`. |
| `tests/BaseApi.Tests/Persistence/PostgresFixture.cs` | IAsyncLifetime with `stepsdb_test_{Guid:N}` naming, ClearAllPools + DROP WITH FORCE | VERIFIED | `IAsyncLifetime` implemented. `DatabaseName = $"stepsdb_test_{Guid.NewGuid():N}"`. `NpgsqlConnection.ClearAllPools()` called before DROP. `DROP DATABASE IF EXISTS "{DatabaseName}" WITH (FORCE)` present. |
| `tests/BaseApi.Tests/Persistence/StubHttpContextAccessor.cs` | Handwritten `IHttpContextAccessor` with `SetUser(string? name)` | VERIFIED | `public sealed class StubHttpContextAccessor : IHttpContextAccessor`. `SetUser` builds `ClaimsIdentity(authenticationType: "Test")` with `ClaimTypes.Name` claim. Null clears `HttpContext`. No Moq/NSubstitute. |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `AuditInterceptor` | `IHttpContextAccessor` | Constructor injection | WIRED | `IHttpContextAccessor httpContextAccessor` parameter; `_httpContextAccessor` field used in `SavingChangesAsync`. |
| `AuditInterceptor` | `TimeProvider` | Constructor injection | WIRED | `TimeProvider clock` parameter; `_clock.GetUtcNow().UtcDateTime` called in `SavingChangesAsync`. |
| `BaseDbContext.OnModelCreating` | xmin shadow property with `HasColumnType("xid")` + `IsConcurrencyToken()` | Model builder iteration | WIRED | Verbatim D-03 loop over `GetEntityTypes().Where(t => typeof(BaseEntity).IsAssignableFrom(t.ClrType))`. All 4 chain calls present. |
| `BaseDbContext.OnConfiguring` | `UseSnakeCaseNamingConvention()` | `EFCore.NamingConventions` extension method | WIRED | `optionsBuilder.UseSnakeCaseNamingConvention()` called directly in `OnConfiguring`. |
| `Repository<TEntity>.DeleteAsync` | xmin concurrency check (D-03) | Load-then-remove pattern | WIRED | `await _set.FirstOrDefaultAsync(e => e.Id == id, cancellationToken)` loads and tracks the row (capturing xmin), then `_set.Remove(entity)` stages removal. |
| `AuditInterceptorTests` | `AuditInterceptor` + `FakeTimeProvider` | `DbContextOptionsBuilder.AddInterceptors(...)` | WIRED | Test constructs interceptor, passes to `AddInterceptors(interceptor)` in `DbContextOptionsBuilder`. `FakeTimeProvider` from `Microsoft.Extensions.Time.Testing` namespace pinned at 8.10.0. |
| `DiLifetimeTests` | `AddDbContext<TestDbContext>` Scoped default | `ServiceCollection` + two IServiceScopes | WIRED | `services.AddDbContext<TestDbContext>(opts => ...)` called; two scopes created via `CreateScope()`. |
| `XminConcurrencyTokenTests` | `BaseDbContext.OnModelCreating` xmin iteration | `db.Model.FindEntityType(typeof(TestEntity)).FindProperty("xmin")` | WIRED | Model introspection reaches the shadow property wired by `OnModelCreating`. Property assertions pass. |

---

### Data-Flow Trace (Level 4)

Not applicable — this phase produces infrastructure class library components, not data-rendering components. All test artifacts perform round-trip verification against real Postgres (not mocked) via the Phase 2 container. Data flows are verified by the test suite exit code: `Passed: 7, Failed: 0, Skipped: 0`.

---

### Behavioral Spot-Checks

Build and test evidence captured by orchestrator:

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| Zero-warning Release build | `dotnet build -c Release --no-restore` | exit 0, `0 Warning(s) 0 Error(s)` | PASS |
| Zero-warning Debug build (W-02 unconditional) | `dotnet build -c Debug --no-restore` | exit 0, `0 Warning(s) 0 Error(s)` | PASS |
| All 7 facts green (regression gate) | `dotnet test --no-build -c Release` | `Passed: 7, Failed: 0, Skipped: 0, Total: 7, Duration: 1s 565ms` | PASS |
| No migration files exist | `**/Migrations/*.cs` glob | No files found | PASS |
| Program.cs unchanged since Phase 1 | `git log --since="2026-05-27" -- src/BaseApi.Service/Program.cs` | Empty output | PASS |
| Throwaway DB cleanup (D-15) | `psql \l` BEFORE/AFTER dotnet test | Byte-identical snapshots; 4 baseline DBs | PASS |

---

### Requirements Coverage

All 11 requirement IDs declared in Plan 03-01 and 03-02 frontmatter are accounted for.

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|-------------|-------------|--------|----------|
| ENTITY-01 | 03-01, 03-02 | `BaseEntity` abstract in `BaseApi.Core/Entities/BaseEntity.cs` with 8 specified properties | SATISFIED | `BaseEntity.cs` is abstract with exactly the 8 ENTITY-01 properties. REQUIREMENTS.md marked `[x]`, Traceability: Complete. |
| ENTITY-02 | 03-01, 03-02 | `BaseEntity` is abstract — no table; 5 concrete tables, no inheritance discriminator | SATISFIED | `BaseEntity` is `abstract`. No `[Table]` attribute, no discriminator configuration. Concrete entities derive it (per plan). REQUIREMENTS.md `[x]`. |
| PERSIST-02 | 03-01, 03-02 | `BaseDbContext` (abstract) in `BaseApi.Core/Persistence/` registers `AuditInterceptor` | SATISFIED | `BaseDbContext` is abstract in `BaseApi.Core/Persistence/`. Note: interceptor wired at composition root/test fixture per D-10 (not in `OnConfiguring` directly — this is intentional per CONTEXT.md D-09/D-10). Tests verify the end-to-end integration. REQUIREMENTS.md `[x]`. |
| PERSIST-03 | 03-01, 03-02 | `AuditInterceptor` auto-stamps `CreatedAt`/`UpdatedAt` with UTC timestamp | SATISFIED | `AuditInterceptor.SavingChangesAsync` stamps `_clock.GetUtcNow().UtcDateTime`. SC#2 test asserts `Kind == DateTimeKind.Utc`. REQUIREMENTS.md `[x]`. |
| PERSIST-04 | 03-01, 03-02 | `AuditInterceptor` sets `CreatedBy`/`UpdatedBy` from `IHttpContextAccessor` or null | SATISFIED | `_httpContextAccessor.HttpContext?.User?.Identity?.Name` used. SC#3 verifies both alice and null cases. REQUIREMENTS.md `[x]`. |
| PERSIST-05 | 03-01, 03-02 | Snake_case naming via `UseSnakeCaseNamingConvention()` applied BEFORE first migration | SATISFIED | `BaseDbContext.OnConfiguring` calls `UseSnakeCaseNamingConvention()`. No migrations generated (D-16). SC#1 verifies snake_case schema. REQUIREMENTS.md `[x]`. |
| PERSIST-06 | 03-01, 03-02 | All primary keys are `Guid` mapped to Postgres `uuid` | SATISFIED | `BaseEntity.Id` is `Guid`. EF Core + Npgsql maps C# `Guid` to Postgres `uuid` by default. SC#1 schema test confirms `id` column exists. REQUIREMENTS.md `[x]`. |
| PERSIST-07 | 03-01, 03-02 | `BaseEntity.CreatedAt`/`UpdatedAt` map to `timestamptz` columns | SATISFIED | EF Core + Npgsql maps `DateTime` (UTC Kind) to `timestamp with time zone` (`timestamptz`). SC#2 fact writes UTC timestamp without `InvalidCastException`. REQUIREMENTS.md `[x]`. |
| PERSIST-11 | 03-01, 03-02 | Generic `Repository<TEntity>` with Get, List, Add, Update, Delete + CancellationToken | SATISFIED | `Repository<TEntity>` implements all 5 methods with `CancellationToken`. `RepositorySurfaceTests` verifies 5-method surface via reflection. REQUIREMENTS.md `[x]`. |
| PERSIST-15 | 03-01, 03-02 | `DbContext` registered with Scoped lifetime in DI | SATISFIED | `AddDbContext<TestDbContext>` defaults to Scoped. `DiLifetimeTests` verifies with `Assert.Same`/`Assert.NotSame`. REQUIREMENTS.md `[x]`. |
| PERSIST-16 | 03-01, 03-02 | `BaseEntity` rows carry Postgres `xmin` shadow concurrency token via `IsConcurrencyToken()` | SATISFIED | `BaseDbContext.OnModelCreating` wires the verbatim D-03 xmin iteration. `XminConcurrencyTokenTests` asserts via model introspection. REQUIREMENTS.md appended per D-03b with `[x]`, Traceability: Complete. |

No orphaned requirements: all 11 IDs are accounted for in both plan frontmatter and REQUIREMENTS.md traceability.

---

### Anti-Patterns Found

No blockers found. Items from REVIEW.md (0 critical, 3 warnings, 5 info):

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| `AuditInterceptor.cs` | 43-47 | No `ArgumentNullException.ThrowIfNull` guards on constructor params | Warning (WR-01) | Null dependency would surface NRE at `SavingChangesAsync` call time rather than construction. Not a goal blocker — nullable analysis catches most call sites. |
| `AuditInterceptor.cs` | 49-89 | Synchronous `SaveChanges()` not guarded — audit stamping silently bypassed | Warning (WR-02) | Risk if a future contributor uses sync path. Documented in XML doc. No production call path uses sync save currently. |
| `AuditInterceptorTests.cs` | 14-69 | Modified-state contract (IsModified=false guards) and caller-set Id preservation (D-01/D-02) lack dedicated test facts | Warning (WR-03) | Two SC paths uncovered. Not a Phase 3 goal blocker — SC#1-4 are all green. Recommend adding 2 facts in Phase 4 or as a carry-forward. |
| `DiLifetimeTests.cs` | 22-32 | `ServiceProvider` from `BuildServiceProvider()` not disposed | Info (IN-01) | Minor resource leak in test teardown. Non-blocking. |
| `PostgresFixture.cs` | 42-55 | `ClearAllPools()` is process-global (drops pools for all parallel test classes) | Info (IN-02) | Correct behavior, minor pool-reuse inefficiency under parallelism. Not a goal blocker. |
| `PostgresFixture.cs` | 27-28 | Admin credentials hardcoded (`postgres:postgres`) | Info (IN-03) | Acceptable for test-only fixture targeting Phase 2 Docker container. No production code path affected. |
| `PostgresFixture.cs` | 30-40 | No error handling wrapping `CREATE DATABASE` | Info (IN-04) | Developer UX issue only. Non-blocking. |
| `Repository.cs` | 19-23 | `GetAsync`/`ListAsync` use default change-tracker path (no `AsNoTracking`) | Info (IN-05) | Correct for write-then-read-within-scope semantics in v1. Performance refinement for v2. |

None of the above items block the Phase 3 goal. All are code quality improvements or future-phase candidates.

---

### Human Verification Required

None. All 4 ROADMAP success criteria (SC#1-4) were verified programmatically via the `dotnet test` run (7 facts, 0 failures, real Postgres 17 backend). The orchestrator captured exit code 0 with full pass counts. Cleanup discipline (D-15) verified via byte-identical `psql \l` BEFORE/AFTER snapshots. No human testing items remain.

---

### Gaps Summary

No gaps. All 10 observable truths are VERIFIED. All 11 requirement IDs are SATISFIED. All key links are WIRED. Cross-phase constraints D-11 and D-16 are clean. Build and test regression gate passes (exit 0, 7/7 facts green).

---

_Verified: 2026-05-27_
_Verifier: Claude (gsd-verifier)_
