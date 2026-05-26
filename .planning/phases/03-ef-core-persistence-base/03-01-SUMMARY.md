---
phase: 03-ef-core-persistence-base
plan: 01
subsystem: database
tags: [ef-core, npgsql, postgres, snake-case, xmin, audit-interceptor, repository-pattern, fake-time-provider, framework-reference, cpm]

# Dependency graph
requires:
  - phase: 01-repository-scaffold
    provides: Zero-warning build regime (Directory.Build.props TreatWarningsAsErrors=true + EnforceCodeStyleInBuild=true), CPM contract (Directory.Packages.props), file-scoped namespace + outside-namespace using convention, xunit.v3 + MTP scaffold, BaseApi.Core/BaseApi.Tests csproj baselines
  - phase: 02-postgres-docker-compose
    provides: postgres:17-alpine on host port 5433 (compose stack), stepsdb/postgres/postgres seed credentials, baseapi-service phase-8 profile placeholder
provides:
  - "PERSIST-16 requirement: BaseEntity rows carry Postgres xmin shadow concurrency token (D-03b cross-phase impact appended to REQUIREMENTS.md)"
  - "Microsoft.Extensions.TimeProvider.Testing 8.10.0 pinned in Directory.Packages.props (FakeTimeProvider for audit-interceptor tests)"
  - "BaseApi.Core.csproj wires EF Core 8 + Npgsql + EFCore.NamingConventions + FrameworkReference Microsoft.AspNetCore.App (D-12)"
  - "BaseApi.Tests.csproj wires the same 4 EF Core packages + FakeTimeProvider + FrameworkReference (D-13)"
  - "BaseEntity abstract base class with 8 ENTITY-01 properties + Pitfall 1 UTC-only docs"
  - "BaseDbContext abstract base with UseSnakeCaseNamingConvention() in OnConfiguring + xmin shadow-property iteration in OnModelCreating (Pitfall 4 + Pitfall 6 verbatim)"
  - "AuditInterceptor sealed, derives SaveChangesInterceptor base, TimeProvider-driven UTC stamping, null-safe HttpContext user chain, D-08 Added/Modified branching"
  - "IRepository<TEntity : BaseEntity> 5-method surface (D-04 — no IQueryable, no ExistsAsync, no Where predicate)"
  - "Repository<TEntity> sealed concrete, BaseDbContext-typed ctor, load-then-remove DeleteAsync (preserves xmin per D-03)"
affects: [03-02 verification plan, Phase 4 error-mapping (DbUpdateConcurrencyException -> HTTP 409), Phase 7 composition root (AddBaseApi<TDbContext> wires AuditInterceptor + AddDbContext Scoped), Phase 8 AppDbContext + InitialCreate migration + 5 concrete entities + 3 junction entities]

# Tech tracking
tech-stack:
  added:
    - "Microsoft.EntityFrameworkCore 8.0.27"
    - "Microsoft.EntityFrameworkCore.Relational 8.0.27"
    - "Npgsql.EntityFrameworkCore.PostgreSQL 8.0.10"
    - "EFCore.NamingConventions 8.0.3"
    - "Microsoft.Extensions.TimeProvider.Testing 8.10.0 (FakeTimeProvider — new Directory.Packages.props pin, +1 over Phase 1's 22)"
    - "FrameworkReference Microsoft.AspNetCore.App (Core + Tests projects)"
  patterns:
    - "Abstract base class + verbatim Pitfall 6 xmin shadow-property iteration (model-builder)"
    - "Sealed concrete generic Repository + BaseDbContext-typed constructor (type-system enforcement)"
    - "Audit interception via SaveChangesInterceptor base (override async-only path) + TimeProvider abstraction for test pinning"
    - "Snake_case naming wired in BaseDbContext.OnConfiguring (defense-in-depth — composition root + base both call UseSnakeCaseNamingConvention)"
    - "5-method IRepository surface lock (no IQueryable leakage; D-04)"
    - "Load-then-remove DeleteAsync pattern (preserves tracked xmin for concurrency check; D-03)"

