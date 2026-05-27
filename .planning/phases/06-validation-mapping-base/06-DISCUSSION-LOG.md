# Phase 6: Validation + Mapping Base - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-05-27
**Phase:** 06-validation-mapping-base
**Areas discussed:** BaseDtoValidator<T> contract, IEntityMapper<,,,> method surface + Update semantics, Mapperly diagnostics promotion site, DI registration ownership in Phase 6 vs Phase 7

---

## Gray Area Selection

| Option | Description | Selected |
|--------|-------------|----------|
| BaseDtoValidator<T> contract | How T exposes Name/Version/Description for `Include(new BaseDtoValidator<MyDto>())` — marker interface vs abstract record vs selector funcs | ✓ |
| IEntityMapper<,,,> method surface + Update semantics | Exact 3 methods + mutating vs functional Update + server-side field exclusion | ✓ |
| Mapperly diagnostics promotion site | MP0001/MP0011/MP0020/MP0021 → errors site: Directory.Build.props vs per-csproj vs Phase 1 D-04 site | ✓ |
| DI registration ownership in Phase 6 vs Phase 7 | Phase 6 ships AddBaseApi* extensions vs raw Program.cs lines vs types-only with test fixtures wiring | ✓ |

**User's choice:** All 4 areas (multi-select)

---

## Area 1: BaseDtoValidator<T> contract

### Q1.1: How should `BaseDtoValidator<T>` expose Name/Version/Description for `Include(new BaseDtoValidator<MyDto>())`?

| Option | Description | Selected |
|--------|-------------|----------|
| Marker interface IBaseDto | `IBaseDto { Name, Version, Description? }`; `BaseDtoValidator<T> where T : IBaseDto`. Type-safe at compile time; concrete DTOs free to inherit anything else; reads cleanly with C# records. | ✓ |
| Abstract record BaseDto | `public abstract record BaseDto(string Name, string Version, string? Description);`. Inheritance is forced; record positional syntax gets verbose. | |
| Constructor Func selectors | `BaseDtoValidator<T>(Expression<Func<T,string>> name, ...)`. Flexible but verbose at every call site — violates VALID-04 spirit. | |

**User's choice:** Marker interface IBaseDto

### Q1.2: Should Read DTOs also implement IBaseDto, or only Create/Update DTOs?

| Option | Description | Selected |
|--------|-------------|----------|
| Create + Update only | Read DTOs are server-emitted, never validated. Keeps contract minimal and intent clear. | |
| All three (Create, Update, Read) | Read DTOs also implement IBaseDto for symmetry. Mapper signatures uniform. Costs nothing functionally but blurs purpose. | ✓ |

**User's choice:** All three (Create, Update, Read) — chose symmetry over minimalism

### Q1.3: Where should IBaseDto live in the BaseApi.Core folder tree?

| Option | Description | Selected |
|--------|-------------|----------|
| BaseApi.Core/Validation/IBaseDto.cs | Co-located with BaseDtoValidator; Validation/ folder already exists (.gitkeep). | ✓ |
| BaseApi.Core/Dtos/IBaseDto.cs | New folder for DTO contracts. | |
| BaseApi.Core/Contracts/IBaseDto.cs | Generic 'contracts' folder for cross-cutting interfaces. | |

**User's choice:** BaseApi.Core/Validation/IBaseDto.cs (next to BaseDtoValidator)

### Q1.4: Should Description on IBaseDto be `string?` or `string`?

| Option | Description | Selected |
|--------|-------------|----------|
| string? Description (nullable) | Matches BaseEntity.Description. JSON null / absent map to null. MaxLength(2000) treats null as valid. | ✓ |
| string Description (non-null, empty default) | Forces clients to send "" instead of null. Mismatch with BaseEntity. | |

**User's choice:** string? Description (nullable)

---

## Area 2: IEntityMapper<,,,> method surface + Update semantics

### Q2.1: What should the IEntityMapper Update method signature be?

