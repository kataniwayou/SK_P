# Phase 6: Validation + Mapping Base - Pattern Map

**Mapped:** 2026-05-27
**Files analyzed:** 17 (5 new in Core + 3 modified configs + 9 new test scaffolds/facts)
**Analogs found:** 17 / 17 (all have strong analogs from Phases 3, 4, 5)

> **Reads (this session):** `06-CONTEXT.md`, `06-RESEARCH.md`, `BaseEntity.cs`, `Program.cs`, `ValidationExceptionHandler.cs`, `NotFoundExceptionHandler.cs`, `DbUpdateExceptionHandler.cs` (head only), `StartupCompletionService.cs`, `IStartupGate.cs`, `Repository.cs`, `IRepository.cs`, `AuditInterceptor.cs` (head only), `TestController.cs`, `WebAppFactory.cs`, `OtelCollectorFixture.cs`, `OtelEndOfSuiteCleanup.cs` (head only), `HealthEndpointsTests.cs` (heads), `TestObservabilityController.cs`, `ValidationErrorTests.cs`, `CorrelationIdTests.cs`, `TestEntity.cs` (Persistence), `TestParentEntity.cs`, `TestDbContext.cs`, `TestErrorDbContext.cs`, `DiLifetimeTests.cs`, `PostgresFixture.cs`, `BaseApi.Core.csproj`, `BaseApi.Service.csproj`, `BaseApi.Tests.csproj`, `Directory.Build.props`, `Directory.Packages.props` (head only).

> **Folder verification (Glob):** `src/BaseApi.Core/Validation/.gitkeep` EXISTS; `src/BaseApi.Core/DependencyInjection/.gitkeep` EXISTS; `src/BaseApi.Core/Mapping/` DOES NOT EXIST (Phase 6 creates).

---

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/BaseApi.Core/Validation/IBaseDto.cs` | Interface (marker contract) | DTO surface (3 getters) | `src/BaseApi.Core/Health/IStartupGate.cs` | role-match (interface w/ XML doc + minimal surface) |
| `src/BaseApi.Core/Validation/BaseDtoValidator.cs` | Implementation (generic base class) | Class library — instantiated by `Include(new …)` from concrete validators | `src/BaseApi.Core/Persistence/Repositories/Repository.cs` (generic class with `where T : BaseEntity`) | role-match (generic base, public, non-sealed; constraint on marker) |
| `src/BaseApi.Core/Mapping/IEntityMapper.cs` | Interface (4-generic contract, 3 methods) | Pure type contract — consumed by `BaseService` (Phase 7) + Mapperly partial classes (Phase 8) | `src/BaseApi.Core/Persistence/Repositories/IRepository.cs` (generic interface, exact-N-methods contract, `where T : BaseEntity`) | exact (same shape: generic interface with constraint + small fixed method count) |
| `src/BaseApi.Core/DependencyInjection/ValidationServiceCollectionExtensions.cs` | Extension (`IServiceCollection`) | Startup composition — discovers validators via reflection assembly scan | Inline DI block in `src/BaseApi.Service/Program.cs` lines 73-76 (`AddExceptionHandler<…>` chain) | role-match (DI extension pattern; first BaseApi.Core extension class — sets convention) |
| `src/BaseApi.Core/DependencyInjection/MappingServiceCollectionExtensions.cs` | Extension (`IServiceCollection`) | Startup composition — reflection scan for closed-generic `IEntityMapper<,,,>` | Inline DI block in `Program.cs` line 144 (`AddHealthChecks().AddCheck<T>` registration) + RESEARCH Example 5 | role-match (DI extension; closed-generic scan idiom matches `AddValidatorsFromAssembly` shape) |
| `src/BaseApi.Service/Program.cs` (EDIT) | Composition root | 2-line insertion between `AddProblemDetails` (line 68 close) and `AddExceptionHandler` chain (line 73) per D-18 | Self — Phase 4 D-04→D-06 insertion pattern (lines 57-76) | exact (same file, same insertion-discipline convention) |
| `Directory.Build.props` (EDIT) | MSBuild config | Build-time diagnostic promotion | Self — Phase 1 D-02 `<TreatWarningsAsErrors>` line 33 | exact (same file; add `<WarningsAsErrors>` sibling line within same `<PropertyGroup>`) |
| `tests/BaseApi.Tests/BaseApi.Tests.csproj` (EDIT) | csproj | Add Mapperly PackageReference with source-gen attributes | Self — Phase 4 `<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />` (lines 78-80) + `xunit.runner.visualstudio` lines 58-61 (asset attribute pattern) | exact (same file; one more PackageReference) |
| `tests/BaseApi.Tests/Validation/TestEntity.cs` (NEW) | Test scaffold (entity) | EF entity subclass of `BaseEntity` | `tests/BaseApi.Tests/Persistence/TestEntity.cs` (Phase 3) + `tests/BaseApi.Tests/Middleware/TestParentEntity.cs` (Phase 4) | exact (literal Phase 3 pattern; sealed class : BaseEntity with extra scalar field per RESEARCH Example 6) |
| `tests/BaseApi.Tests/Validation/TestCreateDto.cs` + `TestUpdateDto.cs` + `TestReadDto.cs` (NEW, 3 files or 1) | Test scaffold (DTOs) | Records implementing `IBaseDto`; consumed by validator + mapper | `src/BaseApi.Core/Entities/BaseEntity.cs` field shape (Name/Version/Description) | role-match (mirror BaseEntity narrative fields; positional record syntax per CONTEXT D-03) |
| `tests/BaseApi.Tests/Validation/TestDtoValidator.cs` (NEW) | Test scaffold (validator) | `AbstractValidator<TestUpdateDto>` with `Include(new BaseDtoValidator<TestUpdateDto>())` | RESEARCH Example 8 (no in-repo analog — Phase 4 doesn't have concrete validators) | no-analog (use RESEARCH Pattern 1 / Example 8 verbatim) |
| `tests/BaseApi.Tests/Validation/TestEntityMapper.cs` (NEW) | Test scaffold (Mapperly partial) | `[Mapper] partial class : IEntityMapper<...>` with 5 `[MapperIgnoreTarget]` attrs on Update | RESEARCH Example 6 + Pitfall 2 resolution (no in-repo Mapperly analog yet) | no-analog (first Mapperly use; RESEARCH Example 6 is the source-of-truth) |
| `tests/BaseApi.Tests/Validation/TestValidationService.cs` (NEW) | Test scaffold (service) | Injects `IValidator<TestUpdateDto>`; calls `ValidateAsync`; throws `FluentValidation.ValidationException` | `src/BaseApi.Core/Exceptions/Handlers/ValidationExceptionHandler.cs` (consumer side — Phase 4 maps the exception) + `src/BaseApi.Core/Health/StartupCompletionService.cs` (ctor-injected service shape) | role-match (minimal sealed service class with single ctor + single async method) |
| `tests/BaseApi.Tests/Endpoints/TestController.cs` (EDIT) | Test scaffold (controller endpoint addition) | New `[HttpPost("validate")]` action calling `TestValidationService.ValidateAsync` | Self — Phase 4 `TestController` 8 existing endpoints, particularly `[HttpPost("validation-error-via-fv")]` (lines 55-64) and `[HttpPost("validation-error-via-modelbinding")]` (lines 66-68) | exact (same file, mirror existing endpoint pattern; `[FromBody]` + `[FromServices]` per Pitfall 4) |
| `tests/BaseApi.Tests/Validation/ValidationWebAppFactory.cs` (NEW, optional subclass) | Test scaffold (WAF override) | Subclass `WebAppFactory` to add Tests-assembly validator scan + register `TestValidationService` | `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` lines 215-256 (nested `HealthDeadPostgresFixture : OtelCollectorFixture` pattern) + RESEARCH Pitfall 5 | role-match (WAF subclass overriding `ConfigureWebHost` via `ConfigureTestServices`) |
| `tests/BaseApi.Tests/Validation/BaseDtoValidatorRuleTests.cs` + `BaseDtoValidatorIncludeTests.cs` (NEW) | Test fact (unit) | Construct validator directly, call `Validate`, assert `ValidationResult` shape | `tests/BaseApi.Tests/Persistence/DiLifetimeTests.cs` (pure-unit shape: build container, resolve, assert) | role-match (xUnit `[Fact]` style; no HTTP wire) |
| `tests/BaseApi.Tests/Validation/ValidatorAutoDiscoveryTests.cs` (NEW) | Test fact (DI scan) | Build a ServiceCollection, call `AddBaseApiValidation`, resolve `IValidator<TestUpdateDto>`, assert non-null | `tests/BaseApi.Tests/Persistence/DiLifetimeTests.cs` lines 14-32 (ServiceCollection → BuildServiceProvider → GetRequiredService) | exact (same DI-resolution test idiom) |
| `tests/BaseApi.Tests/Validation/ValidationEndpointTests.cs` (NEW) | Test fact (HTTP roundtrip) | POST bad TestDto → 400 ProblemDetails with `errors` map + `correlationId` header parity | `tests/BaseApi.Tests/Middleware/ValidationErrorTests.cs` (Phase 4 — POSTs to `/test/validation-error-via-fv`, asserts 400 + ProblemDetails + correlationId echo) | exact (literal Phase 4 SC#2 fact shape — Phase 6 just changes the route + body) |
| `tests/BaseApi.Tests/Validation/MapperRegistrationTests.cs` (NEW) | Test fact (DI scan) | Build a ServiceCollection, call `AddBaseApiMapping(typeof(TestEntityMapper).Assembly)`, resolve `IEntityMapper<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>`, assert non-null + Singleton | `tests/BaseApi.Tests/Persistence/DiLifetimeTests.cs` lines 14-32 | exact (mirror of ValidatorAutoDiscoveryTests — closed-generic resolution) |
| `tests/BaseApi.Tests/Validation/MapperlyCompileTests.cs` + `MapperlyDriftTests.cs` (NEW; optional drift-detection per RESEARCH Open Question 2) | Test fact (build assertion) | Smoke: assembly loads + mapper compiles; drift: temporarily injected mismatched field fails build with RMG012 | RESEARCH Open Question 2 (no in-repo analog) | no-analog (build-time fact; planner may invert to compile-only) |
| `src/BaseApi.Service/BaseApi.Service.csproj` (OPTIONAL EDIT — D-13 planner's call) | csproj | Forward-place Mapperly PackageReference for Phase 8 | Same pattern as `BaseApi.Tests.csproj` edit | role-match (defer per RESEARCH Open Question 1 recommendation) |

---

## Pattern Assignments

### `src/BaseApi.Core/Validation/IBaseDto.cs` (Interface, marker contract)

**Analog:** `src/BaseApi.Core/Health/IStartupGate.cs`

**Why this analog:** Same shape — small public interface in `BaseApi.Core/<Subfolder>/` with substantial XML `<summary>` doc explaining role in the architecture; uses property getters (StartupGate exposes `bool IsReady { get; }`). No FrameworkReference dependency needed — `BaseApi.Core.csproj` already references `FluentValidation` (line 54 — Phase 4 D-10) but `IBaseDto` itself depends on NO package — pure C# language constructs.

**XML doc + namespace pattern** (from `IStartupGate.cs` lines 1-27):
```csharp
namespace BaseApi.Core.Health;

