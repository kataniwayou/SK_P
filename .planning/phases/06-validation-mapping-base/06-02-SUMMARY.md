---
phase: 06-validation-mapping-base
plan: 02
subsystem: api
tags: [fluentvalidation, mapperly, dependency-injection, dotnet8, baseapi-tests, source-gen, verification]

# Dependency graph
requires:
  - phase: 06-validation-mapping-base
    plan: 01
    provides: "IBaseDto / BaseDtoValidator<T> / IEntityMapper<,,,> / AddBaseApiValidation / AddBaseApiMapping seam in BaseApi.Core; Mapperly RMG promotion solution-wide; WebAppFactory unsealed for subclassing"

provides:
  - "Behavioral verification of Phase 6 SC#1-4 via 7 fact-test classes (BaseDtoValidatorRuleTests, BaseDtoValidatorIncludeTests, ValidatorAutoDiscoveryTests, ValidationEndpointTests, MapperRegistrationTests, MapperlyCompileTests, PackageAuditTests)"
  - "6 test scaffolds enabling SC#1-4 verification (TestEntity, TestDtos, TestDtoValidator, TestValidationService, TestEntityMapper, ValidationWebAppFactory)"
  - "TestController extension exposing /test/validate endpoint (SC#3 HTTP-roundtrip surface)"
  - "Demonstrated [MapperIgnoreTarget] / [MapperIgnoreSource] attribute patterns Phase 8's 5 entity mappers MUST replicate across all 3 mapper methods (ToEntity / Update / ToRead)"

affects: [07-base-controller-service (test patterns inherited — WAF subclass + DI fact-test idioms), 08-concrete-entities (5 entity mappers must replicate the 3-method [MapperIgnoreTarget]/[MapperIgnoreSource] attribute pattern proven here; 5 entity validators absorb base rules via Include with no per-validator DI line)]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Mapperly [MapperIgnoreTarget]/[MapperIgnoreSource] 3-method attribute pattern — ToEntity ignores 5 target server-side fields, Update ignores 5 target server-side fields, ToRead ignores 4 source audit fields (TestReadDto carries Id but NOT audit fields)"
    - "WebAppFactory subclass + ConfigureTestServices override + base.ConfigureWebHost(builder) call — Pitfall 5 resolution for Tests-assembly validator discovery"
    - "Service-layer FluentValidation explicit ValidateAsync — VALID-03 proof end-to-end via HTTP POST → controller → service.ValidateAsync → throw FluentValidation.ValidationException → Phase 4 ValidationExceptionHandler → 400 ProblemDetails"
    - "Closed-generic IEntityMapper<,,,> Singleton-lifetime DI resolution — m1 == m2 assertion via Assert.Same"

key-files:
  created:
    - "tests/BaseApi.Tests/Validation/TestEntity.cs (20 LOC) — scaffold"
    - "tests/BaseApi.Tests/Validation/TestDtos.cs (16 LOC) — scaffold (3 records)"
    - "tests/BaseApi.Tests/Validation/TestDtoValidator.cs (18 LOC) — scaffold"
    - "tests/BaseApi.Tests/Validation/TestValidationService.cs (23 LOC) — scaffold"
    - "tests/BaseApi.Tests/Validation/TestEntityMapper.cs (57 LOC) — scaffold ([Mapper] partial class : IEntityMapper<,,,>)"
    - "tests/BaseApi.Tests/Validation/ValidationWebAppFactory.cs (42 LOC) — scaffold (WAF subclass)"
    - "tests/BaseApi.Tests/Validation/BaseDtoValidatorRuleTests.cs (99 LOC) — fact (10 facts: VALID-05/06/07)"
    - "tests/BaseApi.Tests/Validation/BaseDtoValidatorIncludeTests.cs (41 LOC) — fact (2 facts: SC#1/VALID-04)"
    - "tests/BaseApi.Tests/Validation/ValidatorAutoDiscoveryTests.cs (53 LOC) — fact (2 facts: SC#2/VALID-02)"
    - "tests/BaseApi.Tests/Validation/ValidationEndpointTests.cs (61 LOC) — fact (2 facts: SC#3/VALID-03)"
    - "tests/BaseApi.Tests/Validation/MapperRegistrationTests.cs (46 LOC) — fact (2 facts: SC#4 DI half / HTTP-10)"
    - "tests/BaseApi.Tests/Validation/MapperlyCompileTests.cs (94 LOC) — fact (3 facts: SC#4 runtime half)"
    - "tests/BaseApi.Tests/Validation/PackageAuditTests.cs (60 LOC) — fact (2 facts: VALID-01)"
  modified:
    - "tests/BaseApi.Tests/Endpoints/TestController.cs (+12 lines: 1 using, 1 doc-map entry, 8-line Validate action)"

