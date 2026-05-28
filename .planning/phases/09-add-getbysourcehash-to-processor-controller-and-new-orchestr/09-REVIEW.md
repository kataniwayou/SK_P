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
  info: 0
  total: 0
status: clean
iteration: 3
prior_review_commits:
  - e456551  # WR-01 fix (iteration 1)
  - cde104e  # WR-02 fix (iteration 1)
  - c5da00a  # WR-03 fix (iteration 1)
  - d170630  # IN-01 fix (iteration 2)
  - 095b039  # IN-03 fix (iteration 2)
  - 907b802  # IN-04 fix (iteration 2)
prior_skipped:
  - IN-02  # test helper duplication — intentionally skipped per CONTEXT decision (sibling-file coupling cost > DRY win)
---

# Phase 9: Code Review Report (Re-review, Iteration 3)

**Reviewed:** 2026-05-28T00:00:00Z
**Depth:** standard
**Files Reviewed:** 10
**Status:** clean

## Summary

Iteration-3 re-review after the three iteration-2 info-fix commits (`d170630` IN-01, `095b039` IN-03, `907b802` IN-04) on top of the iteration-1 warning-fix commits (`e456551` WR-01, `cde104e` WR-02, `c5da00a` WR-03). All 7 of the 7 in-scope findings across iterations 1 and 2 are verified resolved; the single remaining iteration-2 item (IN-02 — test helper duplication) was intentionally skipped per CONTEXT decision and is **not** re-raised in this iteration.

### Verification of iteration-2 fix commits

- **IN-01 (verified — commit `d170630`):** `ProcessorService.GetBySourceHashAsync` (`ProcessorService.cs:53-65`) and `ProcessorsController.GetBySourceHash` (`ProcessorController.cs:44-52`) now carry matching `<remarks>` XML doc blocks documenting the case-sensitivity contract (callers MUST supply the hash lowercased; uppercase/mixed-case 404 via row-miss), the SPEC.md Constraint + CONTEXT D-03 references, and why option (a) — `ToLowerInvariant()` normalization — was deliberately rejected (would break round-trip symmetry with the create-time validator that enforces lowercase `[a-f0-9]{64}`). The controller's remarks cross-reference the service method's remarks, keeping the two blocks in sync. The cref to `ProcessorCreateDtoValidator` (not the file name `ProcessorDtoValidator`) is correctly resolved — no CS1574.
- **IN-03 (verified — commit `095b039`):** The five `_ = _xxxMapper;` discard lines were removed from the `OrchestrationService` ctor and replaced with per-field `[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0052:Remove unread private members", Justification = "Phase 9 CONTEXT D-05 — pre-injected for v2 ctor stability.")]` attributes on each of the five mapper fields (`OrchestrationService.cs:47-56`). Suppression now travels with each field declaration (more localized than a pragma region, robust across analyzer configurations). The CONTEXT D-05 rationale (pre-inject all 5 mappers for v2 ctor stability) is preserved in the field-level Justification and an explanatory comment block at the end of the ctor.
- **IN-04 (verified — commit `907b802`):** Both `StartOrchestrationFacts` and `StopOrchestrationFacts` had their single-id 404 assertion tightened from `Assert.Contains` to `Assert.Equal(missingId.ToString(), resourceId)` (`StartOrchestrationFacts.cs:183`, `StopOrchestrationFacts.cs:118`), and each file gained a new multi-id 404 fact (`Start_Returns404_WithCommaJoinedIds_WhenMultipleWorkflowIdsMissing` at lines 193-229; `Stop_Returns404_WithCommaJoinedIds_WhenMultipleWorkflowIdsMissing` at lines 129-157) that asserts both id substrings, the `", "` separator, and exact-equality against both possible LINQ-`Except` orderings. The dual-ordering check (`resourceId == expectedInOrder || resourceId == expectedReversed`) avoids over-coupling to LINQ's incidental input-order behaviour while still locking the `string.Join(", ", missing)` shape.

### Verification that iteration-1 fixes remain intact

- **WR-01 (still verified):** `OrchestrationService.ValidateWorkflowIdsAsync` still throws `FluentValidation.ValidationException` with `new ValidationFailure(nameof(ids), "Request body must not be null.")` when `ids is null`, before any validator/DB call (`OrchestrationService.cs:103-109`). The Phase 4 `ValidationExceptionHandler` maps this to 400 `ValidationProblemDetails`.
- **WR-02 (still verified):** `ProcessorService.GetBySourceHashAsync` still short-circuits on `string.IsNullOrWhiteSpace(sourceHash)` before the EF round-trip (`ProcessorService.cs:74-75`).
- **WR-03 (still verified):** `WorkflowIdsValidator` still uses a single `Cascade(CascadeMode.Stop)` chain ordered `NotNull → NotEmpty → Must(distinct)` (`OrchestrationDtoValidator.cs:44-49`), with `RuleForEach(...).NotEqual(Guid.Empty)` correctly preserved as a separate top-level rule.

### Re-scan for new issues introduced by the fix commits

A fresh standard-depth scan of all 10 files turned up no new bugs, security issues, NRE candidates, or unhandled-exception paths introduced by the three iteration-2 fix commits:

- The new `[SuppressMessage]` attributes on `OrchestrationService` (IN-03 fix) are pure metadata — no runtime behaviour change.
- The new XML `<remarks>` blocks on `ProcessorService` / `ProcessorsController` (IN-01 fix) are pure documentation — no runtime behaviour change. The cref targets resolve correctly.
- The two new multi-id 404 facts (IN-04 fix) reuse the existing `Phase8WebAppFactory` harness and the same code path under test as the single-id facts already exercised in iteration 1; the new assertions are stricter than the originals and do not over-couple to LINQ ordering. No flakiness or test-isolation concerns: `missingId1` / `missingId2` are freshly-generated `Guid.NewGuid()` values with no DB seeding, so cross-test interference is impossible.

### Carry-forward of IN-02 (intentionally skipped, NOT re-raised)

Per CONTEXT decision recorded in `09-REVIEW-FIX.md` iteration-2, the duplicated `SeedWorkflowAsync` / `RandomSha256Hex` helpers across `StartOrchestrationFacts` and `StopOrchestrationFacts` are an accepted trade-off (sibling-file independence valued over DRY at this scale). Re-raising this in iteration 3 would contradict the documented decision. Per the orchestrator prompt this finding is explicitly out of scope for this review pass and is therefore not listed as a current finding.

### Status decision

All in-scope findings from iterations 1 and 2 are resolved; the single deferred item (IN-02) is documented as a deliberate non-issue per CONTEXT and out-of-scope for re-raising. No new findings were detected during the fresh standard-depth scan. Status is **clean**.

---

_Reviewed: 2026-05-28T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
_Iteration: 3 (re-review after IN-01/IN-03/IN-04 fix commits d170630 / 095b039 / 907b802; IN-02 intentionally deferred per CONTEXT)_