key-files:
  created:
    - "src/BaseApi.Core/Entities/BaseEntity.cs - Abstract base, 8 ENTITY-01 properties, Pitfall 1 UTC <remarks> on CreatedAt/UpdatedAt"
    - "src/BaseApi.Core/Persistence/BaseDbContext.cs - Abstract DbContext base, no DbSets, snake_case + xmin model-builder iteration"
    - "src/BaseApi.Core/Persistence/Interceptors/AuditInterceptor.cs - Sealed SaveChangesInterceptor, TimeProvider clock, null-safe HttpContext user"
    - "src/BaseApi.Core/Persistence/Repositories/IRepository.cs - 5-method interface, BaseEntity-constrained"
    - "src/BaseApi.Core/Persistence/Repositories/Repository.cs - Sealed generic concrete, BaseDbContext-typed ctor, load-then-remove DeleteAsync"
  modified:
    - ".planning/REQUIREMENTS.md - Appended PERSIST-16 bullet under § Persistence + Traceability row (D-03b)"
    - "Directory.Packages.props - Added Microsoft.Extensions.TimeProvider.Testing 8.10.0 pin (+1 = 23 total)"
    - "src/BaseApi.Core/BaseApi.Core.csproj - Added 4 EF Core PackageReferences + FrameworkReference Microsoft.AspNetCore.App"
    - "tests/BaseApi.Tests/BaseApi.Tests.csproj - Added 5 PackageReferences (4 EF + FakeTimeProvider) + FrameworkReference Microsoft.AspNetCore.App"

key-decisions:
  - "D-03b implemented: PERSIST-16 appended to REQUIREMENTS.md as Task 0 (doc-first traceability landed BEFORE implementation tasks)"
  - "D-04 (5-method surface): IRepository has exactly 5 methods (GetAsync, ListAsync, AddAsync, Update, DeleteAsync) — no IQueryable, no ExistsAsync, no Where overload"
  - "D-07 (TimeProvider injection): AuditInterceptor accepts TimeProvider (.NET 8 BCL abstraction), not DateTime.UtcNow direct — enables FakeTimeProvider in tests"
  - "D-09 (no DbSets on base): BaseDbContext is abstract with NO DbSet<> properties — concrete AppDbContext (Phase 8) and TestDbContext (Plan 03-02) own DbSets"
  - "D-10 (interceptor NOT in OnConfiguring): AuditInterceptor wired at composition root (Phase 7) via DbContextOptionsBuilder.AddInterceptors(...) — keeps BaseDbContext simple-constructor"
  - "D-12 (FrameworkReference over PackageReference): Used <FrameworkReference Include='Microsoft.AspNetCore.App' /> in BaseApi.Core.csproj for IHttpContextAccessor surface — zero runtime weight, documented MS Learn pattern"

patterns-established:
  - "Verify scripts pattern: complex PowerShell verification logic stored as ephemeral .ps1 files at repo root, invoked via `powershell -NoProfile -ExecutionPolicy Bypass -File <name>.ps1`, deleted after use — avoids Bash $-sigil eating in inline -Command form"
  - "csproj inserts as two-ItemGroup-per-concern blocks (FrameworkReference and PackageReference each get their own ItemGroup) — mirrors xunit MTP scaffold convention from Phase 1"
  - "All audit-stamped DateTime values pass through TimeProvider.GetUtcNow().UtcDateTime — explicit UTC by construction (Pitfall 1 anchor)"
  - "Generic repository constrained to where T : BaseEntity — junction entities (non-BaseEntity) use raw DbContext.Set<TJunction>() directly from Service layer (Phase 8 pattern)"

requirements-completed:
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

# Metrics
duration: 8min
completed: 2026-05-26
---

# Phase 3 Plan 01: EF Core Persistence Base Summary

**Five-file EF Core persistence base library (BaseEntity, BaseDbContext, AuditInterceptor, IRepository, Repository) wired with snake_case + Pitfall 6 xmin shadow concurrency tokens + TimeProvider-driven audit stamping; both Release and Debug builds green at zero warnings.**

## Performance

