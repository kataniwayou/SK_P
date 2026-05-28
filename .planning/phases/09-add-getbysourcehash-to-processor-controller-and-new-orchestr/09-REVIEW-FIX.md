---
phase: 09-add-getbysourcehash-to-processor-controller-and-new-orchestr
fixed_at: 2026-05-28T00:00:00Z
review_path: .planning/phases/09-add-getbysourcehash-to-processor-controller-and-new-orchestr/09-REVIEW.md
iteration: 1
findings_in_scope: 3
fixed: 3
skipped: 0
status: all_fixed
---

# Phase 9: Code Review Fix Report

**Fixed at:** 2026-05-28T00:00:00Z
**Source review:** `.planning/phases/09-add-getbysourcehash-to-processor-controller-and-new-orchestr/09-REVIEW.md`
**Iteration:** 1

**Summary:**
- Findings in scope: 3 (warnings only; 4 info findings are out of scope)
- Fixed: 3
- Skipped: 0

All warnings in scope were applied cleanly, verified via `dotnet build SK_P.sln` (0 warnings, 0 errors), and committed atomically. Info findings (IN-01 through IN-04) were not addressed per the `critical_warning` fix scope.

## Fixed Issues

### WR-01: Null JSON body to `/orchestration/{start,stop}` is not handled cleanly

**Files modified:** `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs`
**Commit:** `e456551`
**Applied fix:** Added a null-`ids` guard at the top of `OrchestrationService.ValidateWorkflowIdsAsync` that throws `FluentValidation.ValidationException` (carrying a single `ValidationFailure` with property name `ids` and the message "Request body must not be null."). This translates a `null` request body into the HTTP 400 `ValidationProblemDetails` advertised by `[ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]` on both `Start` and `Stop`, instead of surfacing as an unhandled `ArgumentNullException` (HTTP 500) when `IValidator.ValidateAndThrowAsync(null, ...)` is called. Also added `using FluentValidation.Results;` for the `ValidationFailure` symbol. The guard is placed in the service (not the controller) so the contract holds for any future non-controller caller. The fix matches the review's preferred option and routes through the Phase 4 `ValidationExceptionHandler` mapping.

### WR-02: `ProcessorService.GetBySourceHashAsync` does not guard against null/empty `sourceHash`

**Files modified:** `src/BaseApi.Service/Features/Processor/ProcessorService.cs`
**Commit:** `cde104e`
**Applied fix:** Added a `string.IsNullOrWhiteSpace(sourceHash)` short-circuit at the top of `GetBySourceHashAsync` that throws `NotFoundException(nameof(ProcessorEntity), sourceHash ?? "(null)")` before any DB call. This (a) avoids a guaranteed-empty round-trip on `WHERE source_hash = ''`, (b) prevents a misleading 404 with `resourceId=""`, and (c) makes the public method safe for non-controller callers (the `OrchestrationService` already pre-injects mappers per CONTEXT D-05, signalling future cross-entity reuse). The 404 outcome is consistent with the existing miss-equals-404 contract and the off-format-hash CONTEXT D-03 rule. Mirrors the review's suggested guard verbatim.

### WR-03: `WorkflowIdsValidator` duplicate-detection rule emits redundant work for the empty case

**Files modified:** `src/BaseApi.Service/Features/Orchestration/OrchestrationDtoValidator.cs`
**Commit:** `c5da00a`
**Applied fix:** Collapsed the two top-level `RuleFor(ids => ids)` chains into a single `.Cascade(CascadeMode.Stop)` chain ordered `NotNull -> NotEmpty -> Must(distinct)`. The cascade short-circuits the duplicate scan whenever `NotNull` or `NotEmpty` already fails, removes the now-redundant inline `ids is null ||` null-guard from the duplicate rule (the chain head owns the null case), and matches the rule order documented in the class summary. The per-item `RuleForEach(...).NotEqual(Guid.Empty)` rule was left as a separate top-level rule (it has its own iteration semantics and the review's fix snippet preserves it that way). The `.Cascade(CascadeMode.Stop)` extension method is the FluentValidation 12 idiom (verified against Phase 6 RESEARCH FV12 upgrade notes: only the `AbstractValidator.CascadeMode` *property* was removed; the per-rule extension remains in FV12.1.1 — the version pinned in `Directory.Packages.props`).

## Verification

- **Tier 1 (mandatory):** Re-read each modified file section after edit and confirmed the fix text was present with surrounding code intact.
- **Tier 2 (preferred):** `dotnet build src/BaseApi.Service/BaseApi.Service.csproj` succeeded after each fix (0 warnings, 0 errors). After all three fixes, `dotnet build SK_P.sln` (full solution including `BaseApi.Tests`) reported 0 warnings, 0 errors — confirming the fixes do not regress any caller, including the integration test project.
- **Tier 3:** Not required (Tier 2 covered every modified file).

No rollbacks were needed. No files were left in an uncommitted state.

---

_Fixed: 2026-05-28T00:00:00Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
