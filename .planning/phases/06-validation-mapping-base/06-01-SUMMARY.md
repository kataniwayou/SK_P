---
phase: 06-validation-mapping-base
plan: 01
subsystem: api
tags: [fluentvalidation, mapperly, dependency-injection, dotnet8, baseapi-core, source-gen]

# Dependency graph
requires:
  - phase: 01-repository-scaffold
    provides: "TreatWarningsAsErrors=true global gate, CPM (Directory.Packages.props pins for FluentValidation 12.1.1 + DI extensions + Riok.Mapperly 4.3.1), folder skeleton with Validation/ and DependencyInjection/ .gitkeeps, source-gen package convention (PrivateAssets=all ExcludeAssets=runtime)"
  - phase: 03-ef-core-persistence-base
    provides: "BaseEntity (8 fields — Name/Version/Description nullability mirrored on IBaseDto), Scoped DI lifetime precedent (PERSIST-15), xmin shadow concurrency token (informs IEntityMapper.Update void-mutating semantics)"
  - phase: 04-cross-cutting-middleware-error-handling
    provides: "ValidationExceptionHandler (FluentValidation.ValidationException → HTTP 400 + ProblemDetails), AddProblemDetails customizer (correlationId injection), Program.cs composition root with load-bearing ordering (AddProblemDetails before AddControllers), WebAppFactory<Program> + TestController scaffold"
  - phase: 05-observability-health-probes
    provides: "Program.cs IHostedService pattern (informs Phase 7 AddBaseApi cut-paste target placement), public sealed class precedent for IStartupGate (informs BaseDtoValidator non-sealed exception per D-05)"

provides:
  - "IBaseDto marker interface (Name/Version/Description? — 3 get-only getters mirroring BaseEntity narrative-field nullability)"
  - "BaseDtoValidator<T> public non-sealed generic class with NotEmpty+MaxLength(200) Name, NotEmpty+strict-SemVer regex Version, MaxLength(2000) Description"
  - "IEntityMapper<TEntity, TCreate, TUpdate, TRead> generic 3-method mapping contract (ToEntity, void Update, ToRead) for HTTP-10"
  - "AddBaseApiValidation DI extension (Scoped lifetime, includeInternalTypes:false, params Assembly[])"
  - "AddBaseApiMapping DI extension (closed-generic reflection scan via GetExportedTypes, Singleton lifetime)"
  - "Solution-wide promotion of Mapperly RMG007/RMG012/RMG020/RMG089 to build errors via Directory.Build.props (corrects MP→RMG per RESEARCH A-01)"
  - "FluentValidation.DependencyInjectionExtensions PackageReference on BaseApi.Core.csproj (resolves AddValidatorsFromAssembly)"
  - "Riok.Mapperly PackageReference on BaseApi.Tests.csproj with source-gen attributes (PrivateAssets=all ExcludeAssets=runtime)"
  - "WebAppFactory unsealed (public class, no longer public sealed) — Plan 06-02 ValidationWebAppFactory can subclass"
  - "Program.cs D-18 wiring: AddBaseApiValidation + AddBaseApiMapping inserted between AddProblemDetails and AddExceptionHandler chain"

affects: [06-02 (verification battery — TestDtoValidator + TestEntityMapper + endpoint roundtrip + WAF subclass), 07-base-controller-service (AddBaseApi composition root absorbs both DI extension calls verbatim), 08-concrete-entities (5 concrete validators via Include + 5 [Mapper] partial classes via IEntityMapper<,,,>)]

# Tech tracking
tech-stack:
  added:
    - "FluentValidation.DependencyInjectionExtensions 12.1.1 (on BaseApi.Core.csproj) — adds AddValidatorsFromAssembly assembly-scan API"
    - "Riok.Mapperly 4.3.1 (on BaseApi.Tests.csproj only — source-gen analyzer for Plan 06-02 SC#4 partial-class scaffold)"
  patterns:
    - "Marker-interface generic constraint (where T : IBaseDto) for shared validation rules — concrete validators absorb via Include(new BaseDtoValidator<MyDto>())"
    - "Closed-generic reflection scan via Assembly.GetExportedTypes() + typeof(IGeneric<,,,>) comparison — safer than GetTypes() (Pitfall 7)"
    - "params Assembly[] DI extension signature — flexible for production single-assembly wiring AND multi-assembly test wiring (Pitfall 5 remediation)"
    - "DI extension class in BaseApi.Core/DependencyInjection/<Concern>ServiceCollectionExtensions.cs follows Microsoft.Extensions.* convention — first BaseApi.Core extension class establishes Phase 7 AddBaseApi precedent"
    - "Solution-wide Mapperly diagnostic promotion via Directory.Build.props <WarningsAsErrors> (NOT per-csproj per Phase 1 D-04 supersession)"

