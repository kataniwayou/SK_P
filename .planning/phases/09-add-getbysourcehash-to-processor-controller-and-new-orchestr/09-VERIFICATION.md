---
phase: 09-add-getbysourcehash-to-processor-controller-and-new-orchestr
verified: 2026-05-28T00:00:00Z
status: human_needed
score: 6/6 must-haves verified (automated checks)
overrides_applied: 0
human_verification:
  - test: "Run dotnet test SK_P.sln --no-restore -c Release three consecutive times and confirm 138/138 Passed, Failed: 0 each run"
    expected: "Three consecutive GREEN runs, 138 facts passing, zero failures"
    why_human: "Cannot execute dotnet test in this environment; the SUMMARY documents 3 consecutive GREEN runs (138/138) but this must be confirmed by running the suite live"
  - test: "Confirm dotnet build SK_P.sln -c Release --no-restore and -c Debug --no-restore both exit 0 with zero warnings"
    expected: "Build succeeded. 0 Warning(s). 0 Error(s). on both configurations"
    why_human: "Cannot invoke dotnet build in this environment; SUMMARY claims 0/0 on both configurations"
---

# Phase 9: Processor.GetBySourceHash + Orchestration Start/Stop Verification Report

**Phase Goal:** Add one new endpoint on `ProcessorsController` (`GetBySourceHash`) and create a new `Features/Orchestration/` feature folder containing `OrchestrationController` with two endpoints (`StartOrchestration`, `StopOrchestration`). v1 orchestration endpoints validate only (duplicates + non-empty + non-`Guid.Empty` + existence of every Workflow id) and return `204 No Content` on success — no entity projection, no response payload, no orchestration side-effects.
**Verified:** 2026-05-28
**Status:** human_needed
**Re-verification:** No — initial verification

## Goal Achievement

All six SPEC-local requirements are satisfied at the code level. Every artifact exists, is substantive (not a stub), and is fully wired. The only items requiring human verification are the live build and test-run confirmations, which cannot be executed programmatically in this environment.

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | `GET /api/v1/processors/by-source-hash/{sourceHash}` returns `200 OK + ProcessorReadDto` when a row exists | VERIFIED | `ProcessorController.cs:44-48` — `[HttpGet("by-source-hash/{sourceHash}")]` calls `_processorService.GetBySourceHashAsync` and returns `Ok(...)`. `ProcessorService.cs:53-59` — `DbContext.Set<ProcessorEntity>().AsNoTracking().FirstOrDefaultAsync(p => p.SourceHash == sourceHash, ct)` then `_mapper.ToRead(entity)`. |
| 2 | `GET /api/v1/processors/by-source-hash/{sourceHash}` returns `404 ProblemDetails` when no row matches (including off-format strings) | VERIFIED | `ProcessorService.cs:58` — `throw new NotFoundException(nameof(ProcessorEntity), sourceHash)` on null result. Phase 4 `NotFoundExceptionHandler` maps to 404. `GetBySourceHashFacts.cs` asserts both the random-hash-miss and the `"not-a-hash"` malformed-hash paths return 404 with `resourceType="ProcessorEntity"`. |
| 3 | `POST /api/v1/orchestration/start` and `POST /api/v1/orchestration/stop` return `204 No Content` (empty body) when the `List<Guid>` body is valid and all ids exist | VERIFIED | `OrchestrationController.cs:44-52, 60-68` — both `Start` and `Stop` call `await _service.ValidateWorkflowIdsAsync(workflowIds, ct); return NoContent();`. `StartOrchestrationFacts.cs:94-109` and `StopOrchestrationFacts.cs:80-93` assert `HttpStatusCode.NoContent` with empty body. |
| 4 | Both Orchestration endpoints return `400 ValidationProblemDetails` when the body is null, empty, contains duplicates, or contains `Guid.Empty` | VERIFIED | `OrchestrationDtoValidator.cs:35-43` — `RuleFor(ids => ids).NotNull().NotEmpty()`, `.Must(ids => ids is null or ids.Distinct().Count() == ids.Count)`, `RuleForEach(ids => ids).NotEqual(Guid.Empty)`. `OrchestrationService.cs:89` — `await _idsValidator.ValidateAndThrowAsync(ids, ct)` as step 1. `StartOrchestrationFacts.cs` asserts 400 for duplicate, empty, and `Guid.Empty` inputs. |
| 5 | Both Orchestration endpoints return `404 ProblemDetails` when any GUID in the body does not match an existing `WorkflowEntity` row | VERIFIED | `OrchestrationService.cs:92-103` — `_db.Set<WorkflowEntity>().AsNoTracking().Where(w => ids.Contains(w.Id)).Select(w => w.Id).ToListAsync(ct)` then `throw new NotFoundException(nameof(WorkflowEntity), string.Join(", ", missing))`. `StartOrchestrationFacts.cs:159-181` and `StopOrchestrationFacts.cs:97-116` assert `resourceType="WorkflowEntity"` and `Assert.Contains(missingId.ToString(), resourceId)`. |
| 6 | `Features/Orchestration/` folder exists with the four mandated files; `AddOrchestrationFeature()` is wired into `AppFeatures.AddAppFeatures()`; no new entity, no migration, `Program.cs` unchanged | VERIFIED | Folder contains exactly `OrchestrationController.cs`, `OrchestrationService.cs`, `OrchestrationDtoValidator.cs`, `OrchestrationServiceCollectionExtensions.cs`. `AppFeatures.cs:38` — `services.AddOrchestrationFeature();   // Phase 9 REQ-2`. Zero `OrchestrationEntity`, zero migration files, zero `OrchestrationReadDto`, `Program.cs` unchanged (calls existing `AddAppFeatures()`). |

