---
phase: 07-generic-http-base-composition-root
fixed_at: 2026-05-27T00:00:00Z
fix_scope: all
findings_in_scope: 14
fixed: 14
skipped: 0
iteration: 1
status: all_fixed
---

# Phase 7: Code Review Fix Report

**Fixed at:** 2026-05-27
**Source review:** `.planning/phases/07-generic-http-base-composition-root/07-REVIEW.md`
**Iteration:** 1

**Summary:**
- Findings in scope: 14 (0 Critical / 5 Warning / 9 Info — `--all` scope)
- Fixed: 14
- Skipped: 0
- Build status after all fixes: GREEN (0 warnings, 0 errors, Release configuration)
- Test status after all fixes: 98/98 PASS (xUnit v3, `dotnet test SK_P.sln`)

## Fixed Issues

### WR-01: `BaseService._logger` is assigned but never read

**Files modified:** `src/BaseApi.Core/Services/BaseService.cs`, `tests/BaseApi.Tests/Composition/RecordingTestService.cs`, `tests/BaseApi.Tests/Services/BaseServiceOrderingFacts.cs`
**Commit:** `f472af5`
**Path chosen:** B (drop the field + ctor parameter entirely — per workflow instructions).
**Applied fix:** Removed `private readonly ILogger _logger` field, dropped the `ILogger<BaseService<...>>` ctor parameter and its null guard, removed the `Microsoft.Extensions.Logging` using directive from BaseService.cs. Updated `RecordingTestService` ctor + base call (5 args now) and `BaseServiceOrderingFacts.CreateAsync_Runs_SixSteps_In_LockedOrder` constructor call to match. Class XML doc updated to note concrete services may take their own typed `ILogger<TConcreteService>`.

---

### WR-02: `BaseServiceOrderingFacts` SaveChanges path writes Kind=Unspecified timestamp