/// <summary>
/// One-shot startup gate exposed via DI as a Singleton.
///
/// <para>
/// <b>Phase 5 default (D-01 / D-13):</b> ...
/// </para>
/// </summary>
public interface IStartupGate
{
    /// <summary>True once <see cref="MarkReady"/> has been called at least once.</summary>
    bool IsReady { get; }

    /// <summary>Idempotently transitions the gate to the ready state. Thread-safe.</summary>
    void MarkReady();
}
```

**Member-name parity** (from `src/BaseApi.Core/Entities/BaseEntity.cs` lines 22-40):
```csharp
public abstract class BaseEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    ...
    public string? Description { get; set; }
}
```
Mirror EXACTLY: `Name` + `Version` are `string` (non-null), `Description` is `string?`. Get-only on the interface (per CONTEXT D-02). NO `Id` or audit fields (HTTP-05).

**Verbatim shape to land** (per RESEARCH Example 1):
```csharp
namespace BaseApi.Core.Validation;

public interface IBaseDto
{
    string Name { get; }
    string Version { get; }
    string? Description { get; }
}
```

---

### `src/BaseApi.Core/Validation/BaseDtoValidator.cs` (Implementation, generic base class)

**Analog:** `src/BaseApi.Core/Persistence/Repositories/Repository.cs` (generic class with generic constraint on marker type)

**Why this analog:** Both are public generic classes that derive from a third-party base (`AbstractValidator<T>` vs. `IRepository<TEntity>`), constrain T to an in-house marker (`IBaseDto` vs. `BaseEntity`), and ship as the "common base" companion to a Core-defined interface.

**Generic constraint pattern** (from `Repository.cs` lines 13-17):
```csharp
public sealed class Repository<TEntity> : IRepository<TEntity> where TEntity : BaseEntity
{
    private readonly DbSet<TEntity> _set;

    public Repository(BaseDbContext db) => _set = db.Set<TEntity>();
```

**Difference vs. Repository:** `BaseDtoValidator<T>` is `public class` (NOT sealed) per CONTEXT D-05 — must be instantiable AND inheritable so concrete Phase 8 validators absorb its rules via `Include(new BaseDtoValidator<MyDto>())` (the canonical FluentValidation pattern). Concrete validators in Phase 8 will be `public sealed class`.

**Rule chain pattern** (no in-repo analog — first FluentValidation use; from RESEARCH Example 2):
```csharp
using FluentValidation;

namespace BaseApi.Core.Validation;

public class BaseDtoValidator<T> : AbstractValidator<T>
    where T : IBaseDto
{
    public BaseDtoValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.Version)
            .NotEmpty()
            .Matches(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$");

        RuleFor(x => x.Description)
            .MaximumLength(2000);
    }
}
```

**Pitfall 3 enforcement:** the regex MUST be a C# verbatim literal `@"..."` (NOT a regular `"..."` literal) — `\d` in a non-verbatim literal triggers `CS1009 Unrecognized escape sequence` under `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` (Phase 1 D-02). Already enforced by RESEARCH Example 2.

---

### `src/BaseApi.Core/Mapping/IEntityMapper.cs` (Interface, 4-generic / 3-method contract)

**Analog:** `src/BaseApi.Core/Persistence/Repositories/IRepository.cs` (generic interface with fixed-N method count)

**Why this analog:** Identical structural pattern — a generic interface in `BaseApi.Core/<Subfolder>/` exposing EXACTLY N methods with no fluff (CONTEXT D-06 says "EXACTLY 3 methods" — same discipline as Phase 3 D-04 "EXACTLY 5 methods"). Same XML-doc-heavy style. Same role: define the Service-tier contract that concrete impls (Repository<T> / Mapperly partial classes) implement.

**Interface shape pattern** (from `IRepository.cs` lines 22-43):
```csharp
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

    /// <summary>...</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
