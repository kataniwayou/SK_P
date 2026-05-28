---
phase: 09-add-getbysourcehash-to-processor-controller-and-new-orchestr
reviewed: 2026-05-28T00:00:00Z
depth: standard
files_reviewed: 10
files_reviewed_list:
  - src/BaseApi.Service/Composition/AppFeatures.cs
  - src/BaseApi.Service/Features/Orchestration/OrchestrationController.cs
  - src/BaseApi.Service/Features/Orchestration/OrchestrationDtoValidator.cs
  - src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs
  - src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs
  - src/BaseApi.Service/Features/Processor/ProcessorController.cs
  - src/BaseApi.Service/Features/Processor/ProcessorService.cs
  - tests/BaseApi.Tests/Features/Orchestration/StartOrchestrationFacts.cs
  - tests/BaseApi.Tests/Features/Orchestration/StopOrchestrationFacts.cs
  - tests/BaseApi.Tests/Features/Processor/GetBySourceHashFacts.cs
findings:
  critical: 0
  warning: 3
  info: 4
  total: 7
status: issues_found
---

# Phase 9: Code Review Report

**Reviewed:** 2026-05-28T00:00:00Z
**Depth:** standard
**Files Reviewed:** 10
**Status:** issues_found

## Summary

Phase 9 adds two surfaces: (1) `GET /api/v1/processors/by-source-hash/{sourceHash}` on the existing Processor feature and (2) a new singular `OrchestrationController` exposing validation-only `start` / `stop` endpoints over `List<Guid>` workflow ids. The implementation is small, follows the established CONTEXT decisions (D-01..D-13), and is well-documented inline. Integration tests cover happy path + validation + existence/404 mapping for all three new endpoints.

The review found no critical (security / data-loss / crash) issues. The 3 warnings are correctness/robustness concerns at the controller -> service -> validator boundary that can produce HTTP 500 responses where 400/404 is expected; specifically, a `null` JSON request body to `/orchestration/start` (or `/stop`) will currently surface as an unhandled exception rather than a `ValidationProblemDetails`. The 4 info items are minor cleanups (input normalization, redundant docs, weak assertions).

Performance considerations (e.g., the `WHERE id IN (...)` translation, allocation of the `Distinct().Count()` pass, the EF Core `Contains` parameter-expansion behavior for very large id lists) are out of scope per the v1 review brief and intentionally not flagged.

## Warnings

### WR-01: Null JSON body to `/orchestration/{start,stop}` is not handled cleanly

**File:** `src/BaseApi.Service/Features/Orchestration/OrchestrationController.cs:48,64`
**Issue:** Both action methods accept `[FromBody] List<Guid> workflowIds` without any null check, and pass the value straight to `_service.ValidateWorkflowIdsAsync(workflowIds, ct)`. When a client posts a JSON `null` (or an empty body), ASP.NET Core binds `workflowIds` to `null` — `[FromBody]` on a reference-type list does not enforce non-null unless an `[ApiController]` / nullable-reference-aware contract forces it (and `List<Guid>` is non-nullable in the signature but the binder does not synthesize a 400 for `null` body by default in many configurations). The service then calls `_idsValidator.ValidateAndThrowAsync(null, ct)` which, depending on the FluentValidation version, throws `ArgumentNullException` (current 11.x) rather than the `ValidationException` the Phase 4 handler maps to 400. The `WorkflowIdsValidator.NotNull()` rule is therefore unreachable for the bare-null case — it can only fire for nested-null scenarios that do not apply to a bare list. Inside `OrchestrationService.ValidateWorkflowIdsAsync`, the subsequent `ids.Contains(w.Id)` and `ids.Except(...)` would also NRE if validation didn't throw first.
**Fix:** Guard at the action edge (or at the top of the service method) and translate to a `ValidationException` so the existing 400 mapping fires. Either:
```csharp
// In OrchestrationService.ValidateWorkflowIdsAsync, before the validator call:
ArgumentNullException.ThrowIfNull(ids);   // becomes 500 — NOT what we want
// — OR (preferred, matches the 400 contract advertised by ProducesResponseType):
if (ids is null)
{
    throw new ValidationException(new[]
    {
        new ValidationFailure(nameof(ids), "Request body must not be null.")
    });
}
await _idsValidator.ValidateAndThrowAsync(ids, ct);
```
Add a test fact: `Start_Returns400_WhenBodyIsNull` posting raw `"null"` as the JSON body and asserting 400 + `application/problem+json`.

