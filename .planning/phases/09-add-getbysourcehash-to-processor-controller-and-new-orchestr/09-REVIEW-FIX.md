---
phase: 09-add-getbysourcehash-to-processor-controller-and-new-orchestr
fixed_at: 2026-05-28T00:00:00Z
review_path: .planning/phases/09-add-getbysourcehash-to-processor-controller-and-new-orchestr/09-REVIEW.md
iteration: 2
findings_in_scope: 4
fixed: 3
skipped: 1
status: partial
---

# Phase 9: Code Review Fix Report (Iteration 2)

**Fixed at:** 2026-05-28T00:00:00Z
**Source review:** `.planning/phases/09-add-getbysourcehash-to-processor-controller-and-new-orchestr/09-REVIEW.md`
**Iteration:** 2

**Summary:**
- Findings in scope: 4 (all info — `fix_scope: all`)
- Fixed: 3 (IN-01, IN-03, IN-04)
- Skipped: 1 (IN-02 — deferred per CONTEXT decision; documented below)

Iteration-1 warnings (WR-01 `e456551`, WR-02 `cde104e`, WR-03 `c5da00a`) are already fixed and were re-verified in the iteration-2 review. This run addresses the 4 info-only findings carried forward from iteration 1 under the `all` fix policy.

Full-solution build (`dotnet build SK_P.sln`) reports **0 warnings, 0 errors** after all three fixes. No rollbacks were needed; no files were left in an uncommitted state.

## Fixed Issues

### IN-01: `sourceHash` route segment is not normalized — case sensitivity may surprise callers

**Files modified:** `src/BaseApi.Service/Features/Processor/ProcessorService.cs`, `src/BaseApi.Service/Features/Processor/ProcessorController.cs`
**Commit:** `d170630`
**Applied fix:** Selected REVIEW option **(b) — documentation only**, not option (a) (`ToLowerInvariant()` normalization). Rationale: option (a) would silently accept inputs the `ProcessorCreateDtoValidator` rejects on writes (the validator enforces lowercase `[a-f0-9]{64}` at create time), breaking round-trip symmetry between writes and reads and contradicting SPEC.md Constraint "no route-level validation" + CONTEXT D-03 "off-format strings 404 via row-miss". Option (b) preserves the strict miss-equals-404 contract.

Added `<remarks>` XML doc-comment blocks to both `ProcessorService.GetBySourceHashAsync` and `ProcessorsController.GetBySourceHash` explicitly documenting (1) the case-sensitivity contract ("callers MUST supply the hash lowercased; uppercase/mixed-case variants will 404"), (2) the reference to SPEC.md Constraint + CONTEXT D-03, and (3) why option (a) was rejected. The controller's remarks point readers to the service method's remarks for the full rationale, keeping the two blocks in sync.

Build verification: initial cref `ProcessorDtoValidator` failed (CS1574 — the file is `ProcessorDtoValidator.cs` but the class is `ProcessorCreateDtoValidator`); cref was corrected to `ProcessorCreateDtoValidator` and the build then succeeded with 0 warnings, 0 errors.

### IN-03: `_ = _schemaMapper;` discard pattern in `OrchestrationService` ctor is unusual

