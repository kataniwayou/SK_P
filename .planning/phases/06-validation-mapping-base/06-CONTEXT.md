# Phase 6: Validation + Mapping Base - Context

**Gathered:** 2026-05-27
**Status:** Ready for planning

<domain>
## Phase Boundary

Establish the validation + mapping seam in `BaseApi.Core` so Phase 8's 5 concrete entities plug in without rewriting base rules or coupling controllers to Mapperly:

1. **`IBaseDto` marker interface** — exposes the 3 shared DTO fields (`Name`, `Version`, `Description?`) that base validation rules target.
2. **`BaseDtoValidator<T> : AbstractValidator<T> where T : IBaseDto`** — encapsulates VALID-05/06/07 so concrete validators get the shared rules via `Include(new BaseDtoValidator<MyDto>())`.
3. **`IEntityMapper<TEntity, TCreate, TUpdate, TRead>`** — 3-method interface that Phase 8 implements as Mapperly `[Mapper] partial class` per entity.
4. **`AddBaseApiValidation()` + `AddBaseApiMapping()` DI extensions** in `BaseApi.Core/DependencyInjection/` — Phase 7's `AddBaseApi` composition root will compose these later without behavior change.
5. **Mapperly diagnostics RMG007/RMG012/RMG020/RMG089 promoted to errors** in `Directory.Build.props` (solution-wide). (Original CONTEXT-as-gathered listed MP0001/MP0011/MP0020/MP0021 — those codes do not exist in Mapperly; corrected via RESEARCH A-01 amendment on 2026-05-27 before plan creation.)

Out of scope:
- Concrete entity DTOs, validators, and Mapperly partial classes — those belong to Phase 8 (VALID-08..20).
- `BaseController` / `BaseService` / `AddBaseApi` composition root — those belong to Phase 7.
- Migrations — Phase 8 owns the first migration; Phase 3 D-16 still applies.

</domain>

<decisions>
## Implementation Decisions

### IBaseDto + BaseDtoValidator<T> contract (Area 1)

- **D-01:** `BaseDtoValidator<T>` uses a marker interface for type-safe access to shared DTO fields: `public class BaseDtoValidator<T> : AbstractValidator<T> where T : IBaseDto`. Chosen over (a) abstract record `BaseDto` (forces inheritance, verbose record positional syntax) and (b) constructor selector funcs (violates VALID-04 spirit "shared rules without restating"). FluentValidation 12.x `Include(new BaseDtoValidator<MyDto>())` resolves at compile time against the interface constraint.
- **D-02:** `IBaseDto` exposes EXACTLY 3 read-only property getters mirroring `BaseEntity`'s 3 narrative fields:
  ```csharp
  public interface IBaseDto
  {
      string Name { get; }
      string Version { get; }
      string? Description { get; }
  }
  ```
  `Description` is `string?` (nullable) to match `BaseEntity.Description` (Phase 3) — JSON `null` and absent property both map to `null`; FluentValidation `MaxLength(2000)` treats `null` as valid (no length to check). No `Id`/audit fields on the interface — those are server-side per HTTP-05.
- **D-03:** `IBaseDto` is implemented by ALL 3 DTO flavors (Create, Update, Read) for symmetry — even though Read DTOs are server-emitted and never validated, uniform contract simplifies the mapper interface and eliminates per-DTO interface-membership decisions in Phase 8. Records satisfy the interface via auto-implemented properties; positional record syntax works (`record SchemaReadDto(Guid Id, string Name, string Version, string? Description, ...) : IBaseDto`).
- **D-04:** `IBaseDto` and `BaseDtoValidator<T>` live at `BaseApi.Core/Validation/` (folder already exists with `.gitkeep` from Phase 1 D-08). Co-locates the interface with the validator base class — the interface exists FOR validation purposes.
- **D-05:** `BaseDtoValidator<T>` is `public class` (non-abstract, instantiable) per SC#1 `new BaseDtoValidator<MyDto>()` syntax. The 3 rules:
  - `Name`: `NotEmpty()` + `MaximumLength(200)` (VALID-05)
  - `Version`: `NotEmpty()` + `Matches(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")` (VALID-06, strict SemVer, no leading zeros)
  - `Description`: `MaximumLength(2000)` (VALID-07; null is valid)

