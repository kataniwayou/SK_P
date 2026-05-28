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
  warning: 0
  info: 4
  total: 4
status: issues_found
iteration: 2
prior_review_commits:
  - e456551  # WR-01 fix
  - cde104e  # WR-02 fix
  - c5da00a  # WR-03 fix
---

# Phase 9: Code Review Report (Re-review, Iteration 2)

**Reviewed:** 2026-05-28T00:00:00Z
**Depth:** standard
**Files Reviewed:** 10
**Status:** issues_found (info-only — all warnings resolved)

## Summary

Re-review after commits `e456551` (WR-01), `cde104e` (WR-02), and `c5da00a` (WR-03). All three prior warnings are resolved and the fixes were applied cleanly with appropriate inline comments tying each guard back to its review item:

- **WR-01 (verified):** `OrchestrationService.ValidateWorkflowIdsAsync` now throws `FluentValidation.ValidationException` with a single `ValidationFailure(nameof(ids), "Request body must not be null.")` when `ids is null`, before any validator/DB call (`OrchestrationService.cs:96-102`). The Phase 4 `ValidationExceptionHandler` will map this to the 400 `ValidationProblemDetails` advertised by `[ProducesResponseType]` on both `Start` and `Stop`. Guard lives in the service rather than the controller, so non-controller callers also benefit. `using FluentValidation.Results;` was added correctly.
- **WR-02 (verified):** `ProcessorService.GetBySourceHashAsync` now short-circuits on `string.IsNullOrWhiteSpace(sourceHash)` and throws `NotFoundException(nameof(ProcessorEntity), sourceHash ?? "(null)")` before the EF Core round-trip (`ProcessorService.cs:61-62`). Behaviour matches the existing "miss → 404" contract and the CONTEXT D-03 off-format-hash rule.
- **WR-03 (verified):** `WorkflowIdsValidator` collapsed the prior two top-level `RuleFor(...)` chains into a single `Cascade(CascadeMode.Stop)` chain ordered `NotNull → NotEmpty → Must(distinct)` (`OrchestrationDtoValidator.cs:44-49`). The previously-redundant inline `ids is null || …` null-guard on the duplicate rule was correctly removed. The per-item `RuleForEach(...).NotEqual(Guid.Empty)` is correctly preserved as a separate top-level rule (its iteration semantics differ from the cascading head rule).

No new bugs, security issues, crashes, NRE candidates, or unhandled-exception paths were introduced by the fix commits. No critical or warning items remain. The four info items below are carried forward verbatim from the iteration-1 review (none of them were in scope for `critical_warning` fixing) and remain accurate against the current source. Performance topics (EF Core `Contains` parameter expansion, `Distinct().Count()` allocations) remain out of v1 review scope.

## Info

### IN-01: `sourceHash` route segment is not normalized — case sensitivity may surprise callers

**File:** `src/BaseApi.Service/Features/Processor/ProcessorService.cs:64-67`
**Issue:** The `ProcessorDtoValidator` enforces that `SourceHash` is stored as a lowercase SHA-256 hex string (`[a-f0-9]{64}`), but `GetBySourceHashAsync` performs a case-sensitive equality match on the raw route segment (`p.SourceHash == sourceHash`). A caller who passes the same hash uppercased will receive a 404 even though semantically the resource exists. This is consistent with SPEC.md Constraint ("no route-level validation") but is invisible to API consumers without a docs note. The WR-02 guard runs before the DB call but does not normalize case — only whitespace/null is handled.
**Fix:** Either (a) add a single line `sourceHash = sourceHash.ToLowerInvariant();` immediately after the `IsNullOrWhiteSpace` guard (matches storage-side normalization the validator enforces), or (b) add an XML `<remarks>` block on both `ProcessorsController.GetBySourceHash` and `ProcessorService.GetBySourceHashAsync` stating "Callers MUST supply the hash lowercased; uppercase variants will 404." Option (a) is a one-liner and removes a footgun; option (b) preserves strict round-tripping.

### IN-02: Duplicated `SeedWorkflowAsync` / `RandomSha256Hex` across `StartOrchestrationFacts` and `StopOrchestrationFacts`

**File:** `tests/BaseApi.Tests/Features/Orchestration/StartOrchestrationFacts.cs:41-91` and `tests/BaseApi.Tests/Features/Orchestration/StopOrchestrationFacts.cs:36-77`
**Issue:** The two test classes copy ~40 lines of seeding helpers verbatim (the only delta is the `orch-stop-` vs `orch-` name prefix). This violates DRY and means any change to the Processor / Step / Workflow create-DTO shape requires editing both files. `StopOrchestrationFacts`' own XML doc already says "behaviorally identical to Start" — the seeding helper is a natural shared-fixture candidate.
**Fix:** Extract `SeedWorkflowAsync` and `RandomSha256Hex` to a `static class OrchestrationTestSeeds` under `tests/BaseApi.Tests/Features/Orchestration/`. Both fact classes call into it. Optional: parameterize the name prefix so the two suites still produce distinct seed names for grep-ability.

### IN-03: `_ = _schemaMapper;` discard pattern in `OrchestrationService` ctor is unusual

**File:** `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs:65-71`
**Issue:** Storing a field and then immediately discarding the local reference (`_ = _schemaMapper;` etc.) does not reliably suppress IDE0052 ("private field is assigned but its value is never used") across all analyzer configurations — the discard expression is a *field* read in C# semantics, but some analyzer rule-sets still flag the field as write-only if no other method reads it. The CONTEXT D-05 rationale (future-proofing the ctor surface so v2 phases add methods rather than ctor params) remains valid, but the suppression mechanism is non-idiomatic and creates reader noise. The five non-discarded fields (`_db`, `_idsValidator`, and the same five mappers again) all already get a write — only the four `v1-unused` mappers need a suppression.
**Fix:** Either (a) replace the four `_ = …;` lines with `#pragma warning disable IDE0052` / `#pragma warning restore IDE0052` around the four declarations with a comment pointing to CONTEXT D-05, or (b) annotate each unused field with `[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0052:Remove unread private members", Justification = "Phase 9 CONTEXT D-05 — pre-injected for v2 ctor stability.")]`. Option (b) is more localized and travels with the field.

### IN-04: Test assertion `Assert.Contains(missingId.ToString(), resourceId)` accepts substring match — could mask a serialization bug

**File:** `tests/BaseApi.Tests/Features/Orchestration/StartOrchestrationFacts.cs:180` and `tests/BaseApi.Tests/Features/Orchestration/StopOrchestrationFacts.cs:115`
**Issue:** The 404 tests assert that the missing-id GUID is *contained* in the `resourceId` extension, but do not assert the exact format produced by `string.Join(", ", missing)`. If a future refactor changes the separator (e.g., to `;` or `\n`) or wraps the ids (e.g., `"[id1, id2]"`), the substring match still passes for the single-id case — defeating the purpose of the test. The single-id test cannot detect a regression in the multi-id formatting either.
**Fix:** For the single-id case, assert exact equality: `Assert.Equal(missingId.ToString(), resourceId);` Add a multi-id 404 fact that posts `[Guid.NewGuid(), Guid.NewGuid()]` and asserts the comma-separated format (e.g., `Assert.Contains(", ", resourceId)` plus both id substrings) — this locks in the `string.Join(", ", missing)` contract.

---

_Reviewed: 2026-05-28T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
_Iteration: 2 (re-review after WR-01/WR-02/WR-03 fix commits e456551 / cde104e / c5da00a)_