**Score:** 6/6 truths verified (code-level)

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/BaseApi.Service/Features/Processor/ProcessorService.cs` | `GetBySourceHashAsync` method + injected `_mapper` field | VERIFIED | Lines 31-60: `private readonly IEntityMapper<...> _mapper;`, `public async Task<ProcessorReadDto> GetBySourceHashAsync(string sourceHash, CancellationToken ct)`, uses `DbContext.Set<ProcessorEntity>().AsNoTracking().FirstOrDefaultAsync(...)`, throws `NotFoundException(nameof(ProcessorEntity), sourceHash)` on null |
| `src/BaseApi.Service/Features/Processor/ProcessorController.cs` | `GetBySourceHash` action + `[HttpGet("by-source-hash/{sourceHash}")]` | VERIFIED | Lines 26-48: `private readonly ProcessorService _processorService;`, ctor with both abstract base and concrete service, `[HttpGet("by-source-hash/{sourceHash}")]` + both `[ProducesResponseType]` attributes, returns `Ok(await _processorService.GetBySourceHashAsync(sourceHash, ct))` |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationDtoValidator.cs` | `WorkflowIdsValidator : AbstractValidator<IReadOnlyList<Guid>>` | VERIFIED | 45 lines: `public sealed class WorkflowIdsValidator : AbstractValidator<IReadOnlyList<Guid>>`, three rule groups (NotNull+NotEmpty, Distinct.Count check, RuleForEach NotEqual Guid.Empty), no MaximumCount, no BaseDtoValidator include |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs` | `ValidateWorkflowIdsAsync` + 7-param ctor (db + validator + 5 mappers) | VERIFIED | 106 lines: `public sealed class OrchestrationService` (not BaseService-derived), all 5 mapper fields, `ValidateAndThrowAsync` as step 1, `_db.Set<WorkflowEntity>().AsNoTracking().Where(w => ids.Contains(w.Id)).Select(w => w.Id).ToListAsync(ct)`, `throw new NotFoundException(nameof(WorkflowEntity), string.Join(", ", missing))` |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationController.cs` | `[HttpPost("start")]` and `[HttpPost("stop")]` returning `204 No Content` | VERIFIED | 69 lines: `public sealed class OrchestrationController : ControllerBase`, class-level `[ApiController]`, `[ApiVersion("1.0")]`, `[Route("api/v{version:apiVersion}/[controller]")]`, both endpoints call `ValidateWorkflowIdsAsync` then `return NoContent()` |
| `src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs` | `internal static class` with `AddOrchestrationFeature()` registering `OrchestrationService` Scoped | VERIFIED | 37 lines: `internal static class OrchestrationServiceCollectionExtensions`, `services.AddScoped<OrchestrationService>(); return services;`, no abstract-base alias, no manual validator registration |
| `src/BaseApi.Service/Composition/AppFeatures.cs` | 6th call `services.AddOrchestrationFeature();` after `AddWorkflowFeature()` | VERIFIED | Line 38: `services.AddOrchestrationFeature();   // Phase 9 REQ-2`, correct position after `AddWorkflowFeature()`, `using BaseApi.Service.Features.Orchestration;` present on line 2 |
| `tests/BaseApi.Tests/Features/Processor/GetBySourceHashFacts.cs` | 3 facts: 200 hit, 404 miss, 404 malformed | VERIFIED | 123 lines: `IClassFixture<Phase8WebAppFactory>`, `[Trait("Phase", "9")]`, 3 `[Fact]` methods with correct route `/api/v1/processors/by-source-hash/{hash}`, asserts `resourceType="ProcessorEntity"` on 404 paths |
| `tests/BaseApi.Tests/Features/Orchestration/StartOrchestrationFacts.cs` | 5 facts: 204 happy, 400 dup, 400 empty, 400 Guid.Empty, 404 missing | VERIFIED | 182 lines: `IClassFixture<Phase8WebAppFactory>`, `[Trait("Phase", "9")]`, 5 `[Fact]` methods, seeds Workflow via Processor→Step→Workflow HTTP chain, asserts `resourceType="WorkflowEntity"` on 404 path |
| `tests/BaseApi.Tests/Features/Orchestration/StopOrchestrationFacts.cs` | 2 facts: 204 happy, 404 missing for `/stop` URL | VERIFIED | 117 lines: `IClassFixture<Phase8WebAppFactory>`, `[Trait("Phase", "9")]`, 2 `[Fact]` methods, uses `/api/v1/orchestration/stop`, zero `/start` URL references |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `ProcessorController.cs` | `ProcessorService.cs` | `_processorService.GetBySourceHashAsync(sourceHash, ct)` | WIRED | Line 48: `Ok(await _processorService.GetBySourceHashAsync(sourceHash, ct))` — concrete service injected in ctor (line 30-33), stored in `_processorService` field (line 26) |
| `ProcessorService.cs` | Postgres `processors.source_hash` column | `DbContext.Set<ProcessorEntity>().AsNoTracking().FirstOrDefaultAsync(p => p.SourceHash == sourceHash, ct)` | WIRED | Lines 55-57: exact chain present, parameterized EF Core LINQ predicate, `AsNoTracking()` present |
| `ProcessorService.cs` | `NotFoundException` | `throw new NotFoundException(nameof(ProcessorEntity), sourceHash)` | WIRED | Line 58: exact throw present on null entity result |
| `OrchestrationController.cs` | `OrchestrationService.cs` | `_service.ValidateWorkflowIdsAsync(workflowIds, ct)` | WIRED | Lines 50 and 66: both `Start` and `Stop` call `await _service.ValidateWorkflowIdsAsync(workflowIds, ct)` |
| `OrchestrationService.cs` | `OrchestrationDtoValidator.cs` | `_idsValidator.ValidateAndThrowAsync(ids, ct)` | WIRED | Line 89: `await _idsValidator.ValidateAndThrowAsync(ids, ct)` — `IValidator<IReadOnlyList<Guid>>` injected in ctor (line 49) |
| `OrchestrationService.cs` | Postgres `workflows` table | `_db.Set<WorkflowEntity>().AsNoTracking().Where(w => ids.Contains(w.Id)).Select(w => w.Id).ToListAsync(ct)` | WIRED | Lines 92-96: exact projection chain present, single SQL round-trip |
| `AppFeatures.cs` | `OrchestrationServiceCollectionExtensions.cs` | `services.AddOrchestrationFeature()` | WIRED | Line 38: `services.AddOrchestrationFeature();   // Phase 9 REQ-2`, using directive on line 2 |
| `GetBySourceHashFacts.cs` | `ProcessorController.cs` | `client.GetAsync("/api/v1/processors/by-source-hash/{hash}")` | WIRED | Lines 68, 87, 112: all three facts call the correct route segment |
| `StartOrchestrationFacts.cs` | `OrchestrationController.cs` | `client.PostAsJsonAsync("/api/v1/orchestration/start", ...)` | WIRED | Lines 101, 119, 135, 149, 167: all five facts POST to the `/start` route |
| `StopOrchestrationFacts.cs` | `OrchestrationController.cs` | `client.PostAsJsonAsync("/api/v1/orchestration/stop", ...)` | WIRED | Lines 87 and 103: both Stop facts POST to the `/stop` route, zero `/start` references |

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|---------------|--------|--------------------|--------|
| `ProcessorService.GetBySourceHashAsync` | `entity` (ProcessorEntity) | `DbContext.Set<ProcessorEntity>().AsNoTracking().FirstOrDefaultAsync(...)` | Yes — EF Core translates to parameterized SQL `WHERE source_hash = $1` against the unique index | FLOWING |
| `OrchestrationService.ValidateWorkflowIdsAsync` | `existingIds` (List of Guid) | `_db.Set<WorkflowEntity>().AsNoTracking().Where(w => ids.Contains(w.Id)).Select(w => w.Id).ToListAsync(ct)` | Yes — single SQL `SELECT id WHERE id IN (...)` projection against Postgres | FLOWING |