### IEntityMapper<,,,> method surface + Update semantics (Area 2)

- **D-06:** `IEntityMapper<TEntity, TCreate, TUpdate, TRead>` exposes EXACTLY 3 methods:
  ```csharp
  TEntity ToEntity(TCreate dto);
  void Update(TUpdate dto, TEntity target);
  TRead ToRead(TEntity entity);
  ```
  Method names chosen for verb-noun symmetry at call sites: `_mapper.ToEntity(createDto)` / `_mapper.Update(updateDto, entity)` / `_mapper.ToRead(entity)`.
- **D-07:** `Update` is mutating (`void`, not `TEntity` return) — Mapperly fills `target` in-place; EF Core change tracking + Phase 3 `xmin` shadow concurrency token detect conflicts at SaveChanges. Plays best with EF's tracked-entity pattern; matches PUT-as-full-replace semantics (HTTP-05). Rejected alternatives: (b) functional `TEntity Update(TUpdate, TEntity existing)` returning a new instance forces re-attach and conflicts with xmin; (c) `ToEntity(TUpdate)` + caller copies server-side fields is most error-prone (every concrete service must remember the copy).
- **D-08:** Server-side fields (`Id`, `CreatedAt`, `UpdatedAt`, `CreatedBy`, `UpdatedBy`) are excluded from Update mapping via **two complementary mechanisms** (amended 2026-05-27 per RESEARCH A-02):
  1. **Source-side (DTO contract):** Phase 8's `TUpdate` DTOs do NOT expose `Id`/audit fields. Mapperly cannot map a field that isn't on the source DTO — compile-time prevention of "client overwrites audit field" at the DTO declaration site.
  2. **Target-side (Mapperly attribute):** Each `[Mapper] partial class`'s `Update(TUpdate dto, TEntity target)` method MUST declare 5 `[MapperIgnoreTarget]` attributes — one per server-side field:
     ```csharp
     [MapperIgnoreTarget(nameof(TestEntity.Id))]
     [MapperIgnoreTarget(nameof(TestEntity.CreatedAt))]
     [MapperIgnoreTarget(nameof(TestEntity.UpdatedAt))]
     [MapperIgnoreTarget(nameof(TestEntity.CreatedBy))]
     [MapperIgnoreTarget(nameof(TestEntity.UpdatedBy))]
     public partial void Update(TestUpdateDto dto, TestEntity target);
     ```
     Required because Mapperly 4.x defaults `RequiredMappingStrategy = Both` — its strict-mappings check fires on UNMAPPED TARGET members regardless of whether the source DTO has the field. Without the attributes, Mapperly emits `RMG012: Source member 'Id' was not found for target member 'Id'`, which `TreatWarningsAsErrors=true` (Phase 1 D-02) promotes to a build error.
  3. **Why explicit attributes rather than `[Mapper(RequiredMappingStrategy = .Source)]`:** Explicit per-field attributes preserve target-side drift detection — if Phase 8 adds a new property to an entity (e.g., a `Status` field on StepEntity) and forgets to add it to the corresponding TUpdate DTO, RMG012 still fires for that property and the build breaks. Globally disabling target-side strict-mappings would silently hide that drift.
  4. Audit fields remain owned by `AuditInterceptor` (Phase 3 D-08 + Phase 3 PERSIST-03/04/07) — the interceptor stamps them on `SavingChangesAsync`; Mapperly is contractually forbidden from touching them via the dual mechanism above.