**Files modified:** `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs`
**Commit:** `095b039`
**Applied fix:** Selected REVIEW option **(b) — per-field `[SuppressMessage]` attribute**, not option (a) (`#pragma warning disable/restore` blocks). Rationale: the attribute travels with the field declaration (a reader of the field sees the justification right there), is more localized than a pragma region, and is robust across analyzer configurations (the iteration-1 review's specific concern — that "some analyzer rule-sets still flag the field as write-only" with the discard pattern).

Annotated all 5 mapper fields with `[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0052:Remove unread private members", Justification = "Phase 9 CONTEXT D-05 — pre-injected for v2 ctor stability.")]`. Removed the 5 `_ = _xxxMapper;` discard lines from the ctor and left a brief comment block at the end of the ctor pointing to the new field-level attributes. CONTEXT D-05 design decision (pre-inject all 5 mappers for v2 ctor stability) is fully preserved — only the suppression mechanism changed.

Build verification: `dotnet build src/BaseApi.Service/BaseApi.Service.csproj` succeeded with 0 warnings, 0 errors.

### IN-04: Test assertion `Assert.Contains(missingId.ToString(), resourceId)` accepts substring match — could mask a serialization bug

**Files modified:** `tests/BaseApi.Tests/Features/Orchestration/StartOrchestrationFacts.cs`, `tests/BaseApi.Tests/Features/Orchestration/StopOrchestrationFacts.cs`
**Commit:** `907b802`
**Applied fix:** Two changes per file:

1. **Tightened existing single-id 404 assertion** from `Assert.Contains(missingId.ToString(), resourceId)` to `Assert.Equal(missingId.ToString(), resourceId)`. For a one-element list, `string.Join(", ", missing)` emits the bare GUID with no separator or wrapping, so strict equality is the correct shape and catches any future regression that wraps the id (e.g., `[id]`) or changes the join character.

2. **Added a new multi-id 404 fact** (`Start_Returns404_WithCommaJoinedIds_WhenMultipleWorkflowIdsMissing` / `Stop_Returns404_WithCommaJoinedIds_WhenMultipleWorkflowIdsMissing`) that POSTs `[Guid.NewGuid(), Guid.NewGuid()]` and asserts (a) both id substrings appear in `resourceId`, (b) the `", "` (comma + single space) separator is present, and (c) `resourceId` is exactly `"{id1}, {id2}"` OR `"{id2}, {id1}"` — locking the `string.Join(", ", missing)` contract while not over-coupling to LINQ Except's incidental input-order behaviour. The Stop variant carries an XML doc-comment noting it defends against a future Start/Stop divergence per CONTEXT D-12.

Build verification: `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj` succeeded with 0 warnings, 0 errors. The new facts were not executed (no `dotnet test` run per `<verification_strategy>` — full test suite is the verifier phase's job), but they share the same `Phase8WebAppFactory` harness and DB shape as the existing 404 facts already exercised by iteration 1, so the runtime contract is the same.

## Skipped Issues

### IN-02: Duplicated `SeedWorkflowAsync` / `RandomSha256Hex` across `StartOrchestrationFacts` and `StopOrchestrationFacts`

**Files:** `tests/BaseApi.Tests/Features/Orchestration/StartOrchestrationFacts.cs:41-91`, `tests/BaseApi.Tests/Features/Orchestration/StopOrchestrationFacts.cs:36-77`
**Reason:** Skipped per orchestrator prompt guidance ("IN-02 deferred per CONTEXT decision — 'would couple sibling files'"). Extracting the helpers to a shared `OrchestrationTestSeeds` static class would intentionally couple two sibling test files that the CONTEXT decision keeps independent. The duplication is cheap (~40 lines, one helper) and the test files remain self-contained, which is the deliberate trade-off. Defer until a third orchestration test class lands or the seeding shape needs to change in lockstep across files — at that point the DRY win clearly exceeds the coupling cost.
**Original issue:** The two test classes copy ~40 lines of seeding helpers verbatim (the only delta is the `orch-stop-` vs `orch-` name prefix). This violates DRY and means any change to the Processor / Step / Workflow create-DTO shape requires editing both files. `StopOrchestrationFacts`' own XML doc already says "behaviorally identical to Start" — the seeding helper is a natural shared-fixture candidate.

## Verification

- **Tier 1 (mandatory):** Re-read each modified file section after edit and confirmed the fix text was present with surrounding code intact. Done for all 3 fixed findings (5 files total).
- **Tier 2 (preferred):** `dotnet build` was executed after each fix:
  - IN-01: initial build failed (CS1574 on `cref="ProcessorDtoValidator"`); corrected to `ProcessorCreateDtoValidator`, rebuild succeeded with 0 warnings, 0 errors. (No rollback needed — the failure was caught by Tier 2 and corrected in-place before commit.)
  - IN-03: `dotnet build src/BaseApi.Service/BaseApi.Service.csproj` succeeded with 0 warnings, 0 errors.
  - IN-04: `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj` succeeded with 0 warnings, 0 errors.
  - Final full-solution check: `dotnet build SK_P.sln` reported 0 warnings, 0 errors after all three commits.
- **Tier 3:** Not required (Tier 2 covered every modified file).

No rollbacks were needed. No files were left in an uncommitted state.

---

_Fixed: 2026-05-28T00:00:00Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 2_
