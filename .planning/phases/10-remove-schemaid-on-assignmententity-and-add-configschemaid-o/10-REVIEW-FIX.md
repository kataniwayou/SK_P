---
phase: 10-remove-schemaid-on-assignmententity-and-add-configschemaid-o
fixed_at: 2026-05-28T00:00:00Z
fix_scope: critical_warning
findings_in_scope: 1
fixed: 1
skipped: 0
iteration: 1
status: all_fixed
---

# Phase 10: Code Review Fix Report

**Fix Scope:** Critical + Warning (default)
**Findings In Scope:** 1 (WR-01)
**Fixed:** 1
**Skipped:** 0
**Status:** all_fixed

## Fixed Findings

### WR-01: AssignmentDtoValidator Payload DoS-prevention claim is inaccurate

**File:** `src/BaseApi.Service/Features/Assignment/AssignmentDtoValidator.cs`
**Commit:** `2de324c` — `fix(10): WR-01 add Cascade(CascadeMode.Stop) to Assignment Payload validator chain`

**Change:** Added `.Cascade(CascadeMode.Stop)` to both `RuleFor(x => x.Payload)` chains
(in `AssignmentCreateDtoValidator` and `AssignmentUpdateDtoValidator`) so that
`MaximumLength` failure short-circuits before the `.Custom(JsonDocument.Parse)`
callback runs. This makes the docstring's load-bearing DoS-prevention claim
(lines 17-19) accurate: an oversized payload now fails fast at the length
check and never hits `JsonDocument.Parse`.

Mirrors the symmetric pattern in
`src/BaseApi.Service/Features/Orchestration/OrchestrationDtoValidator.cs:44-49`
(`WorkflowIdsValidator` WR-03 fix). Added a 4-line comment block on the Create
validator explaining the FluentValidation 12 cascade-mode rationale and a
back-reference on the Update validator.

**Verification:**

- `dotnet build SK_P.sln -c Release --nologo` → 0 Warning(s), 0 Error(s) (7.51s).
- `dotnet test SK_P.sln --no-restore --no-build -c Release --nologo` → 142/142
  GREEN passing in 30.858s (no regression from the Plan 10-05 baseline).

## Skipped Findings

None — only 1 finding (WR-01) was in scope (Critical + Warning).

## Out-of-Scope (Info-level) Findings

The 3 Info-level findings were NOT in scope under the default fix policy:

- **IN-01** — Stale `Assignment.SchemaId` reference in `SchemaEntity.cs:5-7` doc.
  Out of Phase 10 SPEC's documented scope; surface for a future doc-cleanup pass.
- **IN-02** — `ProcessorEntity.cs:7-8` doc says "3 new scalar properties" but
  the entity now has 4 (`ConfigSchemaId` added). Comment-only stale count.
- **IN-04** — `RandomSha256Hex()` test helper duplicated verbatim across 8
  files. Pre-existing; recommend extracting to a shared `TestHelpers` namespace
  in a future refactor pass.

(**IN-03** was advisory-only and self-resolving — the comment "7 positional params"
already matched the post-Phase-10 record arity, no change needed.)

To address Info-level findings, re-run with `--all`:
`/gsd-code-review-fix 10 --all`

---

_Fixed: 2026-05-28T00:00:00Z_
_Iteration: 1 (single pass, no --auto)_