- **D-09:** `IEntityMapper<,,,>` stays scalar-only — M2M list fields on DTOs (e.g., `StepUpdateDto.NextStepIds`, `WorkflowUpdateDto.EntryStepIds`) have NO matching scalar property on the entity (junctions live in separate tables: `StepNextSteps`, `WorkflowEntrySteps`, `WorkflowAssignments`). With RMG020 (source-not-mapped) promoted to errors via D-11, Phase 8 partial classes MUST declare `[MapperIgnoreSource(nameof(StepUpdateDto.NextStepIds))]` per M2M field. M2M sync is handled by Phase 7's `BaseService<...>.SyncJunctionsAsync` virtual (ROADMAP Phase 7 SC#2 ordering: `validator → ToEntity → repo.Add → SyncJunctionsAsync → SaveChangesAsync → ToRead`). Mapper does NOT call into `DbSet<StepNextSteps>`.
- **D-10:** `IEntityMapper<,,,>` lives at `BaseApi.Core/Mapping/` (new folder — does not yet exist; Phase 1 D-08 skeleton predates Mapperly placement decision). `IEntityMapper.cs` is the only file in this folder at Phase 6 completion; Phase 8 entity mappers can live in `BaseApi.Service/Mappers/` (Phase 8's call).

### Mapperly diagnostics promotion (Area 3)

- **D-11:** `<WarningsAsErrors>$(WarningsAsErrors);RMG007;RMG012;RMG020;RMG089</WarningsAsErrors>` is added to `Directory.Build.props` (solution-wide, applies to Core + Service + Tests). This **SUPERSEDES Phase 1 D-04** which targeted `BaseApi.Service` csproj only — that decision predated SC#4's "trivial `[Mapper] partial class` in BaseApi.Tests" verification approach. Single edit catches all current and future csproj. Phase 1 D-04 is now historically accurate but no longer the live location. **Code correction history (RESEARCH A-01, amended 2026-05-27):** the original CONTEXT-as-gathered listed `MP0001/MP0011/MP0020/MP0021` — those codes do NOT exist in any version of Mapperly. Mapperly uses RMG-prefix exclusively (verified at mapperly.riok.app/docs/configuration/analyzer-diagnostics/). Intent-equivalent codes per RESEARCH:
  - `MP0001` (mapping in nullable context) → **RMG089** (default severity: Info)
  - `MP0011` (cannot map property) → **RMG007** (default severity: Error — already an error; listing for auditability)
  - `MP0020` (source not mapped to target) → **RMG020** (default severity: Warning)
  - `MP0021` (target source not found) → **RMG012** (default severity: Warning)
- **D-12:** Mechanism is explicit `<WarningsAsErrors>` listing the 4 RMG codes by name (auditability) — NOT `<RiokMapperlyDiagnosticsAsErrors>` (Mapperly-specific MSBuild property, less portable across Mapperly versions). Note: `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` is already set globally in `Directory.Build.props` per Phase 1 D-02 — RMG020/RMG012 (Warning default) are already promoted automatically; RMG089 (Info default) is **only** promoted via the explicit list (load-bearing); RMG007 (Error default) is already failing builds. The explicit `<WarningsAsErrors>` list serves THREE purposes:
  1. **Promotes RMG089** (Info → Error) — the only code in the list that wouldn't otherwise be promoted by global TreatWarningsAsErrors.
  2. **Defense-in-depth** — if a future Mapperly release lowers RMG020/RMG012 from Warning to Info, the explicit listing still promotes them.
  3. **Auditability** — visible in `Directory.Build.props` for future maintainers reviewing what Mapperly diagnostics are intentionally promoted.
- **D-13:** Mapperly source-gen package convention (Phase 1 D-07 + Directory.Packages.props header comment) remains: any csproj that references `Riok.Mapperly` MUST add `PrivateAssets="all"` + `ExcludeAssets="runtime"` to that `<PackageReference>`. Phase 6 adds Mapperly to `BaseApi.Tests.csproj` (for SC#4 trivial partial class) and `BaseApi.Service.csproj` (forward placement for Phase 8 — optional; planner may decide to defer until Phase 8). `BaseApi.Core.csproj` does NOT reference Mapperly — `IEntityMapper<,,,>` is a pure interface; only concrete `[Mapper]` partial classes need the analyzer.

### DI registration ownership (Area 4)

- **D-14:** Phase 6 ships `AddBaseApiValidation()` extension at `BaseApi.Core/DependencyInjection/ValidationServiceCollectionExtensions.cs`:
  ```csharp
  public static IServiceCollection AddBaseApiValidation(this IServiceCollection services, params Assembly[] assemblies)
  ```
  Implementation wraps `services.AddValidatorsFromAssembly(assembly, ServiceLifetime.Scoped, includeInternalTypes: false)` per assembly (VALID-02). Wired in `BaseApi.Service/Program.cs` now: `builder.Services.AddBaseApiValidation(typeof(Program).Assembly);`. Phase 7's `AddBaseApi` will internally call `services.AddBaseApiValidation(...)` — zero refactor needed when Phase 7 lands. Chosen over (b) raw `AddValidatorsFromAssembly` in Program.cs (forces Phase 7 churn) and (c) types-only shipping (production wiring unverified until Phase 7).
- **D-15:** Phase 6 ships parallel `AddBaseApiMapping()` extension at `BaseApi.Core/DependencyInjection/MappingServiceCollectionExtensions.cs`:
  ```csharp
  public static IServiceCollection AddBaseApiMapping(this IServiceCollection services, params Assembly[] assemblies)
  ```
  Implementation scans assemblies for all types implementing a closed-generic `IEntityMapper<,,,>` and registers each as `services.AddSingleton(closedInterface, concreteType)`. Mappers are stateless (source-gen produces pure functions) — Singleton is the correct lifetime. Phase 8's 5 entity mappers register automatically with zero per-entity DI code. Auto-discovery pattern parallels FluentValidation's `AddValidatorsFromAssembly` and minimizes Phase 8 boilerplate. The reflection scan happens once at startup; runtime is reflection-free.
- **D-16:** Both extensions take `params Assembly[] assemblies` — flexible for production (`typeof(Program).Assembly` single-arg) and tests (Program assembly + Tests assembly). Rejected (b) single `Assembly` parameter (forces two calls) and (c) `Assembly.GetCallingAssembly()` (fragile across DI containers + hosting scenarios).
- **D-17:** SC#3 verification reuses Phase 4 `WebAppFactory` (already at `tests/BaseApi.Tests/Middleware/WebAppFactory.cs` — path corrected 2026-05-27 via PATTERNS.md finding) + extends Phase 4's `TestController` with a `/validate-test` endpoint. The endpoint accepts a `TestDto` (implementing `IBaseDto`) via `[FromBody]`, calls `IValidator<TestDto>.ValidateAsync(dto)` from a thin TestService (proves VALID-03: explicit ValidateAsync from service layer, NOT MVC auto-validation), throws `FluentValidation.ValidationException` on failure. Phase 4's `ValidationExceptionHandler` already maps to HTTP 400 + ProblemDetails + `errors` map + `correlationId`. SC#3 fact: `POST /validate-test` with bad `Version=""` → HTTP 400 + ProblemDetails body with `errors["Version"]` populated + `correlationId` matching response header.

### Wiring order in Program.cs (composition implication)

- **D-18:** Phase 6 inserts BOTH new extension calls into `Program.cs` BEFORE `AddControllers()` to match the Phase 4 D-11 ordering principle (`AddProblemDetails` BEFORE `AddControllers`). The 2 new lines slot after `AddHttpContextAccessor` + `AddProblemDetails` and before `AddControllers`. Phase 7's `AddBaseApi` will absorb these calls — Phase 6 places them at the precise location Phase 7 will compose from, so the migration is mechanical (cut-paste into AddBaseApi body).

### Test-project Mapperly opt-in

- **D-19:** `BaseApi.Tests.csproj` adds a `<PackageReference Include="Riok.Mapperly" PrivateAssets="all" ExcludeAssets="runtime" />` per Phase 1 D-07 convention. The SC#4 verification fact creates a `TestEntityMapper : IEntityMapper<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>` as a `[Mapper] partial class` in the test assembly; the compile-success of the test assembly under `Directory.Build.props` D-11 promotion is the proof. The partial class body is empty (Mapperly fills it via source-gen). `BaseApi.Service.csproj` Mapperly reference is **planner's call** — adding it now (D-13) future-proofs Phase 8 but isn't strictly required for any Phase 6 SC.

### Claude's Discretion

- **Mapperly partial class signature for the SC#4 scaffold** — the test entity/DTO field shape is implementation detail. A trivial `TestEntity { Id, Name, Version, Description, Note }` + `TestCreateDto { Name, Version, Description?, Note }` + `TestUpdateDto { Name, Version, Description?, Note }` + `TestReadDto { Id, Name, Version, Description?, Note }` suffices. Planner chooses field set.
- **FluentValidation `ServiceLifetime` for assembly-scan** — defaults to `Scoped` (matches MVC request lifetime). Honor the default unless a research finding contradicts (e.g., FluentValidation 12.x change).
- **Folder name for the DI extensions** — `BaseApi.Core/DependencyInjection/` already exists per Phase 1 D-08 (or can be created). Filename pattern `ValidationServiceCollectionExtensions.cs` + `MappingServiceCollectionExtensions.cs` matches the Microsoft.Extensions.* convention.
- **TestController route** for the SC#3 endpoint — `/validate-test` or `/test/validate` — planner picks; should NOT collide with Phase 4's existing 8 deliberately-throwing endpoints.
- **Reflection scan implementation** for `AddBaseApiMapping` auto-discovery — `assembly.GetTypes().Where(t => !t.IsAbstract && !t.IsInterface).SelectMany(t => t.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEntityMapper<,,,>)))` or equivalent LINQ. Planner picks specifics; ensure no `TypeLoadException` for partially-built types.
- **Plan wave structure** — prior phases used a 2-plan pattern (`06-01-PLAN.md` build + `06-02-PLAN.md` verification). Per ROADMAP "Parallelizable: yes" hint for Phase 6, planner may split `06-01` into parallel sub-plans (e.g., `06-01a` BaseDtoValidator + IBaseDto + AddBaseApiValidation, `06-01b` IEntityMapper + AddBaseApiMapping + Directory.Build.props edit) — both must land before the verification plan. Verification plan stays sequential (single test-suite append).

### Folded Todos

No todos folded — no `.planning/todos/` directory found at this time.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Roadmap + Project Anchors
- `.planning/ROADMAP.md` §"Phase 6: Validation + Mapping Base" — phase goal, 4 SC, 8 REQ-IDs, Parallelizable=yes hint
- `.planning/PROJECT.md` §"Key Decisions" — locks: "Mapperly (source-gen) over AutoMapper", "FluentValidation over DataAnnotations", "3 DTOs per entity (Create/Update/Read)", "3-tier layering Controller→Service→Repository"
- `.planning/REQUIREMENTS.md` §"VALID-01..07" + §"HTTP-10" — 8 phase REQ-IDs with locked text (FluentValidation 12.x, no FluentValidation.AspNetCore, AddValidatorsFromAssembly discovery, explicit `ValidateAsync` in Service layer, `BaseDtoValidator<T>` Include pattern, SemVer regex, MaxLength rules, IEntityMapper<TEntity, TCreate, TUpdate, TRead>)
- `.planning/STATE.md` — Phase 5 complete; Phase 6 status "Not started"

### Prior Phase Decisions (locked, MUST honor)
- `.planning/phases/01-repository-scaffold/01-CONTEXT.md` §D-02 — `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` globally; D-04 (Mapperly MP-code promotion deferred to Phase 6 — see D-11 here for the supersession); D-07 (Mapperly source-gen convention: `PrivateAssets="all" ExcludeAssets="runtime"`); D-08 (folder skeleton already includes `BaseApi.Core/Validation/`)
- `.planning/phases/03-ef-core-persistence-base/03-CONTEXT.md` — `BaseEntity` 8-field shape; `xmin` shadow concurrency token (PERSIST-16); D-15 byte-identical psql `\l` snapshots BEFORE/AFTER (test cleanup discipline); D-11 (no `Program.cs` touch in Phase 3 — Phase 4 took ownership); `AuditInterceptor` owns audit fields (no DTO should overwrite)
- `.planning/phases/04-cross-cutting-middleware-error-handling/04-CONTEXT.md` §D-10 — Phase 4 ships `ValidationExceptionHandler` + adds `FluentValidation` PackageReference to `BaseApi.Core.csproj` defensively. Phase 6 builds ON this: validator throws `FluentValidation.ValidationException`; Phase 4 handler maps to HTTP 400 + ProblemDetails with `errors` map + `correlationId`. D-04 (AddProblemDetails customizer injects `correlationId` + `instance` into ALL ProblemDetails). D-06 (4-handler chain order: NotFound → Validation → DbUpdate → Fallback)
- `.planning/phases/04-cross-cutting-middleware-error-handling/04-CONTEXT.md` §"Phase 4 WAF + TestController" — reusable test scaffold at `tests/BaseApi.Tests/Middleware/WebAppFactory.cs` + existing `TestController` with 8 deliberately-throwing endpoints; SC#3 extends this controller with one new endpoint (D-17)
- `.planning/phases/05-observability-health-probes/05-CONTEXT.md` — Phase 5 D-11 cleanup discipline (xUnit v3 `[assembly: AssemblyFixture]` for end-of-suite teardown); D-16 `/health/*` exclusion (informational — Phase 6 has no new endpoints in production); MEL bridge pattern (informational — Phase 6 doesn't touch logging)

### Source-of-Truth Code (planner MUST read before designing types)
- `src/BaseApi.Core/Entities/BaseEntity.cs` — 8-field abstract base; `Name` + `Version` are `string` (non-null with empty default), `Description` is `string?`; mirror the same nullability in `IBaseDto`
- `src/BaseApi.Core/Validation/.gitkeep` — folder already exists from Phase 1 D-08; Phase 6 lands `IBaseDto.cs` + `BaseDtoValidator.cs` here
- `src/BaseApi.Core/DependencyInjection/` — folder created Phase 5 for OTel/Health DI helpers (verify during research); Phase 6 lands `ValidationServiceCollectionExtensions.cs` + `MappingServiceCollectionExtensions.cs` here
- `src/BaseApi.Service/Program.cs` — current composition root; Phase 6 adds 2 lines (`AddBaseApiValidation` + `AddBaseApiMapping`) AFTER `AddHttpContextAccessor` + `AddProblemDetails` and BEFORE `AddControllers` (D-18)
- `tests/BaseApi.Tests/Middleware/WebAppFactory.cs` (Phase 4) — Phase 6 reuses; SC#3 facts extend it with TestDto + TestDtoValidator services
- `tests/BaseApi.Tests/` Phase 4 `TestController` location — Phase 6 adds one new endpoint (`/validate-test` or planner's chosen route per D-17 Claude's Discretion)
- `Directory.Build.props` — Phase 6 adds `<WarningsAsErrors>$(WarningsAsErrors);RMG007;RMG012;RMG020;RMG089</WarningsAsErrors>` per D-11/D-12 (preserve `$(WarningsAsErrors)` prefix so the addition is additive, not replacing; codes corrected from MP→RMG per RESEARCH A-01 amendment)
- `Directory.Packages.props` — NO edits needed; FluentValidation 12.1.1, FluentValidation.DependencyInjectionExtensions 12.1.1, and Riok.Mapperly 4.3.1 are ALREADY pinned (Phase 1 D-05 front-loaded all 22 pins)

### External Library Docs (research-phase)
- **FluentValidation 12.x** — `AbstractValidator<T>`, `Include(IValidator<T> other)`, `RuleFor(x => x.Member).NotEmpty().MaximumLength(N).Matches(regex)`, `AddValidatorsFromAssembly(Assembly, ServiceLifetime, includeInternalTypes)` discovery, `IValidator<T>.ValidateAsync(T)` Service-layer invocation. Verify breaking changes 11.x → 12.x (12.0 release notes — major version bump).
- **Riok.Mapperly 4.3.1** — `[Mapper] partial class` source-gen idiom; `[MapperIgnoreSource(string)]` + `[MapperIgnoreTarget(string)]` attributes; RMG007 (cannot map member — default Error), RMG012 (source member not found for target — default Warning), RMG020 (source member not mapped to target — default Warning), RMG089 (mapping nullable source to non-nullable target — default Info). Default `RequiredMappingStrategy = Both` (Mapperly 4.0+) — strict-mappings check fires on BOTH source AND target. (Original MP-prefix codes in pre-amendment CONTEXT were a documentation error; corrected per RESEARCH A-01.)

### Specs / ADRs
- No external ADRs or feature specs — requirements fully captured in REQUIREMENTS.md + ROADMAP.md + prior CONTEXT.md decisions.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`tests/BaseApi.Tests/Middleware/WebAppFactory.cs`** (Phase 4) — `WebApplicationFactory<Program>` with Postgres-backed DI; SC#3 reuses for the `/validate-test` endpoint fact (D-17).
- **Phase 4 `TestController`** — 8 deliberately-throwing endpoints already wired; Phase 6 adds 1 endpoint (`POST /validate-test` or planner-chosen route).
- **`src/BaseApi.Core/Exceptions/Handlers/ValidationExceptionHandler.cs`** (Phase 4) — already maps `FluentValidation.ValidationException` → HTTP 400 + ProblemDetails with `errors` map + `correlationId` + `instance`. Phase 6 does NOT touch this; the validator just THROWS the typed exception.
- **`Directory.Packages.props` Mapperly + FluentValidation pins** (Phase 1 D-05) — already at 4.3.1 / 12.1.1; no new CPM edits.

### Established Patterns
- **Phase 1 D-08 folder skeleton** — `BaseApi.Core/Validation/` already exists empty with `.gitkeep`. Phase 6 lands `IBaseDto.cs` + `BaseDtoValidator.cs` here.
- **Phase 1 D-02 + D-03 strictness** — `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` + nullable + analysis-mode-latest mean Phase 6's `IBaseDto` and `BaseDtoValidator<T>` MUST be warning-clean against Roslyn analyzer rules.
- **Phase 3 PERSIST-15** — `DbContext` Scoped DI lifetime verified end-to-end; Phase 6's `AddValidatorsFromAssembly` ServiceLifetime.Scoped (default) is consistent.
- **Phase 4 D-11 Program.cs ordering** — `AddProblemDetails` BEFORE `AddControllers`. Phase 6 inserts `AddBaseApiValidation` + `AddBaseApiMapping` between them (D-18). Phase 7 will absorb both into `AddBaseApi`.
- **Phase 4 fix-forward convention** — `fix(04-01) ad3f1a1` corrected Npgsql 9.0.0 → 8.0.9 mid-Plan-02. Phase 6 should anticipate similar fix-forward discipline if any package or csproj surfaces a Phase-1-time-of-pin mismatch (e.g., FluentValidation 12.1.1 + DependencyInjectionExtensions 12.1.1 lockstep — verify during planning).
- **Phase 3 D-15 + Phase 5 D-11 cleanup discipline** — `psql \l` byte-identical BEFORE/AFTER + xUnit v3 `[assembly: AssemblyFixture]` for end-of-suite teardown. Phase 6's verification plan inherits this contract for its new fact tests.

### Integration Points
- **`src/BaseApi.Service/Program.cs`** — Phase 6 adds 2 lines (D-18) after the `AddProblemDetails` block.
- **`Directory.Build.props`** — Phase 6 adds 1 line (D-11/D-12) — additive `<WarningsAsErrors>` preserving any existing value via `$(WarningsAsErrors)` prefix.
- **`tests/BaseApi.Tests/BaseApi.Tests.csproj`** — Phase 6 adds 1 `<PackageReference Include="Riok.Mapperly" PrivateAssets="all" ExcludeAssets="runtime" />` per Phase 1 D-07 (D-19).
- **`src/BaseApi.Service/BaseApi.Service.csproj`** — optional Phase 6 add of Riok.Mapperly per same convention (D-13 — planner's call; can defer to Phase 8).
- **`src/BaseApi.Core/Validation/`** — Phase 6 file landings: `IBaseDto.cs` + `BaseDtoValidator.cs`.
- **`src/BaseApi.Core/Mapping/`** — Phase 6 creates this new folder; lands `IEntityMapper.cs` only.
- **`src/BaseApi.Core/DependencyInjection/`** — Phase 6 lands `ValidationServiceCollectionExtensions.cs` + `MappingServiceCollectionExtensions.cs`.

</code_context>

<specifics>
## Specific Ideas

- The Phase 4 WAF + TestController pattern is the chosen verification scaffold for SC#3 — same approach proven across Phase 4 (31/31 GREEN) and Phase 5 (47/47 GREEN). Phase 6 extends, doesn't reinvent.
- `BaseDtoValidator<T>` is `public class` (non-abstract) — SC#1 says `Include(new BaseDtoValidator<MyDto>())`, and `new` requires instantiable.
- `IEntityMapper<,,,>` is the smallest viable interface — exactly 3 methods. No "Patch" overload, no "Validate" inside the mapper, no eager M2M handling. Keep the contract minimal so Phase 8 partial classes are pure source-gen.
- `params Assembly[]` on both DI extensions because tests need to pass the test assembly explicitly (e.g., `services.AddBaseApiValidation(typeof(Program).Assembly, typeof(TestDtoValidator).Assembly)`).
- The Mapperly diagnostic site MUST cover BaseApi.Tests — that's the SC#4 verification location — and the cleanest way is solution-wide `Directory.Build.props`.

</specifics>

<deferred>
## Deferred Ideas

- **`services.AddSingleton` vs `services.AddScoped` for IEntityMapper<,,,>** — Phase 6 chooses Singleton (D-15) because Mapperly source-gen produces stateless pure functions. If Phase 8 introduces a mapper that depends on a scoped service (e.g., `IHttpContextAccessor`), the lifetime decision is revisited at Phase 8. Not a Phase 6 concern.
- **Mapperly strict-mode `RequiredMappingStrategy`** — Mapperly 4.x defaults `RequiredMappingStrategy = Both` and emits RMG020 + RMG012 by default. Phase 6 leaves the default behavior; explicit `[MapperConfig(RequiredMappingStrategy = ...)]` is needed only if Phase 8 requires per-mapper override (NOT recommended — would weaken drift detection per D-08 rationale).
- **Custom validator severity (`WithSeverity(Severity.Warning)`)** — FluentValidation 12.x supports custom severity; not needed for VALID-05/06/07 (all are errors). Defer to per-entity validators in Phase 8 if any rule needs non-error severity.
- **Per-mapper performance config** — Mapperly's `[Mapper]` attribute can configure `EnumMappingStrategy`, `IgnoreObsoleteMembersStrategy`, etc. Phase 6 leaves all defaults; per-entity tuning is Phase 8's call.
- **`BaseApi.Service.csproj` Mapperly PackageReference** — planner may add now (D-13) for forward placement or defer to Phase 8 when concrete `[Mapper]` partial classes land in `BaseApi.Service`.
- **Mapper auto-discovery edge cases** — `AddBaseApiMapping` reflection scan may encounter abstract classes, generic-open implementations, partial-build types. Planner enumerates edge cases; verification plan covers them with a fact (e.g., "registering only closed-generic implementations").
- **Reviewed Todos (not folded)** — None; no `.planning/todos/` directory exists.

</deferred>

---

*Phase: 06-validation-mapping-base*
*Context gathered: 2026-05-27*