key-files:
  created:
    - "src/BaseApi.Core/Validation/IBaseDto.cs (25 LOC)"
    - "src/BaseApi.Core/Validation/BaseDtoValidator.cs (42 LOC)"
    - "src/BaseApi.Core/Mapping/IEntityMapper.cs (32 LOC) — first file in new Mapping/ folder"
    - "src/BaseApi.Core/DependencyInjection/ValidationServiceCollectionExtensions.cs (40 LOC)"
    - "src/BaseApi.Core/DependencyInjection/MappingServiceCollectionExtensions.cs (56 LOC)"
  modified:
    - "src/BaseApi.Core/BaseApi.Core.csproj (added FluentValidation.DependencyInjectionExtensions PackageReference)"
    - "src/BaseApi.Service/Program.cs (D-18: AddBaseApiValidation + AddBaseApiMapping between AddProblemDetails and exception-handler chain; new using BaseApi.Core.DependencyInjection)"
    - "Directory.Build.props (D-11/D-12: WarningsAsErrors RMG007/RMG012/RMG020/RMG089 added; D-04 comment updated MP→RMG)"
    - "tests/BaseApi.Tests/BaseApi.Tests.csproj (D-19: Riok.Mapperly PackageReference with PrivateAssets=all ExcludeAssets=runtime)"
    - "tests/BaseApi.Tests/Middleware/WebAppFactory.cs (removed `sealed` keyword from class declaration so Plan 06-02 ValidationWebAppFactory can subclass)"

key-decisions:
  - "Followed verbatim plan content for 5 new C# files (RESEARCH Code Examples 1-5)"
  - "Removed MP0001/MP0011/MP0020/MP0021 codes from Directory.Build.props comment block to satisfy Verification line 734 grep-empty assertion (Rule 1 — plan's own verbatim sub-edit text contradicted the plan's own verification criterion; chose verification criterion as authoritative since it is the success gate)"

patterns-established:
  - "Pattern: BaseDtoValidator<T> Include — concrete Phase 8 validators absorb shared rules via Include(new BaseDtoValidator<MyDto>()) without restating them"
  - "Pattern: IEntityMapper<,,,> implementation contract — Phase 8 concrete mappers will be `[Mapper] partial class : IEntityMapper<TEntity, TCreate, TUpdate, TRead>` with 5 [MapperIgnoreTarget] attributes on Update method per D-08 amendment"
  - "Pattern: AddBaseApi*-style DI extension — Phase 7 AddBaseApi composition root will absorb both Phase 6 extension calls (AddBaseApiValidation + AddBaseApiMapping) as mechanical cut-paste with zero behavior change"
  - "Pattern: Solution-wide diagnostic promotion via Directory.Build.props (not per-csproj) — sets precedent for any future code-quality gate additions"

requirements-completed: [VALID-01, VALID-02, VALID-04, VALID-05, VALID-06, VALID-07, HTTP-10]

# Metrics
duration: ~9 min
completed: 2026-05-27
---

# Phase 6 Plan 01: Validation + Mapping Base Summary

**BaseApi.Core IBaseDto + BaseDtoValidator<T> (FluentValidation 12.x SemVer regex base) + IEntityMapper<,,,> 3-method contract + two DI auto-discovery extensions wired into Program.cs D-18 slot, with solution-wide Mapperly RMG diagnostic promotion and WebAppFactory unsealed for Plan 06-02 subclassing.**

## Performance

- **Duration:** ~9 min (2026-05-27T11:52:23Z planning end → 2026-05-27T12:01:16Z plan execution end; effective build/edit/commit cycle ~9 min)
- **Started:** 2026-05-27T11:52:30Z (approx — first file write)
- **Completed:** 2026-05-27T12:01:16Z
- **Tasks:** 3
- **Files created:** 5 (195 LOC total)
- **Files modified:** 4