### Behavioral Spot-Checks

Step 7b: SKIPPED — cannot run `dotnet test` or start the server in this environment. The 09-03-SUMMARY.md documents 3 consecutive GREEN runs (138/138 each) that serve as the behavioral proof. Live confirmation is delegated to human verification.

### Requirements Coverage

Phase 9 REQ-IDs are SPEC-local (not in milestone REQUIREMENTS.md). All 6 are accounted for via the three plans:

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| REQ-1 | 09-01 | Processor.GetBySourceHash endpoint (GET by source_hash, 200/404) | SATISFIED | `ProcessorService.GetBySourceHashAsync` + `ProcessorsController.GetBySourceHash` + 3 integration facts (hit, miss, malformed) |
| REQ-2 | 09-02 | OrchestrationController exists in `Features/Orchestration/` folder with correct layout | SATISFIED | Folder contains 4 mandated files; `AddOrchestrationFeature()` wired into `AppFeatures.AddAppFeatures()` |
| REQ-3 | 09-02, 09-03 | StartOrchestration endpoint (POST /start, 204 on valid body) | SATISFIED | `[HttpPost("start")]` on `OrchestrationController`; `Start_Returns204_AndEmptyBody_WhenWorkflowIdsValid` fact |
| REQ-4 | 09-02, 09-03 | StopOrchestration endpoint (POST /stop, 204 on valid body, identical to Start in v1) | SATISFIED | `[HttpPost("stop")]` on `OrchestrationController`; `Stop_Returns204_AndEmptyBody_WhenWorkflowIdsValid` fact |
| REQ-5 | 09-02, 09-03 | Duplicate-id validation (400 on duplicates, null/empty, Guid.Empty) | SATISFIED | `WorkflowIdsValidator` with 3 rule groups; 3 Start facts (duplicate/empty/Guid.Empty) |
| REQ-6 | 09-02, 09-03 | Existence validation (404 when any Workflow id does not exist, all-or-nothing) | SATISFIED | `OrchestrationService` id-projection existence check + `NotFoundException`; `Start_Returns404_WhenAnyWorkflowIdMissing` + `Stop_Returns404_WhenAnyWorkflowIdMissing` facts |