- **Duration:** 8 min 10 sec
- **Started:** 2026-05-26T21:16:19Z
- **Completed:** 2026-05-26T21:24:29Z
- **Tasks:** 9 (Task 0 through Task 8; Task 8 is a verification gate with no commit)
- **Files modified:** 9 (1 doc + 1 props + 2 csproj + 5 new C# files)
- **Commits:** 8 (per-task atomic commits; Task 8 produces no commit since it's a build-gate)

## Accomplishments

- **PERSIST-16 traceability landed first:** PERSIST-16 ("`BaseEntity` rows carry Postgres `xmin` shadow concurrency token mapped via `IsConcurrencyToken()`") appended to `.planning/REQUIREMENTS.md` under § Persistence + Traceability table (D-03b cross-phase impact). Doc-first so the requirement-to-code link is established before the implementation.
- **NuGet pin landed cleanly:** `Microsoft.Extensions.TimeProvider.Testing 8.10.0` pinned in `Directory.Packages.props` as the only new pin (23 pins total, +1 over Phase 1's 22). All existing 22 pins untouched. CPM property preserved.
- **EF Core 8 surface wired in both projects:** `BaseApi.Core.csproj` and `BaseApi.Tests.csproj` both get the 4 EF Core packages (Microsoft.EntityFrameworkCore, Microsoft.EntityFrameworkCore.Relational, Npgsql.EntityFrameworkCore.PostgreSQL, EFCore.NamingConventions) plus `<FrameworkReference Include="Microsoft.AspNetCore.App" />`. Tests project also gets `Microsoft.Extensions.TimeProvider.Testing`. Zero `Version=` attributes — full CPM contract intact.
- **Five production C# files landed verbatim per 03-RESEARCH.md / 03-PATTERNS.md skeletons:** `BaseEntity.cs` (abstract; 8 properties; Pitfall 1 XML docs), `BaseDbContext.cs` (abstract; no DbSets; UseSnakeCaseNamingConvention + xmin shadow-property iteration), `AuditInterceptor.cs` (sealed; SaveChangesInterceptor base; TimeProvider clock; null-safe HttpContext), `IRepository.cs` (5-method interface), `Repository.cs` (sealed; BaseDbContext-typed ctor; load-then-remove DeleteAsync).
- **Build aggregate W-02 green:** `dotnet restore --force --no-cache` exit 0; `dotnet build --configuration Release --no-restore` exit 0 with `0 Warning(s)` `0 Error(s)`; `dotnet build --configuration Debug --no-restore` (run UNCONDITIONALLY per W-02) exit 0 with `0 Warning(s)` `0 Error(s)`. CPM resolution verified for all 4 packages at exact pinned versions on BaseApi.Tests (EF Core 8.0.27, Npgsql 8.0.10, EFCore.NamingConventions 8.0.3, Microsoft.Extensions.TimeProvider.Testing 8.10.0).

## Task Commits

Each task was committed atomically (Task 8 is a verification gate — no commit):

1. **Task 0: Append PERSIST-16 to REQUIREMENTS.md (D-03b)** — `28838cc` (docs)
2. **Task 1: Pin Microsoft.Extensions.TimeProvider.Testing 8.10.0** — `c15787d` (chore)
3. **Task 2: Add EF Core + FrameworkReference to BaseApi.Core.csproj (D-12)** — `426a25d` (build)
4. **Task 3: Add EF Core + FakeTimeProvider + FrameworkReference to BaseApi.Tests.csproj (D-13)** — `1a47f7f` (build)
5. **Task 4: Create BaseEntity.cs (ENTITY-01, ENTITY-02)** — `eea41bf` (feat)
6. **Task 5: Create BaseDbContext.cs (PERSIST-02, PERSIST-05, PERSIST-16)** — `abbd666` (feat)
7. **Task 6: Create AuditInterceptor.cs (PERSIST-03, PERSIST-04, PERSIST-07)** — `bd65944` (feat)
8. **Task 7: Create IRepository.cs + Repository.cs (PERSIST-11)** — `41b3927` (feat)
9. **Task 8: Restore + Release + Debug build (verification gate, no commit)** — verified green; outputs captured below

**Plan metadata commit:** appended after this SUMMARY lands.

## Build Verification (Task 8 verbatim output)

### `dotnet restore --force --no-cache`

```
Determining projects to restore...
Restored C:\Users\UserL\source\repos\SK_P\tests\BaseApi.Tests\BaseApi.Tests.csproj (in 7.88 sec).
Restored C:\Users\UserL\source\repos\SK_P\src\BaseApi.Service\BaseApi.Service.csproj (in 7.88 sec).
Restored C:\Users\UserL\source\repos\SK_P\src\BaseApi.Core\BaseApi.Core.csproj (in 7.88 sec).
Restore exit: 0
```

### `dotnet list tests/BaseApi.Tests/BaseApi.Tests.csproj package` (CPM resolution evidence)

```
Project 'BaseApi.Tests' has the following package references
   [net8.0]:
   Top-level Package                                Requested   Resolved
   > EFCore.NamingConventions                       8.0.3       8.0.3
   > Microsoft.EntityFrameworkCore                  8.0.27      8.0.27
   > Microsoft.EntityFrameworkCore.Relational       8.0.27      8.0.27
   > Microsoft.Extensions.TimeProvider.Testing      8.10.0      8.10.0
   > Npgsql.EntityFrameworkCore.PostgreSQL          8.0.10      8.0.10
   > xunit.runner.visualstudio                      3.1.5       3.1.5
   > xunit.v3                                       3.2.2       3.2.2
   > xunit.v3.assert                                3.2.2       3.2.2
```

| Package | Requested | Resolved | Pin source |
|---------|-----------|----------|------------|
| Microsoft.EntityFrameworkCore | 8.0.27 | 8.0.27 | Phase 1 D-05 |
| Microsoft.EntityFrameworkCore.Relational | 8.0.27 | 8.0.27 | Phase 1 D-05 |
| Npgsql.EntityFrameworkCore.PostgreSQL | 8.0.10 | 8.0.10 | Phase 1 D-05 |
| EFCore.NamingConventions | 8.0.3 | 8.0.3 | Phase 1 D-05 |
| Microsoft.Extensions.TimeProvider.Testing | 8.10.0 | 8.10.0 | Phase 3 Task 1 (NEW) |

### `dotnet build --configuration Release --no-restore`

```
BaseApi.Core -> C:\Users\UserL\source\repos\SK_P\src\BaseApi.Core\bin\Release\net8.0\BaseApi.Core.dll
BaseApi.Tests -> C:\Users\UserL\source\repos\SK_P\tests\BaseApi.Tests\bin\Release\net8.0\BaseApi.Tests.dll
BaseApi.Service -> C:\Users\UserL\source\repos\SK_P\src\BaseApi.Service\bin\Release\net8.0\BaseApi.Service.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:04.40
Release exit: 0
```

### `dotnet build --configuration Debug --no-restore` (W-02 unconditional aggregate)

```
BaseApi.Core -> C:\Users\UserL\source\repos\SK_P\src\BaseApi.Core\bin\Debug\net8.0\BaseApi.Core.dll
BaseApi.Tests -> C:\Users\UserL\source\repos\SK_P\tests\BaseApi.Tests\bin\Debug\net8.0\BaseApi.Tests.dll
BaseApi.Service -> C:\Users\UserL\source\repos\SK_P\src\BaseApi.Service\bin\Debug\net8.0\BaseApi.Service.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:01.38
Debug exit: 0
```

**Aggregate result:** `Release exit=0 zero-warnings AND Debug exit=0 zero-warnings`. W-02 aggregate-build gate is green.

## Files Created/Modified

### Created (5 production C# files)

- `src/BaseApi.Core/Entities/BaseEntity.cs` — Abstract base class with the 8 ENTITY-01 properties (Id Guid, Name string, Version string, CreatedAt DateTime, UpdatedAt DateTime, CreatedBy string?, UpdatedBy string?, Description string?). Pitfall 1 `<remarks>` XML doc on both `CreatedAt` and `UpdatedAt` ("UTC by convention — set by AuditInterceptor; never assign manually"). Mutable `{ get; set; }` accessors so AuditInterceptor can mutate via ChangeTracker. No EF data-annotation attributes — convention-based mapping only.
- `src/BaseApi.Core/Persistence/BaseDbContext.cs` — Abstract DbContext base. NO `DbSet<>` properties (D-09). Constructor `protected BaseDbContext(DbContextOptions options) : base(options) { }`. `OnConfiguring` calls `optionsBuilder.UseSnakeCaseNamingConvention()` (Pitfall 4 — wired BEFORE first migration). `OnModelCreating` iterates `modelBuilder.Model.GetEntityTypes().Where(t => typeof(BaseEntity).IsAssignableFrom(t.ClrType))` and configures the Pitfall 6 verbatim xmin shadow property: `Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken()`.
- `src/BaseApi.Core/Persistence/Interceptors/AuditInterceptor.cs` — Sealed; derives `SaveChangesInterceptor` (the no-op base class, NOT raw `ISaveChangesInterceptor`). Constructor takes `IHttpContextAccessor` + `TimeProvider`. Overrides only `SavingChangesAsync` (sync `SaveChanges` will NOT stamp audit fields — documented in XML summary). UTC timestamp via `_clock.GetUtcNow().UtcDateTime` (Pitfall 1 anchor). User via `_httpContextAccessor.HttpContext?.User?.Identity?.Name` (full null-conditional chain — D-08 null HttpContext is safe). `EntityState.Added` branch: generates Id only when `Guid.Empty` (D-02 honors caller-set non-empty); stamps all 5 audit fields. `EntityState.Modified` branch: defensive `IsModified = false` on `CreatedAt` + `CreatedBy`; stamps `UpdatedAt` + `UpdatedBy`. Filter `ChangeTracker.Entries<BaseEntity>()` excludes junction entities naturally.
- `src/BaseApi.Core/Persistence/Repositories/IRepository.cs` — Public generic interface `IRepository<TEntity> where TEntity : BaseEntity`. EXACTLY 5 methods: `Task<TEntity?> GetAsync(Guid id, CancellationToken)`, `Task<IReadOnlyList<TEntity>> ListAsync(CancellationToken)`, `Task AddAsync(TEntity, CancellationToken)`, `void Update(TEntity)`, `Task DeleteAsync(Guid id, CancellationToken)`. No `IQueryable<T>` return. No `ExistsAsync`. No `Where(predicate)` overload (D-04 surface lock).
- `src/BaseApi.Core/Persistence/Repositories/Repository.cs` — `public sealed class Repository<TEntity> : IRepository<TEntity> where TEntity : BaseEntity`. Constructor takes `BaseDbContext` (the abstract base — NOT raw `DbContext`) so the type system enforces that only BaseApi.Core ecosystem DbContexts can construct a Repository. `DeleteAsync` is load-then-remove (`FirstOrDefaultAsync` → null-check → `_set.Remove(entity)`) — preserves the D-03 xmin check because the load tracks the row's current xmin. Never calls `SaveChangesAsync` (D-05 — Service owns the transaction boundary).

### Modified (4 files)

- `.planning/REQUIREMENTS.md` — Appended `PERSIST-16` bullet under § Persistence (after PERSIST-15) + matching `| PERSIST-16 | Phase 3 | Pending |` row in the Traceability table (D-03b applied). v1 requirements count footer (lines 294-298) intentionally NOT touched — STATE.md tracks the count discrepancy separately.
- `Directory.Packages.props` — Added one new section comment `<!-- Microsoft.Extensions.* family — versions on own cadence (Phase 3: FakeTimeProvider) -->` and one new `<PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="8.10.0" />` line after the existing Tests section. Pin count: 22 → 23. All existing 22 pins untouched. CPM property `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>` preserved.
- `src/BaseApi.Core/BaseApi.Core.csproj` — Inserted two new `<ItemGroup>` blocks before the closing `</Project>` (one for `<FrameworkReference Include="Microsoft.AspNetCore.App" />`, one for the 4 EF Core `<PackageReference>` entries). Existing `<PropertyGroup>` (4 props: RootNamespace, AssemblyName, GenerateDocumentationFile, NoWarn) and header comment unchanged. Zero `Version=` attributes on PackageReferences (CPM contract).
- `tests/BaseApi.Tests/BaseApi.Tests.csproj` — Inserted two new `<ItemGroup>` blocks between the existing xunit `<ItemGroup>` and the existing ProjectReference `<ItemGroup>` (one for the 5 PackageReferences: 4 EF + FakeTimeProvider; one for `<FrameworkReference Include="Microsoft.AspNetCore.App" />`). Existing `<PropertyGroup>` (5 props: RootNamespace, AssemblyName, IsPackable=false, IsTestProject=true, OutputType=Exe, UseMicrosoftTestingPlatformRunner=true, TestingPlatformDotnetTestSupport=true), existing 3 xunit PackageReferences, and existing ProjectReference to `..\..\src\BaseApi.Core\BaseApi.Core.csproj` all untouched. Total PackageReferences: 3 → 8 (3 xunit + 5 new). Zero `Version=` attributes on new PackageReferences (CPM contract).

## Decisions Made

- **D-03b doc-first ordering confirmed:** PERSIST-16 went into REQUIREMENTS.md as Task 0 (BEFORE any code commit) so the requirement-to-code traceability is established up front. Future Phase 4 work referencing the cross-phase impact (DbUpdateConcurrencyException → HTTP 409) will find the PERSIST-16 bullet already in place.
- **FrameworkReference over per-package PackageReference (D-12):** Chose `<FrameworkReference Include="Microsoft.AspNetCore.App" />` for IHttpContextAccessor + future controllers/middleware/healthchecks. Zero runtime weight (framework already on host) and one line covers all future ASP.NET surface phases need. The decision rationale was already locked in 03-CONTEXT.md D-12 + 03-RESEARCH.md lines 692-727; this plan executed the decision verbatim.
- **Section-comment-then-pin convention for new TimeProvider pin (D-13):** New Directory.Packages.props pin landed under its own section comment "`<!-- Microsoft.Extensions.* family — versions on own cadence (Phase 3: FakeTimeProvider) -->`" rather than appended to the existing Tests section. Microsoft.Extensions.* family follows its own cadence (NOT lockstep with EF Core 8.0.27) — separating it visually documents that future bumps don't need to track EF Core.
- **Two-ItemGroup-per-concern csproj layout:** Both BaseApi.Core.csproj and BaseApi.Tests.csproj got separate ItemGroups for `<FrameworkReference>` vs `<PackageReference>` (and BaseApi.Tests got a third ItemGroup for the EF Core packages distinct from xunit). Mirrors xunit MTP scaffold convention from Phase 1 and makes the diff intent obvious.
- **No commit for Task 8 (build-gate semantics):** Task 8 is purely a verification gate (`dotnet restore` + Release + Debug build aggregate per W-02) and produces no file changes. Per the task body, "Capture all command outputs verbatim for the eventual Plan 03-01 SUMMARY" — outputs captured above instead.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Fixed false-positive PackageReference regex in Task 2 verify script**
- **Found during:** Task 2 (BaseApi.Core.csproj insert) verify step
- **Issue:** The verify regex `<PackageReference ` counted 5 matches when the csproj actually has 4 PackageReference elements — because the file's header comment (line 11) contains the example text `<PackageReference Include="..." />` which the regex picks up.
- **Fix:** Updated the verify script to strip XML comments (`[regex]::Replace($c, '<!--[\s\S]*?-->', '')`) BEFORE counting PackageReference / FrameworkReference elements. The actual csproj content was correct per plan; only the verify gate's regex needed strengthening. No change to the csproj contents.
- **Files modified:** verify-task2.ps1 (ephemeral; deleted after use). No source file changes.
- **Verification:** Re-ran verify-task2.ps1 → PASS with 4 PackageReferences + 1 FrameworkReference counted from comment-stripped content.
- **Committed in:** n/a (verify script is ephemeral, never committed; the csproj commit `426a25d` was unchanged)

**2. [Rule 3 - Blocking] Fixed false-positive AddInterceptors regex in Task 5 verify script**
- **Found during:** Task 5 (BaseDbContext.cs create) verify step
- **Issue:** The verify regex `AddInterceptors\(` flagged the file as violating D-10 — but the only match was inside an XML doc `///` comment that says "via AddInterceptors(...)" describing where the interceptor IS wired (composition root), not where it is NOT wired.
- **Fix:** Updated the verify script to strip `///` line comments, `/* */` block comments, and `//` line comments BEFORE running semantic checks. The actual code does NOT call AddInterceptors anywhere; only the descriptive XML doc mentions the name. No change to BaseDbContext.cs.
- **Files modified:** verify-task5.ps1 (ephemeral; deleted after use). No source file changes.
- **Verification:** Re-ran verify-task5.ps1 → PASS with all 14 semantic checks green against comment-stripped content.
- **Committed in:** n/a (verify script is ephemeral, never committed; the BaseDbContext.cs commit `abbd666` was unchanged)

**3. [Rule 3 - Blocking] Fixed method-count regex in Task 7 verify script (`\s+` vs `[ \t]+` + `Task<` vs `Task `)**
- **Found during:** Task 7 (IRepository.cs / Repository.cs create) verify step
- **Issue:** The verify regex `(?m)^\s+(Task|void) ` for counting IRepository method signatures matched only 3 of the 5 actual methods because: (a) `\s+` in multiline mode is greedy across blank lines, consuming multiple method lines into one match; (b) the literal trailing space in `(Task|void) ` excluded `Task<TEntity?> GetAsync` and `Task<IReadOnlyList<TEntity>> ListAsync` which have `<` immediately after `Task`.
- **Fix:** Changed the regex to `(?m)^[ \t]+(Task[<\s]|void )` — uses `[ \t]+` for line-prefix indent (no newline matching) and allows `<` or whitespace after `Task`. The actual IRepository.cs has exactly 5 methods per plan; only the verify gate's regex was wrong.
- **Files modified:** verify-task7.ps1 (ephemeral; deleted after use). No source file changes.
- **Verification:** Re-ran verify-task7.ps1 → PASS with method count = 5, all semantic checks green.
- **Committed in:** n/a (verify script is ephemeral; the IRepository.cs + Repository.cs commit `41b3927` was unchanged)

---

**4. [Rule 1 - Bug] gsd-sdk `requirements mark-complete` handler corrupted UTF-8 + introduced split-line bullets**
- **Found during:** State-updates phase (after SUMMARY creation)
- **Issue:** Calling `gsd-sdk query requirements.mark-complete ENTITY-01 ... PERSIST-16` (11 IDs) rewrote `.planning/REQUIREMENTS.md` with TWO defects: (a) every checked-off bullet header `**ID-NN**:` was split into `**ID-NN\n**:` (two lines, broken markdown rendering); (b) every em-dash `—` was rewritten as `â€"` and every right-arrow `→` as `â†'` (Windows-1252 misinterpretation of UTF-8). The Traceability table rows were NOT updated to "Complete" either — only the inline checkboxes.
- **Fix:** Reverted REQUIREMENTS.md via `git checkout 28838cc -- .planning/REQUIREMENTS.md` (back to clean post-Task-0 state), then re-applied the 11 `[ ]` → `[x]` toggles + 11 Traceability `Pending` → `Complete` row updates via direct `Edit` tool calls. UTF-8 em-dashes and arrows preserved.
- **Files modified:** `.planning/REQUIREMENTS.md` (reverted + manually re-toggled). The ephemeral `fix-requirements.ps1` script that attempted in-place repair was deleted (it preserved encoding corruption because it round-tripped via Get-Content -Raw which inherits the file's saved encoding).
- **Verification:** `grep -c '— '` returns 23 (em-dashes intact); `grep -c 'â€"'` returns 0 (no Windows-1252 corruption); `grep -c '^- \[x\] \*\*(PERSIST|ENTITY)-\d+'` returns 11 (all Phase 3 checkboxes flipped); Traceability table shows 11 Phase 3 rows as `Complete`.
- **Committed in:** Final metadata commit (along with this SUMMARY).
- **Note for future:** Pre-existing split-line bullets `**INFRA-01\n**:` etc. for INFRA-01..04, INFRA-06..07 exist in REQUIREMENTS.md from prior Plan 01-03 / 02-02 executions of the same SDK handler. They are OUT OF SCOPE for Plan 03-01 (SCOPE BOUNDARY rule — pre-existing damage). Logged under "Issues Encountered" below for future cleanup.

---

**Total deviations:** 4 auto-fixed (3× Rule 3 verify-script regex bugs + 1× Rule 1 SDK data-corruption bug)
**Impact on plan:** Zero impact on production code. All deviations were in tooling/state-tracking, not the 5 production C# files or the 4 csproj/props/doc edits. The csproj/.cs files all match the plan's verbatim skeletons exactly. Lessons captured under `patterns-established` for future plan executors on Windows + PowerShell + gsd-sdk.

## Issues Encountered

- **PowerShell `$` sigil eaten by Bash invocation:** Initial attempts to run verify commands via `Bash → powershell -Command "$var = ..."` resulted in Bash stripping the PowerShell variable sigils because Bash treats `$var` as an unset variable and expands to empty. Resolved by writing each verify block as a `.ps1` script file and invoking via `powershell -NoProfile -ExecutionPolicy Bypass -File verify-taskN.ps1`. This pattern is now documented under `patterns-established` for future Windows + PowerShell + Bash executors.
- **Pre-existing gsd-sdk handler damage in REQUIREMENTS.md (OUT OF SCOPE for this plan):** The bullets for INFRA-01, INFRA-02, INFRA-03, INFRA-04, INFRA-06, INFRA-07 currently display as `- [x] **INFRA-01\n**:` (split across two lines, broken markdown rendering). This was introduced by earlier `gsd-sdk requirements mark-complete` calls in Plan 01-03 and Plan 02-02 (visible in git history `git show 28838cc:.planning/REQUIREMENTS.md`). Plan 03-01 deliberately did NOT fix these per the SCOPE BOUNDARY rule (only auto-fix issues directly caused by current task's changes). **Recommended next action:** dedicated docs cleanup pass that collapses all `\*\*([A-Z]+-\d+)\r?\n\*\*:` → `**$1**:` patterns across REQUIREMENTS.md AND files an upstream bug report against `gsd-sdk requirements.mark-complete` handler (defects: (1) corrupts UTF-8 to Windows-1252; (2) splits `**ID**` headers across newlines; (3) does not update Traceability table column despite the field's stated purpose).

## User Setup Required

None — no external service configuration required for this plan. All work is in-tree (REQUIREMENTS.md doc + Directory.Packages.props pin + 2 csproj edits + 5 new C# files in BaseApi.Core). No new env vars, no dashboard configuration, no new ports. Phase 2's compose stack (postgres:17-alpine on 5433) remains the only external surface and is unchanged here.

## Next Phase Readiness

- **Plan 03-02 (verification + smoke tests) is ready to execute:** Plan 03-02 will create `TestDbContext` (derives `BaseDbContext`, adds `DbSet<TestEntity>`), `StubHttpContextAccessor`, and 4 fact-test files (AuditInterceptorTests, RepositorySurfaceTests, XminConcurrencyTokenTests, SchemaTests) against the persistence base landed in this plan. The csproj wiring (FrameworkReference + FakeTimeProvider pin) gives Plan 03-02's tests the surface they need for `DefaultHttpContext` + `ClaimsPrincipal` + `FakeTimeProvider`.
- **Phase 4 (Error Handling) cross-phase work locked in:** PERSIST-16 + D-03 + D-03a establish that Phase 4 must map `DbUpdateConcurrencyException` → HTTP 409. The xmin shadow-property is now wired in `BaseDbContext.OnModelCreating`; Phase 4 will add the exception-to-Problem-Details mapper.
- **Phase 7 (composition root) cross-phase work locked in:** D-06 + D-10 establish that Phase 7's `AddBaseApi<TDbContext>` will `services.AddSingleton<AuditInterceptor>()` and register the interceptor via `optionsBuilder.AddInterceptors(serviceProvider.GetRequiredService<AuditInterceptor>())` when building the DbContextOptions. Phase 3 deliberately did NOT wire the interceptor (D-10) — Phase 7's territory.
- **Phase 8 (entities + migration) ready:** The 5 concrete entities (SchemaEntity, ProcessorEntity, StepEntity, AssignmentEntity, WorkflowEntity) will derive `BaseEntity` and inherit the 8 audit/Id properties + the xmin shadow concurrency token automatically (via `OnModelCreating` iteration). The 3 junction entities (StepNextSteps, WorkflowEntrySteps, WorkflowAssignments) will NOT derive `BaseEntity` and will be excluded from xmin tracking automatically. The `InitialCreate` migration will be the FIRST migration ever generated — Pitfall 4 satisfied (snake_case convention is active before any migration).
- **No blockers, no carry-forward concerns.** Zero-warning regime holds. CPM contract intact. All 9 success criteria green.

## Self-Check: PASSED

**Files verified (10/10):**
- FOUND: .planning/REQUIREMENTS.md
- FOUND: Directory.Packages.props
- FOUND: src/BaseApi.Core/BaseApi.Core.csproj
- FOUND: tests/BaseApi.Tests/BaseApi.Tests.csproj
- FOUND: src/BaseApi.Core/Entities/BaseEntity.cs
- FOUND: src/BaseApi.Core/Persistence/BaseDbContext.cs
- FOUND: src/BaseApi.Core/Persistence/Interceptors/AuditInterceptor.cs
- FOUND: src/BaseApi.Core/Persistence/Repositories/IRepository.cs
- FOUND: src/BaseApi.Core/Persistence/Repositories/Repository.cs
- FOUND: .planning/phases/03-ef-core-persistence-base/03-01-SUMMARY.md

**Commits verified (8/8):**
- FOUND: 28838cc (Task 0 — PERSIST-16 docs)
- FOUND: c15787d (Task 1 — TimeProvider.Testing pin)
- FOUND: 426a25d (Task 2 — BaseApi.Core.csproj EF Core wiring)
- FOUND: 1a47f7f (Task 3 — BaseApi.Tests.csproj EF Core + FakeTimeProvider wiring)
- FOUND: eea41bf (Task 4 — BaseEntity.cs)
- FOUND: abbd666 (Task 5 — BaseDbContext.cs)
- FOUND: bd65944 (Task 6 — AuditInterceptor.cs)
- FOUND: 41b3927 (Task 7 — IRepository.cs + Repository.cs)

---
*Phase: 03-ef-core-persistence-base*
*Plan: 01*
*Completed: 2026-05-26*
