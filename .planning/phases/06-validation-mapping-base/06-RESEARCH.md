# Phase 6: Validation + Mapping Base - Research

**Researched:** 2026-05-27
**Domain:** FluentValidation 12.x DI seam + Mapperly 4.3.1 source-gen interface seam (.NET 8)
**Confidence:** HIGH

## Summary

This phase lands four small, tightly-related concerns in `BaseApi.Core`: a marker interface (`IBaseDto`), a reusable validator base class (`BaseDtoValidator<T>`), a generic mapping interface (`IEntityMapper<TEntity, TCreate, TUpdate, TRead>`), and two DI extension methods (`AddBaseApiValidation` + `AddBaseApiMapping`). All packages are pre-pinned in CPM (FluentValidation 12.1.1, Riok.Mapperly 4.3.1). The infrastructure already exists: Phase 4's `ValidationExceptionHandler` already maps `FluentValidation.ValidationException → HTTP 400 + ProblemDetails`, Phase 4's `WebAppFactory` + `TestController` provide the verification scaffold, and the global `TreatWarningsAsErrors=true` baseline from Phase 1 D-02 already promotes warnings to errors.

The single LOAD-BEARING research finding that contradicts CONTEXT.md is the **Mapperly diagnostic code naming**: CONTEXT D-11 specifies `MP0001;MP0011;MP0020;MP0021`, but these codes do **NOT exist** in any version of Mapperly. Mapperly uses the `RMG`-prefix exclusively (RMG001–RMG094 in 4.3.1). The intent of D-11/D-12 (promote unmapped-source + unmapped-target + nullable-in-mapping-context to errors) maps cleanly to **RMG012 + RMG020 + RMG089** (plus RMG007 for "could not map", though that's already Error by default). All four target codes default to **Warning** in Mapperly 4.x — so the global `TreatWarningsAsErrors=true` already promotes them to errors. The explicit `<WarningsAsErrors>` listing CONTEXT D-12 describes as "defense-in-depth" can still be added with the corrected RMG codes; it's still load-bearing as documentation and as protection if a future Mapperly release lowers any of them to Info severity.

The second important finding is that Mapperly 4.x ships with `RequiredMappingStrategy = Both` as the default — meaning both unmapped-source (RMG020) and unmapped-target (RMG012) warnings fire automatically without any explicit `[MapperConfig]` attribute. The SC#4 `[Mapper] partial class` scaffold compiles clean only if the test DTO field shape matches the test entity field shape one-to-one (or explicit `[MapperIgnoreSource]` / `[MapperIgnoreTarget]` attributes are present). This is exactly the catch-DTO-drift mechanism Phase 8 will depend on.

**Primary recommendation:** Substitute RMG codes (RMG012, RMG020, RMG089) for the non-existent MP codes in `Directory.Build.props`. Land BaseDtoValidator + IBaseDto + AddBaseApiValidation in one parallelizable plan, IEntityMapper + AddBaseApiMapping + Directory.Build.props edit in a second parallelizable plan, then verification (TestDto + TestEntityMapper + WebAppFactory `/validate-test` endpoint) in a third sequential plan.

## User Constraints (from CONTEXT.md)

### Locked Decisions

**Domain (Phase Boundary):** Phase 6 lands exactly 5 artifacts in `BaseApi.Core`:
1. `IBaseDto` marker interface (`Name`, `Version`, `Description?`) at `BaseApi.Core/Validation/`
2. `BaseDtoValidator<T> : AbstractValidator<T> where T : IBaseDto` (public class, non-abstract) at `BaseApi.Core/Validation/`
3. `IEntityMapper<TEntity, TCreate, TUpdate, TRead>` (3 methods: `ToEntity`, `Update`, `ToRead`) at `BaseApi.Core/Mapping/` (NEW folder)
4. `AddBaseApiValidation()` + `AddBaseApiMapping()` DI extensions at `BaseApi.Core/DependencyInjection/`
5. `Directory.Build.props` adds explicit `<WarningsAsErrors>` listing the Mapperly target codes

**Out of scope:**
- Concrete entity DTOs / validators / Mapperly partial classes (Phase 8)
- `BaseController` / `BaseService` / `AddBaseApi` composition root (Phase 7)
- Migrations (Phase 8 owns first migration)

**Implementation Decisions (locked, verbatim):**
- **D-01:** `BaseDtoValidator<T>` uses marker-interface generic constraint `where T : IBaseDto`. `Include(new BaseDtoValidator<MyDto>())` compiles against the interface.
- **D-02:** `IBaseDto` exposes exactly 3 read-only getters: `string Name`, `string Version`, `string? Description`. No `Id` / audit fields.
- **D-03:** All 3 DTO flavors (Create/Update/Read) implement `IBaseDto` for symmetry. Positional records satisfy via auto-implemented properties.
- **D-04:** `IBaseDto` + `BaseDtoValidator<T>` land at `src/BaseApi.Core/Validation/` (folder exists with .gitkeep from Phase 1 D-08).
- **D-05:** `BaseDtoValidator<T>` is `public class` (non-abstract, instantiable). Rules:
  - `Name`: `NotEmpty()` + `MaximumLength(200)`
  - `Version`: `NotEmpty()` + `Matches(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")`
  - `Description`: `MaximumLength(2000)` (null is valid)
- **D-06:** `IEntityMapper<TEntity, TCreate, TUpdate, TRead>` exposes EXACTLY 3 methods: `TEntity ToEntity(TCreate dto)`, `void Update(TUpdate dto, TEntity target)`, `TRead ToRead(TEntity entity)`.
- **D-07:** `Update` is mutating (`void` return) — Mapperly fills `target` in-place; EF Core change tracking + xmin detect conflicts.
- **D-08:** Server-side fields (`Id`, `CreatedAt`, `UpdatedAt`, `CreatedBy`, `UpdatedBy`) excluded from Update mapping **by interface contract** — `TUpdate` DTOs do NOT expose them. NOT via per-mapper `[MapperIgnoreTarget]`.
- **D-09:** `IEntityMapper<,,,>` stays scalar-only. M2M list fields handled per-Phase-8-mapper with `[MapperIgnoreSource(nameof(...))]` + Phase 7's `BaseService<...>.SyncJunctionsAsync` virtual.
- **D-10:** `IEntityMapper<,,,>` lives at `BaseApi.Core/Mapping/` (NEW folder — does NOT yet exist).
- **D-11:** `<WarningsAsErrors>$(WarningsAsErrors);MP0001;MP0011;MP0020;MP0021</WarningsAsErrors>` added to `Directory.Build.props` (solution-wide). **SUPERSEDES Phase 1 D-04** (Service-only). ⚠ See A-01 in Assumptions Log — MP codes do NOT exist; must remap to RMG codes.
- **D-12:** Mechanism is explicit `<WarningsAsErrors>` listing codes by name (audit-friendly) — NOT `<RiokMapperlyDiagnosticsAsErrors>`. `TreatWarningsAsErrors=true` is already global; the explicit list is defense-in-depth.
- **D-13:** Mapperly source-gen package convention: `PrivateAssets="all" ExcludeAssets="runtime"` on every consumer csproj that references `Riok.Mapperly`. Phase 6 adds Mapperly to `BaseApi.Tests.csproj`; `BaseApi.Service.csproj` is planner's call. `BaseApi.Core.csproj` does NOT reference Mapperly (pure interface).
- **D-14:** `AddBaseApiValidation(this IServiceCollection services, params Assembly[] assemblies)` at `BaseApi.Core/DependencyInjection/ValidationServiceCollectionExtensions.cs`. Implementation calls `services.AddValidatorsFromAssembly(assembly, ServiceLifetime.Scoped, includeInternalTypes: false)` per assembly.
- **D-15:** `AddBaseApiMapping(this IServiceCollection services, params Assembly[] assemblies)` at `BaseApi.Core/DependencyInjection/MappingServiceCollectionExtensions.cs`. Implementation scans for closed-generic `IEntityMapper<,,,>` implementations and registers each as Singleton.
- **D-16:** Both extensions take `params Assembly[] assemblies` — flexibility for production (single assembly) and tests (Program + Tests).
- **D-17:** SC#3 verification reuses Phase 4 `WebAppFactory` + extends `TestController` with `/validate-test`-style endpoint that calls `IValidator<TestDto>.ValidateAsync` from a thin TestService, throwing `FluentValidation.ValidationException` on failure.
- **D-18:** Insert `AddBaseApiValidation` + `AddBaseApiMapping` calls AFTER `AddHttpContextAccessor` + `AddProblemDetails` and BEFORE `AddControllers` in Program.cs.
- **D-19:** `BaseApi.Tests.csproj` adds `<PackageReference Include="Riok.Mapperly" PrivateAssets="all" ExcludeAssets="runtime" />`.

### Claude's Discretion

- Mapperly partial-class signature for SC#4 scaffold (test entity/DTO field shape — planner picks).
- FluentValidation `ServiceLifetime` for assembly-scan (default Scoped per FluentValidation 12.1.1 — honor unless contradicted; this research confirms default is Scoped per VERIFIED line in Standard Stack table).
- Folder name for DI extensions (`BaseApi.Core/DependencyInjection/` already exists; filenames follow `*ServiceCollectionExtensions.cs` MS convention).
- TestController route for SC#3 endpoint (`/validate-test` or `/test/validate` — must not collide with existing 8 Phase 4 endpoints).
- Reflection-scan implementation specifics (planner picks; ensure no `TypeLoadException` for partially-built types).
- Plan wave structure: 2-plan vs. 3-plan (parallel sub-plans `06-01a` + `06-01b` then verification `06-02`).

### Deferred Ideas (OUT OF SCOPE)

- IEntityMapper lifetime if Phase 8 introduces scoped dependencies (Phase 8 concern).
- Mapperly per-mapper `RequiredMappingStrategy` override (Phase 8).
- Custom validator severity `WithSeverity(Severity.Warning)` (Phase 8).
- Per-mapper Mapperly config knobs (`EnumMappingStrategy` etc., Phase 8).
- `BaseApi.Service.csproj` Mapperly PackageReference (planner's call — defer to Phase 8 OK).
- Mapper auto-discovery edge cases enumeration (planner enumerates in plan).

## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| VALID-01 | FluentValidation 12.x + DependencyInjectionExtensions only; no FluentValidation.AspNetCore | Verified: 12.1.1 pinned in CPM (line 68-69 of Directory.Packages.props); no .AspNetCore variant exists for FV 12 (deprecated/removed in 12.0). |
| VALID-02 | Validators discovered via `AddValidatorsFromAssembly(...)` | Verified API exists with default ServiceLifetime.Scoped, `includeInternalTypes: false` default. See Standard Stack + Code Examples below. |
| VALID-03 | `IValidator<TDto>` invoked explicitly in Service layer via `ValidateAsync`; no MVC auto-validation | Verified — FV 12 no longer auto-wires MVC pipeline; explicit ValidateAsync is the only path. SC#3 verification fact extends TestController to call ValidateAsync from a service. |
| VALID-04 | `BaseDtoValidator<T>` in `BaseApi.Core/Validation/` provides shared rules; concrete validators inherit via `Include(...)` | Verified — `AbstractValidator<T>.Include(IValidator<T> other)` accepts an instance; `Include(new BaseDtoValidator<MyDto>())` is canonical. Pattern documented at https://docs.fluentvalidation.net/en/latest/including-rules.html |
| VALID-05 | `Name`: NotEmpty, MaxLength(200) | Verified API: `RuleFor(x => x.Name).NotEmpty().MaximumLength(200)` |
| VALID-06 | `Version`: NotEmpty, regex `^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$` | Verified API: `.Matches(string pattern)` — uses `Regex.IsMatch` semantics. Use C# verbatim literal `@"..."` to avoid double-escaping. |
| VALID-07 | `Description`: MaxLength(2000) | Verified API: `.MaximumLength(2000)` — null passes (no length check on null per FV docs). |
| HTTP-10 | `IEntityMapper<TEntity, TCreate, TUpdate, TRead>` interface in `BaseApi.Core` defines mapping signatures consumed by `BaseService` | Verified — 3-method interface (`ToEntity`, `Update`, `ToRead`) per D-06; Mapperly `[Mapper] partial class : IEntityMapper<...>` compiles with source-gen filling method bodies. |

## Project Constraints (from CLAUDE.md)

No `CLAUDE.md` exists at the repository root. Project constraints derive from `.planning/PROJECT.md` Key Decisions table and prior-phase CONTEXT.md locks. Relevant active constraints for Phase 6:

- **Phase 1 D-02 (build strictness):** `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` globally — every C# file Phase 6 lands must be warning-clean under Roslyn analyzer rules.
- **Phase 1 D-03 (language strictness):** `<Nullable>enable</Nullable>` + `<AnalysisMode>latest</AnalysisMode>` + `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>` — nullable annotations on every reference type; style violations are compile errors.
- **Phase 1 D-05/D-06 (CPM contract):** No `Version=` attributes on any `<PackageReference>`. Versions resolve from `Directory.Packages.props`.
- **Phase 1 D-07 (source-gen package convention):** Mapperly references MUST carry `PrivateAssets="all" ExcludeAssets="runtime"`.
- **PROJECT.md Key Decision (locked):** "Mapperly (source-gen) over AutoMapper" — runtime reflection forbidden.
- **PROJECT.md Key Decision (locked):** "FluentValidation over DataAnnotations" — `[Required]` / `[StringLength]` are forbidden for DTO validation (model-binding 400s on `[Required]` are the framework default for trivial null checks ONLY; entity validation goes through FluentValidation).
- **VALID-01 (REQUIREMENTS.md):** `FluentValidation.AspNetCore` is FORBIDDEN — it's deprecated and removed in FV 12; explicit `ValidateAsync` is the only modern pattern.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| `IBaseDto` marker interface | API/Backend (BaseApi.Core) | — | Pure-contract type; no runtime behavior; lives where its consumers live. |
| `BaseDtoValidator<T>` (rules) | API/Backend (BaseApi.Core) | — | Validators are Service-layer concerns per VALID-03 (explicit ValidateAsync). Library-shared baseline. |
| `IValidator<T>` runtime invocation | API/Backend (Service layer) | — | VALID-03 locks the call site to the Service tier — never controller, never client. |
| `IEntityMapper<,,,>` interface | API/Backend (BaseApi.Core) | — | Mapping is between the Service layer's wire types (DTOs) and the Persistence layer's entities. Lives in Core for shared definition. |
| Concrete `[Mapper] partial class` impls | API/Backend (BaseApi.Service for prod; BaseApi.Tests for SC#4 scaffold) | Source-gen toolchain (build time) | Source-gen runs at build; runtime is reflection-free. Per-entity mappers in BaseApi.Service Phase 8 + a test-only mapper in BaseApi.Tests for Phase 6 SC#4. |
| DI registration (`AddBaseApiValidation`, `AddBaseApiMapping`) | API/Backend (BaseApi.Core composition seam) | — | Microsoft.Extensions.* convention; folder `BaseApi.Core/DependencyInjection/` already exists per Phase 1 D-08. |
| Wiring in Program.cs | API/Backend (BaseApi.Service entry point) | — | Phase 6 D-18 placement; Phase 7 will absorb both into `AddBaseApi`. |
| MSBuild diagnostic promotion | Build toolchain (Directory.Build.props) | — | `<WarningsAsErrors>` is an MSBuild property; solution-wide via Directory.Build.props per Phase 1 D-01. |

**Sanity check:** All Phase 6 capabilities land in the API/Backend tier. No browser/client tier involvement. Build-toolchain (MSBuild) is a secondary concern only for diagnostic promotion. No cross-tier misassignments detected.

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `FluentValidation` | **12.1.1** | Validator base class (`AbstractValidator<T>`), `RuleFor` chain (`NotEmpty`, `MaximumLength`, `Matches`), `Include`, `IValidator<T>.ValidateAsync` | [VERIFIED: Directory.Packages.props line 68] [CITED: docs.fluentvalidation.net] — the inheritable-validator pattern matches the BaseDtoValidator + concrete-Include design and the `ValidateAsync` Service-layer pattern locked by VALID-03. |
| `FluentValidation.DependencyInjectionExtensions` | **12.1.1** | `AddValidatorsFromAssembly(Assembly, ServiceLifetime, Func<AssemblyScanResult,bool>?, bool)` — assembly-scan discovery for `IValidator<T>` registrations | [VERIFIED: Directory.Packages.props line 69] [CITED: github.com/FluentValidation/FluentValidation/blob/main/src/FluentValidation.DependencyInjectionExtensions/ServiceCollectionExtensions.cs] — default `ServiceLifetime.Scoped` matches the project's PERSIST-15 Scoped lifetime convention. |
| `Riok.Mapperly` | **4.3.1** | Source-gen mapper (`[Mapper] partial class`); zero runtime reflection; AOT-friendly | [VERIFIED: Directory.Packages.props line 65; npm view equivalent confirmed via nuget.org listing] [CITED: mapperly.riok.app] — locked in PROJECT.md Key Decisions over AutoMapper for runtime-reflection-free, AOT-safe mapping. v4.x ships strict mappings by default (`RequiredMappingStrategy = Both`) which is exactly the DTO-vs-entity drift detector D-11/D-12 want to make build-fatal. |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| `Microsoft.Extensions.DependencyInjection.Abstractions` (in `Microsoft.AspNetCore.App`) | net8.0 framework | `IServiceCollection` + `ServiceLifetime` for the DI extensions | Already provided via `<FrameworkReference Include="Microsoft.AspNetCore.App" />` on BaseApi.Core.csproj. No new PackageReference needed. [VERIFIED: BaseApi.Core.csproj line 34] |
| `System.Reflection` (built-in) | net8.0 framework | `Assembly.GetTypes()` + `Type.GetInterfaces()` for IEntityMapper auto-discovery scan | Built into BCL. No package reference. |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `BaseDtoValidator<T> : AbstractValidator<T> where T : IBaseDto` | Abstract record `BaseDto` with positional inheritance | Forces every concrete DTO to inherit from a base record (verbose positional re-syntax for Create/Update/Read variants). Marker interface lets each DTO be a free-standing record or class and still satisfy the constraint. [CONTEXT D-01 explicitly rejects this.] |
| `Include(new BaseDtoValidator<MyDto>())` per-validator | `services.AddScoped<IValidator<MyDto>, MyDtoValidator>()` manual registration | VALID-02 explicitly FORBIDS manual registration. `AddValidatorsFromAssembly` is the only allowed path. |
| Reflection scan in `AddBaseApiMapping` | Source-generated registration helper | Source-gen path would require either a second analyzer or a Mapperly-internal API; the one-time-at-startup reflection scan is the canonical AspNetCore convention (mirrors `AddValidatorsFromAssembly` itself). |
| `void Update(TUpdate dto, TEntity target)` (mutating) | `TEntity Update(TUpdate dto, TEntity existing)` returning new instance | Functional shape forces EF Core re-attach + breaks xmin shadow concurrency token (PERSIST-16). CONTEXT D-07 explicitly rejects. |
| `<WarningsAsErrors>RMG012;RMG020;RMG089</WarningsAsErrors>` (defense-in-depth) | `<RiokMapperlyDiagnosticsAsErrors>true</RiokMapperlyDiagnosticsAsErrors>` MSBuild property | The Mapperly-specific property is less portable across Mapperly versions and not auditable in a code review (one boolean vs. an explicit list). CONTEXT D-12 prefers the explicit list. |

**Installation:**
No new package installs needed for Phase 6 — all packages already pinned in `Directory.Packages.props` since Phase 1 D-05.

Phase 6 PackageReference adds (no `Version=` per CPM):
- `BaseApi.Core.csproj` — **NO Mapperly reference** (D-13). Add nothing for IEntityMapper (pure interface). `FluentValidation` already referenced (Phase 4 line 54).
- `BaseApi.Tests.csproj` — add **one** new entry:
  ```xml
  <PackageReference Include="Riok.Mapperly" PrivateAssets="all" ExcludeAssets="runtime" />
  ```
- `BaseApi.Service.csproj` — planner's discretion (D-13). If added now (forward placement for Phase 8): same syntax as above. If deferred, Phase 8 adds it.
- `Directory.Packages.props` — **NO edits** (CPM pins unchanged).

**Version verification:** Confirmed against `Directory.Packages.props` in the repository working tree. FluentValidation 12.1.1 published 2025-05-22 (latest 12.x line per release notes). Riok.Mapperly 4.3.1 published 2025-04-16 (latest 4.3.x line). Both are current stable releases.

## Architecture Patterns

### System Architecture Diagram

```
                  ┌────────────────────────────────────────────────────┐
                  │  HTTP Request (POST /validate-test, [FromBody] dto)│
                  └─────────────────────┬──────────────────────────────┘
                                        │
                                        ▼
              ┌──────────────────────────────────────────┐
              │ MVC binds JSON to TestDto (record/class) │  <─── TestDto : IBaseDto
              │   ([ApiController] short-circuits if     │
              │   model-binding fails — Phase 4 D-04)    │
              └─────────────────────┬────────────────────┘
                                    │
                                    ▼
       ┌──────────────────────────────────────────────────────┐
       │ Controller delegates to TestService                  │
       │   (D-17: SERVICE layer, not auto-validation)         │
       └─────────────────────┬────────────────────────────────┘
                             │
                             ▼
   ┌───────────────────────────────────────────────────────────────┐
   │ TestService.ValidateAndStoreAsync(TestDto dto, CancellationToken)
   │   - IValidator<TestDto> v;  // ← resolved via DI by AddValidatorsFromAssembly
   │   - ValidationResult r = await v.ValidateAsync(dto, ct);
   │   - if (!r.IsValid) throw new FluentValidation.ValidationException(r.Errors);
   └─────────────────────┬─────────────────────────────────────────┘
                         │  throws FluentValidation.ValidationException
                         ▼
   ┌───────────────────────────────────────────────────────────────┐
   │ Phase 4 ValidationExceptionHandler (IExceptionHandler #2)     │
   │   - vex.Errors → GroupBy(PropertyName).ToDictionary           │
   │   - Status 400 + ValidationProblemDetails + correlationId     │
   │   - Phase 4 AddProblemDetails customizer injects correlationId│
   └─────────────────────┬─────────────────────────────────────────┘
                         │
                         ▼
                 HTTP 400 application/problem+json
                 {
                   "type": "...", "title": "Validation failed",
                   "status": 400, "errors": { "Version": [...] },
                   "correlationId": "{guid}", "instance": "/validate-test"
                 }


   ──────────── Separately, the BaseDtoValidator<T> inheritance chain ────────────

   ┌──────────────────────────────────────────────────────────┐
   │ BaseDtoValidator<T> : AbstractValidator<T>               │   (Phase 6)
   │   where T : IBaseDto                                     │
   │   - RuleFor(x => x.Name).NotEmpty().MaximumLength(200)   │
   │   - RuleFor(x => x.Version).NotEmpty().Matches(@semver)  │
   │   - RuleFor(x => x.Description).MaximumLength(2000)      │
   └────────────────────┬─────────────────────────────────────┘
                        │
                        │ Include(new BaseDtoValidator<MyDto>())
                        ▼
   ┌──────────────────────────────────────────────────────────┐
   │ MyDtoValidator : AbstractValidator<MyDto>                │   (Phase 8)
   │   - Include(new BaseDtoValidator<MyDto>());              │
   │   - RuleFor(x => x.SourceHash).Matches(@"^[a-f0-9]{64}$");
   └──────────────────────────────────────────────────────────┘


   ──────────── IEntityMapper<,,,> + AddBaseApiMapping reflection scan ────────────

   ┌──────────────────────────────────────────────────────────┐
   │ IEntityMapper<TEntity, TCreate, TUpdate, TRead>          │   (Phase 6)
   │   TEntity ToEntity(TCreate dto);                         │
   │   void    Update(TUpdate dto, TEntity target);           │
   │   TRead   ToRead(TEntity entity);                        │
   └────────────────────┬─────────────────────────────────────┘
                        │
                        │ [Mapper] partial class : IEntityMapper<...>
                        ▼  (source-gen fills method bodies at build time)
   ┌──────────────────────────────────────────────────────────┐
   │ TestEntityMapper : IEntityMapper<TestEntity,             │   (Phase 6 SC#4 — Tests project)
   │   TestCreateDto, TestUpdateDto, TestReadDto>             │
   │   public partial TestEntity ToEntity(TestCreateDto);     │
   │   public partial void Update(TestUpdateDto, TestEntity); │
   │   public partial TestReadDto ToRead(TestEntity);         │
   └────────────────────┬─────────────────────────────────────┘
                        │
                        │  AddBaseApiMapping(typeof(Program).Assembly)
                        │  scans for closed-generic IEntityMapper<,,,>,
                        │  registers each as Singleton.
                        ▼
                 Concrete mapper resolvable via DI in
                 Phase 7 BaseService<TEntity, TCreate, TUpdate, TRead>.
```

### Recommended Project Structure

After Phase 6 completes:

```
src/BaseApi.Core/
├── Validation/                        # exists (Phase 1 D-08 .gitkeep)
│   ├── IBaseDto.cs                    # NEW — Phase 6
│   └── BaseDtoValidator.cs            # NEW — Phase 6
├── Mapping/                           # NEW folder — Phase 6
│   └── IEntityMapper.cs               # NEW — Phase 6
├── DependencyInjection/               # exists (Phase 1 D-08 .gitkeep)
│   ├── ValidationServiceCollectionExtensions.cs  # NEW — Phase 6
│   └── MappingServiceCollectionExtensions.cs     # NEW — Phase 6
├── Entities/BaseEntity.cs             # Phase 3
├── Persistence/*                       # Phase 3
├── Middleware/CorrelationIdMiddleware.cs # Phase 4
├── Exceptions/*                       # Phase 4 (includes ValidationExceptionHandler — unchanged)
└── Health/*                           # Phase 5

src/BaseApi.Service/
├── Program.cs                         # 2-line EDIT — Phase 6 (D-18)
└── (no other Phase 6 changes)

tests/BaseApi.Tests/
├── BaseApi.Tests.csproj               # 1-line EDIT — adds Mapperly PackageReference (D-19)
├── Endpoints/TestController.cs        # EDIT — adds /validate-test endpoint (D-17)
├── Middleware/WebAppFactory.cs        # unchanged (reusable scaffold)
└── Validation/                        # NEW folder — Phase 6 verification artifacts
    ├── TestDto.cs                     # NEW — implements IBaseDto
    ├── TestDtoValidator.cs            # NEW — Include(new BaseDtoValidator<TestDto>())
    ├── TestService.cs                 # NEW — calls IValidator<TestDto>.ValidateAsync
    ├── ValidationEndpointTests.cs     # NEW — SC#3 facts
    ├── TestEntity.cs                  # NEW — 5-field test entity (extends BaseEntity)
    ├── TestCreateDto.cs / TestUpdateDto.cs / TestReadDto.cs  # NEW
    ├── TestEntityMapper.cs            # NEW — [Mapper] partial class : IEntityMapper<...> (SC#4)
    └── MapperRegistrationTests.cs     # NEW — auto-discovery DI scan facts

Directory.Build.props                  # 1-line EDIT — adds <WarningsAsErrors> for RMG codes
```

### Pattern 1: BaseDtoValidator<T> with Include in Concrete Validator

**What:** Compose validation rules by inheritance/composition. The base class declares shared rules against the marker interface; concrete validators bring them in with `Include(new BaseDtoValidator<MyDto>())`.

**When to use:** Always when a DTO shares fields with `IBaseDto`. Phase 8's 5 concrete validators all follow this pattern (VALID-20).

**Example:**
```csharp
// Source: docs.fluentvalidation.net/en/latest/including-rules.html
// + CONTEXT D-01/D-02/D-05 (locks marker-interface generic constraint)

namespace BaseApi.Core.Validation;

public interface IBaseDto
{
    string Name { get; }
    string Version { get; }
    string? Description { get; }
}

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

// In Phase 8 (or in test scaffold for Phase 6 SC#3):
public sealed class MyDtoValidator : AbstractValidator<MyDto>
{
    public MyDtoValidator()
    {
        Include(new BaseDtoValidator<MyDto>());
        // Add concrete-entity-specific rules here
    }
}
```

### Pattern 2: Mapperly Partial Class Implementing IEntityMapper<,,,>

**What:** Per-entity mapper as a partial class decorated with `[Mapper]`. The interface provides the contract; Mapperly's source generator fills in the method bodies at build time.

**When to use:** One partial class per concrete entity in Phase 8 (HTTP-11). For Phase 6 SC#4, one trivial scaffold in the Tests project.

**Example:**
```csharp
// Source: mapperly.riok.app/docs/configuration/existing-target/
// + mapperly.riok.app (generic interface implementations)
// + CONTEXT D-06/D-07 (3-method contract; void Update for in-place mutation)

using BaseApi.Core.Mapping;
using BaseApi.Core.Entities;
using Riok.Mapperly.Abstractions;

namespace BaseApi.Tests.Validation;  // Phase 6 SC#4 scaffold

// Test-only entity with the 8 BaseEntity fields + 1 extra scalar.
public sealed class TestEntity : BaseEntity
{
    public string Note { get; set; } = string.Empty;
}

public sealed record TestCreateDto(string Name, string Version, string? Description, string Note) : IBaseDto;
public sealed record TestUpdateDto(string Name, string Version, string? Description, string Note) : IBaseDto;
public sealed record TestReadDto(Guid Id, string Name, string Version, string? Description, string Note) : IBaseDto;

[Mapper]
public sealed partial class TestEntityMapper :
    IEntityMapper<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>
{
    public partial TestEntity ToEntity(TestCreateDto dto);
    public partial void Update(TestUpdateDto dto, TestEntity target);
    public partial TestReadDto ToRead(TestEntity entity);
}
```

**Source-gen output (illustrative — emitted at build time, NOT to be hand-written):**
The generator emits method bodies that assign each TCreate property to the matching TEntity property by name. For the void `Update`, it assigns each TUpdate property to the matching TEntity property on the supplied `target` instance in place.

**Why the 5 server-side fields (`Id`, `CreatedAt`, `UpdatedAt`, `CreatedBy`, `UpdatedBy`) are not warning sources:** Per CONTEXT D-08, `TUpdate` does NOT expose them — Mapperly's property-by-property mapping cannot touch a target property if the source has no member with that name. RMG012 ("source not found for target") would only fire if there were a target member with no matching source. Mapperly only emits RMG012 for required-strategy unmapped members on the TARGET — but for in-place Update, the target `BaseEntity` already has those properties set by the AuditInterceptor; `RequiredMappingStrategy = Both` means Mapperly DOES warn that they're not mapped from source. This means the test scaffold must include `[MapperIgnoreTarget(nameof(TestEntity.Id))]` etc. for the `Update` method, OR rely on RMG012 being warning-not-error. See Pitfall 1 below for the full resolution.

### Pattern 3: AddBaseApiValidation extension

**What:** Wrap `services.AddValidatorsFromAssembly(...)` per assembly with the locked default lifetime and visibility settings.

**When to use:** Once in Program.cs (D-18) and once in Phase 7's `AddBaseApi` composition root (no behavior change at the Phase-7 transition).

**Example:**
```csharp
// Source: github.com/FluentValidation/FluentValidation/blob/main/src/FluentValidation.DependencyInjectionExtensions/ServiceCollectionExtensions.cs
// + CONTEXT D-14/D-16 (params Assembly[]; Scoped + includeInternalTypes:false explicitly)

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

### Pattern 4: AddBaseApiMapping extension (closed-generic reflection scan)

**What:** Scan supplied assemblies for non-abstract, non-interface types that implement at least one closed-generic `IEntityMapper<,,,>`, register each with `services.AddSingleton(closedInterfaceType, concreteType)`.

**When to use:** Once in Program.cs (D-18) and once in Phase 7's `AddBaseApi`. The reflection scan runs at startup only; per-request lookups are O(1) container resolutions.

**Example:**
```csharp
// Source: VERIFIED reflection idiom (cross-checked against Microsoft.Extensions.DependencyInjection conventions)
// + CONTEXT D-15/D-16

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
            // GetExportedTypes() — only public types; safer than GetTypes()
            // which can throw ReflectionTypeLoadException for partially-built assemblies.
            // If internal mapper visibility is required (it isn't per Phase 6 contract),
            // swap to GetTypes() inside a try/catch on ReflectionTypeLoadException.
            foreach (var type in assembly.GetExportedTypes())
            {
                if (type.IsAbstract || type.IsInterface) continue;

                // A type can implement multiple closed-generic IEntityMapper<,,,> if it
                // owns mappers for multiple entities — register each closed-generic
                // interface separately so DI can resolve any of them.
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

**Edge cases handled:**
- Abstract/interface types skipped (would fail Singleton registration).
- Open-generic implementations (e.g., `Foo<T> : IEntityMapper<T, ...>` with unbound `T`) are filtered out because `type.GetInterfaces()` only returns closed-generic constructions for concrete types.
- Multiple closed-generic implementations on one type are each registered.
- `GetExportedTypes()` over `GetTypes()` avoids `ReflectionTypeLoadException` on partially-built assemblies (e.g., during incremental Roslyn builds in IDE scenarios).

### Pattern 5: SC#3 TestController endpoint extension

**What:** Add ONE new endpoint to Phase 4's `TestController` that proves Service-layer `ValidateAsync` produces the Phase 4 HTTP 400 + ProblemDetails response.

**When to use:** Once, in Phase 6 verification plan.

**Example:**
```csharp
// Source: CONTEXT D-17 + Phase 4 TestController pattern (tests/BaseApi.Tests/Endpoints/TestController.cs)

// In BaseApi.Tests/Validation/TestService.cs:
public sealed class TestValidationService
{
    private readonly IValidator<TestDto> _validator;
    public TestValidationService(IValidator<TestDto> validator) => _validator = validator;

    public async Task ValidateAsync(TestDto dto, CancellationToken ct)
    {
        var result = await _validator.ValidateAsync(dto, ct);
        if (!result.IsValid)
            throw new FluentValidation.ValidationException(result.Errors);
    }
}

// Extension to TestController:
[HttpPost("validate")]
public async Task<IActionResult> ValidateTest(
    [FromServices] TestValidationService svc,
    [FromBody] TestDto dto,
    CancellationToken ct)
{
    await svc.ValidateAsync(dto, ct);  // throws FluentValidation.ValidationException
    return Ok();
}
```

**Route choice:** Use `/test/validate` (not `/validate-test`) for consistency with the existing 8 endpoints all under `[Route("test")]` prefix.

### Anti-Patterns to Avoid

- **❌ `FluentValidation.AspNetCore` package:** Deprecated and removed in FV 12. VALID-01 explicitly forbids. The DI extension lives in `FluentValidation.DependencyInjectionExtensions` only.
- **❌ MVC auto-validation via `[ValidatorAttribute]` or pipeline filter:** VALID-03 forbids — explicit `ValidateAsync` in the Service layer is the contract. The Controller is "thin" — it delegates to a Service which calls `ValidateAsync`.
- **❌ Manual `services.AddScoped<IValidator<MyDto>, MyDtoValidator>()`:** VALID-02 forbids. Use `AddValidatorsFromAssembly`.
- **❌ Per-csproj `<WarningsAsErrors>` for Mapperly codes:** CONTEXT D-11 supersedes Phase 1 D-04 — solution-wide via `Directory.Build.props`.
- **❌ `Include(typeof(BaseDtoValidator<MyDto>))` (type-not-instance):** `Include` takes an `IValidator<T>` instance. Use `Include(new BaseDtoValidator<MyDto>())`.
- **❌ Hand-rolling property-copy in mappers:** Mapperly source-gen produces faster, AOT-safe code with build-time diagnostics. Phase 8's 5 concrete mappers MUST be `[Mapper] partial class`.
- **❌ Letting RMG012/RMG020 warnings escape the build:** With `TreatWarningsAsErrors=true` globally (Phase 1 D-02), they already fail the build. The explicit `<WarningsAsErrors>` is defense-in-depth, not the primary mechanism.
- **❌ Mapping server-side fields (`Id`, `CreatedAt`, `UpdatedAt`, `CreatedBy`, `UpdatedBy`) on Update:** D-08 — Phase 8 `TUpdate` DTOs do not expose them, so Mapperly cannot map them. Hand-rolled mappers that copy these fields would break the AuditInterceptor contract.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| DTO field validation rules (NotEmpty, MaxLength, regex) | Custom `[Required]` / `[StringLength]` DataAnnotations attributes or hand-written `if(dto.X.Length > 200)` blocks | `FluentValidation.AbstractValidator<T>` + `RuleFor(...).NotEmpty().MaximumLength().Matches()` | PROJECT.md Key Decision: "FluentValidation over DataAnnotations". FluentValidation supports inheritable validators (Include), composable rules, per-property cascade modes, async validation, and integrates cleanly with the Phase 4 ValidationException → 400 ProblemDetails mapping. |
| Object-to-object property mapping (DTO ↔ Entity) | Hand-written `entity.Name = dto.Name; entity.Version = dto.Version; ...` in services | `Riok.Mapperly` `[Mapper] partial class` source generators | PROJECT.md Key Decision: "Mapperly (source-gen) over AutoMapper". Source-gen produces compile-time-safe property mapping, catches DTO/entity drift at build time (RMG012, RMG020 with `RequiredMappingStrategy=Both` default), is AOT-safe, and runs faster than runtime-reflection mappers. Hand-rolled is brittle (drift silently breaks) and verbose. |
| Validator DI registration (one `AddScoped<IValidator<MyDto>>` per validator) | Repeated `services.AddScoped<IValidator<X>, XValidator>()` lines | `services.AddValidatorsFromAssembly(typeof(Program).Assembly)` | VALID-02 forbids manual registration. The single `AddValidatorsFromAssembly` call discovers every validator in the assembly automatically — zero per-entity boilerplate. Phase 8's 5 entity validators register with one DI line, not five. |
| Entity-mapper DI registration | Repeated `services.AddSingleton<IEntityMapper<SchemaEntity, SchemaCreateDto, SchemaUpdateDto, SchemaReadDto>, SchemaMapper>()` etc. | `services.AddBaseApiMapping(typeof(Program).Assembly)` (Phase 6 ships this; reflection scan over closed-generic `IEntityMapper<,,,>`) | Phase 8's 5 entity mappers register with one DI line. Pattern parallels `AddValidatorsFromAssembly`. |
| Strict-mapping warnings (catch DTO/entity drift) | Custom analyzer / Roslyn rule / unit-test reflection comparing source-target property sets | Mapperly's built-in **RMG012** (source not found for target) + **RMG020** (source not mapped to target) — both default Warning severity in 4.x; promoted to Error by global `TreatWarningsAsErrors=true` | Mapperly 4.x ships strict-mappings-on by default. The build fails the moment DTO and entity drift apart. No custom tooling needed. |
| Regex compilation for SemVer | `Regex.IsMatch(version, pattern, RegexOptions.Compiled)` in code | `RuleFor(x => x.Version).Matches(@"^...$")` | FluentValidation handles regex caching/compilation internally. Use the verbatim literal `@"..."` form to avoid double-escaping the backslashes. |

**Key insight:** Phase 6 is intentionally small because every concern it touches has a battle-tested standard tool in the .NET ecosystem (FluentValidation, Mapperly). The phase is plumbing: connect the seams correctly, then walk away. Resist the urge to add cross-cutting validation helpers, mapping conventions, or DI registration cleverness — Phase 8 has 5 concrete mappers + 5 concrete validators that will exercise the patterns; if a Phase 6 abstraction is over-engineered, it will surface there.

## Common Pitfalls

### Pitfall 1: Phase 6 D-11/D-12 "MP0001/MP0011/MP0020/MP0021" Mapperly diagnostic codes DO NOT EXIST

**What goes wrong:** Adding `<WarningsAsErrors>$(WarningsAsErrors);MP0001;MP0011;MP0020;MP0021</WarningsAsErrors>` to `Directory.Build.props` verbatim from CONTEXT D-11 silently does NOTHING at the diagnostic-promotion level. The `MP` prefix is not used by Mapperly. The phrase "promoted to errors" in SC#4 would be a no-op.

**Why it happens:** CONTEXT D-11 documents the codes as `MP0001/MP0011/MP0020/MP0021` (matching the intent — null-context, cannot-map, unmapped-source, unmapped-target), but Mapperly's actual diagnostic codes are RMG-prefixed (RMG001–RMG094). The MP-prefix is not used by any version of Mapperly. (Verified via [CITED: mapperly.riok.app/docs/configuration/analyzer-diagnostics/] — the canonical analyzer-diagnostics page lists 94 codes, all RMG-prefixed.)

**How to avoid:** Use the actual Mapperly diagnostic codes that match D-11's intent:

| CONTEXT D-11 code (NOT REAL) | Actual Mapperly code | Default severity | Description |
|------------------------------|----------------------|------------------|-------------|
| MP0001 (intent: mapping in nullable context) | **RMG089** | **Info** | "Mapping nullable source to non-nullable target member" |
| MP0011 (intent: cannot map property) | **RMG007** | **Error** (already) | "Could not map member" |
| MP0020 (intent: source not mapped) | **RMG020** | **Warning** | "Source member is not mapped to any target member" |
| MP0021 (intent: target not mapped) | **RMG012** | **Warning** | "Source member was not found for target member" |

**Recommended fix:** Phase 6 D-11/D-12 spirit is "make DTO/entity drift fatal." The minimal accurate replacement is:

```xml
<!-- Directory.Build.props -->
<PropertyGroup>
  <!-- Existing: Phase 1 D-02 -->
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>

  <!-- Phase 6 D-11/D-12 — defense in depth: even if a future Mapperly version
       lowers RMG089/RMG020/RMG012 to Info severity, the explicit list still
       promotes them. RMG007 is already Error by default but listing it makes
       the intent explicit. -->
  <WarningsAsErrors>$(WarningsAsErrors);RMG007;RMG012;RMG020;RMG089</WarningsAsErrors>
</PropertyGroup>
```

**Note on RMG089 (info-severity diagnostic):** Because RMG089 defaults to **Info** (not Warning), `TreatWarningsAsErrors=true` alone does NOT promote it. The explicit `<WarningsAsErrors>` listing IS load-bearing for RMG089 specifically — this is the strongest case for the explicit listing pattern D-12 anticipates.

**Warning signs:** A trivial `[Mapper] partial class` with deliberate DTO/entity field drift (e.g., add an extra field on TUpdate not on TEntity) builds successfully under `TreatWarningsAsErrors=true` — that would prove the diagnostic codes are not being promoted, and the planner needs to investigate which RMG code Mapperly actually emits for the test scenario.

**This finding requires user confirmation via `/gsd-discuss-phase` amendment before the planner uses the MP codes. See A-01 in Assumptions Log.**

### Pitfall 2: `RequiredMappingStrategy = Both` default emits RMG020 for server-side fields on the Update method

**What goes wrong:** When the SC#4 scaffold declares `public partial void Update(TestUpdateDto dto, TestEntity target)`, Mapperly's default `RequiredMappingStrategy = Both` checks that every property on BOTH the source AND the target is mapped. Since `TestEntity : BaseEntity` has 8 BaseEntity properties (`Id`, `Name`, `Version`, `CreatedAt`, `UpdatedAt`, `CreatedBy`, `UpdatedBy`, `Description`) but `TestUpdateDto` (per D-08) only has 3 (`Name`, `Version`, `Description`) + 1 test field (`Note`), Mapperly emits **RMG020** ("source member is not mapped to any target member") for source `Note` against target... wait, that's mapped. The actual emission is **RMG012** ("Source member was not found for target member") for TARGET members `Id`, `CreatedAt`, `UpdatedAt`, `CreatedBy`, `UpdatedBy` — none of which exist on `TestUpdateDto`. With RMG012 promoted to Error, the SC#4 partial class WILL NOT COMPILE.

**Why it happens:** D-08's "exclude server-side fields by contract" works for the spec but doesn't satisfy Mapperly's strict-mappings check. The compiler needs to be told explicitly that these targets are intentionally unmapped — either by `[MapperIgnoreTarget(nameof(...))]` per field OR by setting `[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Source)]` on the partial class (which only requires source members to be mapped, leaving target members unchecked).

**How to avoid:** Two acceptable approaches; pick one in Phase 6 SC#4 scaffold (and apply consistently to all 5 Phase 8 mappers):

**Approach A — Per-method `[MapperIgnoreTarget]`** (matches D-09 pattern for M2M ignores):
```csharp
[Mapper]
public sealed partial class TestEntityMapper :
    IEntityMapper<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>
{
    public partial TestEntity ToEntity(TestCreateDto dto);

    [MapperIgnoreTarget(nameof(TestEntity.Id))]
    [MapperIgnoreTarget(nameof(TestEntity.CreatedAt))]
    [MapperIgnoreTarget(nameof(TestEntity.UpdatedAt))]
    [MapperIgnoreTarget(nameof(TestEntity.CreatedBy))]
    [MapperIgnoreTarget(nameof(TestEntity.UpdatedBy))]
    public partial void Update(TestUpdateDto dto, TestEntity target);

    public partial TestReadDto ToRead(TestEntity entity);
}
```

**Approach B — Class-level `RequiredMappingStrategy.Source`** (less explicit, fewer attributes):
```csharp
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Source)]
public sealed partial class TestEntityMapper : ...
```

**Recommendation:** **Approach A** — explicit `[MapperIgnoreTarget]` attributes preserve drift detection for new TARGET members (i.e., if a developer adds a new property to `TestEntity` and forgets to wire it in `TestUpdateDto`, RMG012 still fires). Approach B silently hides all target-side drift, which weakens the SC#4 guarantee. CONTEXT D-08's "by contract" wording aligns more with A.

**Cross-reference:** Phase 8 will need to apply this pattern to all 5 entity mappers for their `Update` methods. The 5 attributes per mapper become boilerplate; consider a code-comment in the SC#4 scaffold establishing the convention.

**Warning signs:** SC#4 build fails with `error RMG012: Source member 'Id' was not found for target member ...` — that's the symptom. Fix is to add the `[MapperIgnoreTarget]` attribute(s).

### Pitfall 3: FluentValidation `Matches` with `\d` and `[0-9]` — escape vs. verbatim

**What goes wrong:** Writing `.Matches("^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)$")` (double-escaped C# regular string literal) produces an unreadable wall of backslashes. Using `\d` in a regular `"..."` literal accidentally triggers a C# string-escape error (unrecognized escape sequence under analyzers).

**Why it happens:** `\d` is not a valid C# string escape (it's a regex escape). In a regular `"..."` literal, `\d` either compiles (Roslyn forgives it) or warns/errors under analyzer rules. The verbatim `@"..."` literal treats backslashes literally, so `\d` passes through cleanly to the regex engine.

**How to avoid:** Always use the C# verbatim literal `@"..."` for regex patterns:

```csharp
RuleFor(x => x.Version)
    .NotEmpty()
    .Matches(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$");
```

**Verification:** This is the EXACT pattern locked in CONTEXT D-05 and VALID-06. The `@` prefix is mandatory under `TreatWarningsAsErrors=true` + `AnalysisMode=latest`.

**Warning signs:** Compiler warning `CS1009 Unrecognized escape sequence` — caused by `\d` in non-verbatim literal.

### Pitfall 4: Forgetting `[FromBody]` on the TestController endpoint causes 400 before validation runs

**What goes wrong:** If the SC#3 endpoint signature is `public async Task<IActionResult> ValidateTest(TestDto dto, ...)` without `[FromBody]`, MVC binds `dto` from query/route parameters, not the request body. The validator never sees the bad body; the test asserting "Version regex rejection" returns either 404 (route mismatch) or 400 (model-binding null check) — but NOT the FluentValidation error message expected by SC#3.

**Why it happens:** `[ApiController]` inference rules infer `[FromBody]` only for complex types via reflection rules. Mixing `[FromBody]` complex types with primitive `[FromQuery]`-able parameters can produce unintended binding sources. Being explicit is safer.

**How to avoid:** Always annotate the DTO parameter with `[FromBody]`:

```csharp
[HttpPost("validate")]
public async Task<IActionResult> ValidateTest(
    [FromServices] TestValidationService svc,
    [FromBody] TestDto dto,
    CancellationToken ct) { ... }
```

**Warning signs:** SC#3 test fails with HTTP 400 + `ValidationProblemDetails` that lists "dto field is required" — that's the model-binding 400, not the FluentValidation 400. Look at the `errors` map keys; if it says `dto` or no `errors` at all, it's a binding-source mistake.

### Pitfall 5: WebAppFactory test assembly does NOT auto-discover validators in BaseApi.Tests

**What goes wrong:** Calling `services.AddBaseApiValidation(typeof(Program).Assembly)` in `Program.cs` only scans `BaseApi.Service.dll`. The Phase 6 test scaffold places `TestDtoValidator` in `BaseApi.Tests.dll` — that assembly is NOT scanned by the production `Program.cs` line. `IValidator<TestDto>` resolves to null and the `TestValidationService` constructor injection fails.

**Why it happens:** `AddValidatorsFromAssembly` only scans the assembly passed in. The test scaffold runs `WebApplicationFactory<Program>` which bootstraps the actual `Program.cs` — but the test validator lives in the Tests assembly that `Program.cs` doesn't know about.

**How to avoid:** Pass BOTH assemblies in the `WebAppFactory.ConfigureWebHost` override:

```csharp
// In tests/BaseApi.Tests/Middleware/WebAppFactory.cs (or a Phase 6 subclass)
builder.ConfigureTestServices(services =>
{
    services.AddControllers()
        .AddApplicationPart(typeof(WebAppFactory).Assembly);

    // Phase 6: re-run the validator scan with the Tests assembly so
    // TestDtoValidator is registered alongside Program-assembly validators.
    services.AddBaseApiValidation(typeof(WebAppFactory).Assembly);
});
```

Since `params Assembly[]` is the signature (D-16), this can also be solved by passing both assemblies in a single call inside Program.cs:

```csharp
// Alternative: a Phase 6 WebAppFactory subclass overrides services to re-call with the test assembly.
services.AddBaseApiValidation(typeof(Program).Assembly, typeof(WebAppFactory).Assembly);
```

**Warning signs:** Test fails with `InvalidOperationException: Unable to resolve service for type 'FluentValidation.IValidator<...TestDto>'` — that's the missing registration. Fix is to add the test assembly to the scan.

### Pitfall 6: Mapperly source-gen requires the analyzer to be loaded — `PrivateAssets="all" ExcludeAssets="runtime"` only

**What goes wrong:** Adding `<PackageReference Include="Riok.Mapperly" />` to `BaseApi.Tests.csproj` WITHOUT the asset attributes either (a) leaks the analyzer to consumers of BaseApi.Tests (no consumers, so OK in this case), or (b) loads a runtime DLL that isn't used (negligible but wrong).

**Why it happens:** Mapperly is a source generator + analyzer. The analyzer must be loaded at build time; the runtime DLL is irrelevant. Forgetting either attribute is the Phase-1-flagged "frequent footgun" per Directory.Packages.props line 18-29 header comment.

**How to avoid:** Use the EXACT attribute pattern documented in `Directory.Packages.props` lines 24-26:

```xml
<PackageReference Include="Riok.Mapperly" PrivateAssets="all" ExcludeAssets="runtime" />
```

**Verification (xml-attribute syntax):** Both attributes are on the same element opener, separated by spaces. No closing tag needed (self-closing `/>`). No child `<PrivateAssets>` element required when using the attribute form.

**Warning signs:** Build succeeds but `Riok.Mapperly.dll` appears in the test project's output `bin/Debug/net8.0/` directory — that's the missing `ExcludeAssets="runtime"`. Fix: add the attribute and rebuild after `dotnet clean`.

### Pitfall 7: Reflection-scan `GetTypes()` vs `GetExportedTypes()` for the mapper auto-discovery

**What goes wrong:** Using `assembly.GetTypes()` in `AddBaseApiMapping` can throw `ReflectionTypeLoadException` if the assembly contains a type whose dependencies are not loadable (e.g., a Mapperly-generated type that depends on a partial-build symbol during incremental builds in the IDE).

**Why it happens:** `GetTypes()` returns ALL types including non-public, generic-open, and types with unresolved dependencies. In a healthy compile-and-run scenario this is fine, but Roslyn's incremental compilation in the IDE can leave the assembly in a state where some types throw on load.

**How to avoid:** Use `GetExportedTypes()` instead. It returns only public types, which:
- Skips the internal source-gen scaffolding types Mapperly emits.
- Avoids the IDE incremental-build edge case.
- Matches the Phase 6 contract that mappers are public (a concrete mapper unused by external assemblies but registered via DI in Program.cs has no reason to be internal).

If internal mappers ARE needed in Phase 8 (D-13 says they aren't), wrap `GetTypes()` in a try/catch:

```csharp
Type[] types;
try
{
    types = assembly.GetTypes();
}
catch (ReflectionTypeLoadException ex)
{
    // Filter out types that failed to load — log the failures if a logger is available.
    types = ex.Types.Where(t => t is not null).ToArray()!;
}
```

**Warning signs:** Tests pass locally, build fails on CI or after `dotnet clean` with `System.Reflection.ReflectionTypeLoadException`. Switch to `GetExportedTypes()`.

### Pitfall 8: `Include(new BaseDtoValidator<T>())` allocates a validator instance per parent-validator-instantiation

**What goes wrong:** Every time `MyDtoValidator` is instantiated (per Scoped lifetime, per request), a new `BaseDtoValidator<MyDto>` is allocated and immediately discarded (its rules are absorbed into MyDtoValidator's rule set at construction). For 5 entity validators × 1000 requests/sec, that's 5000 unnecessary allocations/sec.

**Why it happens:** The Phase 6 D-05 pattern says `Include(new BaseDtoValidator<MyDto>())` because that's the canonical FluentValidation pattern from the official docs. The alternative is to make BaseDtoValidator stateless (which it is) and reuse a static instance, but FluentValidation's design doesn't anticipate that.

**How to avoid:** Don't. The allocation is small (a few bytes for the validator + rule list), the rules are absorbed once at construction, and Phase 6's verification doesn't measure allocation rate. This is a non-issue for v1.

If profiling in v2 ever flags this, the fix is to cache `new BaseDtoValidator<T>()` per closed `T` in a static dictionary — but that's a v2 optimization, not Phase 6 work.

**Warning signs:** None at Phase 6 scope. Performance profiling in Phase 8 or v2 might surface this; ignore until then.

## Code Examples

Verified patterns from official sources. All snippets are AOT-safe under .NET 8 + `Nullable=enable` + `TreatWarningsAsErrors=true`.

### Example 1: `IBaseDto` marker interface

```csharp
// Source: CONTEXT D-02; PROJECT.md ENTITY-01 field-name parity
// File: src/BaseApi.Core/Validation/IBaseDto.cs

namespace BaseApi.Core.Validation;

/// <summary>
/// Marker interface exposing the three narrative fields shared by every domain DTO
/// (Create, Update, Read) — used as the generic constraint on
/// <see cref="BaseDtoValidator{T}"/> so shared validation rules target by member name.
///
/// <para>
/// Mirrors <c>BaseEntity</c> (Phase 3) field nullability:
/// <c>Name</c> + <c>Version</c> are non-null with empty-string default;
/// <c>Description</c> is nullable.
/// </para>
///
/// <para>
/// Server-side fields (<c>Id</c>, <c>CreatedAt</c>, <c>UpdatedAt</c>, <c>CreatedBy</c>,
/// <c>UpdatedBy</c>) are NOT on this interface — they are owned by
/// <c>AuditInterceptor</c> per HTTP-05 and never appear on inbound DTOs.
/// </para>
/// </summary>
public interface IBaseDto
{
    string Name { get; }
    string Version { get; }
    string? Description { get; }
}
```

### Example 2: `BaseDtoValidator<T>` with verbatim SemVer regex

```csharp
// Source: docs.fluentvalidation.net/en/latest/including-rules.html
//       + CONTEXT D-05 (rule set verbatim)
// File: src/BaseApi.Core/Validation/BaseDtoValidator.cs

using FluentValidation;

namespace BaseApi.Core.Validation;

/// <summary>
/// Reusable validator providing the <see cref="IBaseDto"/> shared field rules.
/// Concrete validators absorb these by calling
/// <c>Include(new BaseDtoValidator&lt;MyDto&gt;())</c> in their constructor.
///
/// <para>Public, non-abstract — instantiable from the <c>Include</c> call site per SC#1.</para>
/// </summary>
public class BaseDtoValidator<T> : AbstractValidator<T>
    where T : IBaseDto
{
    public BaseDtoValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(200);

        // Strict SemVer numeric triple — no leading zeros, no pre-release tag.
        // Verbatim literal @"..." avoids C# escape ambiguity on \d and \..
        RuleFor(x => x.Version)
            .NotEmpty()
            .Matches(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$");

        // Description: nullable — FluentValidation MaximumLength treats null as valid.
        RuleFor(x => x.Description)
            .MaximumLength(2000);
    }
}
```

### Example 3: `IEntityMapper<,,,>` 3-method interface

```csharp
// Source: CONTEXT D-06/D-07/D-08; HTTP-10
// File: src/BaseApi.Core/Mapping/IEntityMapper.cs  (NEW FOLDER)

namespace BaseApi.Core.Mapping;

/// <summary>
/// Generic 3-method mapping contract consumed by <c>BaseService&lt;...&gt;</c> (Phase 7)
/// and implemented per-entity by a Mapperly <c>[Mapper] partial class</c> (Phase 8).
///
/// <para>
/// <list type="bullet">
///   <item><see cref="ToEntity"/>: build a NEW entity from the Create DTO; AuditInterceptor stamps audit fields on SaveChanges.</item>
///   <item><see cref="Update"/>: MUTATE the existing target in place; EF Core change tracking + Phase 3 <c>xmin</c> detect conflicts.</item>
///   <item><see cref="ToRead"/>: project an entity to the Read DTO for HTTP responses (includes server-side fields).</item>
/// </list>
/// </para>
///
/// <para>
/// Server-side fields (<c>Id</c>, audit fields) are excluded from <see cref="Update"/> mapping
/// by interface contract: <typeparamref name="TUpdate"/> DTOs do NOT expose them
/// (HTTP-06). Mapperly cannot map what isn't on the source — compile-time prevention
/// of client-overwrites-audit-field bugs.
/// </para>
/// </summary>
public interface IEntityMapper<TEntity, TCreate, TUpdate, TRead>
{
    TEntity ToEntity(TCreate dto);
    void    Update(TUpdate dto, TEntity target);
    TRead   ToRead(TEntity entity);
}
```

### Example 4: `AddBaseApiValidation` extension

```csharp
// Source: github.com/FluentValidation/FluentValidation/blob/main/src/FluentValidation.DependencyInjectionExtensions/ServiceCollectionExtensions.cs
//       + CONTEXT D-14/D-16
// File: src/BaseApi.Core/DependencyInjection/ValidationServiceCollectionExtensions.cs

using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace BaseApi.Core.DependencyInjection;

/// <summary>
/// Wires FluentValidation 12.x validator auto-discovery via assembly scan.
/// Default lifetime: <see cref="ServiceLifetime.Scoped"/> — matches the request-scoped
/// pattern locked by PERSIST-15.
///
/// <para>
/// <c>params Assembly[]</c> signature supports both production wiring
/// (<c>AddBaseApiValidation(typeof(Program).Assembly)</c>) and test wiring
/// (<c>AddBaseApiValidation(typeof(Program).Assembly, typeof(WebAppFactory).Assembly)</c>).
/// </para>
/// </summary>
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

### Example 5: `AddBaseApiMapping` extension with closed-generic reflection scan

```csharp
// Source: CONTEXT D-15/D-16; reflection-scan idiom verified against Microsoft.Extensions.DependencyInjection conventions
// File: src/BaseApi.Core/DependencyInjection/MappingServiceCollectionExtensions.cs

using System.Reflection;
using BaseApi.Core.Mapping;
using Microsoft.Extensions.DependencyInjection;

namespace BaseApi.Core.DependencyInjection;

/// <summary>
/// Auto-discovers all closed-generic <see cref="IEntityMapper{TEntity,TCreate,TUpdate,TRead}"/>
/// implementations in the supplied assemblies and registers each as Singleton.
///
/// <para>Mappers are stateless (Mapperly source-gen emits pure functions) — Singleton is correct.</para>
///
/// <para>
/// Uses <c>GetExportedTypes()</c> (public types only) to avoid
/// <see cref="System.Reflection.ReflectionTypeLoadException"/> on partially-built assemblies
/// (Roslyn incremental builds in IDE scenarios).
/// </para>
/// </summary>
public static class MappingServiceCollectionExtensions
{
    public static IServiceCollection AddBaseApiMapping(
        this IServiceCollection services,
        params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
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

### Example 6: Trivial `[Mapper] partial class` for SC#4 verification

```csharp
// Source: mapperly.riok.app/docs/configuration/existing-target/
//       + CONTEXT D-08 (server-side fields by contract)
//       + Pitfall 2 resolution (Approach A: [MapperIgnoreTarget] for audit fields)
// File: tests/BaseApi.Tests/Validation/TestEntityMapper.cs

using BaseApi.Core.Entities;
using BaseApi.Core.Mapping;
using BaseApi.Core.Validation;
using Riok.Mapperly.Abstractions;

namespace BaseApi.Tests.Validation;

// Test entity with the 8 BaseEntity fields + 1 extra scalar.
public sealed class TestEntity : BaseEntity
{
    public string Note { get; set; } = string.Empty;
}

// 3 DTOs — all implement IBaseDto for symmetry (D-03).
public sealed record TestCreateDto(string Name, string Version, string? Description, string Note) : IBaseDto;
public sealed record TestUpdateDto(string Name, string Version, string? Description, string Note) : IBaseDto;
public sealed record TestReadDto(Guid Id, string Name, string Version, string? Description, string Note) : IBaseDto;

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

### Example 7: Program.cs 2-line insertion (D-18)

```csharp
// File: src/BaseApi.Service/Program.cs (existing line 50 = AddHttpContextAccessor;
// existing line 57 = AddProblemDetails; existing line 153 = AddControllers)
// Phase 6 inserts AFTER AddProblemDetails (line 68 closes) and BEFORE AddExceptionHandler<...>
// OR before AddControllers — both are valid; CONTEXT D-18 says "AFTER AddProblemDetails and
// BEFORE AddControllers" which leaves Phase 4's AddExceptionHandler chain in between.

using BaseApi.Core.DependencyInjection;  // NEW using

// ...existing AddProblemDetails(...)...

// Phase 6 D-18: validation + mapping seam — wired here so Phase 7 AddBaseApi
// can absorb both calls with zero behavior change (mechanical cut-paste).
builder.Services.AddBaseApiValidation(typeof(Program).Assembly);
builder.Services.AddBaseApiMapping(typeof(Program).Assembly);

// ...existing AddExceptionHandler<...> chain unchanged...
// ...existing AddControllers() unchanged...
```

### Example 8: SC#3 verification — TestService + endpoint + WAF assembly registration

```csharp
// Source: CONTEXT D-17 + Pitfall 4 + Pitfall 5
// Files: tests/BaseApi.Tests/Validation/* + Endpoints/TestController.cs edit

// 1. TestDto (implements IBaseDto) — already in TestEntityMapper.cs above.

// 2. TestDtoValidator with Include(new BaseDtoValidator<TestDto>())
// File: tests/BaseApi.Tests/Validation/TestDtoValidator.cs
using BaseApi.Core.Validation;
using FluentValidation;

namespace BaseApi.Tests.Validation;

public sealed class TestDtoValidator : AbstractValidator<TestUpdateDto>
{
    public TestDtoValidator()
    {
        Include(new BaseDtoValidator<TestUpdateDto>());
        // SC#3 verifies that BaseDtoValidator rules fire WITHOUT restating them here.
    }
}

// 3. Thin service that calls ValidateAsync from the Service layer (VALID-03).
// File: tests/BaseApi.Tests/Validation/TestValidationService.cs
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

// 4. Endpoint addition to existing TestController.
// File: tests/BaseApi.Tests/Endpoints/TestController.cs (APPEND)
[HttpPost("validate")]
public async Task<IActionResult> Validate(
    [FromServices] TestValidationService svc,
    [FromBody] TestUpdateDto dto,
    CancellationToken ct)
{
    await svc.ValidateAsync(dto, ct);  // throws on invalid
    return Ok();
}

// 5. WAF wiring (Pitfall 5).
// File: tests/BaseApi.Tests/Validation/ValidationWebAppFactory.cs (NEW subclass)
public sealed class ValidationWebAppFactory : WebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            // Re-scan the Tests assembly so TestDtoValidator is discovered.
            services.AddBaseApiValidation(typeof(ValidationWebAppFactory).Assembly);
            services.AddScoped<TestValidationService>();
        });
    }
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `FluentValidation.AspNetCore` package for auto-validation | `FluentValidation.DependencyInjectionExtensions` + explicit `ValidateAsync` in Service layer | FluentValidation 11.0 deprecated, 12.0 removed (2025) | Per VALID-01: explicit service-layer validation; no MVC pipeline magic. Better testability. |
| `services.AddValidatorsFromAssembly()` with default `ServiceLifetime.Transient` (some older snippets) | Default is `ServiceLifetime.Scoped` since FV 9.x | FV 9.x (2020-09) | Validators are now request-scoped by default; safe to inject scoped dependencies. |
| `[Required]` / `[StringLength]` DataAnnotations attributes | FluentValidation `RuleFor(...).NotEmpty().MaximumLength()` | PROJECT.md Key Decision (project-level) | Composable rules, inheritance via Include, async validation, no attribute-soup on DTOs. |
| `AutoMapper` (runtime reflection) | `Riok.Mapperly` (source-gen, AOT-safe) | Mapperly 1.0 (2022); AutoMapper commercial-license shift (2024) | PROJECT.md Key Decision. Faster, smaller, compile-time diagnostics catch DTO/entity drift. |
| Mapperly `RequiredMappingStrategy.None` (lax mappings, no warnings for unmapped) default | Mapperly `RequiredMappingStrategy.Both` (strict mappings, RMG020+RMG012 emit at Warning) default | Mapperly 4.0 (2024-09) | DTO/entity drift is now build-fatal under `TreatWarningsAsErrors=true`. The "compile-time drift detection" Phase 6 SC#4 expects is ON by default in 4.x. |
| `<RiokMapperlyDiagnosticsAsErrors>true</RiokMapperlyDiagnosticsAsErrors>` (Mapperly-specific) | Explicit `<WarningsAsErrors>RMG012;RMG020;...;</WarningsAsErrors>` (general MSBuild) | CONTEXT D-12 (project-level) | Explicit list is portable across Mapperly versions and auditable in code review. |

**Deprecated/outdated:**
- `FluentValidation.AspNetCore` — removed in FV 12. Do not search for `[ValidatorAttribute]` patterns in docs (12+ deprecated).
- `AbstractValidator.CascadeMode` property — removed in FV 12 (per official upgrade guide). Use `DefaultClassLevelCascadeMode` + `DefaultRuleLevelCascadeMode` instead. Phase 6 doesn't use cascade modes, so this is informational only.
- Mapperly v3.x lax-mapping default — anything documented before September 2024 assumes `RequiredMappingStrategy.None`. v4 changed the default. Confirm version of any blog post or snippet against current docs.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A-01 | The Mapperly diagnostic codes `MP0001/MP0011/MP0020/MP0021` in CONTEXT D-11/D-12 are a documentation error — these codes do not exist in Mapperly. The intent (null-context, cannot-map, unmapped-source, unmapped-target) maps to **RMG089 (Info) / RMG007 (Error) / RMG020 (Warning) / RMG012 (Warning)** respectively. [VERIFIED: mapperly.riok.app/docs/configuration/analyzer-diagnostics/ — 94 codes, all RMG-prefixed; no MP code exists.] | Pitfall 1 + Standard Stack tables + Code Example 7 | HIGH — if the planner uses the verbatim MP codes from CONTEXT D-11 in `Directory.Build.props`, the `<WarningsAsErrors>` listing silently does nothing. SC#4 would still pass by accident (under `TreatWarningsAsErrors=true`, RMG020/RMG012 already promote because they default Warning), but RMG089 would NOT promote (defaults Info), and the listing's defense-in-depth purpose is defeated. Strongly recommend `/gsd-discuss-phase 6 --amend` to update D-11 to RMG codes BEFORE planning starts. |
| A-02 | Phase 6 SC#4 requires `[MapperIgnoreTarget]` attributes on the `Update` method for the 5 server-side BaseEntity fields, because Mapperly 4.x defaults `RequiredMappingStrategy = Both` and emits RMG012 (under `TreatWarningsAsErrors=true`, fatal) for each unmapped target. This is OBLIQUE to CONTEXT D-08's "by contract" wording, which says `TUpdate` simply doesn't expose those fields — but Mapperly's check is on the TARGET (entity), not source (DTO). [VERIFIED: WebFetch of mapperly.riok.app/docs/breaking-changes/4-0/ confirms `RequiredMappingStrategy.Both` default; Pitfall 2 explains the build-fail mechanism.] | Pitfall 2 + Code Example 6 + IEntityMapper interface docs | MEDIUM — without the `[MapperIgnoreTarget]` attributes, the SC#4 partial class WILL NOT COMPILE (RMG012 emits Warning → Error promotion). Planner must include the 5 attributes in the SC#4 scaffold and recommend the same pattern for Phase 8's 5 entity mappers. |
| A-03 | `Include(new BaseDtoValidator<MyDto>())` is the canonical FluentValidation 12.x pattern; calling it from each concrete validator's constructor absorbs the base rules into the concrete validator's rule set at construction time. The base validator instance is discarded after Include returns; no shared state. [CITED: docs.fluentvalidation.net/en/latest/including-rules.html — example uses `Include(new PersonAgeValidator())` per-constructor.] [ASSUMED: behavior unchanged in FV 12 — FV 12 upgrade guide does not mention Include changes; absence of mention in the breaking-changes list is treated as no change.] | Pattern 1, Pattern 4, Code Example 2 | LOW — if behavior changed (e.g., FV 12 introduced shared-state caching), Phase 8 validators might exhibit cross-rule leakage. The FV 12 upgrade guide explicitly lists ALL breaking changes; absence of Include in that list strongly suggests no change. SC#1 test (concrete validator gets base rules without restating) would surface any regression. |
| A-04 | `AddValidatorsFromAssembly` default `ServiceLifetime.Scoped` in FV 12.1.1 is unchanged from FV 9.x. [VERIFIED: github.com/FluentValidation/FluentValidation/blob/main/src/FluentValidation.DependencyInjectionExtensions/ServiceCollectionExtensions.cs — signature on main branch shows `ServiceLifetime lifetime = ServiceLifetime.Scoped`.] | Standard Stack, Code Example 4 | LOW — verified from current source. If FV 12 had changed the default (it didn't per the upgrade guide), tests would surface it during SC#3 (the validator wouldn't resolve correctly under a different lifetime). |

**Confirmation request:** Items tagged `[ASSUMED]` in this document (A-03 only) are low-risk but should be confirmed by the planner during plan creation by running a verification fact early. Item A-01 (MP vs RMG codes) is HIGH risk and requires user amendment of CONTEXT.md D-11 before planning if the planner intends to use the verbatim codes — or the planner can adopt the corrected RMG codes (A-01) directly with a Plan-level note "DEVIATION FROM CONTEXT D-11/D-12: codes corrected from MP0001/MP0011/MP0020/MP0021 → RMG007/RMG012/RMG020/RMG089 per RESEARCH A-01; verified at mapperly.riok.app."

## Open Questions

1. **Should Phase 6 add Mapperly to `BaseApi.Service.csproj` now (forward placement for Phase 8) or defer to Phase 8?**
   - What we know: CONTEXT D-13 leaves this as planner's call; Phase 8 will eventually need it for 5 concrete mappers in `BaseApi.Service/{Entity}/Mapping/` per HTTP-11. Adding now requires `PrivateAssets="all" ExcludeAssets="runtime"` per Phase 1 D-07.
   - What's unclear: Whether the explicit `<WarningsAsErrors>` Mapperly diagnostic promotion would fire spuriously in `BaseApi.Service` before any `[Mapper]` partial class exists (Phase 6 SC#4 only lands one in `BaseApi.Tests`).
   - Recommendation: **Defer to Phase 8.** Adding Mapperly to Service without a `[Mapper]` partial class produces no diagnostics (no generator targets), but it does load the analyzer at build time — minor, but unnecessary work. Phase 8's first task will be to add the reference along with the first entity mapper. Adding now would require a no-op commit message ("forward-place Mapperly for Phase 8") that adds maintenance noise.

2. **What test entity field shape should SC#4 use to maximize drift-detection signal in subsequent regression scenarios?**
   - What we know: CONTEXT marks this as Claude's Discretion; a minimal `TestEntity { Note }` extending BaseEntity suffices.
   - What's unclear: Whether the SC#4 scaffold should ALSO include a deliberately-misnamed field (e.g., add `TestEntity.Foo` but only `TestUpdateDto.Bar`) to assert that RMG020/RMG012 fire — i.e., a positive build-fail test in addition to the positive build-pass test.
   - Recommendation: **YES — include a "drift detection" negative fact** in the verification plan. The verification fact is `dotnet build` with a temporary `TestEntity.Foo` field that's not on any DTO, assert build fails with `error RMG012`, then revert. This proves the diagnostic promotion is actually active, not just appearing to work because the trivial scaffold has no drift. This is the SC#4 "smoke test" the planner should add as an explicit verification fact alongside the build-pass check.

3. **Does the Phase 5 OtelCollectorFixture / xUnit v3 [assembly: AssemblyFixture] cleanup discipline apply to Phase 6's verification tests?**
   - What we know: Phase 5 D-11 established `[assembly: AssemblyFixture(typeof(OtelEndOfSuiteCleanup))]` for end-of-suite Collector cleanup. Phase 6's verification tests don't touch OTel — they use the existing WebAppFactory (which boots Program.cs and thus the OTel pipeline, but the new validation endpoint shouldn't emit any OTel events that require cleanup).
   - What's unclear: Whether Phase 6's `/test/validate` endpoint produces telemetry that would leak to `tests/.otel-out/telemetry.jsonl` and break the end-of-suite cleanup contract.
   - Recommendation: **No new fixture work needed.** Phase 5's assembly fixture already handles all telemetry cleanup; Phase 6's new endpoint inherits the same lifecycle. Verify by checking that `tests/.otel-out/telemetry.jsonl` is absent after running the full suite (Phase 5 contract).

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET 8 SDK | All Phase 6 build + test work | ✓ | **8.0.421** | — |
| Docker Desktop + Compose | Phase 5 OTel Collector for in-suite cleanup contract (transitively required if Phase 6 tests run the full suite) | ✓ | **29.3.1 / Compose 5.1.1** | — |
| PostgreSQL 17 container | Phase 4 WebAppFactory boot path (Program.cs calls `AddNpgSql` health check) — Phase 6 tests inherit | ✓ (running) | postgres:17-alpine | — |
| FluentValidation 12.1.1 | `BaseDtoValidator<T>`, `AddValidatorsFromAssembly` | ✓ (pinned in CPM) | 12.1.1 | — |
| FluentValidation.DependencyInjectionExtensions 12.1.1 | `AddValidatorsFromAssembly` | ✓ (pinned in CPM) | 12.1.1 | — |
| Riok.Mapperly 4.3.1 | SC#4 source-gen scaffold | ✓ (pinned in CPM) | 4.3.1 | — |
| `Microsoft.AspNetCore.App` shared framework | `IServiceCollection`, MVC `[FromBody]`, `IExceptionHandler` | ✓ (FrameworkReference on Core + Tests) | net8.0 | — |

**Missing dependencies with no fallback:**
- None. All dependencies are available and at the required versions.

**Missing dependencies with fallback:**
- None.

## Validation Architecture

> `workflow.nyquist_validation` is not explicitly set to `false` in `.planning/config.json` — section included per default-enabled.

### Test Framework

| Property | Value |
|----------|-------|
| Framework | xUnit v3 3.2.2 (Microsoft Testing Platform runner) |
| Config file | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (`<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>` + `<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>`) |
| Quick run command | `dotnet test SK_P.sln --no-restore` |
| Full suite command | `dotnet test SK_P.sln --no-restore` (47 facts from Phase 1-5 + new Phase 6 facts) |
| Phase gate | All facts GREEN × 3 consecutive runs; byte-identical `psql \l` snapshots BEFORE/AFTER per Phase 3 D-15 |

### Phase Requirements → Test Map

| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|--------------|
| VALID-01 | No `FluentValidation.AspNetCore` package referenced anywhere | unit (csproj grep) | `dotnet test --filter "FullyQualifiedName~PackageAuditTests"` (or `dotnet list package` smoke) | ❌ Wave 0 — new fact |
| VALID-02 | `AddValidatorsFromAssembly` discovers validators automatically | integration (DI resolve) | `dotnet test --filter "FullyQualifiedName~ValidatorAutoDiscoveryTests"` | ❌ Wave 0 |
| VALID-03 | `IValidator<TDto>.ValidateAsync` invoked from Service layer; exception → HTTP 400 | integration (HTTP roundtrip) | `dotnet test --filter "FullyQualifiedName~ValidationEndpointTests"` | ❌ Wave 0 |
| VALID-04 | `Include(new BaseDtoValidator<MyDto>())` brings shared rules into concrete validator | unit | `dotnet test --filter "FullyQualifiedName~BaseDtoValidatorIncludeTests"` | ❌ Wave 0 |
| VALID-05 | `Name` NotEmpty + MaxLength(200) — empty/long strings rejected | unit (validator) | `dotnet test --filter "FullyQualifiedName~BaseDtoValidatorRuleTests.Name"` | ❌ Wave 0 |
| VALID-06 | `Version` strict SemVer regex — `1.0.0` accepts; `01.0.0`, `1.0`, `v1.0.0` reject | unit (validator) | `dotnet test --filter "FullyQualifiedName~BaseDtoValidatorRuleTests.Version"` | ❌ Wave 0 |
| VALID-07 | `Description` MaxLength(2000); null passes; 2001-char rejects | unit (validator) | `dotnet test --filter "FullyQualifiedName~BaseDtoValidatorRuleTests.Description"` | ❌ Wave 0 |
| HTTP-10 | `IEntityMapper<,,,>` interface exists in BaseApi.Core with 3 methods (compile-time check); test scaffold mapper compiles | unit (compile + DI resolve) | `dotnet build SK_P.sln -c Release` (compile) + `dotnet test --filter "FullyQualifiedName~MapperRegistrationTests"` | ❌ Wave 0 |
| SC#4 build-fail drift detection | Inserting a deliberate DTO/entity mismatch produces RMG012 → Error under `<WarningsAsErrors>` | integration (build invocation) | Drift fact: temporarily add `[MapperIgnoreTarget]`-not-applied target field; `dotnet build` expected exit 1 with RMG012 in output; revert | ❌ Wave 0 (manual fact + automated build invocation) |

### Sampling Rate

- **Per task commit:** `dotnet build SK_P.sln -c Release` (zero-warning gate from Phase 1 D-02; ~5s) + `dotnet test SK_P.sln --no-restore --filter "FullyQualifiedName~BaseDtoValidator"` (~5s)
- **Per wave merge:** `dotnet test SK_P.sln --no-restore` — full 47+ fact suite from Phases 1-5 + Phase 6 additions (~20s; 18s baseline observed on Phase 5 verification)
- **Phase gate:** Full suite GREEN × 3 consecutive runs; byte-identical `psql \l` BEFORE/AFTER (Phase 3 D-15)

### Wave 0 Gaps

Phase 6 requires the following new test infrastructure (none of these files exist yet):

- [ ] `tests/BaseApi.Tests/Validation/TestEntity.cs` — test entity (extends BaseEntity, adds Note field)
- [ ] `tests/BaseApi.Tests/Validation/TestCreateDto.cs` + `TestUpdateDto.cs` + `TestReadDto.cs` — 3 DTOs implementing IBaseDto
- [ ] `tests/BaseApi.Tests/Validation/TestDtoValidator.cs` — concrete validator with `Include(new BaseDtoValidator<TestUpdateDto>())`
- [ ] `tests/BaseApi.Tests/Validation/TestEntityMapper.cs` — `[Mapper] partial class : IEntityMapper<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>` (SC#4)
- [ ] `tests/BaseApi.Tests/Validation/TestValidationService.cs` — calls `IValidator<TestUpdateDto>.ValidateAsync`
- [ ] `tests/BaseApi.Tests/Validation/ValidationWebAppFactory.cs` — WAF subclass that also scans the Tests assembly (Pitfall 5)
- [ ] `tests/BaseApi.Tests/Validation/BaseDtoValidatorRuleTests.cs` — facts for VALID-05/06/07
- [ ] `tests/BaseApi.Tests/Validation/BaseDtoValidatorIncludeTests.cs` — facts for VALID-04 (SC#1)
- [ ] `tests/BaseApi.Tests/Validation/ValidatorAutoDiscoveryTests.cs` — facts for VALID-02 (SC#2)
- [ ] `tests/BaseApi.Tests/Validation/ValidationEndpointTests.cs` — facts for VALID-03 (SC#3) — HTTP-roundtrip
- [ ] `tests/BaseApi.Tests/Validation/MapperRegistrationTests.cs` — facts for HTTP-10 (SC#4) — DI scan + closed-generic resolution
- [ ] `tests/BaseApi.Tests/Validation/PackageAuditTests.cs` (optional) — facts for VALID-01 (no `FluentValidation.AspNetCore` PackageReference anywhere)
- [ ] Endpoint addition: `tests/BaseApi.Tests/Endpoints/TestController.cs` extension — new `[HttpPost("validate")]` action

**Framework install: NONE.** Existing xUnit v3 + MTP + WebApplicationFactory + PostgresFixture infrastructure (Phases 1-5) is sufficient. Phase 6 only ADDS files; no framework changes.

## Security Domain

> `security_enforcement` is not explicitly set to `false` in `.planning/config.json` — section included per default-enabled.

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | PROJECT.md Out of Scope (auth deferred to v2) |
| V3 Session Management | no | No sessions in v1 |
| V4 Access Control | no | No authorization in v1 |
| V5 Input Validation | **YES** | FluentValidation 12.1.1 (entire phase IS V5) |
| V6 Cryptography | no | Phase 6 has no crypto surface |
| V7 Error Handling | yes (existing) | Phase 4 ValidationExceptionHandler + Phase 4 FallbackExceptionHandler T-04-LEAK guard — unchanged by Phase 6 |
| V8 Data Protection | partial | T-06-LOG-INJECT (below): error messages logged should not include user input verbatim (FluentValidation default does not log raw input, only the failing rule name + sanitized field name) |
| V12 Files and Resources | no | No file/resource handling |
| V13 API & Web Service | yes (existing) | RFC 7807 ProblemDetails contract — Phase 4 ensures `errors` map sanitized (errors are FluentValidation rule strings, not raw user input) |

### Known Threat Patterns for FluentValidation + Mapperly (.NET 8 Web API)

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| **T-06-LOG-INJECT:** Raw user input echoed verbatim into log messages via FluentValidation `WithMessage(x => x.Name)` lambda or template interpolation | Information Disclosure (forensic noise; log injection if newline in input) | FluentValidation default messages reference field NAME not VALUE (`"Name is required"`, not `"Name 'alice\n[FAKE LOG]'is required"`). Phase 6 BaseDtoValidator uses default messages — no custom `WithMessage` template that references the value. |
| **T-06-REGEX-DOS:** Pathological regex input causes catastrophic backtracking (ReDoS) | Denial of Service | The SemVer regex `^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$` is anchored and non-backtracking (no nested quantifiers, no overlapping alternation). Verified ReDoS-safe via static analysis: the alternation `(0|[1-9]\d*)` is mutually exclusive (starts with `0` XOR starts with `[1-9]`), so the engine never re-explores. .NET 7+ regex engine also has a default `MatchTimeout` defense. |
| **T-06-OVERPOSTING:** Client posts extra fields (`Id`, `CreatedAt`, etc.) on Create or Update DTO that overwrite server-owned state | Tampering | D-08 prevents at the type level: `TCreate` / `TUpdate` DTOs do not expose server-side fields (HTTP-05/06). Mapperly cannot map what isn't on the source. Defense-in-depth: `AuditInterceptor` overwrites `CreatedAt`/`UpdatedAt`/`CreatedBy`/`UpdatedBy` on every SaveChanges regardless of whether the entity arrived with values set. |
| **T-06-VALIDATION-BYPASS:** Controller forgets to call `ValidateAsync` and persists invalid data | Tampering | VALID-03 + Phase 7 `BaseService<...>.CreateAsync` enforces the ordering `validator → ToEntity → repo.Add → SyncJunctionsAsync → SaveChangesAsync` (ROADMAP Phase 7 SC#2). Phase 6 ships the validator seam; Phase 7 ships the orchestration. SC#3 verifies Phase 6 half: an explicit `ValidateAsync` call in a service-layer method DOES throw `ValidationException` on bad input, which DOES map to HTTP 400. |
| **T-06-MAPPER-LEAK:** Mapperly-generated mapper accidentally exposes a sensitive entity property via the Read DTO | Information Disclosure | RMG020 with `RequiredMappingStrategy.Both` default + `TreatWarningsAsErrors=true` (Pitfall 1 resolution) makes ANY unmapped source property a build error. Combined with the explicit 3-DTO pattern (Create/Update/Read have intentional field sets), this prevents accidental field leakage. Phase 8 Read DTOs are explicit per HTTP-07. |

### Threat Coverage

All applicable V5 (Input Validation) and V8 (Data Protection) threats for Phase 6 are addressed by existing infrastructure (Phase 4 ValidationExceptionHandler, AuditInterceptor) plus Phase 6's strict-mapping defaults. No new mitigations need to be invented in Phase 6 — the phase's job is to wire existing tools correctly.

## Sources

### Primary (HIGH confidence)

- [FluentValidation `ServiceCollectionExtensions.cs` source on main branch](https://github.com/FluentValidation/FluentValidation/blob/main/src/FluentValidation.DependencyInjectionExtensions/ServiceCollectionExtensions.cs) — `AddValidatorsFromAssembly` signature, default `ServiceLifetime.Scoped`
- [FluentValidation 12.0 upgrade guide](https://docs.fluentvalidation.net/en/latest/upgrading-to-12.html) — breaking changes (CascadeMode removal, InjectValidator removal); confirms Include/RuleFor/Matches APIs unchanged
- [FluentValidation Including Rules docs](https://docs.fluentvalidation.net/en/latest/including-rules.html) — canonical `Include(new ChildValidator())` pattern
- [Mapperly Analyzer Diagnostics docs](https://mapperly.riok.app/docs/configuration/analyzer-diagnostics/) — complete RMG001–RMG094 table with default severities (Warning for RMG012/RMG020; Info for RMG089; Error for RMG007)
- [Mapperly v4.0 Breaking Changes](https://mapperly.riok.app/docs/breaking-changes/4-0/) — strict mappings default; `RequiredMappingStrategy = Both` default; RMG012/RMG020/RMG037/RMG038 Warning emission
- [Mapperly Existing Target Object docs](https://mapperly.riok.app/docs/configuration/existing-target/) — `public partial void Update(Source, Target)` pattern for in-place mutation
- `src/BaseApi.Core/Entities/BaseEntity.cs` — verified field shape (Name + Version string; Description string?)
- `src/BaseApi.Service/Program.cs` — verified D-18 insertion point (line 68 `AddProblemDetails` closing, line 153 `AddControllers()`)
- `Directory.Packages.props` — verified FV 12.1.1 + Mapperly 4.3.1 pre-pinned at lines 65, 68-69
- `Directory.Build.props` — verified `TreatWarningsAsErrors=true` at line 33
- `tests/BaseApi.Tests/Middleware/WebAppFactory.cs` — verified reusable scaffold structure
- `tests/BaseApi.Tests/Endpoints/TestController.cs` — verified existing 8 endpoints; `/test/validate` route does not collide
- `src/BaseApi.Core/Exceptions/Handlers/ValidationExceptionHandler.cs` — verified `FluentValidation.ValidationException → 400` mapping already wired

### Secondary (MEDIUM confidence)

- [Mapperly Mapper Configuration docs](https://mapperly.riok.app/docs/configuration/mapper/) — `[MapperIgnoreTarget]` + `[MapperIgnoreSource]` attribute syntax (verified via WebFetch + corroborated by GitHub examples)
- [FluentValidation Dependency Injection docs](https://docs.fluentvalidation.net/en/latest/di.html) — `AddValidatorsFromAssembly` overload list (corroborated by main-branch source)
- [Mapperly Riok GitHub README](https://github.com/riok/mapperly) — partial class pattern, interface implementation example

### Tertiary (LOW confidence)

- Stack Overflow / Medium tutorials on FluentValidation Include and Mapperly partial classes — used for pattern verification only; all critical claims cross-checked against primary sources

## Metadata

**Confidence breakdown:**
- Standard stack (FV 12.1.1, Mapperly 4.3.1): **HIGH** — versions verified in CPM, APIs verified against current source on main branch
- Architecture patterns (BaseDtoValidator + Include + IEntityMapper + DI extensions): **HIGH** — patterns directly from official docs + locked decisions in CONTEXT.md
- Pitfalls (esp. Pitfall 1 MP-vs-RMG and Pitfall 2 RequiredMappingStrategy): **HIGH** — verified via canonical Mapperly docs; surfacing these prevents CONTEXT D-11 verbatim implementation failure
- Code examples: **HIGH** — every snippet sourced and cross-referenced
- Validation Architecture: **HIGH** — test framework + cadence inherits Phase 5 GREEN baseline
- Security domain: **HIGH** — Phase 6 has minimal new attack surface; V5 mitigations are the entire phase

**Research date:** 2026-05-27
**Valid until:** 2026-06-26 (30 days — stable .NET 8 LTS ecosystem; FV 12.1.x and Mapperly 4.3.x both in low-churn maintenance bands)

---

*Phase: 06-validation-mapping-base*
*Research performed: 2026-05-27*