No REQUIREMENTS.md milestone IDs map to Phase 9 — confirmed by inspection of REQUIREMENTS.md traceability table (Phase 9 not present). REQ-1 through REQ-6 are SPEC-local per 09-SPEC.md.

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| `OrchestrationService.cs` | 66-70 | `_ = _schemaMapper;` `_ = _processorMapper;` `_ = _stepMapper;` `_ = _assignmentMapper;` `_ = _workflowMapper;` | INFO | These are intentional v2 ctor-surface stability suppressors per CONTEXT D-05 ("build for the second use"). The 4 v1-unused mappers are pre-injected because future phases will need them; the `_ = ` assignments prevent IDE0052 warnings under `TreatWarningsAsErrors=true`. This is documented as a "known smell accepted for design-mirror stability" — not a stub. |

No blockers or warnings found. The `_ = _xxxMapper;` pattern is not a stub — it is a documented deliberate design choice with a clear upgrade path.

### Human Verification Required

#### 1. Full Solution Build (Release + Debug)

**Test:** Run `dotnet build SK_P.sln -c Release --no-restore` then `dotnet build SK_P.sln -c Debug --no-restore` from the repo root.
**Expected:** Both commands exit 0 with output `Build succeeded. 0 Warning(s). 0 Error(s).`
**Why human:** Cannot invoke the .NET SDK in this environment. The 09-01-SUMMARY.md and 09-02-SUMMARY.md both document 0/0 build results; this needs live confirmation.