| Option | Description | Selected |
|--------|-------------|----------|
| void Update(TUpdate dto, TEntity target) — mutating | Mapperly fills target in-place; EF tracks via xmin. Matches PUT-as-full-replace. Mapperly idiomatic. | ✓ |
| TEntity Update(TUpdate dto, TEntity existing) — functional return | Returns new instance. Conflicts with EF tracked-entity pattern and xmin concurrency. | |
| TEntity ToEntity(TUpdate dto) + caller copies server-side fields | Most explicit but most error-prone — every service must remember to copy. | |

**User's choice:** void Update(TUpdate dto, TEntity target) — mutating

### Q2.2: How are server-side fields (Id, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy) excluded from Update mapping?

| Option | Description | Selected |
|--------|-------------|----------|
| Interface contract: TUpdate doesn't expose them | Cleanest contract; compile-time prevention. | ✓ |
| Per-mapper [MapperIgnoreTarget] attributes | Verbose but explicit. Phase 8 boilerplate increases. | |
| Global Mapperly config + partial override | Less common pattern; Mapperly 4.x assembly-level config is limited. | |

**User's choice:** Interface contract: TUpdate doesn't expose them, so Mapperly never sees them

### Q2.3: What 3 method names should IEntityMapper expose?

| Option | Description | Selected |
|--------|-------------|----------|
| ToEntity(create) / Update(update, entity) / ToRead(entity) | Verb-noun symmetry; reads cleanly at call sites. | ✓ |
| Create(create) / Update(update, entity) / Read(entity) | CRUD verbs; Create(dto) reads ambiguously. | |
| MapToEntity / MapToEntity / MapToRead | Most explicit; slightly verbose. | |

**User's choice:** ToEntity(create) / Update(update, entity) / ToRead(entity)

### Q2.4: Where does responsibility for M2M list fields (e.g., StepUpdateDto.NextStepIds) land?

| Option | Description | Selected |
|--------|-------------|----------|
| Phase 8 partial classes declare [MapperIgnoreSource] per M2M field | IEntityMapper scalar-only; M2M handled by BaseService.SyncJunctionsAsync (Phase 7). | ✓ |
| IEntityMapper adds a 4th method: ExtractJunctions(TCreate/TUpdate) | More uniform API but adds surface area before there's a real consumer. | |
| Defer to Phase 7/8 — not Phase 6's concern | Cleanest scope but loses cross-phase traceability. | |

**User's choice:** Phase 8 partial classes declare [MapperIgnoreSource] per M2M field

---

## Area 3: Mapperly diagnostics promotion site

### Q3.1: Where should `<WarningsAsErrors>MP0001;MP0011;MP0020;MP0021</WarningsAsErrors>` be promoted?

| Option | Description | Selected |
|--------|-------------|----------|
| Directory.Build.props (solution-wide) | Single source of truth; supersedes Phase 1 D-04 (Service-only). | ✓ |
| Per-csproj edits to BaseApi.Core, BaseApi.Service, BaseApi.Tests | Three csproj edits; triples maintenance. | |
| Keep Phase 1 D-04 (Service only) + add Tests separately | Honors original literally; leaves Core uncovered. | |

**User's choice:** Directory.Build.props (solution-wide) — explicitly supersedes Phase 1 D-04

### Q3.2: Which mechanism — `<WarningsAsErrors>` vs `<RiokMapperlyDiagnosticsAsErrors>` vs leveraging existing `TreatWarningsAsErrors=true`?

| Option | Description | Selected |
|--------|-------------|----------|
| <WarningsAsErrors> with explicit MP codes | Standard MSBuild; locked codes visible for auditability; matches SC#4 "promoted to errors in the project file" verbiage. | ✓ |
| <RiokMapperlyDiagnosticsAsErrors> | Mapperly-specific; less portable. | |
| Treat all warnings as errors (already true) | Already in Directory.Build.props per Phase 1 D-02; explicit listing would be redundant. | |

**User's choice:** <WarningsAsErrors> with explicit MP codes

---

## Area 4: DI registration ownership in Phase 6 vs Phase 7

### Q4.1: Where does Phase 6's `services.AddValidatorsFromAssembly(...)` live?