## Accomplishments

- Five new BaseApi.Core seam files landed verbatim from RESEARCH Code Examples 1-5: `IBaseDto` (marker interface), `BaseDtoValidator<T>` (generic FluentValidation base with the locked SemVer regex), `IEntityMapper<TEntity, TCreate, TUpdate, TRead>` (3-method contract), and the two DI extensions (`AddBaseApiValidation` + `AddBaseApiMapping`).
- Program.cs D-18 wiring inserted at the precise Phase-7-cut-paste slot — between `AddProblemDetails` close and the `AddExceptionHandler<NotFoundExceptionHandler>` chain start — so Phase 7's `AddBaseApi` will absorb both calls with zero behavior change.
- Directory.Build.props promoted the correct Mapperly RMG codes (RMG007/RMG012/RMG020/RMG089) solution-wide — the load-bearing entry is RMG089 (default Info severity, not promoted by global `TreatWarningsAsErrors`); the others are defense-in-depth + auditability per D-12.
- Phase 4 WebAppFactory unsealed (`public sealed class` → `public class`) so Plan 06-02 can subclass into `ValidationWebAppFactory` to add the Tests-assembly validator scan (Pitfall 5 remediation path A).
- Zero-warning build gate held: `dotnet build SK_P.sln` exits 0 in both Release and Debug.
- Phase 1-5 regression-free: full 47-fact pre-Phase-6 suite still passes (`Passed: 47, Failed: 0` — MTP overrode the `--filter` and ran the entire battery, which is stronger evidence than the smoke subset the plan asked for).

## Task Commits

Each task was committed atomically:

1. **Task 1: Create IBaseDto + BaseDtoValidator<T> + IEntityMapper<,,,>** — `416d40c` (feat)
2. **Task 2: AddBaseApiValidation + AddBaseApiMapping DI extensions + FluentValidation.DependencyInjectionExtensions PackageReference** — `2b702b1` (feat)
3. **Task 3: Wire Program.cs (D-18), update Directory.Build.props (D-11), add Mapperly to BaseApi.Tests.csproj, unseal WebAppFactory** — `b631ce8` (feat)

## Files Created/Modified

### Created (5 new C# files in BaseApi.Core)

- `src/BaseApi.Core/Validation/IBaseDto.cs` (25 LOC) — Marker interface; 3 get-only properties (Name/Version/Description?) mirroring BaseEntity nullability.
- `src/BaseApi.Core/Validation/BaseDtoValidator.cs` (42 LOC) — `public class BaseDtoValidator<T> : AbstractValidator<T> where T : IBaseDto` with the 3 locked rules. SemVer regex is a verbatim `@"..."` literal (Pitfall 3 — `\d` in a non-verbatim literal triggers CS1009 under `TreatWarningsAsErrors=true`).
- `src/BaseApi.Core/Mapping/IEntityMapper.cs` (32 LOC) — `public interface IEntityMapper<TEntity, TCreate, TUpdate, TRead>` with exactly 3 methods (`TEntity ToEntity(TCreate)`, `void Update(TUpdate, TEntity)`, `TRead ToRead(TEntity)`). First file in new `Mapping/` folder.
- `src/BaseApi.Core/DependencyInjection/ValidationServiceCollectionExtensions.cs` (40 LOC) — `AddBaseApiValidation(this IServiceCollection, params Assembly[])` wrapping `AddValidatorsFromAssembly` with `ServiceLifetime.Scoped` + `includeInternalTypes:false`.
- `src/BaseApi.Core/DependencyInjection/MappingServiceCollectionExtensions.cs` (56 LOC) — `AddBaseApiMapping(this IServiceCollection, params Assembly[])` with closed-generic reflection scan via `Assembly.GetExportedTypes()` (Pitfall 7 — safer than `GetTypes()`) and `services.AddSingleton(closedInterface, type)` per discovered mapper.

### Modified (4 wiring/config files)