#### 2. Integration Test Suite — 3 Consecutive GREEN Runs

**Test:** Run `dotnet test SK_P.sln --no-restore -c Release` three consecutive times (no code edits between runs).
**Expected:** Each run reports `Passed: 138, Failed: 0`. The 10 new Phase 9 facts (3 GetBySourceHash + 5 StartOrchestration + 2 StopOrchestration) plus 128 prior Phase 1-8 facts all GREEN. Note: the documented Run 0 (137/138) warm-up flake is a known pre-existing issue from Phase 8 (`ConcurrencyTokenTests.Test_RacingWrites` or `LogLevelFilterTests` OTel cold-start); it does not count toward the 3-consecutive-GREEN gate.
**Why human:** Cannot run `dotnet test` in this environment. The 09-03-SUMMARY.md documents three consecutive GREEN runs (138/138, ~29s each) and byte-identical psql snapshots (SHA-256 `1C611C6006E27530F5272739292F9A0C455C9C7F05023C1D362B2EFFF209FE5E` BEFORE and AFTER), but live re-confirmation is required for the verification gate.

### Gaps Summary

No gaps. All six SPEC-local REQ-IDs are satisfied at the code level:

- All artifacts exist, are substantive (no stubs, no placeholders, no empty return bodies), and are correctly wired end-to-end.
- Key links verified: controller-to-service, service-to-database, validator-to-service, DI-extension-to-aggregator.
- Forbidden patterns absent: zero `IOrchestrationService`, zero `OrchestrationEntity`, zero `OrchestrationReadDto`, zero `Phase9WebAppFactory`, zero `MaximumCount` rule.
- Constraint honoring verified: `by-source-hash/{sourceHash}` literal (not bare `{sourceHash}`), `AsNoTracking()` on all read paths, `nameof(ProcessorEntity)` and `nameof(WorkflowEntity)` for exception resource types, `string.Join(", ", missing)` for multi-id 404 detail, both endpoints return `NoContent()` with no body.
- CONTEXT decisions D-01 through D-20 all honored as inspected in code.

The two human verification items (build + test suite) are environmental constraints — the implementation code is complete and correct.

---

_Verified: 2026-05-28_
_Verifier: Claude (gsd-verifier)_