key-decisions:
  - "TestEntityMapper requires attribute coverage on ALL 3 methods (ToEntity / Update / ToRead) — not just Update as the plan-as-written specified. Plan gap fix-forward per Rule 3."
  - "ToRead uses [MapperIgnoreSource] for the 4 audit fields on TestEntity that TestReadDto does not carry (Id IS on TestReadDto per HTTP-07). Preserves drift detection — a NEW property on TestEntity not in any DTO still fires RMG020."
  - "Filter-overridden by MTP runner: dotnet test with --filter ran the full 76-fact suite instead of the BaseApi.Tests.Validation subset — strictly stronger evidence than the filtered subset"

requirements-completed: [VALID-01, VALID-02, VALID-03, VALID-04, VALID-05, VALID-06, VALID-07, HTTP-10]

# Metrics
duration: ~20 min
completed: 2026-05-27
---

# Phase 6 Plan 02: Validation + Mapping Base Verification Summary

**6 test scaffolds + 1 TestController endpoint + 7 fact-test classes verify Phase 6 SC#1-4 end-to-end; 76/76 GREEN across 3 consecutive full-suite runs; byte-identical psql `\l` snapshots; telemetry.jsonl absent post-suite; drift-detection probe confirmed strict-mapping promotion is live across all 3 mapper methods.**

## Performance

- **Duration:** ~20 min (2026-05-27 plan execution; effective edit/build/test cycle including the Run-1 cold-start flakiness investigation)
- **Started:** 2026-05-27 (immediately after Plan 06-01 completion at 12:01:16Z)
- **Tasks:** 3 implementation + 1 verification gate
- **Files created:** 13 (630 LOC total across scaffolds + facts)
- **Files modified:** 1 (TestController.cs, +12 lines)

## Accomplishments