| Option | Description | Selected |
|--------|-------------|----------|
| Ship AddBaseApiValidation() extension in BaseApi.Core/DependencyInjection — wire in Program.cs now; Phase 7 AddBaseApi composes later | Phase 7-ready extension method; zero refactor; proves SC#3 against same wiring Phase 7 will use. | ✓ |
| Raw services.AddValidatorsFromAssembly(...) in Program.cs; Phase 7 refactors | Simpler now, more churn later. | |
| Ship types only; test fixtures wire DI inline; Phase 7 owns ALL production wiring | Most isolated but production unverified until Phase 7. | |

**User's choice:** Ship AddBaseApiValidation() extension — wire it now; Phase 7 composes

### Q4.2: Should there be a parallel `AddBaseApiMapping()` extension for IEntityMapper<,,,> registration?

| Option | Description | Selected |
|--------|-------------|----------|
| Phase 6 ships AddBaseApiMapping() that auto-discovers IEntityMapper<,,,> implementations | Symmetric with AddBaseApiValidation; minimizes Phase 8 boilerplate. | ✓ |
| Each entity registers manually in Phase 8 | More boilerplate but DI graph is grep-able. | |
| Defer entirely — Phase 6 only ships the IEntityMapper interface | Cleanest scope but leaves DI pattern undefined. | |

**User's choice:** Phase 6 ships AddBaseApiMapping() with auto-discovery

### Q4.3: How should AddBaseApiValidation/AddBaseApiMapping accept which assembly to scan?

| Option | Description | Selected |
|--------|-------------|----------|
| params Assembly[] assemblies | Flexible; supports test-time injection without compromising production simplicity. | ✓ |
| Single Assembly parameter (required) | Forces single call site; less flexible. | |
| Caller-scoped (Assembly.GetCallingAssembly()) | Fragile across DI containers and hosting scenarios. | |

**User's choice:** params Assembly[] assemblies

### Q4.4: How should Phase 6 verify SC#3?

| Option | Description | Selected |
|--------|-------------|----------|
| Reuse Phase 4 WebApplicationFactory + extend TestController with /validate-test endpoint | Mirrors Phase 4 pattern; proves end-to-end against real DI + handlers. | ✓ |
| Plain xunit + DI container for SC#1/#2/#4 + single WAF test for SC#3 | Less duplication of WAF setup. | |
| All facts via WebApplicationFactory (Phase 4 default) | Mirror Phase 4's all-WAF approach. | |

**User's choice:** Reuse Phase 4 WebApplicationFactory + extend TestController

---

## Final Check

### Q: We've covered DTO contract, mapper surface, Mapperly diagnostics, and DI registration. Any other gray areas remaining?

| Option | Description | Selected |
|--------|-------------|----------|
| Ready for context — write CONTEXT.md now | Lock decisions; proceed to write. | ✓ |
| Explore more gray areas | Surface 2-4 additional gray areas. | |

**User's choice:** Ready for context — write CONTEXT.md now

---

## Claude's Discretion

The following items were intentionally left to planner/research discretion (captured in CONTEXT.md `<decisions>` under "Claude's Discretion"):

- Trivial TestEntity/TestDto field shape for SC#4 Mapperly partial class scaffold
- FluentValidation `ServiceLifetime` for assembly scan (default Scoped expected)
- DI extension folder name (`BaseApi.Core/DependencyInjection/` — verify exists in scout)
- TestController route name for SC#3 endpoint
- Reflection scan implementation details for AddBaseApiMapping auto-discovery
- Plan wave structure (single-plan vs split — "Parallelizable: yes" hint from ROADMAP)
- Whether to add Riok.Mapperly PackageReference to BaseApi.Service.csproj in Phase 6 (forward placement) or defer to Phase 8

## Deferred Ideas

Captured in CONTEXT.md `<deferred>` section:

- Validator DI lifetime tuning (Singleton vs Scoped) for mappers with HttpContext dependencies
- Mapperly `RequiredMappingStrategy` per-mapper config
- Custom FluentValidation severity (WithSeverity)
- Per-mapper performance/enum-mapping config
- BaseApi.Service.csproj Mapperly PackageReference timing
- Mapper auto-discovery edge cases (abstract classes, open generics)