### WR-02: `ProcessorService.GetBySourceHashAsync` does not guard against `null` / empty `sourceHash`

**File:** `src/BaseApi.Service/Features/Processor/ProcessorService.cs:53-60`
**Issue:** The method dereferences `sourceHash` directly into `p.SourceHash == sourceHash`. In normal routing `string sourceHash` from `{sourceHash}` will be a non-null, non-empty segment (the route would not match an empty segment), so this is unlikely to NRE in production. However: (1) the method is `public` and reachable from non-controller call sites (the `OrchestrationService` ctor already pre-injects mappers anticipating cross-entity reuse — Phase 9 CONTEXT D-05), so future callers may pass `null`; (2) an empty string would translate to `WHERE source_hash = ''`, which would issue an unnecessary round-trip and emit a misleading 404 with `resourceId=""`. The CONTEXT D-03 "off-format hashes 404 via row-miss" rule is explicit about *off-format* strings reaching the DB, but does not require null/empty to do so.
**Fix:** Add a defensive guard that mirrors the rest of the service-layer style:
```csharp
public async Task<ProcessorReadDto> GetBySourceHashAsync(string sourceHash, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(sourceHash))
        throw new NotFoundException(nameof(ProcessorEntity), sourceHash ?? "(null)");
    // ... existing body unchanged
}
```
The 404 is consistent with the existing "miss => 404" contract and avoids a DB round-trip on a guaranteed-empty result.

### WR-03: `WorkflowIdsValidator` duplicate-detection rule emits redundant errors for the empty case

**File:** `src/BaseApi.Service/Features/Orchestration/OrchestrationDtoValidator.cs:33-39`
**Issue:** Three top-level `RuleFor(ids => ids)` chains all run unconditionally because FluentValidation does not short-circuit across separate `RuleFor` builders by default. For a `null` body the `.Must(ids => ids is null || ...)` short-circuit lets the duplicate rule pass silently — good — but if the bound value is a non-null empty list, the validator emits one error for `NotEmpty()` and zero errors for the duplicate rule (also good). The actual subtle bug: the duplicate rule uses `ids.Distinct().Count() == ids.Count`, which for `Guid` uses the default `EqualityComparer`, but does NOT short-circuit on the empty case at the rule-level — it allocates an enumerator and a distinct-set even for the trivial `[]` input. More importantly, when validation fails on `NotEmpty`, the duplicate rule still iterates the empty list (harmless but wasteful), AND if a future maintainer reads only the duplicate rule, the inline `ids is null || ...` null-guard implies the rule is responsible for the null case — it is not. Mixing null-handling responsibilities across rules is a small footgun.
**Fix:** Use `RuleFor(...).Cascade(CascadeMode.Stop)` to collapse the three rules into one chain so `NotEmpty()` short-circuits the duplicate scan, and drop the inline `ids is null ||` once the cascade owns null:
```csharp
RuleFor(ids => ids)
    .Cascade(CascadeMode.Stop)
    .NotNull()
    .NotEmpty()
    .Must(ids => ids.Distinct().Count() == ids.Count)
        .WithMessage("WorkflowIds must be unique.");

RuleForEach(ids => ids)
    .NotEqual(Guid.Empty)
    .WithMessage("WorkflowIds must not contain Guid.Empty.");
```
This also makes the rule order matches the documentation block at the top of the file (NotNull -> NotEmpty -> Distinct -> per-item GuidEmpty).

## Info

### IN-01: `sourceHash` route segment is not normalized — case sensitivity may surprise callers

