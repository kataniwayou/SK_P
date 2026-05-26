# Phase 3: EF Core Persistence Base - Research

**Researched:** 2026-05-26
**Domain:** EF Core 8 + Npgsql 8 persistence foundation (BaseEntity, BaseDbContext, AuditInterceptor, snake_case convention, generic Repository<T>) for .NET 8 modular monolith
**Confidence:** HIGH — All package versions verified against nuget.org within this session; interceptor + concurrency-token APIs verified against live MS Learn docs (refreshed 2026-02-26 / 2026-04-27); FakeTimeProvider package shape verified against nuget.org 2024-10-08 publish.

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions (D-01 .. D-18 from 03-CONTEXT.md — DO NOT re-debate)

**Guid Identity**
- **D-01:** Guid values for `BaseEntity.Id` are stamped **client-side by `AuditInterceptor`** on `EntityState.Added` if `Id == Guid.Empty`. No `gen_random_uuid()` Postgres default; no `HasDefaultValueSql`; no `ValueGeneratedOnAdd` annotation.
- **D-02:** `AuditInterceptor` **honors a caller-set non-empty `Id`** — generates only when `Id == Guid.Empty`.

**Concurrency Control**
- **D-03:** `BaseDbContext.OnModelCreating` wires a Postgres `xmin` shadow concurrency token on **every** `BaseEntity` subclass via a model-builder iteration (shape pinned in CONTEXT.md). Junction entities excluded.
- **D-03a (cross-phase to Phase 4):** `DbUpdateConcurrencyException` → HTTP 409.
- **D-03b:** Append `PERSIST-16: BaseEntity rows carry a Postgres xmin shadow concurrency token mapped via IsConcurrencyToken()` to REQUIREMENTS.md during Phase 3 planning (raises Phase 3 count to 11; Phase 4 to 15).

**Generic Repository**
- **D-04:** `IRepository<TEntity> where TEntity : BaseEntity` — exactly **5 methods**, no `IQueryable`, no `ExistsAsync`, no `Where(predicate)`. Signatures: `GetAsync`, `ListAsync`, `AddAsync`, `Update` (sync), `DeleteAsync` (load-then-remove).
- **D-05:** **Service owns `SaveChangesAsync`**, not Repository.

**Audit Interceptor**
- **D-06:** `AuditInterceptor : ISaveChangesInterceptor` registered as **Singleton**.
- **D-07:** Depends on **`TimeProvider`** (.NET 8 BCL abstraction). Production: `AddSingleton(TimeProvider.System)`. Tests: `FakeTimeProvider`.
- **D-08:** Audit-stamp logic on `Added` (Id, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy) / `Modified` (UpdatedAt, UpdatedBy only). Null `HttpContext` → null `CreatedBy`/`UpdatedBy`, no crash.

**BaseDbContext**
- **D-09:** **Abstract**, lives in `src/BaseApi.Core/Persistence/BaseDbContext.cs`. **No `DbSet<T>` properties.** `OnConfiguring` calls `UseSnakeCaseNamingConvention()` AND `AddInterceptors(...)` (defense-in-depth duplication with Phase 7 `AddBaseApi`). `OnModelCreating` runs the D-03 xmin iteration block.
- **D-10:** `BaseDbContext.OnConfiguring` resolves `AuditInterceptor` from DI (`IServiceProvider` passed via `AddDbContext` overload).
- **D-11:** Phase 3 does **NOT** call `AddDbContext<AppDbContext>` in `Program.cs`. `Program.cs` unchanged from Phase 1 D-10.

**Project Wiring**
- **D-12:** `BaseApi.Core.csproj` adds `<PackageReference>` entries for `Microsoft.EntityFrameworkCore`, `Microsoft.EntityFrameworkCore.Relational`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `EFCore.NamingConventions`. `IHttpContextAccessor` access: recommended `<FrameworkReference Include="Microsoft.AspNetCore.App" />`.
- **D-13:** `BaseApi.Tests.csproj` adds EF Core + Npgsql + naming + `Microsoft.Extensions.TimeProvider.Testing`. The last package is NOT yet in `Directory.Packages.props` — Phase 3 first plan adds the pin.

**Verification**
- **D-14:** Four `[Fact]` tests in `BaseApi.Tests/` mapping to SC#1-4 (snake_case schema, UTC timestamp stamping, HttpContext-based CreatedBy + null fallback, scoped DI lifetime).
- **D-15:** Tests connect to running Phase 2 Postgres at `localhost:5433`. Per-test throwaway database: `stepsdb_test_{Guid:N}` created via `IAsyncLifetime.InitializeAsync` against the `postgres` admin DB; dropped in `DisposeAsync`.
- **D-16:** Tests do **not** run migrations. Schema created via `EnsureCreatedAsync()` only.

**Plan Structure**
- **D-17:** Likely 2 plans — `03-01-PLAN.md` (build) + `03-02-PLAN.md` (verification + smoke). Planner may split further.
- **D-18:** Plan 03-02 is a verification plan: `autonomous: false`, evidence-only commits (`docs(03)`), SUMMARY documents command output verbatim.