```

**Differences vs. IRepository:**
- Four type parameters (TEntity, TCreate, TUpdate, TRead) instead of one. NO generic constraint required — Phase 8 mappers will be `[Mapper] partial class` and Mapperly's property-by-property mapping does not need a base-class constraint (CONTEXT D-06).
- Sync return values for `ToEntity`/`ToRead` and `void` for `Update` (per CONTEXT D-06/D-07 — Mapperly methods are reflection-free generated code, no async needed).
- `Update` is mutating (void), not functional — same justification as `IRepository.Update` ("the async-by-symmetry shape would be a lie"): Mapperly fills target in place; EF change tracking + xmin handle conflict detection.

**Verbatim shape to land** (per RESEARCH Example 3 + CONTEXT D-06):
```csharp
namespace BaseApi.Core.Mapping;

public interface IEntityMapper<TEntity, TCreate, TUpdate, TRead>
{
    TEntity ToEntity(TCreate dto);
    void    Update(TUpdate dto, TEntity target);
    TRead   ToRead(TEntity entity);
}
```

**Folder creation note:** `src/BaseApi.Core/Mapping/` does NOT yet exist (verified via Glob — only the 12 Phase 1 D-08 skeleton folders + `Exceptions/Handlers` exist). Planner creates the folder; no `.gitkeep` needed because `IEntityMapper.cs` will be the immediate sole occupant.

---

### `src/BaseApi.Core/DependencyInjection/ValidationServiceCollectionExtensions.cs` (Extension)

**Analogs:**
1. **In-repo:** `src/BaseApi.Service/Program.cs` lines 73-76 (`AddExceptionHandler<…>` chain — currently an inline composition without an extension method wrapper)
2. **External canonical:** RESEARCH Example 4 (`FluentValidation.DependencyInjectionExtensions` source on main branch)

**Why this is the first DI extension in BaseApi.Core:** The `DependencyInjection/` folder skeleton exists (Phase 1 D-08 `.gitkeep` verified) but has no files yet. Phase 6 sets the precedent for Phase 7's `AddBaseApi` extension and any future per-concern extension. Filename pattern matches Microsoft.Extensions.* convention: `<Concern>ServiceCollectionExtensions.cs`.

**Caller-side pattern** (from `Program.cs` lines 50-76, showing how the body of Phase 6's extension wraps a Phase-7-friendly seam):
```csharp
builder.Services.AddHttpContextAccessor();

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = ctx => { /* ... */ };
});

// PHASE 6 INSERTS HERE (per D-18):
// builder.Services.AddBaseApiValidation(typeof(Program).Assembly);
// builder.Services.AddBaseApiMapping(typeof(Program).Assembly);

builder.Services.AddExceptionHandler<NotFoundExceptionHandler>();
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
```

**Extension body** (per RESEARCH Example 4 — no in-repo analog for IServiceCollection extensions yet):
```csharp
using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace BaseApi.Core.DependencyInjection;

public static class ValidationServiceCollectionExtensions
{
    public static IServiceCollection AddBaseApiValidation(
        this IServiceCollection services,
        params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            services.AddValidatorsFromAssembly(
                assembly,
                lifetime: ServiceLifetime.Scoped,
                includeInternalTypes: false);
        }
        return services;
    }
}
```

**Package availability check:** `BaseApi.Core.csproj` line 54 already has `<PackageReference Include="FluentValidation" />`. The `AddValidatorsFromAssembly` extension lives in `FluentValidation.DependencyInjectionExtensions` 12.1.1 (Directory.Packages.props line 69) — Phase 6 MUST add this PackageReference to `BaseApi.Core.csproj` because the existing line 54 ONLY references `FluentValidation` (no DI extensions). Pattern for the additive csproj line (from `BaseApi.Core.csproj` lines 50-55):
```xml
<ItemGroup>
  <PackageReference Include="FluentValidation" />
  <!-- Phase 6 ADD: -->
  <PackageReference Include="FluentValidation.DependencyInjectionExtensions" />
</ItemGroup>
```

**XML doc style pattern** (from `StartupCompletionService.cs` lines 5-23):
- Heavy `<summary>` with `<para>` blocks
- `<b>Phase X contract:</b>` markup for forward-looking notes
- Reference to specific decision IDs (`D-14`, `D-16`)

---

### `src/BaseApi.Core/DependencyInjection/MappingServiceCollectionExtensions.cs` (Extension)

**Analog:** RESEARCH Example 5 (no in-repo analog — this is the first reflection-based assembly scan in the codebase)

**Why no closer in-repo analog:** Phase 5's `AddHealthChecks().AddCheck<StartupHealthCheck>(...)` (Program.cs line 146) is a single-type registration, not a reflection scan. The closest "scan" idiom is FluentValidation's `AddValidatorsFromAssembly` itself, which Phase 6's sister extension wraps.

**Body** (per RESEARCH Example 5 — verbatim, AOT-safe under .NET 8 + Nullable=enable):
```csharp
using System.Reflection;
using BaseApi.Core.Mapping;
using Microsoft.Extensions.DependencyInjection;

namespace BaseApi.Core.DependencyInjection;