- `src/BaseApi.Core/BaseApi.Core.csproj` — added `<PackageReference Include="FluentValidation.DependencyInjectionExtensions" />` adjacent to the existing FluentValidation entry; updated the ItemGroup comment to note the DI extensions package; no Version= attribute (CPM contract).
- `src/BaseApi.Service/Program.cs` — added `using BaseApi.Core.DependencyInjection;` to keep the BaseApi.Core.* group alphabetically sorted; inserted a 7-line comment block + 2 service-collection calls (`AddBaseApiValidation(typeof(Program).Assembly)` then `AddBaseApiMapping(typeof(Program).Assembly)`) between the `AddProblemDetails` closing `});` (line 68) and the `AddExceptionHandler<NotFoundExceptionHandler>` line; zero other lines touched.
- `Directory.Build.props` — superseded the stale Phase 1 D-04 comment block (lines 16-19) with the amended D-04 explanation noting RMG-prefix correction and solution-wide promotion (NOT per-csproj). Added a 4-line explanatory comment plus the `<WarningsAsErrors>$(WarningsAsErrors);RMG007;RMG012;RMG020;RMG089</WarningsAsErrors>` line as a sibling of the existing `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` line — additive via the `$(WarningsAsErrors);` prefix.
- `tests/BaseApi.Tests/BaseApi.Tests.csproj` — added a new ItemGroup containing the Mapperly PackageReference with the Phase 1 D-07 source-gen attributes (`PrivateAssets="all"` `ExcludeAssets="runtime"`). Placed between the Phase 4 Microsoft.AspNetCore.Mvc.Testing ItemGroup and the Phase 3 FrameworkReference ItemGroup. No Version= attribute (CPM).
- `tests/BaseApi.Tests/Middleware/WebAppFactory.cs` — single keyword removed: `public sealed class WebAppFactory` → `public class WebAppFactory`. All other file content byte-identical (XML doc, constructors, `ConfigureWebHost` override).

## Decisions Made

- **DI extension placement:** Phase 7 `AddBaseApi` cut-paste target is the slot between `AddProblemDetails` close and the IExceptionHandler chain start (D-18 verbatim). The 7-line comment header above the calls makes the Phase 7 migration obvious to future maintainers.
- **Mapperly RMG codes added but only RMG089 is load-bearing:** RMG007 defaults to Error (already promoted), RMG012/RMG020 default to Warning (already promoted by `TreatWarningsAsErrors=true`); RMG089 defaults to Info and is the ONLY code that requires explicit `<WarningsAsErrors>` listing to be promoted. The other three are listed for defense-in-depth (in case a future Mapperly release lowers their severity) and auditability per D-12.
- **WebAppFactory unsealed path A chosen** over path B (modifying base WAF to scan additional assemblies). Path A matches the Phase 5 OtelCollectorFixture precedent (also unsealed for nested subclasses) and isolates Phase 6 verification needs into `ValidationWebAppFactory` (Plan 06-02), keeping the Phase 4 base WAF unchanged for the existing 47-fact suite.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug in plan-as-written] Removed MP-code mentions from Directory.Build.props comment to satisfy own verification criterion**

- **Found during:** Task 3 (Directory.Build.props edit)
- **Issue:** Plan sub-edit 1 (Task 3 part (b)) provided a verbatim REPLACE block that explicitly contained the string `MP0001/MP0011/MP0020/MP0021` ("the original CONTEXT D-11 MP0001/MP0011/MP0020/MP0021 codes are a documentation error"). However, the same plan's verification line 734 + acceptance-criteria line 693 require: "No `MP0001`, `MP0011`, `MP0020`, or `MP0021` strings remain anywhere in the repo … verified by `grep -r "MP0001\|MP0011\|MP0020\|MP0021" Directory.Build.props src/ tests/` returning empty". Following the verbatim REPLACE block would have caused the plan to fail its own verification.
- **Fix:** Rephrased the amended D-04 comment to convey the same historical/educational meaning ("the original CONTEXT documentation used a non-existent MP-prefix that was corrected via Phase 6 RESEARCH A-01 amendment") without mentioning the specific MP-prefixed codes. The amendment is fully traceable via the linked RESEARCH A-01 reference.
- **Files modified:** `Directory.Build.props` (comment lines 15-20)
- **Verification:** `grep -r "MP0001\|MP0011\|MP0020\|MP0021" Directory.Build.props src/ tests/` returns empty (EMPTY_RESULT). `dotnet build SK_P.sln` still exits 0 with zero warnings in both Release and Debug after the comment rephrase.
- **Committed in:** `b631ce8` (Task 3 commit — applied before the commit so the deviation is captured in the single Task 3 commit).

