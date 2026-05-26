# Phase 3: EF Core Persistence Base - Pattern Map

**Mapped:** 2026-05-26
**Files analyzed:** 5 production source files + 2 csproj edits + 1 props edit + 9 test files + 3 planning artifacts = 20 entries
**Analogs found:** 7 IDENTICAL / 6 ADAPTED / 7 NEW (no in-repo analog — RESEARCH.md skeleton is the canonical source)

## Scope-File Inventory (extracted from CONTEXT.md D-12/D-13/D-14/D-17/D-18 + RESEARCH.md file-layout sections)

### Production code (Plan 03-01)

| # | File | Status | Category |
|---|------|--------|----------|
| 1 | `src/BaseApi.Core/Entities/BaseEntity.cs` | NEW | NEW (no in-repo C# files exist in Entities/ — only `.gitkeep`) |
| 2 | `src/BaseApi.Core/Persistence/BaseDbContext.cs` | NEW | NEW (no in-repo C# files exist in Persistence/) |
| 3 | `src/BaseApi.Core/Persistence/Interceptors/AuditInterceptor.cs` | NEW | NEW (Persistence/Interceptors/ has only `.gitkeep`) |
| 4 | `src/BaseApi.Core/Persistence/Repositories/IRepository.cs` | NEW | NEW (Persistence/Repositories/ has only `.gitkeep`) |
| 5 | `src/BaseApi.Core/Persistence/Repositories/Repository.cs` | NEW | NEW (Persistence/Repositories/ has only `.gitkeep`) |
| 6 | `src/BaseApi.Core/BaseApi.Core.csproj` | MODIFY | ADAPTED (csproj exists from Phase 1 — Phase 3 inserts `<ItemGroup>` blocks per the established Phase 1 csproj convention) |
| 7 | `Directory.Packages.props` | MODIFY | IDENTICAL (file exists with 22 pins — Phase 3 appends one new `<PackageVersion>` line matching the existing format verbatim) |

### Test code (Plan 03-02 Wave 0 scaffold + facts)

| # | File | Status | Category |
|---|------|--------|----------|
| 8 | `tests/BaseApi.Tests/Persistence/TestEntity.cs` | NEW | NEW (no test fixture/entity scaffolding exists; `MetaTest.cs` is the only test file) |
| 9 | `tests/BaseApi.Tests/Persistence/TestDbContext.cs` | NEW | NEW |
| 10 | `tests/BaseApi.Tests/Persistence/PostgresFixture.cs` | NEW | NEW (no `IAsyncLifetime` fixture exists yet) |
| 11 | `tests/BaseApi.Tests/Persistence/StubHttpContextAccessor.cs` | NEW | NEW (no test doubles exist yet) |
| 12 | `tests/BaseApi.Tests/Persistence/SchemaTests.cs` | NEW | ADAPTED (mirrors `MetaTest.cs` namespace/`[Fact]` shape; new substance) |
| 13 | `tests/BaseApi.Tests/Persistence/AuditInterceptorTests.cs` | NEW | ADAPTED (same — mirrors MetaTest skeleton) |
| 14 | `tests/BaseApi.Tests/Persistence/DiLifetimeTests.cs` | NEW | ADAPTED |
| 15 | `tests/BaseApi.Tests/Persistence/XminConcurrencyTokenTests.cs` | NEW | ADAPTED |
| 16 | `tests/BaseApi.Tests/Persistence/RepositorySurfaceTests.cs` | NEW | ADAPTED |
| 17 | `tests/BaseApi.Tests/BaseApi.Tests.csproj` | MODIFY | ADAPTED (csproj exists with 3 xunit refs + MTP scaffold; Phase 3 inserts EF Core + Npgsql + FakeTimeProvider `<ItemGroup>` blocks following the existing csproj's `<ItemGroup>`-per-concern grouping) |

### Planning artifacts (Phase 3 plan structure per D-17 + D-18)

| # | File | Status | Category |
|---|------|--------|----------|
| 18 | `.planning/phases/03-ef-core-persistence-base/03-01-PLAN.md` | NEW | IDENTICAL (frontmatter + structure mirrors Phase 2's `02-01-PLAN.md`; values change) |
| 19 | `.planning/phases/03-ef-core-persistence-base/03-02-PLAN.md` | NEW | IDENTICAL (verification-plan shape from `01-03-PLAN.md` + `02-02-PLAN.md`) |
| 20 | `.planning/phases/03-ef-core-persistence-base/03-SUMMARY.md` | NEW | ADAPTED (Phase 3 introduces a phase-level rollup; no `01-SUMMARY.md` / `02-SUMMARY.md` exists — only per-plan SUMMARYs. Closest analog is `01-03-SUMMARY.md` frontmatter + body shape) |

## File Classification

### Production code

| File | Role | Data Flow | Closest Analog | Match |
|------|------|-----------|----------------|-------|
| `BaseEntity.cs` | entity (abstract base class — no table) | static (POCO; properties read/written by ChangeTracker) | NONE in repo — RESEARCH.md § "BaseEntity Shape" + ENTITY-01 field list | **NEW** |
| `BaseDbContext.cs` | dbcontext (abstract — no DbSets) | event-driven (OnModelCreating model-builder iteration; OnConfiguring runtime wiring) | NONE in repo — RESEARCH.md § "BaseDbContext — Abstract Shape" (lines 313-409) | **NEW** |
| `AuditInterceptor.cs` | interceptor (EF Core `SaveChangesInterceptor` base) | event-driven (intercepts `SaveChangesAsync`; mutates ChangeTracker entries) | NONE in repo — RESEARCH.md § "AuditInterceptor — Full ISaveChangesInterceptor Skeleton" (lines 131-251) | **NEW** |
| `IRepository.cs` | repository interface (generic, BaseEntity-constrained) | CRUD (5 method surface) | NONE in repo — RESEARCH.md § "Repository<T> — Final 5-Method Surface" (lines 411-444) | **NEW** |
| `Repository.cs` | repository concrete (sealed generic) | CRUD | NONE in repo — RESEARCH.md § "Repository<T>" code block (lines 445-481) | **NEW** |

### csproj edits

| File | Role | Data Flow | Closest Analog | Match |
|------|------|-----------|----------------|-------|
| `BaseApi.Core.csproj` | csproj-edit (class library, adds PackageRefs + FrameworkReference) | static | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (existing csproj that already demonstrates the "`<ItemGroup>`-per-concern grouping + zero `Version=` attributes + ProjectReference" convention) | **ADAPTED** |
| `BaseApi.Tests.csproj` | csproj-edit (test project, adds PackageRefs + FrameworkReference) | static | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (self — extends existing ItemGroup pattern) | **ADAPTED** |

### Central package management

| File | Role | Data Flow | Closest Analog | Match |
|------|------|-----------|----------------|-------|
| `Directory.Packages.props` | props-edit (adds one `<PackageVersion>` pin) | static | `Directory.Packages.props` (self — 22 existing pins, all in identical shape) | **IDENTICAL** |

### Test code

| File | Role | Data Flow | Closest Analog | Match |
|------|------|-----------|----------------|-------|
| `TestEntity.cs` | test-only entity (`BaseEntity` subclass) | static (POCO) | NONE — RESEARCH.md § "TestEntity (lives in test project)" (lines 506-521) | **NEW** |
| `TestDbContext.cs` | test-only dbcontext (`BaseDbContext` subclass with `DbSet<TestEntity>`) | static + event-driven (inherits BaseDbContext model wiring) | NONE — RESEARCH.md § "TestDbContext" (lines 523-538) | **NEW** |
| `PostgresFixture.cs` | test fixture (`IAsyncLifetime` throwaway-DB lifecycle) | request-response (admin DB CREATE/DROP via `NpgsqlConnection`) | NONE — RESEARCH.md § "Throwaway database fixture" (lines 540-592) | **NEW** |
| `StubHttpContextAccessor.cs` | test double (handwritten `IHttpContextAccessor`) | static | NONE — RESEARCH.md § "Handwritten IHttpContextAccessor stub" (lines 606-650) | **NEW** |
| `SchemaTests.cs` | test class (xUnit v3 `[Fact]` — SC#1) | request-response (Postgres `information_schema.columns` query) | `tests/BaseApi.Tests/MetaTest.cs` (xUnit v3 `[Fact]` skeleton — namespace, using-directive, class shape) | **ADAPTED** |
| `AuditInterceptorTests.cs` | test class (xUnit v3 `[Fact]` — SC#2 + SC#3) | event-driven (intercepts via `SavingChangesAsync`) | `MetaTest.cs` (skeleton) + RESEARCH.md § "FakeTimeProvider usage example" (lines 653-682) | **ADAPTED** |
| `DiLifetimeTests.cs` | test class (xUnit v3 `[Fact]` — SC#4) | request-response (`IServiceProvider.GetRequiredService` × 2) | `MetaTest.cs` (skeleton) | **ADAPTED** |
| `XminConcurrencyTokenTests.cs` | test class (xUnit v3 `[Fact]` — Dim 6 model-introspection) | static (EF model metadata read) | `MetaTest.cs` (skeleton) + RESEARCH.md § Validation Dim 6 row (line 804) | **ADAPTED** |
| `RepositorySurfaceTests.cs` | test class (xUnit v3 `[Fact]` — Dim 7 reflection) | static (reflection over `typeof(IRepository<>)`) | `MetaTest.cs` (skeleton) + RESEARCH.md § Validation Dim 7 row (line 805) | **ADAPTED** |

### Planning artifacts

| File | Role | Closest Analog | Match |
|------|------|----------------|-------|
| `03-01-PLAN.md` | build plan (executes file writes) | `.planning/phases/02-postgres-docker-compose/02-01-PLAN.md` (build plan — frontmatter + `<objective>` + `<context>` + `<threat_model>` + `<tasks>` shape) | **IDENTICAL** |
| `03-02-PLAN.md` | verification plan (`autonomous: false`, `files_modified: []`, evidence commits) | `.planning/phases/01-repository-scaffold/01-03-PLAN.md` AND `.planning/phases/02-postgres-docker-compose/02-02-PLAN.md` | **IDENTICAL** |
| `03-SUMMARY.md` | phase-level SUMMARY rollup | `.planning/phases/01-repository-scaffold/01-03-SUMMARY.md` (frontmatter + body shape — closest available since no top-level `01-SUMMARY.md` exists in repo) | **ADAPTED** |

---

## Pattern Assignments

### `src/BaseApi.Core/Entities/BaseEntity.cs` (entity, NEW)

**Status:** NEW — no analog. RESEARCH.md is the authoritative shape.

**Source:** RESEARCH.md § "BaseEntity Shape" requirement (ENTITY-01) + CONTEXT.md D-01..D-02 + Pitfall 1 verbatim guidance.

**ENTITY-01 field list (extracted from REQUIREMENTS.md via RESEARCH.md L71):**
- `Guid Id` (D-01: client-side generated by AuditInterceptor when `Guid.Empty`)
- `string Name` (non-nullable)
- `string Version` (non-nullable; SemVer string — Phase 6 validates)
- `DateTime CreatedAt` (UTC — Pitfall 1)
- `DateTime UpdatedAt` (UTC — Pitfall 1)
- `string? CreatedBy` (nullable — PERSIST-04 / D-08 null-fallback)
- `string? UpdatedBy` (nullable)
- `string? Description` (nullable)

**Pattern (RESEARCH.md doesn't include a verbatim BaseEntity.cs block; planner must synthesize from the field list + Pitfall 1 XML doc per CONTEXT.md "Claude's Discretion"):**

```csharp
// src/BaseApi.Core/Entities/BaseEntity.cs
namespace BaseApi.Core.Entities;

/// <summary>
/// Abstract base for all audit-stamped domain entities.
///
/// <para>
/// Concrete subclasses (SchemaEntity, ProcessorEntity, StepEntity, AssignmentEntity,
/// WorkflowEntity — Phase 8) inherit Id + audit fields. Junction entities
/// (StepNextSteps, WorkflowEntrySteps, WorkflowAssignments) deliberately do
/// NOT derive — they are non-BaseEntity per ARCHITECTURE.md and are excluded
/// from the xmin shadow-property iteration (BaseDbContext.OnModelCreating).
/// </para>
/// </summary>
public abstract class BaseEntity
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    /// <remarks>UTC by convention — set by <see cref="AuditInterceptor"/>; never assign manually. (Pitfall 1)</remarks>
    public DateTime CreatedAt { get; set; }

    /// <remarks>UTC by convention — set by <see cref="AuditInterceptor"/>; never assign manually. (Pitfall 1)</remarks>
    public DateTime UpdatedAt { get; set; }

    public string? CreatedBy { get; set; }

    public string? UpdatedBy { get; set; }

    public string? Description { get; set; }
}
```

**Mapping notes:**
- The `:warning`-severity `csharp_style_namespace_declarations = file_scoped` rule from `.editorconfig` (Phase 1) MAKES file-scoped namespace MANDATORY here (block-scoped fails build).
- `<GenerateDocumentationFile>true</GenerateDocumentationFile>` + `<NoWarn>$(NoWarn);CS1591</NoWarn>` in `BaseApi.Core.csproj` (Phase 1 line 26-27) means XML docs are optional but recommended; the Pitfall 1 `<remarks>` block is the load-bearing one.
- Properties are mutable (`{ get; set; }`) NOT `init` — AuditInterceptor mutates `CreatedAt`/`UpdatedAt`/`CreatedBy`/`UpdatedBy`/`Id` on entries already in the ChangeTracker, which requires settable properties.

---

### `src/BaseApi.Core/Persistence/BaseDbContext.cs` (dbcontext, NEW)

**Status:** NEW. **Pattern source: RESEARCH.md § "BaseDbContext — Abstract Shape" "Simplified constructor (recommended)" code block (lines 397-407).**

**Verbatim simplified-constructor pattern (recommended over the optional-AuditInterceptor variant per RESEARCH.md A6):**

```csharp
// src/BaseApi.Core/Persistence/BaseDbContext.cs
using Microsoft.EntityFrameworkCore;
using BaseApi.Core.Entities;

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
/// <c>UseSnakeCaseNamingConvention()</c> call that Phase 7's <c>AddBaseApi&lt;TDbContext&gt;</c>
/// extension also performs. The duplication ensures test paths (which build DbContextOptions
/// directly without AddBaseApi) still get the correct configuration. The interceptor is
/// wired by the composition root (Phase 7) or the test fixture (Phase 3) via
/// DbContextOptionsBuilder.AddInterceptors(...), NOT here.
/// </para>
/// </summary>
public abstract class BaseDbContext : DbContext
{
    protected BaseDbContext(DbContextOptions options) : base(options) { }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.UseSnakeCaseNamingConvention();
        // AuditInterceptor is wired via AddInterceptors(...) at composition root (Phase 7)
        // OR by the test fixture's options builder (Phase 3). NOT here.
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

**Mapping notes:**
- D-03 iteration block (`foreach` over `modelBuilder.Model.GetEntityTypes()`) is verbatim from CONTEXT.md D-03 + RESEARCH.md lines 257-268. Junction entities are excluded naturally because they do NOT derive `BaseEntity`.
- `using` directives are above the namespace declaration per `.editorconfig`'s `csharp_using_directive_placement = outside_namespace:warning` (build-fatal due to TreatWarningsAsErrors).
- The class is `abstract` — no `DbSet<>` properties (D-09 lock). Concrete `AppDbContext` (Phase 8) and `TestDbContext` (Phase 3 test) derive and add DbSets.
- `protected BaseDbContext(DbContextOptions options) : base(options)` accepts the open-generic — derived classes pass closed `DbContextOptions<TestDbContext>` / `DbContextOptions<AppDbContext>`, which are assignable to the open base. Standard EF pattern.

---

### `src/BaseApi.Core/Persistence/Interceptors/AuditInterceptor.cs` (interceptor, NEW)

**Status:** NEW. **Pattern source: RESEARCH.md § "AuditInterceptor — Full ISaveChangesInterceptor Skeleton" "Complete code skeleton" (lines 161-246).**

**Verbatim skeleton (copy with no structural modification; planner may add `sealed`/XML-doc tightening):**

```csharp
// src/BaseApi.Core/Persistence/Interceptors/AuditInterceptor.cs
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using BaseApi.Core.Entities;

namespace BaseApi.Core.Persistence.Interceptors;

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

**Mapping notes:**
- Derives `SaveChangesInterceptor` (the no-op base class), NOT `ISaveChangesInterceptor` directly — RESEARCH.md Executive Summary bullet 2 + lines 133-135 cite the MS Learn doc.
- Async-only override (no sync `SavingChanges`) — production + tests use `SaveChangesAsync` exclusively (RESEARCH.md lines 137-143).
- `_clock.GetUtcNow().UtcDateTime` — RESEARCH.md line 215 + Pitfall 1 verbatim (UTC Kind enforced).
- `_httpContextAccessor.HttpContext?.User?.Identity?.Name` — Pitfall-1-free null-chain; D-08 null fallback (no exception when HttpContext is null).
- `IsModified = false` on `CreatedAt`/`CreatedBy` during Modified — RESEARCH.md lines 233-237 defensive pattern (prevents accidental overwrite via `Update()`).

---

### `src/BaseApi.Core/Persistence/Repositories/IRepository.cs` (repository interface, NEW)

**Status:** NEW. **Pattern source: RESEARCH.md § "Repository<T> — Final 5-Method Surface" code block (lines 416-443).**

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

**Mapping notes:**
- EXACTLY 5 methods (D-04 lock). No `IQueryable<>` return; no `Where(predicate)` overload; no `ExistsAsync`.
- `TEntity : BaseEntity` generic constraint — narrows the repository to audit-stamped entities; junction entities are non-`BaseEntity` and accessed via raw `DbContext.Set<TJunction>()` in Phase 8 services.
- `void Update(...)` is sync per D-04 + RESEARCH.md "Update sync vs async" (lines 491-495).

---

### `src/BaseApi.Core/Persistence/Repositories/Repository.cs` (repository concrete, NEW)

**Status:** NEW. **Pattern source: RESEARCH.md § "Repository<T>" code block (lines 446-481).**

```csharp
// src/BaseApi.Core/Persistence/Repositories/Repository.cs
using Microsoft.EntityFrameworkCore;
using BaseApi.Core.Entities;
using BaseApi.Core.Persistence;

namespace BaseApi.Core.Persistence.Repositories;

public sealed class Repository<TEntity> : IRepository<TEntity> where TEntity : BaseEntity
{
    private readonly DbContext _db;
    private readonly DbSet<TEntity> _set;

    public Repository(BaseDbContext db)  // BaseDbContext-typed constructor (RESEARCH.md recommendation)
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

**Mapping notes:**
- `Repository(BaseDbContext db)` constructor — RESEARCH.md "Constructor choice: `BaseDbContext` direct (recommended)" (lines 483-489). Stronger type safety than `DbContext`; Phase 7's `AddBaseApi<TDbContext>` extension always provides a `BaseDbContext`-derived concrete.
- `DeleteAsync` is load-then-remove (NOT `Attach + Remove`) — RESEARCH.md lines 497-501 preserves the xmin concurrency check (D-03).
- `sealed` because no `Repository<T>` subclassing is anticipated.
- The `_db` field is unused inside the body but holds the BaseDbContext reference for potential future per-method override paths; planner can drop it if not needed (no functional impact).

---

### `src/BaseApi.Core/BaseApi.Core.csproj` (csproj-edit, ADAPTED)

**Status:** MODIFY. **Analog: the file itself + `tests/BaseApi.Tests/BaseApi.Tests.csproj` (existing `<ItemGroup>`-per-concern grouping convention).**

**Current state (Phase 1 — verbatim, all 30 lines):**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    BaseApi.Core — reusable infrastructure class library.

    Per Phase 1 CONTEXT.md decisions:
      D-01: TargetFramework, Nullable, ImplicitUsings, LangVersion, AnalysisMode,
            EnforceCodeStyleInBuild, TreatWarningsAsErrors are inherited from
            Directory.Build.props at the repo root. DO NOT re-declare them here.
      D-05: NuGet versions are pinned in Directory.Packages.props. Any future
            <PackageReference Include="..." /> entries in this file MUST NOT
            include a Version= attribute (CPM forbids it).

    Phase 1 scope: this file has NO PackageReferences and NO ProjectReferences.
    The csproj exists to make `dotnet build` produce an empty assembly that
    Phase 1 SC#1 ("zero warnings") can be verified against.

    Phases 3-7 will add PackageReferences for EF Core, FluentValidation, OTel,
    AspNetCore.HealthChecks.NpgSql, etc. — all already pinned in
    Directory.Packages.props by Phase 1 Plan 01 Task 3.
  -->

  <PropertyGroup>
    <RootNamespace>BaseApi.Core</RootNamespace>
    <AssemblyName>BaseApi.Core</AssemblyName>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
  </PropertyGroup>

</Project>
```

**Phase 3 patch (per CONTEXT.md D-12 + RESEARCH.md § "BaseApi.Core csproj Changes" lines 690-712):**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!-- ...existing PropertyGroup + Phase 1 header comment unchanged...

       Phase 3 addition (CONTEXT.md D-12 + Phase 3 RESEARCH.md):
         - FrameworkReference Microsoft.AspNetCore.App   — gives IHttpContextAccessor + the
           ASP.NET surface without per-feature PackageReferences. Canonical pattern for an
           ASP.NET-coupled class library (RESEARCH.md Exec Summary bullet 4).
         - 4 PackageReferences for EF Core + Npgsql + EFCore.NamingConventions (pins resolved
           from Directory.Packages.props — no Version= per CPM contract).
  -->

  <PropertyGroup>
    <RootNamespace>BaseApi.Core</RootNamespace>
    <AssemblyName>BaseApi.Core</AssemblyName>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <!-- ASP.NET Core surface (IHttpContextAccessor — Phase 3; HealthChecks, Middleware,
         Controllers in later phases). FrameworkReference is the documented pattern for
         ASP.NET-coupled class libraries (CONTEXT.md D-12 specifics + RESEARCH.md lines 690-727). -->
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <!-- EF Core 8 + Npgsql + snake_case convention (CONTEXT.md D-12) -->
    <PackageReference Include="Microsoft.EntityFrameworkCore" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
    <PackageReference Include="EFCore.NamingConventions" />
  </ItemGroup>

</Project>
```

**Mapping notes:**
- Zero `Version=` attributes on PackageReference (CPM contract — would emit `NU1010` if violated).
- `<FrameworkReference>` takes no version (the runtime is `net8.0` per `Directory.Build.props`).
- `<ItemGroup>`-per-concern grouping (one for FrameworkReference, one for PackageReferences) matches `BaseApi.Tests.csproj`'s existing convention (xunit refs in one ItemGroup, ProjectReference in another).
- Existing Phase 1 header comment in the csproj already explicitly anticipated Phase 3 ("Phases 3-7 will add PackageReferences for EF Core, FluentValidation, OTel..." — line 18-20). Planner can update that comment to reflect the actual Phase 3 additions, or leave it as-is.

---

### `tests/BaseApi.Tests/BaseApi.Tests.csproj` (csproj-edit, ADAPTED)

**Status:** MODIFY. **Analog: the file itself (existing 68-line csproj with the established `<ItemGroup>` grouping).**

**Current state (Phase 1 + Phase 1 fix-forward — relevant excerpt):**

```xml
  <ItemGroup>
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.v3.assert" />
    <PackageReference Include="xunit.runner.visualstudio">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\BaseApi.Core\BaseApi.Core.csproj" />
  </ItemGroup>
```

**Phase 3 patch (per CONTEXT.md D-13 + RESEARCH.md § "BaseApi.Tests csproj Changes" lines 740-773):**

Insert TWO new `<ItemGroup>` blocks between the existing xunit ItemGroup and the existing ProjectReference ItemGroup:

```xml
  <ItemGroup>
    <!-- existing xunit refs UNCHANGED -->
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.v3.assert" />
    <PackageReference Include="xunit.runner.visualstudio">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>

  <ItemGroup>
    <!-- Phase 3 additions: EF Core test fixture + FakeTimeProvider (CONTEXT.md D-13) -->
    <PackageReference Include="Microsoft.EntityFrameworkCore" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
    <PackageReference Include="EFCore.NamingConventions" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
  </ItemGroup>

  <ItemGroup>
    <!-- Phase 3 addition: FrameworkReference for DefaultHttpContext / ClaimsPrincipal
         in StubHttpContextAccessor (RESEARCH.md A3). -->
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <!-- existing ProjectReference UNCHANGED -->
    <ProjectReference Include="..\..\src\BaseApi.Core\BaseApi.Core.csproj" />
  </ItemGroup>
```

**Mapping notes:**
- The existing 4 PropertyGroup MTP scaffold properties (`<OutputType>Exe</OutputType>`, `<UseMicrosoftTestingPlatformRunner>true</>`, `<TestingPlatformDotnetTestSupport>true</>`, `<IsTestProject>true</>`) MUST NOT be touched — they are the load-bearing Phase 1 fix-forward.
- Five new `<PackageReference>` entries, no `Version=` attributes.
- Phase 3's `Directory.Packages.props` edit adds the `Microsoft.Extensions.TimeProvider.Testing` pin BEFORE the csproj reference resolves (else `dotnet restore` emits `NU1604`). Planner sequences: `Directory.Packages.props` edit → csproj edit.

---

### `Directory.Packages.props` (props-edit, IDENTICAL)

**Status:** MODIFY. **Analog: the file itself — appends one `<PackageVersion>` matching the existing 22-row format.**

**Current state (Phase 1 — relevant excerpt, line 80-86):**

```xml
    <!-- Tests -->
    <PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="8.0.27" />
    <PackageVersion Include="xunit.v3" Version="3.2.2" />
    <PackageVersion Include="xunit.v3.assert" Version="3.2.2" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.5" />
    <PackageVersion Include="Testcontainers.PostgreSql" Version="4.11.0" />
  </ItemGroup>
```

**Phase 3 patch (per RESEARCH.md § "New pin in Directory.Packages.props" lines 731-738):**

Append ONE new `<PackageVersion>` line inside the existing `<ItemGroup>`, after the existing 22 pins. Recommended placement: directly after the `Testcontainers.PostgreSql` line (the test family group), or as a new section comment block, e.g.:

```xml
    <!-- Tests -->
    <PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="8.0.27" />
    <PackageVersion Include="xunit.v3" Version="3.2.2" />
    <PackageVersion Include="xunit.v3.assert" Version="3.2.2" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.5" />
    <PackageVersion Include="Testcontainers.PostgreSql" Version="4.11.0" />

    <!-- Microsoft.Extensions.* family — versions on own cadence (Phase 3: FakeTimeProvider) -->
    <PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="8.10.0" />
  </ItemGroup>
```

**Mapping notes:**
- Format matches the existing 22 rows EXACTLY: `<PackageVersion Include="..." Version="..." />` self-closing tag, `Include` before `Version`.
- `8.10.0` is the latest 8.x line per RESEARCH.md line 110 (published 2024-10-08; zero net8.0 deps).
- Section comment `<!-- Microsoft.Extensions.* family — versions on own cadence ... -->` matches the existing comment convention (one section comment per logical family — see lines 47, 54, 57, 61, 65, 69, 73, 76, 80 of the existing file).
- The existing `<PropertyGroup>` block with `<ManagePackageVersionsCentrally>true</>` is NOT touched.

---

### `tests/BaseApi.Tests/Persistence/TestEntity.cs` (test entity, NEW)

**Status:** NEW. **Pattern source: RESEARCH.md § "TestEntity (lives in test project)" (lines 506-521).**

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

**Mapping notes:**
- `sealed` because no further subclassing.
- Empty body — the audit-stamping + xmin + snake_case logic is tested through the inherited `BaseEntity` properties, not through any TestEntity-specific field.
- Lives in the test project (CONTEXT.md "Claude's Discretion" — planner picks; this is the recommended path, NOT InternalsVisibleTo'd Core).

---

### `tests/BaseApi.Tests/Persistence/TestDbContext.cs` (test dbcontext, NEW)

**Status:** NEW. **Pattern source: RESEARCH.md § "TestDbContext" (lines 523-538).**

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

**Mapping notes:**
- Constructor takes the closed-generic `DbContextOptions<TestDbContext>` (which the EF `AddDbContext<TestDbContext>` registration produces). The closed-generic flows up to the open-generic `DbContextOptions` accepted by `BaseDbContext`.
- The `DbSet<TestEntity>` property uses the `=> Set<TestEntity>()` expression-bodied form — this is the EF 8 idiomatic shape (avoids the auto-property + `OnModelCreating` `modelBuilder.Entity<>()` registration; EF auto-discovers entities via DbSet props).

---

### `tests/BaseApi.Tests/Persistence/PostgresFixture.cs` (test fixture, NEW)

**Status:** NEW. **Pattern source: RESEARCH.md § "Throwaway database fixture" (lines 540-592).**

**Verbatim skeleton:**

```csharp
// tests/BaseApi.Tests/Persistence/PostgresFixture.cs
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BaseApi.Tests.Persistence;

public sealed class PostgresFixture : IAsyncLifetime
{
    public string DatabaseName { get; } = $"stepsdb_test_{Guid.NewGuid():N}";
    public string ConnectionString { get; private set; } = default!;

    private const string AdminConnectionString =
        "Host=localhost;Port=5433;Database=postgres;Username=postgres;Password=postgres";

    public async ValueTask InitializeAsync()
    {
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
        NpgsqlConnection.ClearAllPools();

        await using var adminConn = new NpgsqlConnection(AdminConnectionString);
        await adminConn.OpenAsync();
        await using var dropCmd = adminConn.CreateCommand();
        dropCmd.CommandText = $"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE)";
        await dropCmd.ExecuteNonQueryAsync();
    }
}
```

**Mapping notes:**
- `IAsyncLifetime` is xUnit v3's fixture lifecycle interface; `InitializeAsync` runs before the first test in the class; `DisposeAsync` runs after the last.
- Connection-pool clear (`NpgsqlConnection.ClearAllPools()`) + `WITH (FORCE)` on DROP — both are required (RESEARCH.md gotchas table line 598) to avoid "database is being accessed by other users" failure on dispose.
- `DatabaseName` uses `Guid.NewGuid():N` (32 lowercase hex chars; no separators; safe as PG identifier without case-fold issues).
- Admin DB connection uses superuser `postgres` (Phase 2 D-04 grants CREATE DATABASE).
- Hard-coded `localhost:5433` matches Phase 2 D-01 host port (CONTEXT.md D-15). Planner may refactor to read from `appsettings.Development.json` (CONTEXT.md specifics § "appsettings.Development.json connection-string reuse"), but the hard-coded admin string is simpler for the throwaway-DB CREATE/DROP path (the appsettings string points at `stepsdb`, not at the admin `postgres` DB).

---

### `tests/BaseApi.Tests/Persistence/StubHttpContextAccessor.cs` (test double, NEW)

**Status:** NEW. **Pattern source: RESEARCH.md § "Handwritten IHttpContextAccessor stub" (lines 606-650).**

```csharp
// tests/BaseApi.Tests/Persistence/StubHttpContextAccessor.cs
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace BaseApi.Tests.Persistence;

/// <summary>
/// Hand-written test double for <see cref="IHttpContextAccessor"/>.
/// REQUIREMENTS.md Out of Scope excludes Moq/NSubstitute in v1; this is the
/// minimal stub Phase 3 tests need.
/// </summary>
public sealed class StubHttpContextAccessor : IHttpContextAccessor
{
    public HttpContext? HttpContext { get; set; }

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

**Mapping notes:**
- `DefaultHttpContext` available via the test csproj's `<FrameworkReference Include="Microsoft.AspNetCore.App" />` (added in this phase).
- `ClaimsIdentity(authenticationType: "Test")` — non-null `authenticationType` makes `Identity.IsAuthenticated == true`. The audit interceptor only reads `Identity?.Name`, so either form works; explicit `"Test"` makes the SC#3 happy-path assertion clean.
- `SetUser(null)` is the null-fallback case for SC#3 (simulates "no HttpContext" — non-HTTP execution path).
- `System.Security.Principal` import unused after typing — planner removes if unused (TreatWarningsAsErrors+IDE0005 promote unused-using to a build-time suggestion; not fatal here because IDE0005 severity is `suggestion` in `.editorconfig` line 575).

---

### `tests/BaseApi.Tests/Persistence/SchemaTests.cs` (test class, ADAPTED — SC#1)

**Status:** NEW. **Pattern sources:**
1. **Skeleton from `tests/BaseApi.Tests/MetaTest.cs`** (lines 1-23): namespace `BaseApi.Tests`, `using Xunit;`, `public sealed class FooTest`, `[Fact] public void Method()` shape.
2. **Substance from RESEARCH.md Validation Dim 2** (line 800): query `information_schema.columns` for snake_case column names.

**MetaTest.cs skeleton (verbatim — the namespace + class shape to mirror):**

```csharp
using Xunit;

namespace BaseApi.Tests;

public sealed class MetaTest
{
    [Fact]
    public void Sanity() => Assert.True(true);
}
```

**Phase 3 SchemaTests.cs synthesis (planner derives the substance from RESEARCH.md SC#1 line 86 + FakeTimeProvider example):**

```csharp
// tests/BaseApi.Tests/Persistence/SchemaTests.cs
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace BaseApi.Tests.Persistence;

public sealed class SchemaTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public SchemaTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Test_TestDbContext_EnsureCreated_ProducesSnakeCaseSchema()
    {
        // Build options against the throwaway DB
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using var db = new TestDbContext(options);
        await db.Database.EnsureCreatedAsync();

        // Query information_schema.columns for the test_entities table
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT column_name FROM information_schema.columns
            WHERE table_name = 'test_entities' ORDER BY column_name";
        var columns = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));

        // Snake-case columns must be present (PERSIST-05 / SC#1)
        Assert.Contains("created_at", columns);
        Assert.Contains("updated_at", columns);
        Assert.Contains("created_by", columns);
        Assert.Contains("updated_by", columns);
        Assert.Contains("id", columns);
        // PascalCase columns must NOT be present
        Assert.DoesNotContain("CreatedAt", columns);
    }
}
```

**Mapping notes:**
- `IClassFixture<PostgresFixture>` is the xUnit v3 per-class-fixture pattern (RESEARCH.md Executive Summary bullet 5 — each class gets its own throwaway DB).
- The audit interceptor is NOT wired here because SC#1 only checks the schema shape (column names), not audit-stamping behavior.
- Same `using Xunit;` directive shape as `MetaTest.cs`; same `public sealed class` pattern.

---

### `tests/BaseApi.Tests/Persistence/AuditInterceptorTests.cs` (test class, ADAPTED — SC#2 + SC#3)

**Status:** NEW. **Pattern sources:**
1. **Skeleton from `MetaTest.cs`** (as above).
2. **Substance from RESEARCH.md § "FakeTimeProvider usage example" (lines 653-682) + § Validation Dim 3 + 4 (lines 801-802).**

**Verbatim FakeTimeProvider example (RESEARCH.md lines 655-682):**

```csharp
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

**Wrapping this in the MetaTest-style class scaffold:**

```csharp
// tests/BaseApi.Tests/Persistence/AuditInterceptorTests.cs
using BaseApi.Core.Entities;
using BaseApi.Core.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BaseApi.Tests.Persistence;

public sealed class AuditInterceptorTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public AuditInterceptorTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Test_AuditInterceptor_StampsUtcTimestamps_OnInsert()
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(new DateTimeOffset(2026, 1, 15, 12, 30, 0, TimeSpan.Zero));

        var stub = new StubHttpContextAccessor();
        stub.SetUser("alice");

        var interceptor = new AuditInterceptor(stub, clock);

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
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
    }

    [Fact]
    public async Task Test_AuditInterceptor_StampsCreatedBy_FromHttpContext_NullFallback()
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(new DateTimeOffset(2026, 1, 15, 12, 30, 0, TimeSpan.Zero));

        var stub = new StubHttpContextAccessor(); // HttpContext == null
        var interceptor = new AuditInterceptor(stub, clock);

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(interceptor)
            .Options;

        await using var db = new TestDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var entity = new TestEntity();
        await db.TestEntities.AddAsync(entity);
        await db.SaveChangesAsync();  // must NOT throw

        Assert.Null(entity.CreatedBy);
    }
}
```

**Mapping notes:**
- TWO `[Fact]` methods in one class — SC#3 in CONTEXT.md D-14 explicitly says "TWO assertions in one test (or two facts)". The two-facts shape is cleaner.
- `using Microsoft.Extensions.Time.Testing;` — namespace DIFFERS from package name (`Microsoft.Extensions.TimeProvider.Testing`) per RESEARCH.md line 656 + line 684.
- The Pitfall 1 `Kind == Utc` assertion is in the first fact; the null-fallback is in the second.

---

### `tests/BaseApi.Tests/Persistence/DiLifetimeTests.cs` (test class, ADAPTED — SC#4)

**Status:** NEW. **Pattern sources:**
1. **Skeleton from `MetaTest.cs`.**
2. **Substance from RESEARCH.md § Validation Dim 5 (line 803) + CONTEXT.md D-14 SC#4 (lines 91).**

**Synthesis:**

```csharp
// tests/BaseApi.Tests/Persistence/DiLifetimeTests.cs
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BaseApi.Tests.Persistence;

public sealed class DiLifetimeTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public DiLifetimeTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public void Test_DbContext_IsRegisteredScoped_InDI()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IHttpContextAccessor, StubHttpContextAccessor>();
        services.AddDbContext<TestDbContext>(opts => opts.UseNpgsql(_fixture.ConnectionString));

        var provider = services.BuildServiceProvider();

        using var scope1 = provider.CreateScope();
        var a = scope1.ServiceProvider.GetRequiredService<TestDbContext>();
        var b = scope1.ServiceProvider.GetRequiredService<TestDbContext>();
        Assert.Same(a, b); // same instance within a scope (Scoped lifetime)

        using var scope2 = provider.CreateScope();
        var c = scope2.ServiceProvider.GetRequiredService<TestDbContext>();
        Assert.NotSame(a, c); // different instance across scopes
    }
}
```

**Mapping notes:**
- `AddDbContext<TestDbContext>` defaults to Scoped lifetime — Pitfall 2 anchor (CONTEXT.md PERSIST-15 / D-11 / SC#4).
- The test does NOT call `EnsureCreatedAsync` because we're testing the DI lifetime, not the schema — but the connection string still needs to be valid (`PostgresFixture` provides it).
- `Assert.Same` / `Assert.NotSame` are xUnit v3's reference-equality assertions.

---

### `tests/BaseApi.Tests/Persistence/XminConcurrencyTokenTests.cs` (test class, ADAPTED — Dim 6)

**Status:** NEW. **Pattern sources:**
1. **Skeleton from `MetaTest.cs`.**
2. **Substance from RESEARCH.md § Validation Dim 6 (line 804) + § Open Question 2 recommendation (lines 941-944).**

**Synthesis (a pure model-introspection test — no DB I/O needed):**

```csharp
// tests/BaseApi.Tests/Persistence/XminConcurrencyTokenTests.cs
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BaseApi.Tests.Persistence;

public sealed class XminConcurrencyTokenTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public XminConcurrencyTokenTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public void Test_BaseEntity_HasXminShadowProperty()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        using var db = new TestDbContext(options);

        var entityType = db.Model.FindEntityType(typeof(TestEntity));
        Assert.NotNull(entityType);

        var xmin = entityType!.FindProperty("xmin");
        Assert.NotNull(xmin);
        Assert.True(xmin!.IsConcurrencyToken);
        Assert.Equal("xid", xmin.GetColumnType());
    }
}
```

**Mapping notes:**
- Pure model-metadata test (no DB writes; no `EnsureCreatedAsync`). The model is built when the DbContext is instantiated; `db.Model.FindEntityType(...)` reads the in-memory model.
- Covers PERSIST-16 (D-03b) regression: if someone removes the `OnModelCreating` iteration in `BaseDbContext`, this test fails immediately.
- Phase 4 separately tests the runtime 409 path (two racing transactions) — out of Phase 3 scope.

---

### `tests/BaseApi.Tests/Persistence/RepositorySurfaceTests.cs` (test class, ADAPTED — Dim 7)

**Status:** NEW. **Pattern sources:**
1. **Skeleton from `MetaTest.cs`.**
2. **Substance from RESEARCH.md § Validation Dim 7 (line 805) + D-04 lock.**

**Synthesis (reflection over the interface — no DB needed):**

```csharp
// tests/BaseApi.Tests/Persistence/RepositorySurfaceTests.cs
using System.Linq;
using System.Reflection;
using BaseApi.Core.Persistence.Repositories;
using Xunit;

namespace BaseApi.Tests.Persistence;

public sealed class RepositorySurfaceTests
{
    [Fact]
    public void Test_IRepository_ExposesExactlyFiveMethods()
    {
        var methods = typeof(IRepository<>)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .ToList();

        Assert.Equal(5, methods.Count);

        var names = methods.Select(m => m.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "AddAsync", "DeleteAsync", "GetAsync", "ListAsync", "Update" }, names);

        // No method returns IQueryable<>
        foreach (var m in methods)
        {
            Assert.False(
                m.ReturnType.IsGenericType && m.ReturnType.GetGenericTypeDefinition() == typeof(IQueryable<>),
                $"{m.Name} must not return IQueryable<> (D-04 surface lock)");
        }
    }
}
```

**Mapping notes:**
- No `IClassFixture<PostgresFixture>` — this is pure reflection; no DB needed.
- `BindingFlags.DeclaredOnly` ensures we don't count inherited methods from any future base interface.
- The `OrderBy` on names is needed because `GetMethods()` ordering is not guaranteed (.NET runtime behavior).
- Catches a regression if anyone adds `ExistsAsync`, `Where`, or an `IQueryable<>` return to `IRepository<T>`.

---

### `.planning/phases/03-ef-core-persistence-base/03-01-PLAN.md` (build plan, IDENTICAL shape)

**Status:** NEW. **Analog: `.planning/phases/02-postgres-docker-compose/02-01-PLAN.md` (Phase 2 build plan — `autonomous: true`, `feat(02)` evidence commits, full `<tasks>` with `<read_first>`/`<action>`/`<verify>`/`<acceptance_criteria>`/`<done>` shape).**

**Frontmatter shape to copy (verbatim from `02-01-PLAN.md` lines 1-63 — planner adapts values):**

```yaml
---
phase: 03-ef-core-persistence-base
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - Directory.Packages.props
  - src/BaseApi.Core/BaseApi.Core.csproj
  - src/BaseApi.Core/Entities/BaseEntity.cs
  - src/BaseApi.Core/Persistence/BaseDbContext.cs
  - src/BaseApi.Core/Persistence/Interceptors/AuditInterceptor.cs
  - src/BaseApi.Core/Persistence/Repositories/IRepository.cs
  - src/BaseApi.Core/Persistence/Repositories/Repository.cs
  - tests/BaseApi.Tests/BaseApi.Tests.csproj
autonomous: true
requirements:
  - ENTITY-01
  - ENTITY-02
  - PERSIST-02
  - PERSIST-03
  - PERSIST-04
  - PERSIST-05
  - PERSIST-06
  - PERSIST-07
  - PERSIST-11
  - PERSIST-15
  - PERSIST-16   # D-03b new requirement
must_haves:
  truths:
    - "Directory.Packages.props pins Microsoft.Extensions.TimeProvider.Testing at 8.10.0 alongside the existing 22 pins"
    - "src/BaseApi.Core/BaseApi.Core.csproj declares <FrameworkReference Include='Microsoft.AspNetCore.App' /> + 4 EF Core <PackageReference> entries (no Version=)"
    - "BaseEntity.cs is abstract, has 8 properties per ENTITY-01, and includes Pitfall-1 XML doc references on CreatedAt/UpdatedAt"
    - "BaseDbContext.cs is abstract, has NO DbSets, OnConfiguring calls UseSnakeCaseNamingConvention(), OnModelCreating runs the D-03 xmin iteration"
    - "AuditInterceptor.cs derives SaveChangesInterceptor (base class), overrides only SavingChangesAsync, depends on IHttpContextAccessor + TimeProvider"
    - "IRepository<TEntity> exposes exactly 5 methods (GetAsync, ListAsync, AddAsync, Update, DeleteAsync); generic constraint where TEntity : BaseEntity"
    - "Repository<TEntity> is sealed, takes BaseDbContext in ctor, implements DeleteAsync as load-then-remove (preserves xmin check)"
    - "tests/BaseApi.Tests/BaseApi.Tests.csproj adds 5 PackageReferences + FrameworkReference for ASP.NET Core surface"
    - "dotnet restore --force --no-cache exits 0; dotnet build Release exits 0 with 0 warnings, 0 errors"
  artifacts:
    - path: "src/BaseApi.Core/Entities/BaseEntity.cs"
      provides: "Abstract base for all audit-stamped domain entities (ENTITY-01, ENTITY-02)"
      contains: "public abstract class BaseEntity"
    - path: "src/BaseApi.Core/Persistence/BaseDbContext.cs"
      provides: "Abstract DbContext base with snake_case + xmin wiring (PERSIST-02, PERSIST-05, PERSIST-16)"
      contains: "Property<uint>(\"xmin\")"
    - path: "src/BaseApi.Core/Persistence/Interceptors/AuditInterceptor.cs"
      provides: "SaveChangesInterceptor that stamps Id/CreatedAt/UpdatedAt/CreatedBy/UpdatedBy (PERSIST-03, PERSIST-04, PERSIST-07)"
      contains: "_clock.GetUtcNow().UtcDateTime"
    - path: "src/BaseApi.Core/Persistence/Repositories/IRepository.cs"
      provides: "5-method generic repository interface (PERSIST-11)"
      contains: "Task<TEntity?> GetAsync"
    - path: "src/BaseApi.Core/Persistence/Repositories/Repository.cs"
      provides: "Sealed generic repository concrete (PERSIST-11)"
      contains: "public sealed class Repository<TEntity>"
  key_links:
    - from: "AuditInterceptor"
      to: "IHttpContextAccessor (FrameworkReference Microsoft.AspNetCore.App)"
      via: "constructor injection"
      pattern: "IHttpContextAccessor httpContextAccessor"
    - from: "AuditInterceptor"
      to: "TimeProvider (.NET 8 BCL)"
      via: "constructor injection"
      pattern: "TimeProvider clock"
    - from: "BaseDbContext.OnModelCreating"
      to: "Pitfall 6 verbatim xmin shape"
      via: "shadow property with HasColumnType('xid')"
      pattern: "IsConcurrencyToken"
---
```

**Body sections to copy from `02-01-PLAN.md` (sections 65-end):**
- `<objective>` paragraph
- `<execution_context>` with `@$HOME/.claude/get-shit-done/workflows/execute-plan.md` + `@$HOME/.claude/get-shit-done/templates/summary.md`
- `<context>` with `@` links to PROJECT.md, ROADMAP.md, REQUIREMENTS.md, STATE.md, 03-CONTEXT.md, 03-RESEARCH.md, 03-PATTERNS.md (this file), and prior phase SUMMARYs
- `<interfaces>` comment block listing each Phase 3 file and the D-XX decision driving it (mirrors Phase 2 lines 91-125 shape)
- `<threat_model>` table — minimal for Phase 3 (no HTTP surface; threats deferred per Phase 1/2 pattern)
- `<tasks>` blocks — one `<task type="auto">` per logical unit:
  - Task 1: Pin `Microsoft.Extensions.TimeProvider.Testing 8.10.0` in `Directory.Packages.props`
  - Task 2: Add `<FrameworkReference>` + 4 `<PackageReference>` entries to `BaseApi.Core.csproj`
  - Task 3: Add 5 `<PackageReference>` + `<FrameworkReference>` to `BaseApi.Tests.csproj`
  - Task 4: Write `BaseEntity.cs`
  - Task 5: Write `BaseDbContext.cs`
  - Task 6: Write `AuditInterceptor.cs`
  - Task 7: Write `IRepository.cs` + `Repository.cs`
  - Task 8: `dotnet restore --force --no-cache` + `dotnet build Release` + `dotnet build Debug` (unconditional aggregate per W-02 — copy from `01-03-PLAN.md` Task 2 lines 274-295)
- `<verification>`, `<success_criteria>`, `<output>` matching Phase 2 build plan

**Each task's `<action>` block follows the Phase 2 shape:**
- Concrete PowerShell or file-write instructions
- Verbatim code blocks (lifted from PATTERNS.md sections above)
- Diagnosis paths for common failures
- Output capture for the SUMMARY

**Each task's `<verify>` block uses `<automated>powershell -NoProfile -Command "..."</automated>` per the Phase 1/2 convention.**

**Each task's `<acceptance_criteria>` lists per-file `Test-Path`, content `-match` regex, build-output assertions.**

---

### `.planning/phases/03-ef-core-persistence-base/03-02-PLAN.md` (verification plan, IDENTICAL shape)

**Status:** NEW. **Analog: `.planning/phases/01-repository-scaffold/01-03-PLAN.md` AND `.planning/phases/02-postgres-docker-compose/02-02-PLAN.md` (verification-plan pattern — exact match).**

**Frontmatter LOAD-BEARING fields (copy verbatim from `02-02-PLAN.md` lines 1-45):**

```yaml
---
phase: 03-ef-core-persistence-base
plan: 02
type: execute
wave: 2
depends_on:
  - "03-01"
files_modified: []          # ← LOAD-BEARING — verification plan writes ZERO source files
autonomous: false           # ← LOAD-BEARING — human checkpoint required (matches Phase 1/2)
requirements:
  - ENTITY-01
  - ENTITY-02
  - PERSIST-02
  - PERSIST-03
  - PERSIST-04
  - PERSIST-05
  - PERSIST-06
  - PERSIST-07
  - PERSIST-11
  - PERSIST-15
  - PERSIST-16
must_haves:
  truths:
    - "Phase 3 SC#1 verified: TestDbContext.EnsureCreatedAsync produces a snake_case schema (information_schema.columns query returns created_at/updated_at/created_by/updated_by)"
    - "Phase 3 SC#2 verified: AuditInterceptor stamps CreatedAt with Kind=Utc on INSERT; pg_typeof(created_at) returns 'timestamp with time zone'"
    - "Phase 3 SC#3 verified: AuditInterceptor reads HttpContext?.User?.Identity?.Name when set; null when no HttpContext (no exception)"
    - "Phase 3 SC#4 verified: AddDbContext registers Scoped lifetime (Assert.Same within scope, Assert.NotSame across scopes)"
    - "Dim 6 verified (PERSIST-16): xmin shadow property is IsConcurrencyToken=true with column type 'xid' on the TestEntity model"
    - "Dim 7 verified (PERSIST-11): IRepository<> exposes exactly 5 methods, no IQueryable<> return"
    - "Postgres throwaway DBs are created and dropped cleanly via PostgresFixture; no leaked stepsdb_test_* databases after dotnet test (D-15 cleanup)"
  artifacts:
    - path: ".planning/phases/03-ef-core-persistence-base/03-02-SUMMARY.md"
      provides: "Verification log with verbatim command output and GREEN/RED on Phase 3 SC#1-4 + Dim 6 + Dim 7; cleanup status documented"
      contains: "GREEN"
  key_links:
    - from: "dotnet test"
      to: "tests/BaseApi.Tests/Persistence/*.cs (Plan 03-01 — 9 test files)"
      via: "xUnit v3 MTP runner + Microsoft.Testing.Platform"
      pattern: "Passed"
    - from: "PostgresFixture.InitializeAsync"
      to: "docker compose postgres container at localhost:5433 (Phase 2)"
      via: "Npgsql admin connection to postgres DB"
      pattern: "CREATE DATABASE"
---
```

**Body shape (copy from `01-03-PLAN.md` + `02-02-PLAN.md`):**
- `<objective>` paragraph — "Run the full Phase 3 acceptance battery (`dotnet test` + manual throwaway-DB cleanup verification) against the persistence base Plan 03-01 landed..."
- `<execution_context>` — same `@$HOME/...` references
- `<context>` — `@`-links to PROJECT.md, ROADMAP.md, REQUIREMENTS.md, 03-CONTEXT.md, 03-RESEARCH.md, 03-PATTERNS.md, 03-01-PLAN.md, prior phase SUMMARYs, PITFALLS.md (specifically Pitfalls 1, 2, 4, 6)
- `<interfaces>` comment block listing the 9 test files this plan exercises (writes ZERO source files) — same as `02-02-PLAN.md` lines 76-98
- `<threat_model>` — minimal; verification commands only
- `<tasks>` — one `<task type="auto">` per SC + cleanup:
  - Task 1: Pre-flight — `docker compose ps postgres` must report `healthy` (Phase 2 prereq)
  - Task 2: `dotnet test` against the 4 SC facts (+ Dim 6 + Dim 7) — assert exit code 0 + "Passed: 6" (or "Passed: 7" depending on AuditInterceptor 2-fact split)
  - Task 3: `psql -l` (or `docker compose exec postgres psql -U postgres -c "\l"`) before + after — assert no leaked `stepsdb_test_*` databases (D-15)
  - Task 4 checkpoint: Human verification + write `03-02-SUMMARY.md`
- `<verification>` + `<success_criteria>` + `<output>` matching the Phase 1/2 pattern

**Evidence commits in Plan 03-02:** `docs(03-02): <description>` per Phase 1/2 D-18 convention. Fix-forward against Plan 03-01 lands as `fix(03-01): <description>`.

---

### `.planning/phases/03-ef-core-persistence-base/03-SUMMARY.md` (phase-level rollup, ADAPTED)

**Status:** NEW. **Closest analog: `.planning/phases/01-repository-scaffold/01-03-SUMMARY.md` (frontmatter + body shape — the final Plan 03 SUMMARY in Phase 1, which served as the de-facto phase rollup since no top-level 01-SUMMARY exists).**

**Important note:** There is NO `01-SUMMARY.md` or `02-SUMMARY.md` in this repo — only per-plan SUMMARYs (`01-01-SUMMARY.md` ... `01-03-SUMMARY.md`, `02-01-SUMMARY.md`, `02-02-SUMMARY.md`). The pattern-mapping request specifies a `03-SUMMARY.md` (phase rollup); the planner should DECIDE whether to:

1. **Skip `03-SUMMARY.md` entirely** — match prior-phase convention (no rollup; the last `03-02-SUMMARY.md` is the de-facto phase close), OR
2. **Introduce `03-SUMMARY.md`** — establish a new convention where each phase has both per-plan SUMMARYs AND a top-level phase rollup.

**Recommendation: Option 1 (skip).** Consistency with Phase 1/2 is stronger than introducing a new convention. The `03-02-SUMMARY.md` (the verification-plan SUMMARY) already documents Phase 3 closure with GREEN/RED on all SCs + requirements closed. A separate `03-SUMMARY.md` would duplicate that.

**If the planner chooses Option 2 (introduce a phase rollup), the closest shape to mirror is `01-03-SUMMARY.md` frontmatter (lines 1-53):**

```yaml
---
phase: 03-ef-core-persistence-base
plan: <rollup or 02>
subsystem: persistence
tags: [ef-core-8, npgsql-8, snake_case-convention, audit-interceptor, xmin-concurrency, generic-repository, phase-3-acceptance]

# Dependency graph
requires:
  - "03-01: <files list>"
  - "03-02: <verification log>"
provides:
  - "Phase 3 SC#1 GREEN: snake_case schema via EnsureCreatedAsync"
  - "Phase 3 SC#2 GREEN: AuditInterceptor stamps Kind=Utc timestamps"
  - "Phase 3 SC#3 GREEN: CreatedBy from HttpContext + null fallback"
  - "Phase 3 SC#4 GREEN: DbContext registered Scoped"
  - "11 requirements closed: ENTITY-01, ENTITY-02, PERSIST-02..07, PERSIST-11, PERSIST-15, PERSIST-16 (D-03b new)"
affects: [04-middleware, 05-observability, 07-http-base, 08-entities]

tech-stack:
  added: [ef-core-8.0.27, npgsql-ef-8.0.10, efcore-namingconventions-8.0.3, microsoft-extensions-timeprovider-testing-8.10.0]
  patterns:
    - "Audit-interception via SaveChangesInterceptor base class + TimeProvider injection"
    - "xmin shadow concurrency token iteration in OnModelCreating (PERSIST-16)"
    - "Throwaway-DB-per-test-class fixture via IAsyncLifetime + Guid-suffix naming"
    - "Handwritten test doubles (no Moq/NSubstitute per REQUIREMENTS.md Out of Scope)"
    - "FrameworkReference Microsoft.AspNetCore.App for ASP.NET-coupled class libraries (D-12)"

key-files:
  created: [9 production files + 9 test files + 2 plans + 1 SUMMARY]
  modified: [Directory.Packages.props, BaseApi.Core.csproj, BaseApi.Tests.csproj]

requirements-completed: [ENTITY-01, ENTITY-02, PERSIST-02, PERSIST-03, PERSIST-04, PERSIST-05, PERSIST-06, PERSIST-07, PERSIST-11, PERSIST-15, PERSIST-16]

duration: <wall-clock>
completed: <date>
---

# Phase 03: EF Core Persistence Base — Final Summary

[opening paragraph: TL;DR + GREEN/RED status]

## Phase 3 Success Criteria — Verification (verbatim evidence)

### SC#1: ...
[same structure as 01-03-SUMMARY.md lines 78-228]

## Phase requirements closed
[table per 01-03-SUMMARY.md lines 230-237]

## Files Created
[split by plan — 9 production + 9 test + 2 plans + 1 SUMMARY]

## Decisions Made / Deviations from Plan / Auto-fixed Issues
[per 01-03-SUMMARY.md lines 245-303 pattern: numbered deviation blocks]

## Next: Phase 4 — Cross-Cutting Middleware + Error Handling
```

---

## Shared Patterns

### File-scoped namespace + outside-namespace using (cross-cutting, applies to all 9 C# production+test files)

**Source:** `.editorconfig` lines 467 + 510 — `csharp_style_namespace_declarations = file_scoped:warning` AND `csharp_using_directive_placement = outside_namespace:warning`.

**Composition with `<TreatWarningsAsErrors>true</>` in `Directory.Build.props`:** these `:warning` severity rules become BUILD-FATAL. Every C# file in Phase 3 MUST use file-scoped namespaces AND keep `using` directives above the namespace declaration.

**Verbatim correct form (mirrors `tests/BaseApi.Tests/MetaTest.cs` lines 1-3):**

```csharp
using Xunit;

namespace BaseApi.Tests;

public sealed class MetaTest { ... }
```

**Apply to:** Every new `.cs` file in Phase 3 (BaseEntity.cs, BaseDbContext.cs, AuditInterceptor.cs, IRepository.cs, Repository.cs, TestEntity.cs, TestDbContext.cs, PostgresFixture.cs, StubHttpContextAccessor.cs, all 5 test classes).

---

### `public sealed class` for test classes (cross-cutting, applies to all 5 test classes + 4 test scaffold)

**Source:** `tests/BaseApi.Tests/MetaTest.cs` line 19 — `public sealed class MetaTest`.

**Rationale:** xUnit v3 instantiates test classes via reflection; `sealed` is a no-cost annotation that signals "no test subclassing" + satisfies analyzer rule `CA1052` / `CA1822`. The existing Phase 1 test class uses `sealed`; Phase 3 mirrors.

**Apply to:** `SchemaTests`, `AuditInterceptorTests`, `DiLifetimeTests`, `XminConcurrencyTokenTests`, `RepositorySurfaceTests`, plus `TestEntity`, `TestDbContext`, `PostgresFixture`, `StubHttpContextAccessor`.

---

### Zero `Version=` attributes on PackageReference (CPM contract)

**Source:** `Directory.Packages.props` lines 18-19 documentation block + `BaseApi.Core.csproj` lines 9-12 + `BaseApi.Tests.csproj` lines 56-62.

**Apply to:** Every new `<PackageReference>` in Phase 3 csproj edits. Versions resolve from `Directory.Packages.props` central pin table; `NU1010` warning would fire (build-fatal via TreatWarningsAsErrors) if `Version=` leaks in.

---

### `<ItemGroup>`-per-concern grouping (csproj convention)

**Source:** `tests/BaseApi.Tests/BaseApi.Tests.csproj` lines 55-66 — three separate `<ItemGroup>` blocks: one for xunit refs, one (NEW Phase 3 — currently absent in BaseApi.Core.csproj) for EF refs, one for ProjectReference.

**Apply to:** Phase 3 csproj edits group package refs by logical concern, not in a single `<ItemGroup>` blob. The existing csproj already demonstrates this convention with a header comment naming what's NOT yet there ("Phases 3-7 will add ..." — `BaseApi.Core.csproj` line 18-20).

---

### Section-comment-then-pin convention (`Directory.Packages.props`)

**Source:** `Directory.Packages.props` lines 47, 54, 57, 61, 65, 69, 73, 76, 80 — each logical pin group is introduced by an XML comment naming the family ("EF Core 8", "Validation", "Observability", "Tests", etc.).

**Apply to:** Phase 3's `Microsoft.Extensions.TimeProvider.Testing` addition should land under a NEW section comment "Microsoft.Extensions.* family — versions on own cadence (Phase 3: FakeTimeProvider)" matching the established convention.

---

### Verification-as-gate + fix-forward pattern (cross-cutting, planning convention)

**Source:** `.planning/phases/01-repository-scaffold/01-03-PLAN.md` lines 47-55 (objective) + `01-03-SUMMARY.md` lines 245-303 (deviations) + `02-02-SUMMARY.md` lines 36-40 (Plan 02-01 fix-forward).

**Apply to:** Phase 3 Plan 03-02. If verification surfaces a defect in Plan 03-01 (e.g., `xmin` shadow property not registered because of an EF Core convention edge case, or `FrameworkReference` not exposing `DefaultHttpContext` per RESEARCH.md A3), the fix lands as `fix(03-01): <description>` on the build plan, with deviation captured in 03-02-SUMMARY.md.

---

### Evidence-only commits in verification plan (`docs(03-02)`)

**Source:** CONTEXT.md D-18 explicit + `.planning/phases/01-repository-scaffold/01-03-PLAN.md` `autonomous: false` + `02-02-PLAN.md` D-14 convention.

**Apply to:** Plan 03-02 uses `docs(03-02): ...` for non-fix commits (SUMMARY writes). Source-touching commits in 03-02 are exclusively `fix(03-01): ...` fix-forwards.

---

### Deferred-marker convention (cross-cutting from Phase 1 → Phase 3 → future phases)

**Source:** `Directory.Packages.props` lines 76-78 (`Asp.Versioning.Http` + `Swashbuckle.AspNetCore` pinned in Phase 1, consumed in Phase 7) + `compose.yaml` lines 24-25 (Phase 8 INFRA-05 deferred-build marker) + Phase 2 PATTERNS.md "Deferred-marker convention" section.

**Apply to:** Phase 3 production code's XML-doc references to future phases — e.g., the `BaseEntity` XML doc references "Phase 8" (concrete subclasses), `BaseDbContext` XML doc references "Phase 7" (AddBaseApi), `IRepository` XML doc references "Phase 4" (NotFoundException → 422), and `AuditInterceptor` references "Pitfall 1" (UTC enforcement). The cross-phase reference convention is the established style.

---

## No Analog Found

The following Phase 3 deliverables have **NO in-repo source-code analog**. The planner uses RESEARCH.md skeletons as the authoritative pattern:

| File | Role | Data Flow | Why No Analog | RESEARCH.md Reference |
|------|------|-----------|---------------|-----------------------|
| `BaseEntity.cs` | abstract entity | static POCO | First entity in repo; `src/BaseApi.Core/Entities/` contains only `.gitkeep` from Phase 1 D-08 | RESEARCH.md ENTITY-01 row (line 71) + Pitfall 1 verbatim guidance |
| `BaseDbContext.cs` | abstract DbContext | event-driven (OnModelCreating) | First DbContext in repo; `src/BaseApi.Core/Persistence/` contains only `.gitkeep` | RESEARCH.md § "BaseDbContext — Simplified constructor" (lines 397-407) |
| `AuditInterceptor.cs` | EF Core interceptor | event-driven (SavingChangesAsync) | First interceptor in repo | RESEARCH.md § "AuditInterceptor — Complete code skeleton" (lines 161-246) |
| `IRepository.cs` | repository interface | CRUD | First repository in repo | RESEARCH.md § "Repository<T> — Final 5-Method Surface" (lines 416-443) |
| `Repository.cs` | repository concrete | CRUD | First repository concrete in repo | RESEARCH.md § "Repository<T>" code block (lines 446-481) |
| `PostgresFixture.cs` | xUnit IAsyncLifetime fixture | request-response (Npgsql admin CREATE/DROP) | First test fixture in repo | RESEARCH.md § "Throwaway database fixture" (lines 540-592) |
| `StubHttpContextAccessor.cs` | handwritten test double | static | First test double in repo (REQUIREMENTS.md Out of Scope excludes Moq/NSubstitute) | RESEARCH.md § "Handwritten IHttpContextAccessor stub" (lines 606-650) |

**Planner directive:** for all 7 files above, copy the RESEARCH.md skeleton verbatim with name/namespace adjustments only. The skeletons are HIGH-confidence per RESEARCH.md metadata (lines 1004-1011).

---

## Metadata

**Analog search scope:**
- `C:\Users\UserL\source\repos\SK_P\src\BaseApi.Core\` — confirmed `Entities/`, `Persistence/`, `Persistence/Interceptors/`, `Persistence/Repositories/`, `Controllers/`, `ErrorHandling/`, `Health/`, `Middleware/`, `Services/`, `Telemetry/`, `Validation/`, `DependencyInjection/` are all EMPTY (no `.cs` files, only `.gitkeep` placeholders from Phase 1 D-08)
- `C:\Users\UserL\source\repos\SK_P\src\BaseApi.Service\` — has `Program.cs` (Phase 1 D-10 scaffold), `appsettings.json`, `appsettings.Development.json`; no persistence code (Phase 8 lands AppDbContext here)
- `C:\Users\UserL\source\repos\SK_P\tests\BaseApi.Tests\` — has only `MetaTest.cs` (xUnit v3 sanity test) — the test class skeleton analog
- `C:\Users\UserL\source\repos\SK_P\.planning\phases\01-repository-scaffold\` — Plan 01-01/02/03 PLAN + SUMMARY for plan-shape analog
- `C:\Users\UserL\source\repos\SK_P\.planning\phases\02-postgres-docker-compose\` — Plan 02-01/02 PLAN + SUMMARY + PATTERNS for build/verification plan shape
- `C:\Users\UserL\source\repos\SK_P\Directory.Build.props`, `Directory.Packages.props`, root `*.csproj` — csproj/props edit analogs

**Files scanned:** 28 (5 root config files + 1 src/BaseApi.Core/BaseApi.Core.csproj + 1 src/BaseApi.Service/BaseApi.Service.csproj + 1 tests/BaseApi.Tests/BaseApi.Tests.csproj + 1 MetaTest.cs + 1 Program.cs + 2 appsettings*.json + 6 Phase 1 planning artifacts + 6 Phase 2 planning artifacts + 4 Phase 3 inputs)

**Pattern extraction date:** 2026-05-26

---

## PATTERN MAPPING COMPLETE