public static class MappingServiceCollectionExtensions
{
    public static IServiceCollection AddBaseApiMapping(
        this IServiceCollection services,
        params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            // GetExportedTypes() over GetTypes() — avoids ReflectionTypeLoadException
            // on partially-built assemblies during Roslyn incremental builds (Pitfall 7).
            foreach (var type in assembly.GetExportedTypes())
            {
                if (type.IsAbstract || type.IsInterface) continue;

                var closedInterfaces = type.GetInterfaces()
                    .Where(i => i.IsGenericType &&
                                i.GetGenericTypeDefinition() == typeof(IEntityMapper<,,,>));

                foreach (var closedInterface in closedInterfaces)
                {
                    services.AddSingleton(closedInterface, type);
                }
            }
        }
        return services;
    }
}
```

**Singleton lifetime justification** (CONTEXT D-15): Mapperly source-gen emits pure functions — no state — Singleton is correct AND parallels Phase 5's `AddSingleton<IStartupGate, StartupGate>()` (Program.cs line 143) where the same "stateless service" justification applies.

**No new package needed:** `Microsoft.Extensions.DependencyInjection.Abstractions` is in-box via `<FrameworkReference Include="Microsoft.AspNetCore.App" />` on `BaseApi.Core.csproj` line 34. The `BaseApi.Core/Mapping/IEntityMapper.cs` interface is the only new project-internal `using`.

---

### `src/BaseApi.Service/Program.cs` (EDIT — 2-line insertion)

**Analog (Self):** Phase 4 D-04 → D-06 insertion pattern (Program.cs lines 50-76)

**Why self-analog:** Phase 4 established the load-bearing ordering convention in this file (`AddHttpContextAccessor` → `AddProblemDetails` → 4-handler chain → `AddControllers` last). Phase 6 D-18 mandates insertion AFTER `AddProblemDetails` close (line 68) and BEFORE `AddExceptionHandler<…>` chain (line 73) OR BEFORE `AddControllers` (line 153) — RESEARCH Example 7 confirms either is valid; planner picks line 70 (between AddProblemDetails and the IExceptionHandler chain) for the cleanest visual seam.

**Insertion target** (Program.cs lines 70-76):
```csharp
// (line 68 — closing brace of AddProblemDetails block)
});

// ====== PHASE 6 INSERTION POINT (D-18) ======
// builder.Services.AddBaseApiValidation(typeof(Program).Assembly);
// builder.Services.AddBaseApiMapping(typeof(Program).Assembly);
// ============================================

// IExceptionHandler chain — REGISTRATION ORDER IS LOAD-BEARING (D-06).
builder.Services.AddExceptionHandler<NotFoundExceptionHandler>();
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
```

**New `using` directive** (line 28 area):
```csharp
using BaseApi.Core.DependencyInjection;  // Phase 6 — AddBaseApiValidation + AddBaseApiMapping
```

**Forward-looking comment pattern** (from Program.cs lines 22-26):
```csharp
// Phase 7 will replace the body with:
//   builder.Services.AddBaseApi<AppDbContext>(builder.Configuration);
//   app.UseBaseApi();
//   app.MapControllers();
```
Phase 6 should add a similar inline comment marking these 2 lines as "Phase 7's `AddBaseApi` will absorb these unchanged" — preserving the trail Phase 4 established.

---

### `Directory.Build.props` (EDIT — 1 line addition)

**Analog (Self):** Phase 1 D-02 / D-03 `<PropertyGroup>` lines 26-34

**Why self-analog:** Phase 1 established this file as the single-source-of-truth for solution-wide MSBuild properties. Phase 6 D-11/D-12 adds one `<WarningsAsErrors>` sibling to existing `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` (line 33). The existing Phase 1 D-04 comment block (lines 16-19) explicitly says "Phase 6 will add a `<WarningsAsErrors>...</WarningsAsErrors>` entry" — Phase 6 lands AT that comment, also UPDATING the comment to reflect:
1. The codes corrected MP→RMG per RESEARCH A-01 (codes MP0001/MP0011/MP0020/MP0021 do not exist)
2. The promotion is solution-wide via Directory.Build.props, NOT per-csproj (CONTEXT D-11 supersedes Phase 1 D-04)

**Insertion target** (Directory.Build.props lines 26-34):
```xml
<PropertyGroup>
  <TargetFramework>net8.0</TargetFramework>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
  <LangVersion>latest</LangVersion>
  <AnalysisMode>latest</AnalysisMode>
  <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <!-- Phase 6 D-11/D-12 — codes corrected MP→RMG per RESEARCH A-01.
       RMG089 (Info) requires the explicit list to be promoted —
       TreatWarningsAsErrors above only catches Warning/Error severities. -->
  <WarningsAsErrors>$(WarningsAsErrors);RMG007;RMG012;RMG020;RMG089</WarningsAsErrors>
</PropertyGroup>
```

**Existing D-04 comment edit** (lines 16-19) — update OR mark stale: original wording says "Phase 6 will add a `<WarningsAsErrors>...</WarningsAsErrors>` entry to the BaseApi.Service csproj" but CONTEXT D-11 supersedes the "Service csproj" location → solution-wide. Planner may either remove the stale comment or annotate it (matching Phase 5's `// DEVIATION FROM PLAN` style seen in Program.cs lines 109-116).

---

### `tests/BaseApi.Tests/BaseApi.Tests.csproj` (EDIT — 1 line PackageReference)

**Analog (Self):** Phase 4 PackageReference addition pattern (`BaseApi.Tests.csproj` lines 74-80) + source-gen asset attribute pattern from `xunit.runner.visualstudio` (lines 58-61) + Directory.Packages.props lines 24-26 header comment

**Existing additive ItemGroup pattern** (lines 74-80, Phase 4):
```xml
<ItemGroup>
  <!-- Phase 4 addition: WebApplicationFactory<Program> for integration tests
       (Plan 04-02 CONTEXT.md D-14 + PATTERNS.md line 397). Version pinned in
       Directory.Packages.props line 82 (Phase 1 D-13). No Version= attribute
       (CPM contract). -->
  <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
</ItemGroup>
```

**Source-gen attribute pattern** (Directory.Packages.props lines 23-26 header guidance):
```xml
<PackageReference Include="Riok.Mapperly"
                  PrivateAssets="all"
                  ExcludeAssets="runtime" />
```