---

**Total deviations:** 1 auto-fixed (1 Rule 1 — plan internal inconsistency)
**Impact on plan:** No scope creep. The fix was a pure comment-text rephrase that preserves the educational content (RMG correction history) while satisfying the plan's own grep-empty verification criterion. All other plan content was implemented byte-identically per the verbatim instructions.

## Issues Encountered

- **MTP overrode `--filter` and ran the full 47-fact suite instead of the 4-fact `CorrelationIdTests` subset.** The plan's verification step asked for `dotnet test ... --filter "FullyQualifiedName~CorrelationIdTests"` exit 0. MTP emitted warning `MTP0001: VSTest-specific properties are set but will be ignored when using Microsoft.Testing.Platform` and ran all 47 tests instead. Outcome: `Passed: 47, Failed: 0, Duration: 19s 426ms`. This is STRONGER evidence than the requested smoke subset — the full pre-Phase-6 suite is regression-free. The MTP0001 warning is a known xUnit v3 + MTP scaffold quirk (test-runner-side, never reaches code) and does not fail the build or test run.
- **None production-side.** No code-correctness issues, no fix-forward to earlier plans, no architectural surprises.

## User Setup Required

None — no external service configuration required. All work is build-time + DI-wiring infrastructure with no runtime dependencies beyond what Phase 5 already established (Postgres + OTel Collector via Docker Compose).

## Next Phase Readiness

**Plan 06-02 (verification battery) is unblocked:**
- BaseDtoValidator<T> infrastructure ready for `Include(new BaseDtoValidator<TestUpdateDto>())` SC#1 fact.
- AddBaseApiValidation extension ready for `ValidatorAutoDiscoveryTests` SC#2 fact (DI-scan resolution).
- IEntityMapper<,,,> interface + AddBaseApiMapping extension + Mapperly PackageReference + RMG promotion ready for SC#4 `[Mapper] partial class : IEntityMapper<...>` scaffold + `MapperRegistrationTests` (Singleton-lifetime DI fact).
- WebAppFactory unsealed enables `ValidationWebAppFactory : WebAppFactory` subclass to add the Tests-assembly validator scan (Pitfall 5 remediation) and register `TestValidationService` for the `/test/validate` SC#3 HTTP-roundtrip fact.

**Phase 7 (BaseController/BaseService/AddBaseApi composition root) inherits:**
- Both `AddBaseApi*` extension calls are positioned at the exact Program.cs slot Phase 7 will compose from — migration is a mechanical cut-paste of the 2 lines + 1 using-directive into `AddBaseApi`'s body with zero behavior change.

**Phase 8 (5 concrete entities) inherits:**
- VALID-08..20 plug in via `MyDtoValidator : AbstractValidator<MyDto> { Include(new BaseDtoValidator<MyDto>()); ... }` with zero base-rule rewriting.
- HTTP-10..16 plug in via `[Mapper] public sealed partial class MyMapper : IEntityMapper<MyEntity, MyCreateDto, MyUpdateDto, MyReadDto>` with the 5 `[MapperIgnoreTarget]` attributes per D-08 amended for the Update method.
- Zero per-entity DI registrations needed — `AddBaseApiValidation(typeof(Program).Assembly)` + `AddBaseApiMapping(typeof(Program).Assembly)` auto-discover all concrete validators + closed-generic mappers in the BaseApi.Service assembly.

## TDD Gate Compliance

Plan 06-01 is `type: execute` (not `type: tdd`) — no RED→GREEN→REFACTOR gate sequence applies. The 3 `feat(...)` commits are the correct atomic-task pattern.

## Self-Check: PASSED

All 5 new BaseApi.Core C# files exist on disk; SUMMARY.md exists; all 3 task commits (`416d40c`, `2b702b1`, `b631ce8`) resolve via `git log --oneline --all`.

---
*Phase: 06-validation-mapping-base*
*Completed: 2026-05-27*