**Files modified:** `tests/BaseApi.Tests/Services/BaseServiceOrderingFacts.cs`
**Commit:** `09ffd4a`
**Option chosen:** A (pre-stamp CreatedAt/UpdatedAt on the mock entity — per workflow instructions; isolates SC#2 from audit-stamping contract).
**Applied fix:** In `CreateAsync_Runs_SixSteps_In_LockedOrder`, captured `var now = DateTime.UtcNow` and assigned both `CreatedAt = now, UpdatedAt = now` on the `mappedEntity` initializer alongside the existing `Id/Name/Version/Note` properties. The 6-step ordering assertions (`Received.InOrder` block + `EntityState.Added` check + `validateTime <= mapTime <= addTime <= junctionTime <= readTime` chain) are unchanged — the fix only adds Kind=Utc timestamps to the entity that hits Step 5 SaveChangesAsync; it does NOT alter the proof.

---

### WR-03: `Service:Name` / `Service:Version` config access inconsistent

**Files modified:** `src/BaseApi.Core/Configuration/RequiredConfig.cs` (NEW), `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs`, `src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs`, `src/BaseApi.Core/DependencyInjection/PersistenceServiceCollectionExtensions.cs`
**Commit:** `6b0901c`
**Applied fix:** Added `internal static class RequiredConfig` with two extension methods: `Require(this IConfiguration cfg, string key)` and `RequireConnectionString(this IConfiguration cfg, string name)`. Both throw `InvalidOperationException` with an actionable message naming the missing key + remediation hint ("Set it via appsettings.json, environment variables, or user secrets. See README.md."). Applied at four sites:
- `ObservabilityServiceCollectionExtensions.cs:41-42` — `cfg["Service:Name"]!` and `cfg["Service:Version"]!` → `cfg.Require("Service:Name")` / `cfg.Require("Service:Version")`
- `HealthServiceCollectionExtensions.cs:23` — `cfg.GetConnectionString("Postgres")!` → `cfg.RequireConnectionString("Postgres")`
- `PersistenceServiceCollectionExtensions.cs:28` — `cfg.GetConnectionString("Postgres")` (no `!`) → `cfg.RequireConnectionString("Postgres")`

Swagger's `?? "sk-api"` fallback at `ConfigureSwaggerOptions.cs:33` is intentionally preserved per workflow instructions (Swagger doc title is operationally non-critical).

---

### WR-04: `ProgramMinimalityFacts.ProgramCsContent()` brittle relative path

**Files modified:** `tests/BaseApi.Tests/Composition/ProgramMinimalityFacts.cs`
**Commit:** `856c013`
**Applied fix:** Replaced the hardcoded `Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ...)` traversal with a new `FindRepoRoot()` helper that walks `DirectoryInfo.Parent` chain from `AppContext.BaseDirectory` looking for `SK_P.sln` (marker file guaranteed at repo root). Extracted `ProgramCsPath()` so both `ProgramCsContent()` and `ProgramCs_BodyLines_LessThan_OrEqualTo_Ten` share the same anchor — removes the duplicated five-level traversal previously inlined in the body-lines fact.

---

### WR-05: `Phase7WebAppFactory.DisposeAsync` interacts poorly with Npgsql pool

**Files modified:** `tests/BaseApi.Tests/Composition/Phase7WebAppFactory.cs`
**Commit:** `6075a07`
**Option chosen:** B (per-factory ClearPool — workflow explicitly flagged as "correct" fix).
**Applied fix:** Added `using Npgsql;` directive. In `DisposeAsync`, before `await _fixture.DisposeAsync()`, opens an `await using NpgsqlConnection(_fixture.ConnectionString)` (never opened — just used to identify the pool) and calls `NpgsqlConnection.ClearPool(conn)` on it. This narrows the pool-clear blast radius to THIS factory's connection only, so parallel xUnit v3 test classes don't lose their pooled connections to a process-global `ClearAllPools()`.

---

### IN-01: `SyncJunctionsAsync` `default` parameters lack runtime enforcement

**Files modified:** `src/BaseApi.Core/Services/BaseService.cs`, `src/BaseApi.Core/Controllers/BaseController.cs`
**Commit:** `ea9d395`
**Option chosen:** (b) — class-level `where TCreate : class where TUpdate : class` constraint (one-line type-system fix; not the abstract-hook split, which is a Phase 8 API change per workflow instructions).
**Applied fix:** Added two new generic constraints to `BaseService<TEntity, TCreate, TUpdate, TRead>`. The compile-then-fix loop surfaced a CS0452 propagation requirement: `BaseController<TEntity, TCreate, TUpdate, TRead>` consumes BaseService and had to receive the same `where TCreate : class where TUpdate : class` constraints. Both files updated; build clean.

---

### IN-02: `Repository<TEntity>.AddAsync` async wrapper allocates state machine

**Files modified:** `src/BaseApi.Core/Persistence/Repositories/Repository.cs`
**Commit:** `9b11174`
**Applied fix:** Changed
```csharp
public async Task AddAsync(TEntity entity, CancellationToken cancellationToken)
    => await _set.AddAsync(entity, cancellationToken);
```
to the synchronous variant:
```csharp
public Task AddAsync(TEntity entity, CancellationToken cancellationToken)
{
    _set.Add(entity);
    return Task.CompletedTask;
}
```
Interface signature (`Task AddAsync(...)`) and `IRepository<T>` contract are preserved. Comment added explaining the rationale (EF Core's AddAsync is only truly async for HiLo/sequence value generators; our Guid-keyed BaseEntity model completes synchronously).

---

### IN-03: `AddBaseApiFacts.BuildServices` constructs throwaway ServiceProvider

**Files modified:** `tests/BaseApi.Tests/Composition/AddBaseApiFacts.cs`
**Commit:** `a536027`
**Applied fix:** Reordered `BuildServices()` to construct `var cfg = new ConfigurationBuilder().AddInMemoryCollection(...).Build()` first, then `services.AddSingleton<IConfiguration>(cfg)`, then `services.AddBaseApi<AppDbContext>(cfg)`. Removed the throwaway `services.BuildServiceProvider().GetRequiredService<IConfiguration>()` round-trip — the variable it produced was reference-equal to the IConfiguration just registered, so the temp ServiceProvider was a no-op today and a latent every-singleton-allocation risk on future expansion.

---

### IN-04: Hardcoded Postgres credentials in `AddBaseApiFacts`

**Files modified:** `tests/BaseApi.Tests/Composition/AddBaseApiFacts.cs`
**Commit:** `db1fd32`
**Applied fix:** Added a private static `TestConnectionString` property built from `NpgsqlConnectionStringBuilder` reading `POSTGRES_USER` / `POSTGRES_PASSWORD` environment variables with the compose.yaml dev defaults (`postgres`/`postgres`) as fallback. The hardcoded literal `"Host=localhost;Port=5433;...Password=postgres"` is replaced with `TestConnectionString` reference. Added `using Npgsql;` directive.

---

### IN-05: Per-test factory construction creates fresh PostgresFixture per fact

**Files modified:** `tests/BaseApi.Tests/Composition/UseBaseApiPipelineFacts.cs`, `tests/BaseApi.Tests/Versioning/VersioningFacts.cs`, `tests/BaseApi.Tests/Services/NotFoundFacts.cs`, `tests/BaseApi.Tests/Swagger/SwaggerEnvironmentFacts.cs`
**Commit:** `1fba436`
**Applied fix:** Converted all four test classes to receive `Phase7WebAppFactory` via `IClassFixture<Phase7WebAppFactory>` constructor injection instead of `await using var factory = new Phase7WebAppFactory(); await factory.InitializeAsync();` per `[Fact]`. xUnit invokes `IAsyncLifetime.InitializeAsync` on the shared fixture before any fact runs and `DisposeAsync` after all facts finish — single throwaway Postgres DB per class instead of one per fact.

For `SwaggerEnvironmentFacts`: only the two Development-side facts use the shared `Phase7WebAppFactory`. The two Production-side facts still construct `ProductionWebAppFactory` per-fact because that class is `internal sealed` (incompatible with `IClassFixture<T>`'s public-type requirement) and has no DB lifecycle to amortize. Inline comment documents the asymmetry. Full test run after this commit: 98/98 GREEN, no behavioral change.

---

### IN-06: `SwaggerEnvironmentFacts` uses `using` instead of `await using`

**Files modified:** `tests/BaseApi.Tests/Swagger/SwaggerEnvironmentFacts.cs`
**Commit:** `11404f6`
**Applied fix:** `replace_all` over the file changed all four `using var factory = new ProductionWebAppFactory();` instances (lines 45, 49, 60, 64 in the original file) to `await using var factory = new ProductionWebAppFactory();`. This matches the convention already in place for sibling `Phase7WebAppFactory` consumers and ensures async cleanup paths (e.g., async hosted-service StopAsync) run via `IAsyncDisposable.DisposeAsync` rather than the sync `IDisposable.Dispose`.

Note: this commit chronologically preceded IN-05; the IN-05 rewrite of `SwaggerEnvironmentFacts.cs` preserved the `await using` form for the two Production-side facts that remain per-fact.

---

### IN-07: `Phase7WebAppFactory` double-registers app part

**Files modified:** `tests/BaseApi.Tests/Composition/Phase7WebAppFactory.cs`
**Commit:** `46df4f5`
**Applied fix:** Removed the duplicate `services.AddControllers().AddApplicationPart(typeof(Phase7WebAppFactory).Assembly);` line at line 95 — the base `WebAppFactory.ConfigureWebHost` already registers this assembly's application part (`ApplicationPartManager` dedupes by reference equality, so the duplicate was a no-op but misleading). Updated the class-level XML summary to remove the (a) bullet's misleading "so TestsController is discovered" claim, noting instead that the base class handles app-part registration.

---

### IN-08: `BaseService` exception messages use `.Name` rather than `.FullName`

**Files modified:** `src/BaseApi.Core/Services/BaseService.cs`
**Commit:** `9ff8dee`
**Applied fix:** Changed all four `typeof(TCreate).Name` / `typeof(TUpdate).Name` references in the two `InvalidOperationException` messages for missing validators to `.FullName` (namespace-qualified). Inline comment notes the motivation (Tests project already has type-name collisions across namespaces: `BaseApi.Tests.Persistence.TestEntity` vs `BaseApi.Tests.Validation.TestEntity`).

---

### IN-09: `Phase7WebAppFactory` is `public class` not `sealed`

**Files modified:** `tests/BaseApi.Tests/Composition/Phase7WebAppFactory.cs`
**Commit:** `61a4869`
**Applied fix:** Changed class declaration from `public class Phase7WebAppFactory : WebAppFactory, IAsyncLifetime` to `public sealed class Phase7WebAppFactory : WebAppFactory, IAsyncLifetime`. The class remains `public` (required by `IClassFixture<T>` per IN-05, which committed after this); only the `sealed` modifier is added. No subclasses exist in the repo, so the change is contract-tightening only.

---

_Fixed: 2026-05-27_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