**Phase 6 ItemGroup to add** (after line 80, before line 82's FrameworkReference ItemGroup):
```xml
<ItemGroup>
  <!-- Phase 6 D-19 (Phase 1 D-07 convention): Mapperly source generator for SC#4
       TestEntityMapper [Mapper] partial class. PrivateAssets="all" prevents the
       analyzer leaking; ExcludeAssets="runtime" prevents the runtime DLL being
       copied to bin/ (it's source-gen only). Version pinned in
       Directory.Packages.props line 65 (Phase 1 D-05). No Version= attribute
       (CPM contract). -->
  <PackageReference Include="Riok.Mapperly"
                    PrivateAssets="all"
                    ExcludeAssets="runtime" />
</ItemGroup>
```

---

### `tests/BaseApi.Tests/Validation/TestEntity.cs` (NEW)

**Analog:** `tests/BaseApi.Tests/Persistence/TestEntity.cs` (Phase 3) and `tests/BaseApi.Tests/Middleware/TestParentEntity.cs` (Phase 4)

**Verbatim Phase 3 pattern** (`tests/BaseApi.Tests/Persistence/TestEntity.cs` lines 1-12):
```csharp
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

**Phase 4 variant with extra fields** (`tests/BaseApi.Tests/Middleware/TestParentEntity.cs` lines 13-15):
```csharp
public sealed class TestParentEntity : BaseEntity
{
}
```

**Naming collision warning:** `tests/BaseApi.Tests/Persistence/TestEntity.cs` already exists with class name `BaseApi.Tests.Persistence.TestEntity`. Phase 6 lands `BaseApi.Tests.Validation.TestEntity` in a different namespace (`tests/BaseApi.Tests/Validation/`) — both can coexist because they live in different namespaces. Recommended Phase 6 shape (per RESEARCH Example 6):
```csharp
using BaseApi.Core.Entities;

namespace BaseApi.Tests.Validation;

public sealed class TestEntity : BaseEntity
{
    public string Note { get; set; } = string.Empty;
}
```

(The extra `Note` field exists ONLY so Mapperly has at least one concrete-entity scalar to map; otherwise Mapperly would emit RMG020 for "Note on source but no target" if the DTO had a field the entity didn't.)

---

### `tests/BaseApi.Tests/Validation/TestCreateDto.cs` + `TestUpdateDto.cs` + `TestReadDto.cs` (NEW)

**Analog:** No in-repo DTO records yet — first DTO use is Phase 6 itself. Closest field-shape analog is `src/BaseApi.Core/Entities/BaseEntity.cs` for member-name parity.

**Verbatim shape** (per RESEARCH Example 6 lines 360-362 + CONTEXT D-03):
```csharp
using BaseApi.Core.Validation;

namespace BaseApi.Tests.Validation;

public sealed record TestCreateDto(string Name, string Version, string? Description, string Note) : IBaseDto;
public sealed record TestUpdateDto(string Name, string Version, string? Description, string Note) : IBaseDto;
public sealed record TestReadDto(Guid Id, string Name, string Version, string? Description, string Note) : IBaseDto;
```

**One-file-vs-three:** All three records are tiny one-liners; planner may consolidate into a single `TestDtos.cs` file (records together) OR split per file. The Phase 4 `TestParentEntity.cs` / `TestChildEntity.cs` pattern uses one-file-per-type; the planner may follow that convention for symmetry.

**D-03 conformance:** All three DTOs implement `IBaseDto` (auto-implemented via positional record syntax — the C# compiler emits `get`-only properties that satisfy `string Name { get; }` etc.). `TestReadDto` adds `Guid Id` BEFORE the IBaseDto fields per CONTEXT D-03 ("Read DTOs include server-side fields").

---

### `tests/BaseApi.Tests/Validation/TestDtoValidator.cs` (NEW)

**Analog:** No in-repo validator yet. Source is RESEARCH Pattern 1 / Example 2 / Example 8.

**Caller-side pattern (Include) — verbatim from RESEARCH Example 8**:
```csharp
using BaseApi.Core.Validation;
using FluentValidation;

namespace BaseApi.Tests.Validation;

public sealed class TestDtoValidator : AbstractValidator<TestUpdateDto>
{
    public TestDtoValidator()
    {
        Include(new BaseDtoValidator<TestUpdateDto>());
        // SC#1 verifies that BaseDtoValidator rules fire WITHOUT restating them here.
    }
}
```

**Sealed pattern** (matches every other test-assembly class in Phase 3-5: `TestEntity`, `TestParentEntity`, `TestErrorDbContext`, `WebAppFactory`, etc. — all `public sealed`). Phase 8's 5 entity validators will follow the SAME `sealed` pattern.

---

### `tests/BaseApi.Tests/Validation/TestEntityMapper.cs` (NEW — Mapperly partial)

**Analog:** No in-repo Mapperly use. Source is RESEARCH Example 6 + Pitfall 2 resolution (Approach A).

**Verbatim shape (per RESEARCH Example 6 — INCLUDING the 5 `[MapperIgnoreTarget]` attributes from CONTEXT D-08 amendment 2026-05-27):**
```csharp
using BaseApi.Core.Entities;
using BaseApi.Core.Mapping;
using BaseApi.Core.Validation;
using Riok.Mapperly.Abstractions;

namespace BaseApi.Tests.Validation;

[Mapper]
public sealed partial class TestEntityMapper :
    IEntityMapper<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>
{
    public partial TestEntity ToEntity(TestCreateDto dto);

    // [MapperIgnoreTarget] suppresses RMG012 for the 5 server-side fields that
    // Phase 8 TUpdate DTOs deliberately do not expose. Phase 8 mappers replicate
    // this exact attribute set for each entity's Update method.
    [MapperIgnoreTarget(nameof(TestEntity.Id))]
    [MapperIgnoreTarget(nameof(TestEntity.CreatedAt))]
    [MapperIgnoreTarget(nameof(TestEntity.UpdatedAt))]
    [MapperIgnoreTarget(nameof(TestEntity.CreatedBy))]
    [MapperIgnoreTarget(nameof(TestEntity.UpdatedBy))]
    public partial void Update(TestUpdateDto dto, TestEntity target);

    public partial TestReadDto ToRead(TestEntity entity);
}
```

**Load-bearing constraint (CONTEXT D-08 amended 2026-05-27 per RESEARCH A-02):** Without the 5 `[MapperIgnoreTarget]` attributes, Mapperly emits RMG012 ("Source member 'Id' was not found for target member 'Id'") which `TreatWarningsAsErrors=true` (Phase 1 D-02) promotes to Error → the partial class WILL NOT COMPILE. The drift detection that catches Phase 8 entity-property additions remains intact because target members NEWLY added to a TestEntity (without TestUpdateDto having a matching source) will still emit RMG012 (no ignore attribute for them).

---

### `tests/BaseApi.Tests/Validation/TestValidationService.cs` (NEW)

**Analog:** `src/BaseApi.Core/Health/StartupCompletionService.cs` (Phase 5 — minimal sealed service with single ctor + single async method) + RESEARCH Example 8

**StartupCompletionService shape** (lines 24-33):
```csharp
public sealed class StartupCompletionService(IStartupGate gate) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        gate.MarkReady();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

**Verbatim TestValidationService shape (per RESEARCH Example 8):**
```csharp
using FluentValidation;

namespace BaseApi.Tests.Validation;

public sealed class TestValidationService
{
    private readonly IValidator<TestUpdateDto> _validator;
    public TestValidationService(IValidator<TestUpdateDto> validator) => _validator = validator;

    public async Task ValidateAsync(TestUpdateDto dto, CancellationToken ct)
    {
        var result = await _validator.ValidateAsync(dto, ct);
        if (!result.IsValid)
            throw new FluentValidation.ValidationException(result.Errors);
    }
}
```

**Phase 4 handler reference** (consumer side — `src/BaseApi.Core/Exceptions/Handlers/ValidationExceptionHandler.cs` lines 40-65): the throw above is consumed by this handler (already wired in Program.cs line 74). Phase 6 does NOT touch the handler; the validator just THROWS the typed exception the handler is waiting for. **Verbatim end-to-end seam:**
```csharp
// 1. TestValidationService throws (Phase 6):
throw new FluentValidation.ValidationException(result.Errors);

// 2. ValidationExceptionHandler catches (Phase 4, ValidationExceptionHandler.cs lines 42-65):
if (exception is not ValidationException vex) return false;
var errors = vex.Errors
    .GroupBy(e => e.PropertyName)
    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
var problem = new ValidationProblemDetails(errors)
{
    Status = StatusCodes.Status400BadRequest,
    Title = "Validation failed",
};
return await _pdSvc.TryWriteAsync(new ProblemDetailsContext { ... });

// 3. AddProblemDetails customizer (Phase 4, Program.cs lines 57-68) appends correlationId + instance.
```

---

### `tests/BaseApi.Tests/Endpoints/TestController.cs` (EDIT — add `[HttpPost("validate")]`)

**Analog (Self):** Phase 4 `TestController` existing endpoints — particularly lines 55-68 (the FluentValidation-throw and model-binding-trigger endpoints already in the file).

**Existing analog endpoint** (TestController.cs lines 55-64, the exact pattern Phase 6 mirrors):
```csharp
[HttpPost("validation-error-via-fv")]
public IActionResult FluentValidationThrows()
{
    var failures = new[]
    {
        new ValidationFailure("Version", "Version must be SemVer."),
        new ValidationFailure("Name", "Name is required."),
    };
    throw new FluentValidation.ValidationException(failures);
}
```

**Phase 6 endpoint shape (per RESEARCH Example 8 + Pitfall 4 — `[FromBody]` is load-bearing):**
```csharp
[HttpPost("validate")]
public async Task<IActionResult> Validate(
    [FromServices] TestValidationService svc,
    [FromBody] BaseApi.Tests.Validation.TestUpdateDto dto,
    CancellationToken ct)
{
    await svc.ValidateAsync(dto, ct);  // throws FluentValidation.ValidationException on invalid
    return Ok();
}
```

**Route choice:** `/test/validate` (not `/validate-test`) per RESEARCH Pattern 5 — consistency with `[Route("test")]` prefix on the existing controller. Confirmed non-colliding with the 8 existing routes (`ok`, `not-found`, `unhandled`, `validation-error-via-fv`, `validation-error-via-modelbinding`, `fk-violation`, `unique-violation`, `concurrency`).

**XML doc update:** The class-level doc-comment endpoint coverage map (TestController.cs lines 22-33) should be EXTENDED with a 9th `<item>` for `POST /test/validate` → 400 (SC#3 / VALID-03 — service-layer FluentValidation explicit ValidateAsync).

---

### `tests/BaseApi.Tests/Validation/ValidationWebAppFactory.cs` (NEW — WAF subclass for Pitfall 5)

**Analog:** `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` nested `HealthDeadPostgresFixture : OtelCollectorFixture` (lines 215-232) + base WAF `tests/BaseApi.Tests/Middleware/WebAppFactory.cs` (lines 29-53)

**Why a subclass:** RESEARCH Pitfall 5 — `Program.cs`'s `AddBaseApiValidation(typeof(Program).Assembly)` only scans `BaseApi.Service.dll`. The `TestDtoValidator` lives in `BaseApi.Tests.dll`, so `IValidator<TestUpdateDto>` resolves to null and `TestValidationService` constructor injection fails with `InvalidOperationException: Unable to resolve service for type 'FluentValidation.IValidator<…TestUpdateDto>'`. Two acceptable resolutions (per RESEARCH Example 8):
1. **Subclass `WebAppFactory`** and re-call `AddBaseApiValidation(typeof(WebAppFactory).Assembly)` in `ConfigureWebHost`. Cleaner; one-file change.
2. **Modify base `WebAppFactory`** to scan both assemblies via `params Assembly[]` directly. Less isolated.

**Pattern from existing HealthDeadPostgresFixture** (HealthEndpointsTests.cs lines 215-232):
```csharp
private sealed class HealthDeadPostgresFixture : OtelCollectorFixture
{
    private readonly string? _priorEnvValue;
    public HealthDeadPostgresFixture()
    {
        _priorEnvValue = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres",
            "Host=localhost;Port=1;Database=postgres;Username=postgres;Password=postgres;Timeout=2");
    }
    public override async ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", _priorEnvValue);
        await base.DisposeAsync();
    }
}
```

**Base WAF pattern** (`WebAppFactory.cs` lines 29-53):
```csharp
public sealed class WebAppFactory : WebApplicationFactory<Program>
{
    public WebAppFactory() : this(null) { }
    public WebAppFactory(string? connectionString) => _connectionString = connectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.AddControllers()
                .AddApplicationPart(typeof(WebAppFactory).Assembly);
            // ...
        });
    }
}
```

**LOAD-BEARING SUBCLASS BLOCKER:** Base `WebAppFactory` (Phase 4) is `public sealed` (line 29). To subclass, Phase 6 MUST either:
- **(A) Unseal `WebAppFactory`** — minimal one-keyword change matching Phase 5's `OtelCollectorFixture` posture (also unsealed for `HealthDeadPostgresFixture` per comment lines 45-48: *"NOT sealed — HealthEndpointsTests defines three nested subclasses"*). This is the precedent. Phase 6 should apply identical reasoning.
- **(B) Add the Tests-assembly scan to base `WebAppFactory`** itself — a single `services.AddBaseApiValidation(typeof(WebAppFactory).Assembly)` call. Affects all 31 Phase 4/5 tests but is harmless (no existing validators in Tests assembly until Phase 6 adds `TestDtoValidator`).

**Recommended Phase 6 ValidationWebAppFactory body** (per RESEARCH Example 8 — assumes path A above):
```csharp
using BaseApi.Core.DependencyInjection;
using BaseApi.Tests.Middleware;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BaseApi.Tests.Validation;

public sealed class ValidationWebAppFactory : WebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            // Pitfall 5: Program.cs only scans BaseApi.Service.dll. Re-scan the
            // Tests assembly so TestDtoValidator is discovered.
            services.AddBaseApiValidation(typeof(ValidationWebAppFactory).Assembly);
            services.AddScoped<TestValidationService>();
        });
    }
}
```

---

### `tests/BaseApi.Tests/Validation/BaseDtoValidatorRuleTests.cs` + `BaseDtoValidatorIncludeTests.cs` (NEW — unit facts)

**Analog:** `tests/BaseApi.Tests/Persistence/DiLifetimeTests.cs` (pure-unit xUnit pattern, no WAF/HTTP)

**Fact-class shape pattern** (DiLifetimeTests.cs lines 8-33):
```csharp
public sealed class DiLifetimeTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public DiLifetimeTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public void Test_DbContext_IsRegisteredScoped_InDI()
    {
        var services = new ServiceCollection();
        // ...
        var provider = services.BuildServiceProvider();
        using var scope1 = provider.CreateScope();
        var a = scope1.ServiceProvider.GetRequiredService<TestDbContext>();
        // ...
        Assert.Same(a, b);
    }
}
```

**Phase 6 BaseDtoValidatorRuleTests adaptation** (no DI needed — direct validator construction; matches `Include`-instance test for VALID-04):
```csharp
using FluentValidation.Results;
using Xunit;

namespace BaseApi.Tests.Validation;

public sealed class BaseDtoValidatorRuleTests
{
    [Fact]
    public void Test_Name_EmptyString_Rejected()
    {
        var validator = new BaseApi.Core.Validation.BaseDtoValidator<TestUpdateDto>();
        var dto = new TestUpdateDto("", "1.0.0", null, "n");
        var result = validator.Validate(dto);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Name");
    }

    [Theory]
    [InlineData("01.0.0")]   // leading zero
    [InlineData("1.0")]      // only two parts
    [InlineData("v1.0.0")]   // prefix
    [InlineData("1.0.0-alpha")] // pre-release tag
    public void Test_Version_NotStrictSemVer_Rejected(string version)
    {
        var validator = new BaseApi.Core.Validation.BaseDtoValidator<TestUpdateDto>();
        var dto = new TestUpdateDto("ok", version, null, "n");
        var result = validator.Validate(dto);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "Version");
    }
}
```

---

### `tests/BaseApi.Tests/Validation/ValidatorAutoDiscoveryTests.cs` + `MapperRegistrationTests.cs` (NEW — DI scan facts)

**Analog:** `tests/BaseApi.Tests/Persistence/DiLifetimeTests.cs` lines 14-32 (verbatim DI build + resolve pattern)

**Pattern (verbatim from DiLifetimeTests.cs lines 14-32 — adapted to Phase 6 DI extensions):**
```csharp
[Fact]
public void Test_AddBaseApiValidation_DiscoversTestDtoValidator()
{
    var services = new ServiceCollection();
    services.AddBaseApiValidation(typeof(TestDtoValidator).Assembly);
    var provider = services.BuildServiceProvider();

    using var scope = provider.CreateScope();
    var validator = scope.ServiceProvider.GetService<IValidator<TestUpdateDto>>();
    Assert.NotNull(validator);
    Assert.IsType<TestDtoValidator>(validator);
}

[Fact]
public void Test_AddBaseApiMapping_RegistersClosedGenericInterface_AsSingleton()
{
    var services = new ServiceCollection();
    services.AddBaseApiMapping(typeof(TestEntityMapper).Assembly);
    var provider = services.BuildServiceProvider();

    var mapper1 = provider.GetService<IEntityMapper<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>>();
    var mapper2 = provider.GetService<IEntityMapper<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>>();
    Assert.NotNull(mapper1);
    Assert.Same(mapper1, mapper2);  // Singleton lifetime per D-15
}
```

---

### `tests/BaseApi.Tests/Validation/ValidationEndpointTests.cs` (NEW — HTTP-roundtrip fact)

**Analog:** `tests/BaseApi.Tests/Middleware/ValidationErrorTests.cs` (Phase 4 — the literal SC#2 fact this mirrors)

**Verbatim Phase 4 pattern** (ValidationErrorTests.cs lines 15-40):
```csharp
[Fact]
public async Task Test_FluentValidation_Exception_Produces_400_WithErrorsMap_AndCorrelationId()
{
    var ct = TestContext.Current.CancellationToken;
    using var factory = new WebAppFactory();
    using var client = factory.CreateClient();

    var response = await client.PostAsync("/test/validation-error-via-fv", null, ct);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

    var body = await response.Content.ReadAsStringAsync(ct);
    using var doc = JsonDocument.Parse(body);

    Assert.True(doc.RootElement.TryGetProperty("errors", out var errors));
    Assert.True(errors.TryGetProperty("Version", out var versionErrors));
    Assert.Contains(versionErrors.EnumerateArray(), e => e.GetString()!.Contains("SemVer"));

    Assert.True(doc.RootElement.TryGetProperty("correlationId", out var corrProp));
    var corr = corrProp.GetString()!;

    Assert.True(response.Headers.TryGetValues("X-Correlation-Id", out var hdr));
    Assert.Equal(hdr!.First(), corr);
}
```

**Phase 6 adaptation:** Replace `WebAppFactory` → `ValidationWebAppFactory`, replace `/test/validation-error-via-fv` → `/test/validate`, POST a real JSON body with bad `Version=""`:
```csharp
[Fact]
public async Task Test_ServiceLayerValidateAsync_BadVersion_Produces400_WithErrorsAndCorrelationId()
{
    var ct = TestContext.Current.CancellationToken;
    using var factory = new ValidationWebAppFactory();
    using var client = factory.CreateClient();

    var json = """{"name":"ok","version":"","description":null,"note":"n"}""";
    using var content = new StringContent(json, Encoding.UTF8, "application/json");
    var response = await client.PostAsync("/test/validate", content, ct);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    Assert.True(doc.RootElement.TryGetProperty("errors", out var errors));
    Assert.True(errors.TryGetProperty("Version", out _));
    Assert.True(doc.RootElement.TryGetProperty("correlationId", out var corrProp));
    Assert.True(response.Headers.TryGetValues("X-Correlation-Id", out var hdr));
    Assert.Equal(hdr!.First(), corrProp.GetString());
}
```

---

### `tests/BaseApi.Tests/Validation/MapperlyCompileTests.cs` + `MapperlyDriftTests.cs` (NEW — build-time + DI assertions)

**Analog:** None in-repo (first Mapperly use). RESEARCH Open Question 2 recommends the planner add a "drift detection negative fact" that temporarily injects a misnamed field and asserts the build fails with RMG012, then reverts.

**Compile-pass fact** (mirrors `MapperRegistrationTests` above — the trivial scaffold compiling under `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` IS the SC#4 verification):
```csharp
[Fact]
public void Test_TestEntityMapper_RoundtripsAllFields()
{
    var mapper = new TestEntityMapper();

    var create = new TestCreateDto("alice", "1.0.0", "hi", "n");
    var entity = mapper.ToEntity(create);
    Assert.Equal("alice", entity.Name);
    Assert.Equal("n", entity.Note);

    var update = new TestUpdateDto("bob", "2.0.0", null, "n2");
    mapper.Update(update, entity);
    Assert.Equal("bob", entity.Name);  // mutated in place

    var read = mapper.ToRead(entity);
    Assert.Equal("bob", read.Name);
}
```

**Drift-detection fact** (RESEARCH Open Question 2 recommendation — optional; planner may prefer to capture the contract in a Plan-level comment rather than an automated test, since flipping the build state mid-suite is complex):
```csharp
// NOTE: drift detection is most reliably exercised by a manual `dotnet build`
// invocation after temporarily adding `public string Foo { get; set; }` to
// TestEntity (without adding it to TestUpdateDto). Expected: exit code 1
// with RMG012 in stderr. Revert before commit.
```

---

## Shared Patterns

### Pattern A: Public-sealed class convention (Tests assembly)

**Source:** All Phase 3-5 test classes — `TestEntity` (sealed), `TestParentEntity` (sealed), `TestErrorDbContext` (sealed), `WebAppFactory` (sealed), `CorrelationIdTests` (sealed), `ValidationErrorTests` (sealed), every `*Tests` class

**Apply to:** ALL Phase 6 test-assembly types EXCEPT:
- `BaseDtoValidator<T>` (in BaseApi.Core) — `public class` (NOT sealed) per CONTEXT D-05 (must be instantiable AND inheritable via `Include`)
- `WebAppFactory` (Phase 4 base) — may need unsealing if `ValidationWebAppFactory` subclasses it (see "LOAD-BEARING SUBCLASS BLOCKER" above)

**Verbatim:**
```csharp
public sealed class TestEntity : BaseEntity { /* ... */ }
public sealed class TestDtoValidator : AbstractValidator<TestUpdateDto> { /* ... */ }
public sealed partial class TestEntityMapper : IEntityMapper<...> { /* ... */ }
public sealed class TestValidationService { /* ... */ }
public sealed class ValidationWebAppFactory : WebAppFactory { /* ... */ }
public sealed class BaseDtoValidatorRuleTests { /* ... */ }
```

### Pattern B: XML doc with `<para>` block + decision-ID anchors

**Source:** `src/BaseApi.Core/Health/IStartupGate.cs` lines 5-19, `src/BaseApi.Core/Persistence/Repositories/IRepository.cs` lines 5-21, `src/BaseApi.Core/Exceptions/Handlers/ValidationExceptionHandler.cs` lines 8-32

**Apply to:** ALL Phase 6 production-code files (IBaseDto.cs, BaseDtoValidator.cs, IEntityMapper.cs, both `*ServiceCollectionExtensions.cs`)

**Verbatim shape** (extract from `IRepository.cs` lines 5-21):
```csharp
/// <summary>
/// [One-sentence purpose statement].
///
/// <para>
/// [Detailed context — reference specific DECISION IDs (D-NN) and REQ-IDs (VALID-NN, HTTP-NN, etc.).]
/// </para>
///
/// <para>
/// <b>Bold-call-out:</b> [Phase-N contract or pitfall reference.]
/// </para>
/// </summary>
```

The RESEARCH Code Examples 1-5 already include this XML doc style — copy verbatim.

### Pattern C: PackageReference with source-gen asset attributes

**Source:** `Directory.Packages.props` lines 18-29 header comment + `tests/BaseApi.Tests/BaseApi.Tests.csproj` lines 58-61 (xunit.runner.visualstudio's `IncludeAssets`/`PrivateAssets` pattern shows the same XML idiom)

**Apply to:** Riok.Mapperly PackageReference added to `BaseApi.Tests.csproj` (D-19) and optionally `BaseApi.Service.csproj` (D-13 planner's call)

**Verbatim:**
```xml
<PackageReference Include="Riok.Mapperly"
                  PrivateAssets="all"
                  ExcludeAssets="runtime" />
```

### Pattern D: CPM contract (no Version= attribute)

**Source:** `tests/BaseApi.Tests/BaseApi.Tests.csproj` line 56-79 — all PackageReference entries omit `Version=`; versions resolve from `Directory.Packages.props`

**Apply to:** ALL Phase 6 csproj additions (Mapperly in Tests, optionally Mapperly in Service, FluentValidation.DependencyInjectionExtensions in Core if not already there)

**Verbatim (positive example):**
```xml
<PackageReference Include="FluentValidation.DependencyInjectionExtensions" />
```

**Verbatim (anti-pattern — DO NOT):**
```xml
<PackageReference Include="FluentValidation.DependencyInjectionExtensions" Version="12.1.1" />
```

### Pattern E: WebApplicationFactory<Program> client + roundtrip assertion

**Source:** `tests/BaseApi.Tests/Middleware/ValidationErrorTests.cs` lines 16-40 + `tests/BaseApi.Tests/Middleware/CorrelationIdTests.cs` lines 16-27

**Apply to:** `ValidationEndpointTests.cs` (SC#3 HTTP roundtrip)

**Verbatim setup pattern:**
```csharp
var ct = TestContext.Current.CancellationToken;
using var factory = new WebAppFactory();      // Phase 6: ValidationWebAppFactory
using var client = factory.CreateClient();
var response = await client.PostAsync("/test/validate", content, ct);

Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

var body = await response.Content.ReadAsStringAsync(ct);
using var doc = JsonDocument.Parse(body);
// ... ProblemDetails assertions
```

### Pattern F: ServiceCollection DI build-and-resolve pattern

**Source:** `tests/BaseApi.Tests/Persistence/DiLifetimeTests.cs` lines 14-32

**Apply to:** `ValidatorAutoDiscoveryTests.cs`, `MapperRegistrationTests.cs`, any Phase 6 fact that does not need an HTTP server

**Verbatim:**
```csharp
var services = new ServiceCollection();
services.AddBaseApiValidation(typeof(TestDtoValidator).Assembly);  // or AddBaseApiMapping
var provider = services.BuildServiceProvider();

using var scope = provider.CreateScope();
var resolved = scope.ServiceProvider.GetService<IValidator<TestUpdateDto>>();
Assert.NotNull(resolved);
```

### Pattern G: Forward-looking comment on Program.cs additions

**Source:** `src/BaseApi.Service/Program.cs` lines 22-26 (Phase 7 absorption comment) + lines 22-26 + lines 22-26's `// Phase 8 will REMOVE this AddHostedService and register MigrationRunner instead.` pattern (line 150)

**Apply to:** The 2 new lines Phase 6 inserts in Program.cs (D-18)

**Verbatim style:**
```csharp
// Phase 6 D-18: validation + mapping seam. Phase 7's AddBaseApi composition
// root will absorb both calls — Phase 6 places them at the precise location
// Phase 7 composes from, so the migration is a mechanical cut-paste.
builder.Services.AddBaseApiValidation(typeof(Program).Assembly);
builder.Services.AddBaseApiMapping(typeof(Program).Assembly);
```

---

## No Analog Found

Files with no close match in the codebase (planner should use RESEARCH.md patterns instead):

| File | Role | Data Flow | Reason | Source-of-Truth |
|------|------|-----------|--------|-----------------|
| `tests/BaseApi.Tests/Validation/TestDtoValidator.cs` | Test scaffold (FluentValidation validator) | `AbstractValidator<T>` with `Include` | No prior in-repo validator — Phase 4 only ships the EXCEPTION HANDLER, not validators | RESEARCH Pattern 1 + Example 2 + Example 8 |
| `tests/BaseApi.Tests/Validation/TestEntityMapper.cs` | Test scaffold (Mapperly partial class) | `[Mapper] partial class : IEntityMapper<...>` with `[MapperIgnoreTarget]` attrs | First Mapperly use in the repo. Phase 1 D-04's deferral landed Mapperly diagnostic promotion to "Phase 6" (here) but no `[Mapper]` partial has ever existed | RESEARCH Pattern 2 + Example 6 + Pitfall 2 resolution (Approach A) |
| `src/BaseApi.Core/DependencyInjection/MappingServiceCollectionExtensions.cs` | Reflection-scan DI extension | Closed-generic interface discovery | Phase 5 health check registrations are single-type adds; no prior closed-generic scan exists | RESEARCH Pattern 4 + Example 5 + Pitfall 7 (GetExportedTypes over GetTypes) |

---

## Metadata

**Analog search scope:** `src/BaseApi.Core/**`, `src/BaseApi.Service/**`, `tests/BaseApi.Tests/**`, repo root config files (`Directory.Build.props`, `Directory.Packages.props`, `*.csproj`).
**Files scanned (via Glob):** 25 source `.cs` files in production code + 22 test source files + 4 config files + 12 `.gitkeep` markers.
**Files actually opened and excerpted:** 18 (listed in the header reads block above).
**Pattern extraction date:** 2026-05-27
**Confidence:** HIGH — every Phase 6 new file has either an exact-shape analog from Phase 3/4/5 OR a RESEARCH Code Example with verbatim source-quality verification.