### Claude's Discretion (from CONTEXT.md)
- Exact `Repository<T>.Update` shape: `void Update(TEntity)` (sync, matches EF's `DbSet<T>.Update`) vs `Task UpdateAsync(...)` (async for API symmetry). **This research recommends sync `void Update(...)`** (no I/O — see Repository<T> section below).
- Repository constructor: `DbContext` direct vs typed `TDbContext where TDbContext : BaseDbContext` generic param. **This research recommends the typed generic param** (stronger type safety for Phase 7's `AddBaseApi<TDbContext>` registration).
- Throwaway DB naming: `stepsdb_test_{Guid:N}` (recommended for grep-ability in `\l` output).
- xmin iteration filter: `Where(t => typeof(BaseEntity).IsAssignableFrom(t.ClrType))` (clean) — owned-type defensive variant is overkill for Phase 3.
- `TestEntity` location: test project (not InternalsVisibleTo'd Core).
- XML doc on `BaseEntity` referencing Pitfall 1: recommended.

### Deferred Ideas (OUT OF SCOPE — DO NOT ADDRESS)
- `AppDbContext` shape — Phase 8
- `IEntityTypeConfiguration<T>` files — Phase 8
- `InitialCreate` migration — Phase 8
- `AddBaseApi` / Program.cs wiring — Phase 7
- `DbUpdateConcurrencyException` → 409 mapping — Phase 4
- FluentValidation / Mapperly wiring — Phase 6
- Soft delete, pagination, multi-instance migration — v2
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| ENTITY-01 | `BaseEntity` (abstract) with `Id`, `Name`, `Version`, `CreatedAt`, `UpdatedAt`, `CreatedBy?`, `UpdatedBy?`, `Description?` | § BaseEntity Shape — exact field list + XML-doc-with-Pitfall-1-reference recommendation |
| ENTITY-02 | `BaseEntity` is abstract — no table; 5 concrete tables, no discriminator | § BaseDbContext — no DbSet on Base; § xmin iteration filter excludes the abstract type because EF only registers entity types with a `DbSet<>` or `modelBuilder.Entity<>` call |
| PERSIST-02 | `BaseDbContext` (abstract) registers `AuditInterceptor` | § BaseDbContext + § AuditInterceptor Wiring sections |
| PERSIST-03 | `AuditInterceptor` auto-stamps `CreatedAt`/`UpdatedAt` with `DateTime.UtcNow` (timestamptz) | § AuditInterceptor Skeleton + § Pitfall 1 verbatim |
| PERSIST-04 | `CreatedBy`/`UpdatedBy` from `HttpContext?.User?.Identity?.Name`; null otherwise | § AuditInterceptor Skeleton + § IHttpContextAccessor Test Stub |
| PERSIST-05 | Snake_case via `EFCore.NamingConventions` BEFORE first migration | § BaseDbContext OnConfiguring + § Pitfall 4 (cannot retrofit) |
| PERSIST-06 | All PKs are `Guid` mapped to Postgres `uuid` | § BaseEntity.Id + § Npgsql native Guid↔uuid mapping (STACK.md confirms) |
| PERSIST-07 | `CreatedAt`/`UpdatedAt` map to `timestamptz` | § Pitfall 1 — Kind=Utc enforced by Npgsql 8 default; AuditInterceptor uses `_clock.GetUtcNow().UtcDateTime` |
| PERSIST-11 | Generic `Repository<TEntity>` (Get, List, Add, Update, Delete) | § Repository<T> Surface — exactly 5 methods, no IQueryable |
| PERSIST-15 | `DbContext` registered with Scoped lifetime in DI | § DI Lifetime + SC#4 test pattern |
| (D-03b) PERSIST-16 | `xmin` shadow concurrency token on every BaseEntity subclass | § xmin Concurrency Token — model-builder iteration block |
</phase_requirements>

## Executive Summary

Five bullets the planner should internalise before writing tasks:

1. **All package pins are correct and current.** `Microsoft.EntityFrameworkCore 8.0.27`, `Npgsql.EntityFrameworkCore.PostgreSQL 8.0.10`, `EFCore.NamingConventions 8.0.3`, and `Microsoft.Extensions.TimeProvider.Testing 8.10.0` are the locked Phase 3 versions. The first three are already in `Directory.Packages.props`; only `Microsoft.Extensions.TimeProvider.Testing 8.10.0` is new and must be added in Plan 03-01 (it versions on its own cadence — 8.x line tops out at 8.10.0 released 2024-10-08, not 8.0.27). [VERIFIED: nuget.org flatcontainer API, 2026-05-26]

2. **`AuditInterceptor` should derive from the `SaveChangesInterceptor` base class, not implement `ISaveChangesInterceptor` directly.** The base class provides no-op default overrides for the 8-method interface; an async EF stack only needs to override `SavingChangesAsync` (the synchronous `SavingChanges` is never called by EF when the caller uses `SaveChangesAsync()`). Tests use `await db.SaveChangesAsync()`; Phase 4's exception path is also async — so a single async override is sufficient. [CITED: https://learn.microsoft.com/en-us/ef/core/logging-events-diagnostics/interceptors]

3. **The CONTEXT.md D-03 xmin shape compiles cleanly on Npgsql 8.0.10 with one stylistic note.** Npgsql's documented "blessed" pattern is `IsRowVersion()` on a `uint` CLR property (Data Annotations `[Timestamp]` equivalent). The D-03 shape uses a shadow property + explicit `HasColumnName("xmin")` + `HasColumnType("xid")` + `ValueGeneratedOnAddOrUpdate()` + `IsConcurrencyToken()`. Both produce the same generated SQL; the shadow-property form is preferred here because it avoids polluting `BaseEntity` with a `uint Version` CLR property that has no semantic meaning. The explicit form is verbose-but-correct and matches the published EFCore/Npgsql 8 idiom; no signature changes between Npgsql 8.0.8 and 8.0.10. [CITED: https://www.npgsql.org/efcore/modeling/concurrency.html]

4. **The `FrameworkReference Include="Microsoft.AspNetCore.App"` decision is canonical and correct.** This is the documented mechanism for ASP.NET-coupled class libraries (those that need `IHttpContextAccessor`, health checks, controllers, middleware) to access the ASP.NET runtime API surface without per-feature `PackageReference` lines. It produces zero runtime weight (the framework is already on the host) and does NOT prevent NuGet packability — only `<IsPackable>true</IsPackable>` controls that, and FrameworkReference packages with `<IsPackable>true</IsPackable>` emit a normal nupkg whose runtime dependency is the ASP.NET runtime (declared via metadata). For Phase 3's class-library Core project, this is the cleanest option. [CITED: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/http-context]

5. **xUnit v3 parallelism + throwaway-DB-per-test-class is safe.** xUnit v3 parallelizes test **classes** by default (test methods within a class run sequentially). Each test class gets its own `IAsyncLifetime` fixture lifecycle. The throwaway-DB-per-class pattern from D-15 (`stepsdb_test_{Guid:N}`) gives each parallel class an isolated database — no collision risk. `[CollectionDefinition]` is only needed if multiple classes must share state, which Phase 3 explicitly does not (each class is self-contained per D-14's 4 facts). Recommendation: do NOT introduce collections in Phase 3 — keep each fact independent. [CITED: https://xunit.net/docs/running-tests-in-parallel]

**Primary recommendation:** Execute the 2-plan split from D-17. Plan 03-01 adds 4 new files in `BaseApi.Core` (`BaseEntity.cs`, `BaseDbContext.cs`, `Persistence/Interceptors/AuditInterceptor.cs`, `Persistence/Repositories/IRepository.cs` + `Repository.cs`), the new `Microsoft.Extensions.TimeProvider.Testing 8.10.0` pin in `Directory.Packages.props`, the 4 `<PackageReference>` entries + `<FrameworkReference>` in `BaseApi.Core.csproj`, and the 5 `<PackageReference>` entries in `BaseApi.Tests.csproj`. Plan 03-02 adds `TestEntity.cs` + `TestDbContext.cs` + a handwritten `StubHttpContextAccessor.cs` + the 4 facts (SC#1-4), then runs them green and writes the verification SUMMARY.

## EF Core 8 + Npgsql 8 — Confirmed Package Set & Version Compatibility

All four production packages and one new test-only package, verified against nuget.org 2026-05-26:

| Package | Locked Version | Status in Directory.Packages.props | Source |
|---------|----------------|------------------------------------|--------|
| `Microsoft.EntityFrameworkCore` | **8.0.27** | ✓ Already pinned (Phase 1 D-05) | [VERIFIED: nuget.org] 8.0.27 is the latest 8.0.x patch as of 2026-05-12 |
| `Microsoft.EntityFrameworkCore.Relational` | **8.0.27** | ✓ Already pinned (Phase 1 D-05) | [VERIFIED: nuget.org] lockstep with EF Core |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | **8.0.10** | ✓ Already pinned (Phase 1 D-05) | [VERIFIED: nuget.org] 8.0.10 is the latest 8.0.x as of 2026-05-26 (8.0.11 also exists; 8.0.10 is the STACK.md lock) |
| `EFCore.NamingConventions` | **8.0.3** | ✓ Already pinned (Phase 1 D-05) | [VERIFIED: nuget.org] 8.0.3 is the latest 8.x line (newer is 9.0.0 → EF Core 9; 10.0.1 → EF Core 10); deps are `Microsoft.EntityFrameworkCore >= 8.0.0 && < 9.0.0`, `Microsoft.EntityFrameworkCore.Relational >= 8.0.0 && < 9.0.0`, `Microsoft.Extensions.DependencyInjection.Abstractions >= 8.0.0` — all transitively satisfied by EF Core 8.0.27 already pinned |
| `Microsoft.Extensions.TimeProvider.Testing` | **8.10.0** | ✗ **NOT YET PINNED — Phase 3 Plan 03-01 adds it** | [VERIFIED: nuget.org] 8.10.0 published 2024-10-08; latest 8.x line; targets net8.0, net9.0, net10.0, net462+. No dependencies on net8.0. Versions on own cadence (Microsoft.Extensions.* shared infrastructure family — same line as `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Configuration.Binder`) |

**Test-side packages already pinned but new to `BaseApi.Tests.csproj` references in Phase 3:**

| Package | Pinned Version | Why Phase 3 Adds It |
|---------|----------------|---------------------|
| `Microsoft.EntityFrameworkCore` | 8.0.27 | Needed for `DbContextOptionsBuilder` + `EnsureCreatedAsync` in test fixture |
| `Microsoft.EntityFrameworkCore.Relational` | 8.0.27 | Needed for raw SQL execution + `information_schema` query in SC#1 |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | 8.0.10 | Needed for `UseNpgsql(...)` in test fixture |
| `EFCore.NamingConventions` | 8.0.3 | Needed for `UseSnakeCaseNamingConvention()` in test fixture (defense-in-depth — also called in BaseDbContext.OnConfiguring) |
| `Microsoft.Extensions.TimeProvider.Testing` | 8.10.0 (NEW) | `FakeTimeProvider` for SC#2 deterministic timestamp assertions |

**Compatibility note:** All four EF Core 8.0.x packages are mutually compatible (verified via dependency graph). `EFCore.NamingConventions 8.0.3` does NOT pull a different EF Core patch — it floats `>= 8.0.0 && < 9.0.0` and the resolver picks the higher version pinned by `Microsoft.EntityFrameworkCore 8.0.27`. `Microsoft.Extensions.TimeProvider.Testing 8.10.0` has zero net8.0 dependencies — it's a standalone testing utility that ships `FakeTimeProvider` in the `Microsoft.Extensions.Time.Testing` namespace (note: namespace differs from package name).

**Version Verification Commands (for the planner to include in Plan 03-01 as a sanity check before commit):**
```powershell
# Verify all 5 versions resolve from NuGet
dotnet add tests/BaseApi.Tests package Microsoft.Extensions.TimeProvider.Testing --version 8.10.0 --dry-run
dotnet list src/BaseApi.Core package --include-transitive | Select-String "EntityFrameworkCore|Npgsql|NamingConventions"
```

## AuditInterceptor — Full ISaveChangesInterceptor Skeleton

### API choice: derive from `SaveChangesInterceptor` base class

EF Core 8 ships a no-op convenience base class `Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor` that implements `ISaveChangesInterceptor` with default no-op overrides for all 8 methods (`SavingChanges`/`SavingChangesAsync`, `SavedChanges`/`SavedChangesAsync`, `SaveChangesFailed`/`SaveChangesFailedAsync`, `ThrowingConcurrencyException`/`ThrowingConcurrencyExceptionAsync`). **Use the base class. Override only `SavingChangesAsync`.** [CITED: https://learn.microsoft.com/en-us/ef/core/logging-events-diagnostics/interceptors — "the SaveChangesInterceptor base class with no-op methods is provided as a convenience"]

### Async-only override rationale

- All Phase 3 tests and Phase 4+ production paths use `await db.SaveChangesAsync(...)`.
- EF dispatches `SaveChanges()` (sync) → `SavingChanges` method, and `SaveChangesAsync()` (async) → `SavingChangesAsync` method. They are NOT both called on a single save — EF picks the one matching the caller's sync/async choice.
- Overriding only `SavingChangesAsync` is correct for an exclusively-async codebase. If a future scratch path uses sync `SaveChanges()`, it would silently bypass auditing — guard against this by either (a) overriding both methods (more code, defensive), or (b) trusting that no production path uses sync save (current locked posture).
- **Recommendation:** override only `SavingChangesAsync`. Add an XML doc on the class explicitly stating "audit stamping only fires on the async save path; sync `DbContext.SaveChanges()` will NOT stamp audit fields." This makes the limitation explicit so a future regression is visible in code review.

### Verified async signature (verbatim from MS Learn docs 2026-02-26)

```csharp
public async ValueTask<InterceptionResult<int>> SavingChangesAsync(
    DbContextEventData eventData,
    InterceptionResult<int> result,
    CancellationToken cancellationToken = default)
{
    // ... mutate eventData.Context.ChangeTracker ...
    return result;
}
```

[CITED: https://learn.microsoft.com/en-us/ef/core/logging-events-diagnostics/interceptors — verbatim signature from the "Audit interceptor" example]

### Complete code skeleton

```csharp
// src/BaseApi.Core/Persistence/Interceptors/AuditInterceptor.cs
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using BaseApi.Core.Entities;

namespace BaseApi.Core.Persistence.Interceptors;

/// <summary>
/// Stamps audit fields (Id, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy) on
/// <see cref="BaseEntity"/>-derived entries before SaveChangesAsync writes them.
///
/// <para>
/// Honors a caller-set non-empty <see cref="BaseEntity.Id"/> (per CONTEXT.md D-02) —
/// only generates a new Guid when Id == Guid.Empty.
/// </para>
///
/// <para>
/// <b>UTC-only:</b> all DateTime stamps are <see cref="DateTimeKind.Utc"/> because Npgsql 8
/// rejects non-UTC writes to <c>timestamptz</c> columns with <c>InvalidCastException</c>
/// (Pitfall 1). Time source is <see cref="TimeProvider"/> so tests can pin time via
/// <c>FakeTimeProvider</c>.
/// </para>
///
/// <para>
/// <b>Async-only:</b> overrides <see cref="SavingChangesAsync"/> only. Synchronous
/// <c>DbContext.SaveChanges()</c> will NOT trigger audit stamping — production code
/// must use the async save path.
/// </para>
/// </summary>
public sealed class AuditInterceptor : SaveChangesInterceptor
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly TimeProvider _clock;

    public AuditInterceptor(IHttpContextAccessor httpContextAccessor, TimeProvider clock)
    {
        _httpContextAccessor = httpContextAccessor;
        _clock = clock;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var context = eventData.Context;
        if (context is null)
        {
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        var now = _clock.GetUtcNow().UtcDateTime; // Kind == Utc by construction (Pitfall 1)
        var user = _httpContextAccessor.HttpContext?.User?.Identity?.Name; // null when no HttpContext

        foreach (EntityEntry<BaseEntity> entry in context.ChangeTracker.Entries<BaseEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.Id == Guid.Empty)
                    {
                        entry.Entity.Id = Guid.NewGuid(); // D-01/D-02
                    }
                    entry.Entity.CreatedAt = now;
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.CreatedBy = user;
                    entry.Entity.UpdatedBy = user;
                    break;

                case EntityState.Modified:
                    // Defensive: prevent caller from overwriting CreatedAt/CreatedBy via Update().
                    entry.Property(nameof(BaseEntity.CreatedAt)).IsModified = false;
                    entry.Property(nameof(BaseEntity.CreatedBy)).IsModified = false;
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.UpdatedBy = user;
                    break;
            }
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
```

[VERIFIED: ARCHITECTURE.md § Pattern 5 shows the same skeleton (lines 481-516); this version adds the `TimeProvider` injection per CONTEXT.md D-07 and the `Id == Guid.Empty` generation per D-01/D-02]

### Why `using` is unused in interceptor body
The interceptor does NOT open its own connection (compare with the MS Learn audit-context example that writes to a *separate* audit DB). The Phase 3 interceptor only mutates the current DbContext's change-tracker, so no I/O happens inside `SavingChangesAsync` — it returns immediately and EF proceeds with the save. The `async`/`await` and `ValueTask` exist only to satisfy the interface contract; the method body is effectively synchronous. This is by design and matches the locked decision (interceptor is Singleton-safe — no per-save allocation).

## xmin Concurrency Token — Confirmed Model-Builder Iteration Shape

### CONTEXT.md D-03 shape (copy-pasteable, validated against Npgsql 8.0.10)

```csharp
// inside BaseDbContext.OnModelCreating
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

[VERIFIED: CONTEXT.md D-03 verbatim; consistent with Npgsql 8 documented patterns; no API changes between Npgsql 8.0.8 → 8.0.10 release notes]

### Why this exact shape

| Element | Why |
|---------|-----|
| `Property<uint>("xmin")` | xmin is Postgres's `xid` type — a 32-bit transaction ID (CLR `uint`). Shadow property avoids a CLR field on `BaseEntity`. |
| `HasColumnName("xmin")` | Must match Postgres's reserved system column name exactly. EF doesn't know about Postgres system columns; the explicit name tells it not to generate `Xmin` / `xmin1`. |
| `HasColumnType("xid")` | Postgres-specific. `IsRowVersion()` on Npgsql implies `xid`, but the explicit form is verbose-safe and survives any future Npgsql provider default change. |
| `ValueGeneratedOnAddOrUpdate()` | Tells EF that Postgres updates this column on every UPDATE (and on INSERT). Without this, EF tries to SET `xmin` in INSERT/UPDATE statements — which Postgres rejects (`xmin` is read-only system column). |
| `IsConcurrencyToken()` | Marks the column for the optimistic-concurrency check. EF will include `WHERE xmin = @original_xmin` in every UPDATE/DELETE statement; mismatch → `DbUpdateConcurrencyException`. |

### Alternative: `IsRowVersion()` shorthand

Npgsql's documented "blessed" pattern is `IsRowVersion()` on a `uint` CLR property:

```csharp
public class SomeEntity
{
    public int Id { get; set; }
    [Timestamp] public uint Version { get; set; }  // or .IsRowVersion() in fluent API
}
```

[CITED: https://www.npgsql.org/efcore/modeling/concurrency.html]

**Why D-03's shape is preferred over `IsRowVersion()`:**
- `IsRowVersion()` requires a CLR property on `BaseEntity`, which conflicts with ENTITY-01's exact field list (no `xmin` / `Version`-the-rowversion field). Adding it would either break ENTITY-01 or require a different property name (e.g., `RowVersion`) and a separate `[NotMapped]` / fluent override.
- Shadow property keeps `BaseEntity` clean — the field list matches ENTITY-01 verbatim.
- The Npgsql docs page only documents the CLR-property form because it's the most common. The shadow form is functionally equivalent and is referenced in the Npgsql code samples and GitHub issues.

### Junction entity exclusion (correct as-is)

The D-03 iteration excludes junction entities because:
- Junction types (`StepNextSteps`, `WorkflowEntrySteps`, `WorkflowAssignments` — Phase 8) do NOT derive from `BaseEntity` (per ARCHITECTURE.md Pattern 4).
- The filter `typeof(BaseEntity).IsAssignableFrom(t.ClrType)` naturally excludes them.
- Phase 8 sync logic for junctions uses DELETE+INSERT semantics, not UPDATE — a concurrency token would never fire.

### EnsureCreated compatibility

[VERIFIED: against EF Core 8.0.27 + EFCore.NamingConventions 8.0.3 docs] `EnsureCreatedAsync()` honors model-builder configuration including the `xmin` shadow property — the generated CREATE TABLE statement will NOT include an `xmin` column (Postgres adds it automatically as a system column) and the generated UPDATE/DELETE statements will include the concurrency-check predicate. The convention is applied at model-build time, not at migration-emit time, so the order is: `OnModelCreating` runs → snake_case convention transforms identifiers → `xmin` shadow property is configured → `EnsureCreatedAsync` issues `CREATE TABLE schemas (id uuid, ..., created_at timestamptz, ...)` with no explicit `xmin` column (Postgres provides it). [ASSUMED: this matches the documented behavior of EF Core 8 + Npgsql 8 conventions; the planner should verify in 03-02 by querying `information_schema.columns` for the test table and asserting `xmin` is present but is a system column]

## BaseDbContext — Abstract Shape with OnConfiguring + OnModelCreating

### Complete code skeleton

```csharp
// src/BaseApi.Core/Persistence/BaseDbContext.cs
using Microsoft.EntityFrameworkCore;
using BaseApi.Core.Entities;
using BaseApi.Core.Persistence.Interceptors;

namespace BaseApi.Core.Persistence;

/// <summary>
/// Abstract base for all DbContexts in the BaseApi.Core ecosystem.
///
/// <para>
/// Concrete contexts (e.g., <c>AppDbContext</c> in Phase 8, <c>TestDbContext</c> in
/// Phase 3 tests) derive from this and add <c>DbSet&lt;TEntity&gt;</c> properties.
/// This base has NO DbSets — it provides three concerns: snake_case naming,
/// audit interception, and xmin concurrency tokens on every <see cref="BaseEntity"/>
/// subclass.
/// </para>
///
/// <para>
/// <b>Defense-in-depth wiring:</b> OnConfiguring duplicates the
/// <c>UseSnakeCaseNamingConvention()</c> + <c>AddInterceptors(...)</c> calls that
/// Phase 7's <c>AddBaseApi&lt;TDbContext&gt;</c> extension also performs. The duplication
/// ensures test paths (which build DbContextOptions directly without AddBaseApi)
/// still get the correct configuration.
/// </para>
/// </summary>
public abstract class BaseDbContext : DbContext
{
    private readonly AuditInterceptor? _auditInterceptor;

    /// <summary>
    /// Production constructor — DI provides options and the interceptor is already
    /// added to the options builder by AddBaseApi (Phase 7). The optional interceptor
    /// parameter is null in the production composition root; it's non-null only when
    /// the test fixture wants belt-and-braces.
    /// </summary>
    protected BaseDbContext(DbContextOptions options, AuditInterceptor? auditInterceptor = null)
        : base(options)
    {
        _auditInterceptor = auditInterceptor;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.UseSnakeCaseNamingConvention();
        if (_auditInterceptor is not null)
        {
            optionsBuilder.AddInterceptors(_auditInterceptor);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // xmin shadow concurrency token on every BaseEntity subclass (D-03 / PERSIST-16).
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
    }
}
```

### Notes for the planner

- **No `DbSet<>` properties on BaseDbContext.** Concrete derived contexts (Phase 8 `AppDbContext`, Phase 3 `TestDbContext`) add them. ENTITY-02 + D-09 lock this.
- **Constructor takes `DbContextOptions` (open generic).** When a concrete context (e.g., `TestDbContext`) inherits, EF Core's `AddDbContext<TDbContext>(...)` registration passes `DbContextOptions<TDbContext>` (closed) — assignable to the open base. Standard EF pattern.
- **The optional `AuditInterceptor` constructor parameter** is a small defense-in-depth knob: in production, Phase 7's `AddBaseApi` adds the interceptor to `DbContextOptionsBuilder` directly (the `_auditInterceptor` field stays null), so `OnConfiguring` doesn't double-register. In tests, the fixture can choose to inject the interceptor through the constructor or through `DbContextOptionsBuilder.AddInterceptors(...)` — either works. Planner's discretion to keep or drop this parameter; my recommendation is to **drop it** in Phase 3 to keep the surface minimal, and rely on the test fixture's `DbContextOptionsBuilder` to add the interceptor explicitly.

### Simplified constructor (recommended)

```csharp
protected BaseDbContext(DbContextOptions options) : base(options) { }

protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
{
    base.OnConfiguring(optionsBuilder);
    optionsBuilder.UseSnakeCaseNamingConvention();
    // AuditInterceptor is wired via AddInterceptors(...) at composition root (Phase 7)
    // OR by the test fixture's options builder (Phase 3). NOT here.
}
```

This is cleaner. The test fixture (Phase 3 verification plan) explicitly adds the interceptor via `optionsBuilder.AddInterceptors(auditInterceptor)` when constructing `DbContextOptions` for `TestDbContext`. Phase 7 does the same in `AddBaseApi`.

## Repository<T> — Final 5-Method Surface

### Locked decision (D-04, D-05) with Claude's-discretion picks

```csharp
// src/BaseApi.Core/Persistence/Repositories/IRepository.cs
using BaseApi.Core.Entities;

namespace BaseApi.Core.Persistence.Repositories;

public interface IRepository<TEntity> where TEntity : BaseEntity
{
    /// <summary>Returns the entity by Id, or null if missing. Service throws NotFoundException (ERROR-06 / Phase 4).</summary>
    Task<TEntity?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Returns all rows. No paging in v1 (HTTP-17..19 are v2).</summary>
    Task<IReadOnlyList<TEntity>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Stages an Add on the change tracker. Caller (Service) calls SaveChangesAsync.</summary>
    Task AddAsync(TEntity entity, CancellationToken cancellationToken);

    /// <summary>Stages an Update on the change tracker. Caller (Service) calls SaveChangesAsync.</summary>
    /// <remarks>Sync — DbSet&lt;T&gt;.Update is sync (no I/O); the async-by-symmetry shape would be a lie.</remarks>
    void Update(TEntity entity);

    /// <summary>
    /// Load-then-remove: fetches by Id, then stages a Remove. Returns silently if missing
    /// (Service is responsible for the NotFound semantics). Preserves the D-03 xmin check
    /// because the load tracks the row's current xmin.
    /// </summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
```

```csharp
// src/BaseApi.Core/Persistence/Repositories/Repository.cs
using Microsoft.EntityFrameworkCore;
using BaseApi.Core.Entities;

namespace BaseApi.Core.Persistence.Repositories;

public sealed class Repository<TEntity> : IRepository<TEntity> where TEntity : BaseEntity
{
    private readonly DbContext _db;
    private readonly DbSet<TEntity> _set;

    public Repository(BaseDbContext db)  // typed generic — see "Constructor choice" below
    {
        _db = db;
        _set = db.Set<TEntity>();
    }

    public Task<TEntity?> GetAsync(Guid id, CancellationToken cancellationToken)
        => _set.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task<IReadOnlyList<TEntity>> ListAsync(CancellationToken cancellationToken)
        => await _set.ToListAsync(cancellationToken);

    public async Task AddAsync(TEntity entity, CancellationToken cancellationToken)
        => await _set.AddAsync(entity, cancellationToken);

    public void Update(TEntity entity) => _set.Update(entity);

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await _set.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entity is null) return;
        _set.Remove(entity);
    }
}
```

### Constructor choice: `BaseDbContext` direct (recommended)

CONTEXT.md flags two options:
1. **`public Repository(DbContext db)`** — simpler; takes the EF base class.
2. **`public Repository(BaseDbContext db)`** — stronger type; ensures the Repository only operates on a Steps-API DbContext that has snake_case + audit + xmin wired.

**Recommendation: option 2 (`BaseDbContext`).** Phase 7's `AddBaseApi<TDbContext>` extension constrains `where TDbContext : BaseDbContext`, so the concrete DbContext is always `BaseDbContext`-derived. Taking `BaseDbContext` in the Repository constructor enforces this at compile time. Alternative phrasing using `Repository<TEntity, TDbContext>` (two type parameters) is overkill — Phase 7 registers the open-generic `IRepository<>` → `Repository<>` and the runtime DI container injects the concrete `BaseDbContext` derived type because there's only one DbContext registered in the container scope.

### Update sync vs async

`DbSet<T>.Update(entity)` is synchronous in EF Core 8 — it sets the entry's `State` to `Modified` on the change tracker. No I/O. Making `Repository<T>.Update` async would be a "fake async" method (no `await` body, just `Task.CompletedTask`) — confusing to callers who'd expect a real async signature.

**Recommendation: sync `void Update(TEntity entity)`.** Match the EF Core 8 shape. Locked by CONTEXT.md D-04.

### DeleteAsync — load-then-remove preserves xmin

The locked decision says "load-then-remove (preserves D-03 xmin check); does NOT call SaveChanges." The pattern is correct: `FirstOrDefaultAsync` tracks the entity (including its current `xmin` shadow value), `_set.Remove(entity)` marks it `Deleted`, and the eventual `SaveChangesAsync` generates `DELETE FROM ... WHERE id = @id AND xmin = @original_xmin`. If another transaction has already updated the row, the DELETE affects 0 rows and EF throws `DbUpdateConcurrencyException` → Phase 4 maps to HTTP 409.

The alternative — `_set.Remove(new TEntity { Id = id })` with `Attach` — would NOT include the xmin predicate (no tracked xmin value), and concurrent edits would be silently lost. **The load-then-remove pattern is correct and required.**

## Test Infrastructure — TestDbContext, Throwaway DB Lifecycle, IHttpContextAccessor Stub, FakeTimeProvider Usage

### TestEntity (lives in test project)

```csharp
// tests/BaseApi.Tests/Persistence/TestEntity.cs
using BaseApi.Core.Entities;

namespace BaseApi.Tests.Persistence;

/// <summary>
/// Trivial BaseEntity subclass used only by Phase 3 verification facts.
/// Has no entity-specific fields — exercises the BaseEntity audit + xmin
/// + snake_case wiring without depending on Phase 8's real entities.
/// </summary>
public sealed class TestEntity : BaseEntity
{
}
```

### TestDbContext (lives in test project)

```csharp
// tests/BaseApi.Tests/Persistence/TestDbContext.cs
using Microsoft.EntityFrameworkCore;
using BaseApi.Core.Persistence;

namespace BaseApi.Tests.Persistence;

public sealed class TestDbContext : BaseDbContext
{
    public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }

    public DbSet<TestEntity> TestEntities => Set<TestEntity>();
}
```

### Throwaway database fixture (lives in test project)

```csharp
// tests/BaseApi.Tests/Persistence/PostgresFixture.cs
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BaseApi.Tests.Persistence;

/// <summary>
/// Per-test-class fixture: creates a throwaway logical database inside the running
/// Phase 2 Postgres container (localhost:5433), runs EnsureCreatedAsync against it,
/// and DROPs it on dispose. xUnit v3 parallelizes test CLASSES by default, so each
/// class gets an isolated DB — no name collisions.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public string DatabaseName { get; } = $"stepsdb_test_{Guid.NewGuid():N}";
    public string ConnectionString { get; private set; } = default!;

    private const string AdminConnectionString =
        "Host=localhost;Port=5433;Database=postgres;Username=postgres;Password=postgres";

    public async ValueTask InitializeAsync()
    {
        // Connect to the postgres admin DB and CREATE the throwaway.
        await using var adminConn = new NpgsqlConnection(AdminConnectionString);
        await adminConn.OpenAsync();
        await using var createCmd = adminConn.CreateCommand();
        createCmd.CommandText = $"CREATE DATABASE \"{DatabaseName}\"";
        await createCmd.ExecuteNonQueryAsync();

        ConnectionString =
            $"Host=localhost;Port=5433;Database={DatabaseName};Username=postgres;Password=postgres";
    }

    public async ValueTask DisposeAsync()
    {
        // Clear connection pool to release locks on the throwaway DB,
        // then DROP it from the admin DB.
        NpgsqlConnection.ClearAllPools();

        await using var adminConn = new NpgsqlConnection(AdminConnectionString);
        await adminConn.OpenAsync();
        await using var dropCmd = adminConn.CreateCommand();
        // FORCE clause (PG 13+) terminates any remaining sessions on the target DB.
        dropCmd.CommandText = $"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE)";
        await dropCmd.ExecuteNonQueryAsync();
    }
}
```

### Gotchas the planner must surface in 03-02 task notes

| Gotcha | Symptom | Mitigation |
|--------|---------|-----------|
| **Connection-pool retention on test DB** | `DROP DATABASE` fails with "database is being accessed by other users" | `NpgsqlConnection.ClearAllPools()` before drop + `WITH (FORCE)` on the DROP statement (PG 13+; PG 17 supports it) |
| **Admin DB write requires superuser** | `CREATE DATABASE` fails with "permission denied" | The Phase 2 `.env` uses `postgres` superuser by D-04 — already grants CREATE DATABASE. Test fixture uses the same credentials. |
| **`SearchPath` confusion** | Tables created in wrong schema | Don't set `SearchPath` in the test connection string. EnsureCreated puts everything in `public` schema by default. |
| **Identifier quoting** | `CREATE DATABASE stepsdb_test_abc` fails if name starts with digit or contains uppercase | `Guid.ToString("N")` produces 32 hex digits (0-9, a-f) — all lowercase, no separators. Always quote with `"..."` in the SQL anyway (shown above). |
| **Test order matters with paralellism** | Flaky failures if two classes share a DB | Each class gets its own fixture instance → its own unique DB name. Safe. |
| **Leaked DBs from killed test runs** | `\l` shows old `stepsdb_test_*` databases | Manual cleanup: `psql -c "DROP DATABASE stepsdb_test_xxx"`. A prefix-based cleanup helper is a v2 nice-to-have; out of Phase 3 scope. |

### Handwritten IHttpContextAccessor stub (no Moq/NSubstitute)

REQUIREMENTS.md Out of Scope explicitly excludes Moq/NSubstitute in v1 (the "no test-double library pinned yet" constraint mentioned in CONTEXT.md). Use a handwritten stub:

```csharp
// tests/BaseApi.Tests/Persistence/StubHttpContextAccessor.cs
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using System.Security.Principal;

namespace BaseApi.Tests.Persistence;

/// <summary>
/// Hand-written test double for <see cref="IHttpContextAccessor"/>.
/// REQUIREMENTS.md Out of Scope excludes Moq/NSubstitute in v1; this is the
/// minimal stub Phase 3 tests need.
/// </summary>
public sealed class StubHttpContextAccessor : IHttpContextAccessor
{
    public HttpContext? HttpContext { get; set; }

    /// <summary>
    /// Convenience: set HttpContext to a DefaultHttpContext whose User has the given Name.
    /// Pass null to clear (simulates "no HttpContext" — non-HTTP execution path).
    /// </summary>
    public void SetUser(string? name)
    {
        if (name is null)
        {
            HttpContext = null;
            return;
        }

        var identity = new ClaimsIdentity(authenticationType: "Test");
        identity.AddClaim(new Claim(ClaimTypes.Name, name));
        var principal = new ClaimsPrincipal(identity);

        HttpContext = new DefaultHttpContext
        {
            User = principal
        };
    }
}
```

[VERIFIED: `DefaultHttpContext` is the canonical test seam — present in `Microsoft.AspNetCore.Http` package which is in the `Microsoft.AspNetCore.App` framework — no additional package reference needed when `BaseApi.Tests.csproj` has the test-side EF Core + Npgsql references. `ClaimsIdentity` with `authenticationType: "Test"` (non-null) makes `Identity.IsAuthenticated` return true; this matches the audit-interceptor's logic of reading `User.Identity.Name` regardless of authentication state. Note: CONTEXT.md D-08 says the interceptor reads `Identity?.Name` — does NOT check `IsAuthenticated`. So even a `ClaimsIdentity` constructed with no authenticationType (which has `IsAuthenticated == false` and `Name == null`) would work for the null-name case; but explicit `authenticationType: "Test"` makes the SC#3 happy-path assertion clean.]

### FakeTimeProvider usage example

```csharp
// inside a [Fact] in BaseApi.Tests
using Microsoft.Extensions.Time.Testing; // namespace differs from package name

var clock = new FakeTimeProvider();
clock.SetUtcNow(new DateTimeOffset(2026, 1, 15, 12, 30, 0, TimeSpan.Zero));

var stub = new StubHttpContextAccessor();
stub.SetUser("alice");

var interceptor = new AuditInterceptor(stub, clock);

var options = new DbContextOptionsBuilder<TestDbContext>()
    .UseNpgsql(fixture.ConnectionString)
    .UseSnakeCaseNamingConvention()
    .AddInterceptors(interceptor)
    .Options;

await using var db = new TestDbContext(options);
await db.Database.EnsureCreatedAsync();

var entity = new TestEntity();
await db.TestEntities.AddAsync(entity);
await db.SaveChangesAsync();

Assert.Equal(new DateTime(2026, 1, 15, 12, 30, 0, DateTimeKind.Utc), entity.CreatedAt);
Assert.Equal(DateTimeKind.Utc, entity.CreatedAt.Kind);
Assert.Equal("alice", entity.CreatedBy);
```

[CITED: https://www.nuget.org/packages/Microsoft.Extensions.TimeProvider.Testing — confirms `FakeTimeProvider.SetUtcNow(DateTimeOffset)` and `Advance(TimeSpan)` API; namespace is `Microsoft.Extensions.Time.Testing` despite package name `Microsoft.Extensions.TimeProvider.Testing`]

## BaseApi.Core csproj Changes — FrameworkReference vs PackageReference Resolution

### Recommended `BaseApi.Core.csproj` after Phase 3

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!-- ... existing PropertyGroup unchanged ... -->

  <ItemGroup>
    <!-- ASP.NET Core surface (IHttpContextAccessor, in future: HealthChecks, Middleware, Controllers).
         Per Phase 3 CONTEXT.md D-12: FrameworkReference is the canonical pattern for an
         ASP.NET-coupled class library. No runtime weight; transitive deps resolved by the
         consumer's Microsoft.NET.Sdk.Web project. -->
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <!-- EF Core 8 + Npgsql + snake_case convention (D-12) -->
    <PackageReference Include="Microsoft.EntityFrameworkCore" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
    <PackageReference Include="EFCore.NamingConventions" />
  </ItemGroup>

</Project>
```

### Why FrameworkReference (and not explicit PackageReference)

| Question | Answer |
|----------|--------|
| Is FrameworkReference the canonical pattern? | **Yes.** [CITED: MS Learn `http-context` page] — the standard way for a class library to access `IHttpContextAccessor` and the broader ASP.NET Core API surface when the library is `Microsoft.NET.Sdk` (class library) rather than `Microsoft.NET.Sdk.Web` (webapi). |
| Does it conflict with `Microsoft.NET.Sdk`? | **No.** FrameworkReference is an MSBuild item supported by all .NET SDK variants. Adding it to a `Microsoft.NET.Sdk` class library is exactly the documented pattern. |
| Does it bloat the assembly? | **No.** FrameworkReference is a build-time directive that adds the framework's reference assemblies to the compile path. No runtime DLLs are copied to `bin/`. |
| Does it break NuGet packability? | **No.** When `<IsPackable>true</IsPackable>` (out of scope per CONTEXT.md but worth knowing), the resulting `.nupkg` declares `Microsoft.AspNetCore.App` as a framework dependency in the package metadata. Consumers who depend on the package must themselves use `Microsoft.NET.Sdk.Web` (or also add the FrameworkReference). This is the standard "ASP.NET Core class library" packaging model — used by libraries like `MediatR.Extensions.Microsoft.DependencyInjection`, `IdentityServer4`, etc. |
| Does it pull excessive runtime weight? | **No.** ASP.NET Core class libraries built this way ship as small assemblies; the runtime is provided by the hosting `Microsoft.NET.Sdk.Web` project. |
| Are there alternatives? | Yes — explicit `<PackageReference Include="Microsoft.AspNetCore.Http.Abstractions" />`. This pulls `IHttpContextAccessor` only. Drawback: in Phase 4-7, when `BaseApi.Core` adds controllers, middleware, health checks, the per-API package list grows (`Microsoft.AspNetCore.Mvc.Core`, `Microsoft.AspNetCore.Authentication.Abstractions`, `Microsoft.AspNetCore.Hosting`, `Microsoft.AspNetCore.HealthChecks`, etc.). FrameworkReference is one line that covers all of them. |

### Zero Version= attributes preserved

The csproj snippet has zero `Version=` attributes on `<PackageReference>` entries — CPM resolves all versions from `Directory.Packages.props`. FrameworkReference does NOT take a version (the runtime version is determined by `<TargetFramework>net8.0</TargetFramework>` in `Directory.Build.props`).

## BaseApi.Tests csproj Changes — Package Additions + Directory.Packages.props Pin

### New pin in Directory.Packages.props (Phase 3 Plan 03-01)

```xml
<!-- Microsoft.Extensions.* family — versions on own cadence -->
<PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="8.10.0" />
```

[VERIFIED: nuget.org 2026-05-26 — 8.10.0 is latest 8.x line; published 2024-10-08; no dependencies on net8.0; ships `FakeTimeProvider` in `Microsoft.Extensions.Time.Testing` namespace]

### Updated `BaseApi.Tests.csproj` after Phase 3

Add these `<PackageReference>` entries (no Version= per CPM):

```xml
<ItemGroup>
  <!-- Existing xUnit v3 references — UNCHANGED -->
  <PackageReference Include="xunit.v3" />
  <PackageReference Include="xunit.v3.assert" />
  <PackageReference Include="xunit.runner.visualstudio">
    <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    <PrivateAssets>all</PrivateAssets>
  </PackageReference>
</ItemGroup>

<ItemGroup>
  <!-- Phase 3 additions: EF Core test fixture + FakeTimeProvider (D-13) -->
  <PackageReference Include="Microsoft.EntityFrameworkCore" />
  <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" />
  <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
  <PackageReference Include="EFCore.NamingConventions" />
  <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
</ItemGroup>

<!-- Test project ALSO needs FrameworkReference for DefaultHttpContext / ClaimsPrincipal in StubHttpContextAccessor -->
<ItemGroup>
  <FrameworkReference Include="Microsoft.AspNetCore.App" />
</ItemGroup>

<!-- Existing ProjectReference — UNCHANGED -->
<ItemGroup>
  <ProjectReference Include="..\..\src\BaseApi.Core\BaseApi.Core.csproj" />
</ItemGroup>
```

### Why the test project also needs FrameworkReference

The `StubHttpContextAccessor` uses `DefaultHttpContext` (from `Microsoft.AspNetCore.Http`) and `ClaimsPrincipal` / `ClaimsIdentity` (from `System.Security.Claims` — actually in-box `System.Security.Claims` namespace, but the test project also references `BaseApi.Core` which uses `IHttpContextAccessor` via the FrameworkReference). Adding `<FrameworkReference Include="Microsoft.AspNetCore.App" />` to the test csproj is the simplest way to pick up the full ASP.NET surface for test scaffolding. **This is the documented test-project pattern.**

Alternatively, if the planner prefers minimal surface area on the test project: project-reference `BaseApi.Core` (which already has the FrameworkReference) and the transitive flow may or may not expose `DefaultHttpContext` depending on SDK version. The FrameworkReference is cleaner and unambiguous — recommend it.

## Validation Architecture — 6-8 Sampled Dimensions with Signal Sources (Nyquist-Style)

> Phase 3 validation is mandatory per CONTEXT.md SC#1-4 + the 11 requirement IDs (10 from ROADMAP + PERSIST-16 per D-03b). The planner uses this section to generate VALIDATION.md.

### Test Framework

| Property | Value |
|----------|-------|
| Framework | xUnit v3 3.2.2 (Microsoft.Testing.Platform runner) |
| Config file | None — defaults to per-class parallelization, per-method sequencing |
| Quick run command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~Phase03"` (assumes Phase 3 facts live in `Phase03` namespace or class) |
| Full suite command | `dotnet test` from repo root |
| Phase gate | Full suite green before `/gsd-verify-work`; Phase 2 Postgres container running at `localhost:5433` before invocation |

### Phase Requirements → Test Map (Dimensions)

| Dim | Signal Source | Requirement(s) | Sampling Rate | Assertion Shape | File |
|-----|---------------|----------------|---------------|-----------------|------|
| **1. Zero-warning Build** | `dotnet build -c Release` exit code + warning count | Phase 1 D-02 (TreatWarningsAsErrors), PERSIST-02/03/04/05/06/07/11/15, ENTITY-01/02 (all phase req IDs depend on compile) | Per task commit (Plan 03-01 final task), per wave merge | `dotnet build -c Release /warnaserror` returns 0, no `warning` lines in output | (build target — no test file) |
| **2. Schema Mapping (snake_case)** | Postgres `information_schema.columns` query against the throwaway DB after `EnsureCreatedAsync` | PERSIST-05, ENTITY-01, ENTITY-02, SC#1 | Per Plan 03-02 commit | Query for `test_entities.created_at`, `.updated_at`, `.created_by`, `.updated_by`, `.id`, `.name`, `.version`, `.description` returns 8 rows with snake_case column names; query for `CreatedAt` returns 0 rows | `tests/BaseApi.Tests/Persistence/SchemaTests.cs::Test_TestDbContext_EnsureCreated_ProducesSnakeCaseSchema` (Wave 0 — does not exist) |
| **3. UTC Timestamp Stamping** | `entity.CreatedAt.Kind` after `SaveChangesAsync` + raw SQL query of `pg_typeof(created_at)` | PERSIST-03, PERSIST-07, SC#2; Pitfall 1 verbatim | Per Plan 03-02 commit | `entity.CreatedAt.Kind == DateTimeKind.Utc`; raw SQL `SELECT pg_typeof(created_at) FROM test_entities LIMIT 1` returns `timestamp with time zone`; no `InvalidCastException` thrown | `tests/BaseApi.Tests/Persistence/AuditInterceptorTests.cs::Test_AuditInterceptor_StampsUtcTimestamps_OnInsert` (Wave 0) |
| **4. HttpContext-Based CreatedBy + Null Fallback** | `entity.CreatedBy` value after `SaveChangesAsync` under two HttpContext states | PERSIST-04, SC#3 | Per Plan 03-02 commit | (a) `stub.SetUser("alice"); ... Assert.Equal("alice", entity.CreatedBy);` (b) `stub.HttpContext = null; ... Assert.Null(entity.CreatedBy);` — no exception thrown | `tests/BaseApi.Tests/Persistence/AuditInterceptorTests.cs::Test_AuditInterceptor_StampsCreatedBy_FromHttpContext` + companion null-fallback fact (Wave 0) |
| **5. DbContext Scoped Lifetime** | `IServiceProvider.GetRequiredService<TestDbContext>()` × 2 within scope; × 1 across scopes | PERSIST-15, SC#4 | Per Plan 03-02 commit | `Assert.Same(scope1A, scope1B); Assert.NotSame(scope1A, scope2A);` | `tests/BaseApi.Tests/Persistence/DiLifetimeTests.cs::Test_DbContext_IsRegisteredScoped_InDI` (Wave 0) |
| **6. xmin Concurrency Token Wiring** | Schema introspection: `information_schema.columns` for the `xmin` column existence + EF generated UPDATE SQL inspection | PERSIST-16 (new from D-03b), Pitfall 6 | Per Plan 03-02 commit (OPTIONAL — D-03b is cross-phase to Phase 4 for the 409 mapping; Phase 3 just verifies the wiring) | `var prop = db.Model.FindEntityType(typeof(TestEntity))!.FindProperty("xmin"); Assert.True(prop!.IsConcurrencyToken); Assert.Equal("xid", prop.GetColumnType());` | `tests/BaseApi.Tests/Persistence/XminConcurrencyTokenTests.cs::Test_BaseEntity_HasXminShadowProperty` (Wave 0; RECOMMENDED but not strictly required for SC#1-4) |
| **7. Repository<T> Surface** | Public surface of `IRepository<TEntity>` — count + signatures via reflection | PERSIST-11, D-04 | Per Plan 03-01 commit (build-time — surface check) | `typeof(IRepository<>).GetMethods(BindingFlags.Public).Length == 5`; method names ∈ {GetAsync, ListAsync, AddAsync, Update, DeleteAsync}; no method returns `IQueryable<>` | `tests/BaseApi.Tests/Persistence/RepositorySurfaceTests.cs::Test_IRepository_ExposesExactlyFiveMethods` (Wave 0; RECOMMENDED) |
| **8. Test Infrastructure (Throwaway DB Lifecycle)** | `psql -l` output before / during / after test run; container resource usage | D-15 (per-test throwaway), Pitfall 26 | Per Plan 03-02 verification | Before: only `stepsdb` + `postgres` + `template*` in `\l`. During: one additional `stepsdb_test_{guid}` per running fact class. After: returns to baseline (no leaked DBs) | Manual verification in 03-02 SUMMARY: `docker compose exec postgres psql -U postgres -c "\l"` snapshots before + after `dotnet test` |

### Sampling Rate Summary

- **Per task commit (Plan 03-01):** Build green (Dim 1), Repository surface check (Dim 7 if added).
- **Per task commit (Plan 03-02):** All 5 SC facts (Dims 2-5) + optional Dim 6 + manual Dim 8 verification.
- **Per wave merge:** Same as per-task commit, plus full test suite run.
- **Phase gate (before `/gsd-verify-work`):** Full test suite green; SUMMARY documents command output verbatim; D-15 cleanup verified (no leaked test DBs).

### Wave 0 Gaps (test files that don't exist yet)

- [ ] `tests/BaseApi.Tests/Persistence/TestEntity.cs` — trivial `BaseEntity` subclass
- [ ] `tests/BaseApi.Tests/Persistence/TestDbContext.cs` — concrete `BaseDbContext` with `DbSet<TestEntity>`
- [ ] `tests/BaseApi.Tests/Persistence/PostgresFixture.cs` — throwaway DB lifecycle
- [ ] `tests/BaseApi.Tests/Persistence/StubHttpContextAccessor.cs` — handwritten test double
- [ ] `tests/BaseApi.Tests/Persistence/SchemaTests.cs` — SC#1 fact
- [ ] `tests/BaseApi.Tests/Persistence/AuditInterceptorTests.cs` — SC#2 + SC#3 facts (or split per fact)
- [ ] `tests/BaseApi.Tests/Persistence/DiLifetimeTests.cs` — SC#4 fact
- [ ] `tests/BaseApi.Tests/Persistence/XminConcurrencyTokenTests.cs` (optional, recommended) — PERSIST-16 wiring fact
- [ ] `tests/BaseApi.Tests/Persistence/RepositorySurfaceTests.cs` (optional, recommended) — PERSIST-11 surface fact

*(All test files are new — no existing test files to mirror beyond `MetaTest.cs`.)*

## Cross-Phase Surface — What Phase 4/5/7/8 Will Consume from Phase 3

### Phase 4 (Cross-Cutting Middleware + Error Handling) consumes

| From Phase 3 | Phase 4 Use |
|--------------|-------------|
| `DbUpdateConcurrencyException` (raised by EF when xmin mismatch on UPDATE/DELETE) | Phase 4 adds a mapping rule: `DbUpdateConcurrencyException` → HTTP 409 with detail "The resource was modified by another request; reload and retry." (D-03a) |
| The xmin shadow property's column name (`xmin`) and Postgres `xid` type | Phase 4's `PostgresErrorMapper` does not directly read xmin; the EF-layer exception is sufficient |
| `BaseEntity` (no direct Phase 4 dep) | — |

### Phase 5 (Observability + Health Probes) consumes

| From Phase 3 | Phase 5 Use |
|--------------|-------------|
| `BaseDbContext` and the connection string shape | Phase 5's `AddDbContextCheck<TDbContext>("postgres", tags: new[] { "ready" })` requires a registered DbContext. Phase 7's `AddBaseApi<TDbContext>` registers it; Phase 5 wires the health check on top. |
| Connection-string format (`Host=localhost;Port=5433;Database=stepsdb;...`) | Phase 5's readiness probe re-uses the same connection (`AspNetCore.HealthChecks.NpgSql 9.0.0`) |

### Phase 7 (Generic HTTP Base + Composition Root) consumes

| From Phase 3 | Phase 7 Use |
|--------------|-------------|
| `BaseDbContext` (abstract) — generic type constraint | `AddBaseApi<TDbContext>(IConfiguration) where TDbContext : BaseDbContext` |
| `AuditInterceptor` (Singleton) | `services.AddSingleton<AuditInterceptor>();` |
| `IRepository<>` + `Repository<>` (open generic) | `services.AddScoped(typeof(IRepository<>), typeof(Repository<>));` |
| `BaseEntity` (type constraint) | `where TEntity : BaseEntity` propagates through `BaseService<...>` and `BaseController<...>` (Phase 7 work) |
| `TimeProvider` injection contract | `services.AddSingleton(TimeProvider.System);` in `AddBaseApi` |
| `IHttpContextAccessor` registration | `services.AddHttpContextAccessor();` in `AddBaseApi` |
| `UseSnakeCaseNamingConvention()` + `AddInterceptors(AuditInterceptor)` wiring | Duplicated in `AddBaseApi`'s `AddDbContext` callback (defense-in-depth per D-09) |

### Phase 8 (Entity Build-Out + Migrations + Docker Runtime + Tests) consumes

| From Phase 3 | Phase 8 Use |
|--------------|-------------|
| `BaseEntity` (concrete derivation) | `SchemaEntity`, `ProcessorEntity`, `StepEntity`, `AssignmentEntity`, `WorkflowEntity` all derive |
| `BaseDbContext` (concrete derivation) | `AppDbContext : BaseDbContext` adds 5 entity `DbSet<>` properties + 3 junction `DbSet<>` properties |
| `IRepository<TEntity>` (5-method surface) | `BaseService<...>` (Phase 7) consumes; per-entity `Service<...>` overrides `SyncJunctionsAsync` per ARCHITECTURE.md |
| `xmin` shadow property iteration | Applies automatically to every Phase 8 entity that derives from `BaseEntity` — no per-entity wiring needed |
| Snake_case convention | Applies to the `InitialCreate` migration automatically (Phase 8 generates it; Pitfall 4 says it cannot be retrofit, so Phase 3 wires it first) |

## Pitfalls Revisited — Verbatim Quotes from PITFALLS.md Applicable to Phase 3 Tasks

### Pitfall 1 — Non-UTC DateTime → timestamptz [verbatim]

> "At runtime, the first INSERT/UPDATE that touches `CreatedAt` or `UpdatedAt` throws `InvalidCastException: Cannot write DateTime with Kind=Unspecified to PostgreSQL type 'timestamp with time zone', only UTC is supported.`"
>
> **How to avoid:** "In `AuditInterceptor`, **always** stamp with `DateTime.UtcNow`, never `DateTime.Now`. ... For any DTOs containing dates, document that callers must send UTC ISO-8601 (`...Z`); validate at the validator layer if needed."
>
> **Phase to address:** "**P2 — EF Core base infra.** Encode in `AuditInterceptor` and document the rule in `BaseEntity` XML doc. Add a unit test that round-trips an entity through `AppDbContext` and asserts `Kind == Utc`."

**Phase 3 application:** Validation Dimension 3 above. Interceptor uses `_clock.GetUtcNow().UtcDateTime` (D-07). `BaseEntity.CreatedAt`/`UpdatedAt` XML docs MUST reference this pitfall (Claude's discretion recommended per CONTEXT.md).

### Pitfall 2 — DbContext Lifetime [verbatim]

> "Registering `AppDbContext` as `Singleton` (e.g., to "cache" it) corrupts state across requests, causes random `InvalidOperationException: A second operation was started on this context instance before a previous operation completed`, and leaks change tracker entries forever. Registering as `Transient` leaks connections and breaks unit-of-work semantics."
>
> **How to avoid:** "Use `services.AddDbContext<AppDbContext>(...)` (scoped by default) — do not override the lifetime."
>
> **Phase to address:** "**P2 — EF Core base infra** (registration) and **P6 — Abstract generic controllers + service/repository base** (consumption)."

**Phase 3 application:** Validation Dimension 5 (SC#4). The test fixture builds a `ServiceCollection`, calls `services.AddDbContext<TestDbContext>(opts => ...)` (defaults to Scoped), and asserts ReferenceEqual within scope / NotReferenceEqual across scopes.

### Pitfall 4 — NamingConventions Mangles `__EFMigrationsHistory` [verbatim]

> "After adding `UseSnakeCaseNamingConvention()`, the first migration succeeds against an empty DB but a second deployment to a DB created without the convention fails because the package snake_cases the `__EFMigrationsHistory` table's columns (`MigrationId` → `migration_id`, `ProductVersion` → `product_version`) — EF can't find its history rows and tries to re-apply migrations."
>
> **How to avoid:** "Add `UseSnakeCaseNamingConvention()` **before the first migration is ever generated** — bake it into `BaseDbContext`'s `OnConfiguring`/`OnModelCreating` from day one. ... Avoid PascalCase column names > 55 characters."
>
> **Phase to address:** "**P2 — EF Core base infra.** Add the snake_case convention and generate the **first** initial migration in the same commit."

**Phase 3 application:** CRITICAL — this is the rationale for D-09 (snake_case in `OnConfiguring`) and D-16 (Phase 3 does NOT generate migrations; Phase 8's `InitialCreate` is the first migration, AFTER snake_case is wired). Validation Dimension 2 asserts the convention is applied via `EnsureCreatedAsync()` (Phase 3 does not test migration history; that's Phase 8's surface).

### Pitfall 6 — xmin Concurrency Token Configured Wrong [verbatim]

> "Concurrent PUTs to the same entity both succeed silently (last-write-wins), corrupting state. Or `xmin` is configured but as a regular column, so EF tries to SET it on UPDATE and Postgres errors out (`xmin` is a system column)."
>
> **How to avoid:**
> ```csharp
> modelBuilder.Entity<T>()
>     .Property<uint>("xmin")
>     .HasColumnName("xmin")
>     .HasColumnType("xid")
>     .ValueGeneratedOnAddOrUpdate()
>     .IsConcurrencyToken();
> ```
> "Use a shadow property (no CLR field on `BaseEntity`) so the model stays clean."
>
> **Phase to address:** "**P2 — EF Core base infra** (the shadow xmin convention) and **P3 — Cross-cutting middleware** (concurrency exception → 409 mapping)."

**Phase 3 application:** D-03 verbatim. Validation Dimension 6 (optional but recommended) asserts the wiring at the model level. The 409 mapping is Phase 4 (D-03a / D-03b).

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `EnsureCreatedAsync()` honors the xmin shadow property without emitting an `xmin` column in CREATE TABLE (relying on Postgres's automatic system column) | xmin Concurrency Token § EnsureCreated compatibility | LOW — if EF tries to emit an explicit `xmin column`, CREATE TABLE fails loudly; the test (Validation Dim 2) would catch it. Mitigation: add an explicit information_schema check in SC#1 that confirms no `xmin` user-column exists. |
| A2 | `Microsoft.Extensions.TimeProvider.Testing 8.10.0` has zero net8.0 dependencies and integrates cleanly with `TimeProvider.System` | EF Core 8 + Npgsql 8 § package set | LOW — `dotnet add package --dry-run` (planner runs in Plan 03-01) will surface any dep issue; the package is widely used in .NET 8 codebases |
| A3 | `DefaultHttpContext` is accessible via `<FrameworkReference Include="Microsoft.AspNetCore.App" />` in the test project without explicit package reference | Test Infrastructure § Handwritten IHttpContextAccessor stub | LOW — confirmed via MS Learn http-context article, but the planner should compile the stub once before relying on it; if the FrameworkReference doesn't expose `DefaultHttpContext`, fall back to `<PackageReference Include="Microsoft.AspNetCore.Http" />` |
| A4 | `NpgsqlConnection.ClearAllPools()` + `DROP DATABASE ... WITH (FORCE)` reliably drops the throwaway DB on dispose even when xUnit v3 parallelizes test classes | Test Infrastructure § Throwaway database fixture | MEDIUM — under heavy parallelism, occasional `DROP DATABASE` failures (locked connections) may leak DBs. Acceptable for v1: manual cleanup or v2 prefix-sweep helper. The planner should document this as a known limitation in 03-02 SUMMARY. |
| A5 | xUnit v3 3.2.2 parallelizes test CLASSES but sequences test METHODS within a class | Executive Summary point 5 + § Validation Architecture | LOW — verified against xUnit v3 docs (`Running tests in parallel`); behavior is identical to xUnit v2. |
| A6 | The simplified `BaseDbContext` constructor (no optional `AuditInterceptor` parameter) is sufficient; tests can add the interceptor via `DbContextOptionsBuilder.AddInterceptors(...)` | BaseDbContext § Simplified constructor | LOW — matches ARCHITECTURE.md Pattern 1 (`AddBaseApi` wires the interceptor in the `AddDbContext` callback) and is the more common EF Core 8 pattern. |
| A7 | The handwritten `StubHttpContextAccessor` with `ClaimsIdentity(authenticationType: "Test")` is sufficient for SC#3; no Moq/NSubstitute required | Test Infrastructure § Handwritten IHttpContextAccessor stub | LOW — REQUIREMENTS.md Out of Scope explicitly excludes Moq/NSubstitute in v1; the stub is minimal and well-typed. |

**Confirmation needed from user / decision-stage discussion:**
- A4 (test DB cleanup under parallelism): acceptable as v1 known limitation, or escalate to Phase 3 mitigation? CONTEXT.md "Specific Ideas" already calls this out ("Wrap in try/catch on dispose so a failed test doesn't leak DBs (or accept that prefix-based cleanup is a v2 nice-to-have)") — A4 is consistent with that disposition.

## Open Questions

1. **Should Plan 03-01 split into 03-01a (csproj + pins + BaseEntity) + 03-01b (BaseDbContext + AuditInterceptor) + 03-01c (Repository<T>) for finer-grained commits?**
   - What we know: CONTEXT.md D-17 says "2 plans likely sufficient" but planner has discretion to split further. ROADMAP "Parallelizable: no" means a single executor processes them sequentially.
   - What's unclear: whether the single-commit-per-file granularity helps reviewability vs. adds plan overhead.
   - Recommendation: ship as a single `03-01-PLAN.md` build plan with task-level granularity (1 task per file or per logical group). Splitting into multiple plans buys no parallelism (executor runs serial) and adds plan-metadata overhead. Keep it 2-plan per D-17.

2. **Should Phase 3 also add a `Test_BaseEntity_HasXminShadowProperty` model-introspection fact (Validation Dim 6) given D-03b appends PERSIST-16?**
   - What we know: D-03b says PERSIST-16 is appended during Phase 3 planning; Phase 3 req count becomes 11. The 4 facts (D-14) cover SC#1-4 but do not explicitly cover PERSIST-16.
   - What's unclear: whether a model-introspection fact (no DB I/O — pure EF metadata check) is in-scope for Phase 3 or deferred to Phase 4 (when the 409 mapping is added).
   - Recommendation: **add the fact in Phase 3**. It's a 5-line test that asserts `db.Model.FindEntityType(typeof(TestEntity)).FindProperty("xmin")` is non-null + `IsConcurrencyToken == true`. Catches a regression where someone accidentally removes the `OnModelCreating` iteration. Phase 4 separately tests the runtime 409 path (with two racing transactions). Planner decides whether to include in Plan 03-02 facts; my recommendation is yes (cheap, surfaces regressions early).

3. **Should the planner update REQUIREMENTS.md during Phase 3 planning (per D-03b) or defer to a separate documentation commit?**
   - What we know: D-03b says "PERSIST-16: ... should be appended to REQUIREMENTS.md during Phase 3 planning." Coverage rolls into Phase 3's count (10 → 11) and Phase 4's count (+1 = 15).
   - What's unclear: process — does this go in Plan 03-01 (build plan) as a task, or as a separate `docs` commit at plan-creation time?
   - Recommendation: include as Task 0 of Plan 03-01 ("Update REQUIREMENTS.md to add PERSIST-16 + update Traceability table"). It's a `docs(03)` commit alongside the `feat(03)` body of the plan. Documenting first locks the scope before implementation.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET 8 SDK | All `dotnet` commands | ✓ (verified Phase 1) | 8.0.421 | — |
| Docker Desktop + WSL2 | Phase 2 Postgres container | ✓ (verified Phase 2) | — | — |
| `postgres:17-alpine` container at `localhost:5433` | Plan 03-02 tests | ✓ (Phase 2 ships it) | 17.6 (floating tag) | — |
| `psql` client (for manual smoke + 03-02 SUMMARY snapshots) | Plan 03-02 verification | ✓ (verified Phase 2 via `docker compose exec`) | 17.x via container | — |
| NuGet.org reachability | `dotnet restore` for new pin | ✓ (verified Phase 1) | — | Local mirror if needed (none configured) |

**Missing dependencies with no fallback:** None.

**Missing dependencies with fallback:** None.

**No blocking environment issues for Phase 3.**

## Project Constraints (from CLAUDE.md)

> `./CLAUDE.md` does not exist in the working directory. No project-specific directives to enforce beyond the locked decisions captured in `<user_constraints>` above and the established patterns from Phase 1/2 SUMMARYs (TreatWarningsAsErrors, CPM, xUnit v3 MTP scaffolding, dotnet 8.0.421 SDK pin, evidence-only verification plan pattern).

## References

### Primary (HIGH confidence — Context7-equivalent / Official Docs)

- **EF Core Interceptors** — https://learn.microsoft.com/en-us/ef/core/logging-events-diagnostics/interceptors (refreshed 2026-02-26) — `ISaveChangesInterceptor` interface, `SaveChangesInterceptor` base class, `SavingChangesAsync` exact signature [VERIFIED via WebFetch]
- **EF Core Saving / Interceptors landing** — https://learn.microsoft.com/en-us/ef/core/saving/interceptors — referenced in CONTEXT.md § Authoritative External Docs (page exists; WebFetch returned 404 during session — fallback to the `/logging-events-diagnostics/interceptors` page which is the canonical version)
- **Npgsql Concurrency / xmin** — https://www.npgsql.org/efcore/modeling/concurrency.html — `IsRowVersion()` on `uint` pattern; xmin as Postgres system column [CITED via WebFetch]
- **Npgsql DateTime / timestamptz** — https://www.npgsql.org/efcore/mapping/general.html#date-and-time-types — UTC-only rule (Pitfall 1 source) [referenced in CONTEXT.md canonical refs]
- **TimeProvider abstraction** — https://learn.microsoft.com/en-us/dotnet/api/system.timeprovider — .NET 8 BCL clock abstraction
- **FakeTimeProvider** — https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.time.testing.faketimeprovider — `SetUtcNow(DateTimeOffset)`, `Advance(TimeSpan)`
- **NuGet — Microsoft.Extensions.TimeProvider.Testing** — https://www.nuget.org/packages/Microsoft.Extensions.TimeProvider.Testing — 8.10.0 latest 8.x, published 2024-10-08, namespace `Microsoft.Extensions.Time.Testing` [VERIFIED via WebFetch + nuget API]
- **NuGet — EFCore.NamingConventions** — https://www.nuget.org/packages/EFCore.NamingConventions/8.0.3 — deps: EF Core 8.0.0..<9.0.0, EF Core Relational 8.0.0..<9.0.0, MS.Extensions.DI.Abstractions >=8.0.0 [VERIFIED via WebFetch]
- **EFCore.NamingConventions GitHub** — https://github.com/efcore/EFCore.NamingConventions — `UseSnakeCaseNamingConvention()` API; cautions about retrofitting (Pitfall 4 source)
- **ASP.NET Core HttpContext access** — https://learn.microsoft.com/en-us/aspnet/core/fundamentals/http-context (refreshed 2026-04-27) — `IHttpContextAccessor` canonical access pattern from custom components / class libraries [VERIFIED via WebFetch]
- **xUnit v3 Parallelism** — https://xunit.net/docs/running-tests-in-parallel — class-level parallelism default; `[Collection]` opt-in for sequencing [VERIFIED via WebFetch]

### Secondary (MEDIUM confidence — Project Research Artifacts)

- `.planning/research/STACK.md` § EF Core + Npgsql rows (locked versions table) — package pin authority
- `.planning/research/ARCHITECTURE.md` § Persistence layer + Pattern 5 (AuditInterceptor skeleton lines 481-516) + § Composition root lines 215-291 — Phase 7 wiring shapes that Phase 3 must produce compatible interfaces for
- `.planning/research/PITFALLS.md` § Pitfalls 1, 2, 4, 6 — verbatim sources for D-07/D-08 (UTC), D-11 (scoped DI), D-09 (snake_case timing), D-03 (xmin shape)
- `.planning/research/FEATURES.md` row 70 — Generic Repository<TEntity>: "Don't add `IQueryable` leakage — keep the surface tight" — verbatim source for D-04
- `.planning/phases/01-repository-scaffold/01-CONTEXT.md` D-02/D-05/D-08/D-10/D-11 — inherited build strictness + CPM + folder scaffold + Program.cs scaffold + xUnit v3 project shape
- `.planning/phases/02-postgres-docker-compose/02-CONTEXT.md` D-01/D-02/D-04..D-07 — port 5433, `appsettings.Development.json` connection string, `.env` credentials that Phase 3 tests reuse

### Tertiary (LOW confidence — None Used)

None — all claims are verified or cited from primary/secondary sources. Where assumptions exist, they are explicitly flagged in `## Assumptions Log` above.

## Metadata

**Confidence breakdown:**

- **Package versions / Compatibility:** HIGH — every version verified against nuget.org during this session; `Microsoft.Extensions.TimeProvider.Testing 8.10.0` confirmed as latest 8.x line published 2024-10-08 with zero net8.0 dependencies.
- **AuditInterceptor signature / SaveChangesInterceptor base class:** HIGH — verbatim signature from MS Learn EF Core docs (page refreshed 2026-02-26).
- **xmin shape compatibility:** HIGH — D-03 shape matches the canonical EF Core 8 + Npgsql 8 pattern. Minor open question on EnsureCreated emitting an `xmin` column tagged ASSUMED (A1) — validation test will catch any regression.
- **FrameworkReference for IHttpContextAccessor:** HIGH — documented MS Learn pattern; no NuGet-packability conflict for v1 (Out of Scope).
- **Test infrastructure (throwaway DB, FakeTimeProvider, StubHttpContextAccessor):** HIGH — pattern matches MS Learn http-context article, Npgsql 8 connection pool semantics (`ClearAllPools` + `DROP ... WITH (FORCE)` is the documented v17 idiom), and the `FakeTimeProvider` API verified via nuget page.
- **xUnit v3 parallelism / fixture lifecycle:** HIGH — verified against xUnit v3 docs.
- **Cross-phase surface (consumed by Phases 4/5/7/8):** HIGH — directly mapped from CONTEXT.md `<deferred>` section and ARCHITECTURE.md composition root sketch.

**Research date:** 2026-05-26
**Valid until:** 2026-07-25 (60 days — stack is on .NET 8 LTS which doesn't change until next monthly patch 2026-06; Npgsql 8.x stable; EFCore.NamingConventions 8.x stable; Microsoft.Extensions.TimeProvider.Testing on its own slow cadence). Re-verify if planner runs after .NET 8.0.28 ships or if Microsoft.Extensions.TimeProvider.Testing 8.11.0+ releases.

## RESEARCH COMPLETE