**File:** `src/BaseApi.Service/Features/Processor/ProcessorService.cs:57`
**Issue:** The `ProcessorDtoValidator` (referenced in the grep results) enforces that `SourceHash` is stored as a *lowercase* SHA-256 hex string (`[a-f0-9]{64}`), but `GetBySourceHashAsync` performs a case-sensitive equality match on the raw route segment. A caller who passes the same hash uppercased will receive a 404 even though semantically the resource exists. This is consistent with SPEC.md Constraint ("no route-level validation") but worth a `<remarks>` doc note so API consumers know to lowercase before calling.
**Fix:** Either (a) add a single line `sourceHash = sourceHash?.ToLowerInvariant();` before the query (matches the storage normalization the validator enforces), or (b) add an XML `<remarks>` block on `ProcessorsController.GetBySourceHash` and `ProcessorService.GetBySourceHashAsync` stating "Callers MUST supply the hash lowercased; uppercase variants will 404."

### IN-02: Duplicated `SeedWorkflowAsync` / `RandomSha256Hex` across `StartOrchestrationFacts` and `StopOrchestrationFacts`

**File:** `tests/BaseApi.Tests/Features/Orchestration/StartOrchestrationFacts.cs:41-91` and `tests/BaseApi.Tests/Features/Orchestration/StopOrchestrationFacts.cs:36-77`
**Issue:** The two test classes copy ~40 lines of seeding helpers verbatim (the only delta is the `orch-stop-` vs `orch-` name prefix). This violates DRY and means any change to the Processor/Step/Workflow create-DTO shape requires editing both files. The XML doc on `StopOrchestrationFacts` already notes "behaviorally identical to Start" — the seeding helper is a natural shared-fixture candidate.
**Fix:** Extract `SeedWorkflowAsync` and `RandomSha256Hex` to a `static class OrchestrationTestSeeds` under `tests/BaseApi.Tests/Features/Orchestration/`. Both fact classes call into it. Optional: parameterize the name prefix so the two suites still produce distinct seed names for grep-ability.

### IN-03: `_ = _schemaMapper;` discard pattern in `OrchestrationService` ctor is unusual

**File:** `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs:64-70`
**Issue:** Storing a field and then immediately discarding the local reference (`_ = _schemaMapper;`) does NOT suppress IDE0052 ("private field is assigned but its value is never used") in all analyzer configurations — the discard expression is only a read of the *field*, but the compiler analyzer may still flag the field as write-only if no other method reads it. The CONTEXT D-05 rationale (future-proofing ctor surface) is valid, but the suppression mechanism may not work as documented.
**Fix:** Either (a) add `#pragma warning disable IDE0052` blocks around the 4 unused field declarations with a comment pointing to CONTEXT D-05, or (b) annotate the fields with `[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0052:Remove unread private members", Justification = "Phase 9 CONTEXT D-05 — pre-injected for v2 stability.")]`. The current `_ = field;` pattern is non-idiomatic and creates noise for readers.

### IN-04: Test assertion `Assert.Contains(missingId.ToString(), resourceId)` accepts substring match — could mask a serialization bug

**File:** `tests/BaseApi.Tests/Features/Orchestration/StartOrchestrationFacts.cs:180` and `tests/BaseApi.Tests/Features/Orchestration/StopOrchestrationFacts.cs:115`
**Issue:** The 404 test asserts that the missing-id GUID is *contained* in the `resourceId` extension, but does not assert the exact format produced by `string.Join(", ", missing)`. If a future refactor changes the separator (e.g., to `;` or `\n`) or wraps the ids (e.g., `"[id1, id2]"`), the substring match still passes for the single-id case — defeating the purpose of the test. The single-id test cannot detect a regression in the multi-id formatting either.
**Fix:** For the single-id case, assert exact equality: `Assert.Equal(missingId.ToString(), resourceId);` Add a multi-id 404 fact that posts `[Guid.NewGuid(), Guid.NewGuid()]` and asserts the comma-separated format (e.g., `Assert.Contains(", ", resourceId)` plus both id substrings) — this locks in the contract `string.Join(", ", missing)` advertises.

---

_Reviewed: 2026-05-28T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