- **Task 1 — 6 scaffolds:** TestEntity (BaseEntity + Note), TestDtos (TestCreateDto/TestUpdateDto/TestReadDto records implementing IBaseDto), TestDtoValidator (Include-only body), TestValidationService (Service-layer ValidateAsync wrapper), TestEntityMapper ([Mapper] partial class implementing IEntityMapper<,,,> with 14 ignore attributes across 3 methods), ValidationWebAppFactory (WAF subclass with Tests-assembly scan).
- **Task 2 — TestController extension:** added `/test/validate` endpoint with `[FromServices] TestValidationService svc, [FromBody] TestUpdateDto dto, CancellationToken ct`. Extended class-level XML doc endpoint map to 9 items. Existing 8 routes byte-identical.
- **Task 3 — 7 fact-test classes (23 [Fact]/[Theory] methods → 29 effective tests with Theory expansion):**
  - BaseDtoValidatorRuleTests: 10 facts (3 [Fact] + 2 [Theory] expansions = 6+4) covering VALID-05/06/07.
  - BaseDtoValidatorIncludeTests: 2 facts (SC#1/VALID-04 — Include absorbs both Name AND Version base rules without restating; happy-path also asserted).
  - ValidatorAutoDiscoveryTests: 2 facts (SC#2/VALID-02 — DI scan registers TestDtoValidator; Scoped lifetime verified via same-scope same-instance AND different-scope different-instance).
  - ValidationEndpointTests: 2 facts (SC#3/VALID-03 — bad-DTO 400 ProblemDetails with errors["Version"] populated + correlationId parity; good-DTO 200).
  - MapperRegistrationTests: 2 facts (SC#4 DI half / HTTP-10 — closed-generic IEntityMapper<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto> resolves to TestEntityMapper; Singleton lifetime verified).
  - MapperlyCompileTests: 3 facts (SC#4 runtime half — ToEntity copies all 4 Create-DTO fields; Update mutates in place preserving server-side fields originalId/originalCreatedAt/originalCreatedBy; ToRead projects entity → read DTO including Id).
  - PackageAuditTests: 2 facts (VALID-01 — FluentValidation.AspNetCore absent from every csproj AND Directory.Packages.props).
- **Task 4 — End-of-phase verification gate (all 4 checks PASSED automatically by executor):**
  - Check 1: 3 consecutive full-suite GREEN runs (Runs 2/3/4 at 76/76 — Run 1 had Phase 5 fixture cold-start flakiness, well-known and documented in Phase 5 SUMMARY).
  - Check 2: psql `\l` BEFORE/AFTER byte-identical (`diff` empty — zero leaked `stepsdb_test_*` databases).
  - Check 3: `tests/.otel-out/telemetry.jsonl` absent post-suite (only `.gitkeep` remains — Phase 5 `OtelEndOfSuiteCleanup` assembly-fixture worked correctly).
  - Check 4 (OPTIONAL): drift-detection probe — temporarily adding `Drift` property to TestEntity caused build FAILURE with 3 errors (RMG012 from ToEntity, RMG012 from Update, RMG020 from ToRead) — proves strict-mapping promotion is live across ALL 3 mapper methods. Probe reverted, clean rebuild confirmed.

## Task Commits

Each task was committed atomically (per-task commit protocol):

1. **Task 1: 6 verification scaffolds** — `d4a389d` (feat) — also embeds the Rule 3 plan-gap fix-forward for TestEntityMapper attribute coverage.
2. **Task 2: TestController /test/validate endpoint** — `57f4aa0` (feat)
3. **Task 3: 7 fact-test classes** — `ced3c55` (test)

## Files Created/Modified

### Created (13 net new files in `tests/BaseApi.Tests/Validation/`)

| File | LOC | Role |
|------|-----|------|
| TestEntity.cs | 20 | Scaffold — BaseEntity + Note scalar |
| TestDtos.cs | 16 | Scaffold — 3 records (Create/Update/Read) implementing IBaseDto |
| TestDtoValidator.cs | 18 | Scaffold — Include-only body |
| TestValidationService.cs | 23 | Scaffold — Service-layer ValidateAsync wrapper |
| TestEntityMapper.cs | 57 | Scaffold — [Mapper] partial class : IEntityMapper<,,,> (14 ignore attrs across 3 methods) |
| ValidationWebAppFactory.cs | 42 | Scaffold — WAF subclass with Tests-assembly validator scan |
| BaseDtoValidatorRuleTests.cs | 99 | Fact — VALID-05/06/07 unit tests |
| BaseDtoValidatorIncludeTests.cs | 41 | Fact — SC#1/VALID-04 Include absorption |
| ValidatorAutoDiscoveryTests.cs | 53 | Fact — SC#2/VALID-02 DI scan + Scoped lifetime |
| ValidationEndpointTests.cs | 61 | Fact — SC#3/VALID-03 HTTP roundtrip |
| MapperRegistrationTests.cs | 46 | Fact — SC#4 DI half / HTTP-10 |
| MapperlyCompileTests.cs | 94 | Fact — SC#4 runtime half |
| PackageAuditTests.cs | 60 | Fact — VALID-01 audit |

**Total scaffold LOC: 176; fact LOC: 454; combined: 630 LOC.**

### Modified (1 file edit)

- `tests/BaseApi.Tests/Endpoints/TestController.cs` (+12 lines): added 1 using directive (`BaseApi.Tests.Validation`), extended class-level XML doc endpoint coverage list with 9th `<item>` for `/test/validate`, appended 8-line `Validate` action implementation. All 8 existing endpoint method declarations + nested `ValidationDto` type byte-identical pre-Phase-6.

## Decisions Made

- **TestEntityMapper attribute coverage extended to all 3 methods** (vs. plan's specified attribute coverage on Update only): the plan correctly identified Update needs 5 [MapperIgnoreTarget] per Pitfall 2, but did not enumerate that ToEntity ALSO targets the same 5 server-side fields on TestEntity (RMG012 fires identically) and ToRead has 4 audit fields on the source TestEntity that TestReadDto lacks (RMG020 fires). Resolution: replicate the 5 [MapperIgnoreTarget] block on ToEntity; add 4 [MapperIgnoreSource] block on ToRead. Drift detection is preserved — newly added entity properties still fire RMG012/RMG020 on the affected method(s) unless explicitly ignored. Phase 8's 5 entity mappers MUST replicate this exact 3-method attribute pattern.
- **TestReadDto retains the plan's 5-field shape** (Id + Name + Version + Description + Note) rather than including audit fields. HTTP-07 says "Read DTOs include server-side fields" but the plan's DTO definition explicitly excludes audit fields from TestReadDto. The decision is consistent with the plan's stated DTO contract; if Phase 8's entity Read DTOs SHOULD include audit fields, that's a Phase 8 concern (the [MapperIgnoreSource] pattern on ToRead becomes unnecessary).
- **MTP filter override accepted as stronger evidence** (vs. the plan's `--filter "FullyQualifiedName~BaseApi.Tests.Validation"` request): the Microsoft.Testing.Platform runner emits warning `MTP0001: VSTest-specific properties are set but will be ignored` and runs the full 76-fact suite. This is strictly stronger evidence than the requested 13-fact subset — the full Phase 1-5 carryover battery (47 facts) PLUS the 29 new Phase 6 facts ALL pass green together. The 3-consecutive-runs gate effectively becomes a regression-free attestation of the entire codebase.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Plan gap blocking build] TestEntityMapper missing attribute coverage on ToEntity and ToRead methods**

- **Found during:** Task 1 build verification (`dotnet build SK_P.sln -c Release` exited with 9 errors)
- **Issue:** Plan-as-written instructed the executor to apply 5 `[MapperIgnoreTarget]` attributes ONLY to the `Update` method (per CONTEXT D-08 amended). However, Mapperly's `RequiredMappingStrategy = Both` default with Plan 06-01's RMG012 + RMG020 promotion ALSO fires on:
  - `ToEntity(TestCreateDto)` — TestEntity has 5 target members (Id + 4 audit fields) not on TestCreateDto → 5 RMG012 errors at line 26.
  - `ToRead(TestEntity)` — TestEntity has 4 source members (4 audit fields) not on TestReadDto → 4 RMG020 errors at line 35.
- **Fix:** Replicated the 5-attribute `[MapperIgnoreTarget]` block on `ToEntity` (same 5 server-side fields). Added a 4-attribute `[MapperIgnoreSource]` block on `ToRead` (4 audit fields — TestReadDto carries Id but not the 4 audit fields). Updated the class-level XML doc to enumerate the 3 attribute sites with rationale for each.
- **Files modified:** `tests/BaseApi.Tests/Validation/TestEntityMapper.cs` (added 5 attrs on ToEntity, 4 attrs on ToRead, extended XML doc)
- **Verification:** `dotnet build SK_P.sln -c Release --no-restore` exits 0 warnings/errors after the fix. Check 4 drift-detection probe (Task 4) later confirmed the strict-mapping promotion is LIVE across all 3 methods — adding a Drift property to TestEntity fires errors on ToEntity (RMG012), Update (RMG012), AND ToRead (RMG020), proving the 14-attribute coverage preserves drift detection.
- **Committed in:** `d4a389d` (Task 1 commit — applied before commit so the deviation is captured in the single Task 1 commit; commit message documents the Rule 3 fix-forward explicitly).
- **Phase 8 impact:** Phase 8's 5 entity mappers MUST follow the 3-method attribute pattern proven here (not the 1-method pattern the plan originally specified). If concrete Phase 8 Read DTOs DO include audit fields, the [MapperIgnoreSource] block on ToRead becomes unnecessary; if Phase 8 Update DTOs DO include Id/audit fields (which would violate HTTP-06), the [MapperIgnoreTarget] block on Update becomes unnecessary. The pattern adapts based on DTO field coverage.

---

**Total deviations:** 1 auto-fixed (1 Rule 3 — plan gap blocking build).
**Impact on plan:** No scope creep. Pure additive attribute coverage to make the plan's existing scaffold compile under the plan's existing strict-mapping promotion. All other plan content implemented byte-identically per verbatim instructions.

## Authentication Gates

None. All work is build-time + DI-wiring + in-process WAF testing with no external service authentication required.

## Issues Encountered

- **Run 1 of 3 had 7 failures — Phase 5 fixture cold-start flakiness.** The Phase 5 Observability tests (LogExportTests / LogLevelFilterTests / MetricsExportTests / TraceExportTests) failed with "Collection was empty" because the OTel Collector had only been up for ~28 seconds when Run 1 started; telemetry batching + scrape interval hadn't fully cycled. Runs 2/3/4 all 76/76 GREEN. This matches the Phase 5 SUMMARY's note about "3 advisory code-review warnings tracked for Phase 7/8 — all fixture-lifecycle robustness items, none in production paths." The plan's resume-signal explicitly accommodated this: "If Check 1 fails (any test red): re-run once more to confirm flakiness; if still red, capture the failing assertion text and request a fix-forward." Three consecutive GREEN runs (2, 3, 4) confirm the gate. **No Phase 6 code change required.**
- **No other issues encountered.** No Phase 6 code-correctness issues, no fix-forward to Plan 06-01, no architectural surprises.

## Verification Evidence

### Check 1 — 3 consecutive full-suite GREEN runs

```
Run 1: Failed: 7, Passed: 69, Skipped: 0, Total: 76, Duration: 17s 966ms  ← Phase 5 fixture cold-start (NOT Phase 6 code)
Run 2: Failed: 0, Passed: 76, Skipped: 0, Total: 76, Duration: 18s 171ms
Run 3: Failed: 0, Passed: 76, Skipped: 0, Total: 76, Duration: 18s 023ms
Run 4: Failed: 0, Passed: 76, Skipped: 0, Total: 76, Duration: 17s 933ms
```
3 consecutive GREEN runs (2/3/4) → gate condition met. Suite count 76 = 47 Phase 1-5 carryover + 29 new Phase 6 facts (including Theory expansion of BaseDtoValidatorRuleTests). Plan target was ≥60.

### Check 2 — byte-identical psql `\l` snapshots

```
BEFORE: /c/temp/psql_before_phase6.txt (11 lines, 4 baseline DBs: postgres, stepsdb, template0, template1)
AFTER:  /c/temp/psql_after_phase6.txt
diff /c/temp/psql_before_phase6.txt /c/temp/psql_after_phase6.txt → EMPTY (zero diff)
```
Zero leaked `stepsdb_test_*` databases. Phase 3 D-15 cleanup discipline preserved.

### Check 3 — telemetry.jsonl absence post-suite

```
ls tests/.otel-out/ → only .gitkeep (0 bytes)
```
Phase 5 D-11 `OtelEndOfSuiteCleanup` assembly-fixture worked correctly across all 3 (4 including Run 1) runs.

### Check 4 — drift-detection negative fact (RECOMMENDED — executed)

```
TEMP CHANGE: added `public string Drift { get; set; } = string.Empty;` to TestEntity.cs
BUILD RESULT: FAILED with 3 errors (and 0 warnings):
  TestEntityMapper.cs(38,5): error RMG012: The member Drift on the mapping target type TestEntity was not found on the mapping source type TestCreateDto
  TestEntityMapper.cs(45,5): error RMG012: The member Drift on the mapping target type TestEntity was not found on the mapping source type TestUpdateDto
  TestEntityMapper.cs(52,5): error RMG020: The member Drift on the mapping source type TestEntity is not mapped to any member on the mapping target type TestReadDto
REVERT: git checkout -- tests/BaseApi.Tests/Validation/TestEntity.cs
REBUILD RESULT: Build succeeded, 0 Warning(s), 0 Error(s).
```
Strict-mapping promotion is LIVE across all 3 mapper methods (ToEntity, Update, ToRead). Phase 8 can rely on this drift detection.

## REQ-ID Closure Table

| REQ-ID | Description | Closing Fact |
|--------|-------------|--------------|
| VALID-01 | No FluentValidation.AspNetCore reference | PackageAuditTests both facts |
| VALID-02 | Validators discovered via AddValidatorsFromAssembly | ValidatorAutoDiscoveryTests both facts |
| VALID-03 | Explicit ValidateAsync in Service layer → HTTP 400 ProblemDetails | ValidationEndpointTests both facts |
| VALID-04 | BaseDtoValidator<T> shared rules absorbed via Include | BaseDtoValidatorIncludeTests Test_Include_AbsorbsBaseRules_WithoutRestating |
| VALID-05 | Name NotEmpty + MaxLength(200) | BaseDtoValidatorRuleTests Name_Empty_Rejected / 201Chars_Rejected / 200Chars_Accepted |
| VALID-06 | Version NotEmpty + strict SemVer regex | BaseDtoValidatorRuleTests Version_BadShape_Rejected (6 InlineData) + Version_StrictSemVer_Accepted (4 InlineData) |
| VALID-07 | Description MaxLength(2000), null valid | BaseDtoValidatorRuleTests Description_Null_Accepted / 2001Chars_Rejected / 2000Chars_Accepted |
| HTTP-10 | IEntityMapper<TEntity, TCreate, TUpdate, TRead> contract + DI registration | MapperRegistrationTests both facts + MapperlyCompileTests 3 facts |

All 8 Phase 6 REQ-IDs CLOSED with automated proof.

## Phase 6 ROADMAP SC Closure Evidence

| SC | Description | Closing Fact-Class |
|----|-------------|--------------------|
| SC#1 | Include(new BaseDtoValidator<MyDto>()) absorbs base rules | BaseDtoValidatorIncludeTests (Test_Include_AbsorbsBaseRules_WithoutRestating asserts BOTH Name AND Version error in TestDtoValidator's empty body) |
| SC#2 | AddValidatorsFromAssembly discovery; no FluentValidation.AspNetCore | ValidatorAutoDiscoveryTests + PackageAuditTests (DI scan succeeds; audit-absence verified) |
| SC#3 | Service-layer ValidateAsync; controller surface as 400 | ValidationEndpointTests (HTTP POST roundtrip → 400 ProblemDetails with errors["Version"] + correlationId parity matching X-Correlation-Id header) |
| SC#4 | Mapperly [Mapper] partial class : IEntityMapper<,,,> compiles with RMG promotion | TestEntityMapper.cs compiles under Plan 06-01's WarningsAsErrors RMG promotion (BUILD-half SC#4 proof); MapperRegistrationTests + MapperlyCompileTests prove runtime DI + behavior (RUNTIME-half); Check 4 drift-detection probe proves the gate is LIVE |

## Next Phase Readiness

**Phase 7 (BaseController/BaseService + AddBaseApi composition root) inherits:**
- All Phase 6 production code (Plan 06-01) intact and proven via Plan 06-02 facts.
- TestController pattern proven for the SC#3-style controller-thin → service-layer-thick → ValidateAsync → throw → handler chain.
- ValidationWebAppFactory subclass pattern proven for Tests-assembly DI overrides.

**Phase 8 (5 concrete entities) inherits:**
- Concrete validators use `Include(new BaseDtoValidator<MyDto>())` pattern (proven by BaseDtoValidatorIncludeTests).
- Concrete `[Mapper] partial class : IEntityMapper<,,,>` mappers MUST replicate the 3-method attribute pattern from TestEntityMapper:
  - `ToEntity`: 5 `[MapperIgnoreTarget]` for the Id + 4 audit fields (or fewer if the entity has different server-side fields).
  - `Update`: 5 `[MapperIgnoreTarget]` for the Id + 4 audit fields (D-08 amended).
  - `ToRead`: 4 `[MapperIgnoreSource]` for the 4 audit fields if the Read DTO doesn't expose them (omit if Read DTO carries them per HTTP-07).
- Drift detection is LIVE: any new entity property NOT wired through DTOs (or not added to the ignore list) WILL break the build with RMG012/RMG020. This is the intended catch-DTO-drift mechanism that justifies Plan 06-01's RMG promotion.
- Zero per-entity DI lines needed for either validators or mappers — `AddBaseApiValidation(typeof(Program).Assembly)` + `AddBaseApiMapping(typeof(Program).Assembly)` (both in Program.cs since Plan 06-01) auto-discover everything.

## TDD Gate Compliance

Plan 06-02 has `type: execute` at the plan level and Task 3 has `tdd="true"` at the task level. However, because Plan 06-01 already shipped all production code that Task 3 verifies, the tests pass IMMEDIATELY upon writing them — there is no RED phase for these characterization/verification tests. This is correct behavior: per the TDD section's "Fail-fast rule," tests passing unexpectedly on first run indicate the feature already exists, which is exactly the case here (Plan 06-01 is the source of the feature; Plan 06-02 is its behavioral attestation). The Task 3 commit uses `test(...)` prefix to acknowledge the test-first commit style.

The plan-level commits sequence is:
1. `d4a389d` — `feat(06-02): land 6 ... scaffolds` (Task 1 — scaffolds + Mapperly attribute fix)
2. `57f4aa0` — `feat(06-02): add [HttpPost("validate")] endpoint` (Task 2 — controller extension)
3. `ced3c55` — `test(06-02): land 7 ... fact-test classes` (Task 3 — facts; verifies all prior production code)

The verification gate (Task 4) is currently AWAITING USER APPROVAL (checkpoint type human-verify).

## Known Stubs

None. All scaffolds are wired end-to-end (no placeholder data, no "coming soon" text, no unused interfaces). The TestEntityMapper [Mapper] partial class body is filled by Mapperly source-gen at build time (verified by Task 1 build success and MapperlyCompileTests runtime exercise).

## Self-Check: PASSED

All 13 created files exist on disk; SUMMARY.md exists at `.planning/phases/06-validation-mapping-base/06-02-SUMMARY.md`; all 3 task commits (`d4a389d`, `57f4aa0`, `ced3c55`) resolve via `git log --oneline -10`. The post-Task-3 modified TestController.cs is part of `57f4aa0`. Verification evidence (psql diff empty, telemetry.jsonl absent, 3-consecutive GREEN runs, drift-probe build failure with revert) captured above.

---
*Phase: 06-validation-mapping-base*
*Completed: 2026-05-27 (verification gate awaiting user approval)*
